using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Mcp;
using Coda.Tui.Commands;
using LlmAuth;

namespace Coda.Tui.Tests;

// Mutates the process-wide CODA_USER_MCP_DIR env var, so it must not run in parallel with any other
// test that resolves MCP config from the environment (mirrors the SettingsDirEnv / SkillSourceEnv pattern).
[Collection("McpDirEnv")]
public sealed class McpCommandTests
{
    [Fact]
    public async Task List_no_config_reports_none()
    {
        using var dirs = new McpTestDirs();
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("No MCP servers", console.Output);
    }

    [Fact]
    public async Task List_shows_configured_server_not_connected()
    {
        using var dirs = new McpTestDirs();
        dirs.WriteProjectConfig("""{ "mcpServers": { "github": { "command": "npx", "args": ["-y","srv"] } } }""");
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("github", console.Output);
        Assert.Contains("not connected", console.Output);
    }

    [Fact]
    public async Task Info_unknown_server_reports_unknown()
    {
        using var dirs = new McpTestDirs();
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["info", "nope"], CancellationToken.None);

        Assert.Contains("Unknown MCP server", console.Output);
    }

    [Fact]
    public async Task Info_shows_transport_detail_for_configured_server()
    {
        using var dirs = new McpTestDirs();
        dirs.WriteProjectConfig("""{ "mcpServers": { "github": { "command": "npx", "args": ["-y","srv"] } } }""");
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["info", "github"], CancellationToken.None);

        Assert.Contains("npx", console.Output);
        Assert.Contains("stdio", console.Output);
    }

    [Fact]
    public async Task Add_with_flags_writes_config()
    {
        using var dirs = new McpTestDirs();
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["add", "github", "--command", "npx", "--args", "-y srv"], CancellationToken.None);

        Assert.Contains("Added", console.Output);
        Assert.True(McpConfig.Parse(File.ReadAllText(Path.Combine(dirs.Project, ".mcp.json"))).ContainsKey("github"));
    }

    [Fact]
    public async Task Add_existing_is_rejected()
    {
        using var dirs = new McpTestDirs();
        dirs.WriteProjectConfig("""{ "mcpServers": { "github": { "command": "x" } } }""");
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["add", "github", "--command", "npx"], CancellationToken.None);

        Assert.Contains("already exists", console.Output);
    }

    [Fact]
    public async Task Add_no_flags_non_interactive_prompts_for_flags()
    {
        using var dirs = new McpTestDirs();
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["add", "github"], CancellationToken.None);

        Assert.Contains("Provide flags", console.Output);
    }

    [Fact]
    public async Task Add_user_scope_writes_user_file_not_project()
    {
        using var dirs = new McpTestDirs();
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["add", "github", "--user", "--command", "npx"], CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(dirs.User, ".mcp.json")));
        Assert.False(File.Exists(Path.Combine(dirs.Project, ".mcp.json")));
    }

    [Fact]
    public async Task Edit_nonexistent_is_rejected()
    {
        using var dirs = new McpTestDirs();
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["edit", "nope", "--command", "x"], CancellationToken.None);

        Assert.Contains("not configured", console.Output);
    }

    [Fact]
    public async Task Edit_updates_existing()
    {
        using var dirs = new McpTestDirs();
        dirs.WriteProjectConfig("""{ "mcpServers": { "s": { "command": "old" } } }""");
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["edit", "s", "--command", "new"], CancellationToken.None);

        Assert.Contains("Updated", console.Output);
        Assert.Equal("new", ((McpStdioServerConfig)McpConfig.Parse(File.ReadAllText(Path.Combine(dirs.Project, ".mcp.json")))["s"]).Command);
    }

    [Fact]
    public async Task Remove_deletes_entry()
    {
        using var dirs = new McpTestDirs();
        dirs.WriteProjectConfig("""{ "mcpServers": { "s": { "command": "x" } } }""");
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["remove", "s"], CancellationToken.None);

        Assert.Contains("Removed", console.Output);
        Assert.False(McpConfig.Parse(File.ReadAllText(Path.Combine(dirs.Project, ".mcp.json"))).ContainsKey("s"));
    }

    [Fact]
    public async Task Disable_then_enable_toggles_persisted_flag()
    {
        using var dirs = new McpTestDirs();
        dirs.WriteProjectConfig("""{ "mcpServers": { "s": { "command": "x" } } }""");
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;
        var cmd = new McpCommand();
        var path = Path.Combine(dirs.Project, ".mcp.json");

        await cmd.ExecuteAsync(context, ["disable", "s"], CancellationToken.None);
        Assert.True(McpConfig.Parse(File.ReadAllText(path))["s"].Disabled);

        await cmd.ExecuteAsync(context, ["enable", "s"], CancellationToken.None);
        Assert.False(McpConfig.Parse(File.ReadAllText(path))["s"].Disabled);
    }

    [Fact]
    public async Task Disable_unknown_reports_not_configured()
    {
        using var dirs = new McpTestDirs();
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["disable", "nope"], CancellationToken.None);

        Assert.Contains("not configured", console.Output);
    }

    [Fact]
    public async Task Start_unknown_reports_not_configured()
    {
        using var dirs = new McpTestDirs();
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;
        context.Mcp = new McpClientManager();

        await new McpCommand().ExecuteAsync(context, ["start", "nope"], CancellationToken.None);

        Assert.Contains("not configured", console.Output);
    }

    [Fact]
    public async Task Stop_not_running_reports_not_running()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Mcp = new McpClientManager();

        await new McpCommand().ExecuteAsync(context, ["stop", "x"], CancellationToken.None);

        Assert.Contains("not running", console.Output);
    }

    [Fact]
    public async Task Start_connects_configured_server_then_stop_disconnects()
    {
        using var dirs = new McpTestDirs();
        dirs.WriteProjectConfig("""{ "mcpServers": { "remote": { "type": "http", "url": "https://x/mcp" } } }""");
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;
        context.Mcp = new McpClientManager(new StubHttpFactory());
        var cmd = new McpCommand();

        await cmd.ExecuteAsync(context, ["start", "remote"], CancellationToken.None);
        Assert.Contains("Started", console.Output);
        Assert.True(context.Mcp.IsServerConnected("remote"));

        await cmd.ExecuteAsync(context, ["stop", "remote"], CancellationToken.None);
        Assert.False(context.Mcp.IsServerConnected("remote"));
    }

    [Fact]
    public async Task Start_resolves_coda_secret_reference_before_connecting()
    {
        using var dirs = new McpTestDirs();
        dirs.WriteProjectConfig("""{ "mcpServers": { "remote": { "type": "http", "url": "https://x/mcp", "headers": { "X-Key": "coda-secret:mcp:remote/header/X-Key" } } } }""");
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;
        var store = new InMemoryStore();
        await store.SetAsync("mcp:remote/header/X-Key", "decrypted-value");
        context.CredentialStore = store;
        var factory = new StubHttpFactory();
        context.Mcp = new McpClientManager(factory);

        await new McpCommand().ExecuteAsync(context, ["start", "remote"], CancellationToken.None);

        Assert.NotNull(factory.LastConfig);
        Assert.Equal("decrypted-value", factory.LastConfig!.Headers["X-Key"]); // resolved, not the literal ref
    }

    private sealed class StubHttpFactory : IMcpHttpClientFactory
    {
        public McpHttpServerConfig? LastConfig { get; private set; }

        public IMcpClient Create(string serverName, McpHttpServerConfig config)
        {
            this.LastConfig = config;
            return new StubMcpClient(serverName);
        }
    }

    private sealed class InMemoryStore : ITokenStore
    {
        private readonly Dictionary<string, string> map = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(this.map.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            this.map[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            this.map.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class StubMcpClient : IMcpClient
    {
        public StubMcpClient(string serverName) => this.ServerName = serverName;

        public string ServerName { get; }

        public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>([new McpToolInfo("echo", "Echo.", "{}", true)]);

        public Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
            => Task.FromResult((string.Empty, false));

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

        public Task<string> ReadResourceAsync(string uri, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);

        public Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Hermetic MCP dirs: a project working folder plus an empty user MCP dir wired via
    /// CODA_USER_MCP_DIR, so the command never reads the machine's real ~/.coda/.mcp.json.
    /// </summary>
    private sealed class McpTestDirs : IDisposable
    {
        private readonly string? previousUserDir;

        public string Project { get; }

        public string User { get; }

        public McpTestDirs()
        {
            this.Project = NewTemp("proj");
            this.User = NewTemp("user");
            this.previousUserDir = Environment.GetEnvironmentVariable("CODA_USER_MCP_DIR");
            Environment.SetEnvironmentVariable("CODA_USER_MCP_DIR", this.User);
        }

        public void WriteProjectConfig(string json) => File.WriteAllText(Path.Combine(this.Project, ".mcp.json"), json);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CODA_USER_MCP_DIR", this.previousUserDir);
            Delete(this.Project);
            Delete(this.User);
        }

        private static string NewTemp(string tag)
        {
            var path = Path.Combine(Path.GetTempPath(), $"coda-mcp-{tag}-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void Delete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}

/// <summary>Serializes tests that mutate the process-wide CODA_USER_MCP_DIR env var.</summary>
[CollectionDefinition("McpDirEnv", DisableParallelization = true)]
public sealed class McpDirEnvCollection
{
}
