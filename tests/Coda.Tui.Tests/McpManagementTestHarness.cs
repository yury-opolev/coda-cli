using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Mcp;
using Coda.Mcp.Auth;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Events;
using LlmAuth;

namespace Coda.Tui.Tests;

internal sealed class McpManagementTestHarness : IAsyncDisposable
{
    private readonly string root;

    private McpManagementTestHarness(
        string root,
        string user,
        string project,
        TestTokenStore store,
        CountingHttpFactory runtimeFactory,
        McpClientManager runtime,
        SuccessfulOAuthReauthenticator oauth,
        IMcpManagementService service)
    {
        this.root = root;
        this.User = user;
        this.Project = project;
        this.Store = store;
        this.RuntimeFactory = runtimeFactory;
        this.Runtime = runtime;
        this.OAuth = oauth;
        this.Service = service;
    }

    public string User { get; }

    public string Project { get; }

    public TestTokenStore Store { get; }

    public CountingHttpFactory RuntimeFactory { get; }

    public McpClientManager Runtime { get; }

    public SuccessfulOAuthReauthenticator OAuth { get; }

    public IMcpManagementService Service { get; }

    public static Task<McpManagementTestHarness> CreateAsync(IMcpConfigMutator? mutator = null)
    {
        var root = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "mcp-management-tests",
            Guid.NewGuid().ToString("N"));
        var user = Path.Combine(root, "user");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(project);

        try
        {
            var store = new TestTokenStore();
            var runtimeFactory = new CountingHttpFactory();
            var runtime = new McpClientManager(runtimeFactory);
            var oauth = new SuccessfulOAuthReauthenticator();
            IMcpManagementService service = new McpManagementService(
                project,
                user,
                runtime,
                store,
                oauth,
                new RecordingUiEvents(),
                mutator);

            return Task.FromResult(new McpManagementTestHarness(
                root,
                user,
                project,
                store,
                runtimeFactory,
                runtime,
                oauth,
                service));
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    public void WriteUser(string json) =>
        File.WriteAllText(Path.Combine(this.User, ".mcp.json"), json);

    public void WriteProject(string json) =>
        File.WriteAllText(Path.Combine(this.Project, ".mcp.json"), json);

    public void WriteUserBytes(byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(this.User, ".mcp.json"), bytes);

    public void WriteProjectBytes(byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(this.Project, ".mcp.json"), bytes);

    public async Task ConnectEffectiveAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await this.TryConnectEffectiveAsync(name, cancellationToken).ConfigureAwait(false);
        Assert.True(result.Connected, result.Error);
    }

    public Task<McpConnectResult> TryConnectEffectiveAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var config = McpConfig.Load(this.Project, this.User)[name];
        return this.Runtime.ConnectServerAsync(name, config, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.Runtime.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(this.root))
            {
                Directory.Delete(this.root, recursive: true);
            }
        }
    }
}

internal sealed class TestTokenStore : ITokenStore
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public int GetCalls { get; private set; }

    public int SetCalls { get; private set; }

    public int DeleteCalls { get; private set; }

    public bool ContainsKey(string key) => this.values.ContainsKey(key);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.GetCalls++;
        return Task.FromResult(this.values.GetValueOrDefault(key));
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.SetCalls++;
        this.values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.DeleteCalls++;
        this.values.Remove(key);
        return Task.CompletedTask;
    }

    public override string ToString() => "TestTokenStore (redacted)";
}

internal sealed class TestMcpServerBehavior
{
    public IReadOnlyList<McpToolInfo> Tools { get; set; } = [];

    public IReadOnlyList<McpPromptInfo> Prompts { get; set; } = [];

    public IReadOnlyList<McpResourceInfo> Resources { get; set; } = [];

    public string? InitializeFailure { get; set; }

    public string? PromptFailure { get; set; }

    public string? ResourceFailure { get; set; }

    public TimeSpan PromptDelay { get; set; }

    public TimeSpan ResourceDelay { get; set; }

    public McpServerInfo? ServerInfo { get; set; }
}

internal sealed class CountingHttpFactory : IMcpHttpClientFactory
{
    private readonly Dictionary<string, TestMcpServerBehavior> behaviors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TestMcpClient> clients = new(StringComparer.Ordinal);
    private readonly Queue<string?> nextInitializationFailures = new();

    public int ConnectCalls { get; private set; }

    public TestMcpServerBehavior ConfigureServer(string serverName)
    {
        if (!this.behaviors.TryGetValue(serverName, out var behavior))
        {
            behavior = new TestMcpServerBehavior();
            this.behaviors.Add(serverName, behavior);
        }

        return behavior;
    }

    public int PromptCallsFor(string serverName) =>
        this.clients.TryGetValue(serverName, out var client) ? client.PromptCalls : 0;

    public int ResourceCallsFor(string serverName) =>
        this.clients.TryGetValue(serverName, out var client) ? client.ResourceCalls : 0;

    public void FailNext(string message) => this.nextInitializationFailures.Enqueue(message);

    public IMcpClient Create(string serverName, McpHttpServerConfig config)
    {
        var behavior = this.ConfigureServer(serverName);
        var initializeFailure = this.nextInitializationFailures.Count > 0
            ? this.nextInitializationFailures.Dequeue()
            : behavior.InitializeFailure;
        var client = new TestMcpClient(
            serverName,
            () => this.ConnectCalls++,
            initializeFailure,
            behavior);
        this.clients[serverName] = client;
        return client;
    }
}

internal sealed class TestMcpClient(
    string serverName,
    Action onInitialize,
    string? initializeFailure,
    TestMcpServerBehavior behavior) : IMcpClient
{
    public string ServerName => serverName;

    public McpServerInfo? ServerInfo => behavior.ServerInfo;

    public int PromptCalls { get; private set; }

    public int ResourceCalls { get; private set; }

    public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        onInitialize();
        if (initializeFailure is not null)
        {
            throw new InvalidOperationException(initializeFailure);
        }

        return Task.FromResult(behavior.Tools);
    }

    public Task<(string Text, bool IsError)> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(("ok", false));

    public async Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(
        CancellationToken cancellationToken = default)
    {
        this.ResourceCalls++;
        if (behavior.ResourceDelay > TimeSpan.Zero)
        {
            await Task.Delay(behavior.ResourceDelay, cancellationToken).ConfigureAwait(false);
        }

        if (behavior.ResourceFailure is not null)
        {
            throw new InvalidOperationException(behavior.ResourceFailure);
        }

        return behavior.Resources;
    }

    public Task<string> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    public async Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(
        CancellationToken cancellationToken = default)
    {
        this.PromptCalls++;
        if (behavior.PromptDelay > TimeSpan.Zero)
        {
            await Task.Delay(behavior.PromptDelay, cancellationToken).ConfigureAwait(false);
        }

        if (behavior.PromptFailure is not null)
        {
            throw new InvalidOperationException(behavior.PromptFailure);
        }

        return behavior.Prompts;
    }

    public Task<string> GetPromptAsync(
        string name,
        JsonNode? arguments,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SuccessfulOAuthReauthenticator : IMcpOAuthReauthenticator
{
    public int Calls { get; private set; }

    public Task<McpAuthResult> ReauthenticateAsync(
        McpHttpServerConfig config,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.Calls++;
        return Task.FromResult(new McpAuthResult(true, null));
    }
}
