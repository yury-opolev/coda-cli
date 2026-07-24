using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Settings;
using Coda.Mcp;
using Coda.Mcp.Auth;
using Coda.Sdk;
using Coda.Tui.Agent;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;
using Engine.Tests.TestSupport;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

public sealed class CliTuiMcpEndToEndTests
{
    [Fact]
    public async Task Exact_prompt_multi_batch_activity_and_post_turn_mcp_refresh_coexist()
    {
        await using var fixture = await CliTuiMcpEndToEndFixture.CreateAsync(
            systemPromptOverride: string.Empty,
            toolBatches:
            [
                [new ToolUseBlock("a", "read_file", """{"path":"a.txt"}""")],
                [new ToolUseBlock("b", "read_file", """{"path":"b.txt"}""")],
            ]);

        var result = await fixture.Session.RunAsync("go", fixture.Sink);
        var mcpSnapshot = await fixture.McpManagement.RefreshAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, fixture.LastProviderRequest.System);
        Assert.Equal("end_turn", result.StopReason);
        var summary = Assert.IsType<ToolActivitySummary>(result.ToolActivity);
        Assert.Equal(2, summary.TotalCalls);
        Assert.Equal(result.RootTurnId, summary.RootTurnId);

        var activity = Assert.Single(fixture.UiSnapshot.Transcript.OfType<ToolActivityTranscriptBlock>());
        Assert.Equal(ToolActivityCompletionState.Completed, activity.CompletionState);
        Assert.Equal(result.RootTurnId, activity.RootTurnId);
        Assert.Equal(summary.ActivityId, activity.ActivityId);
        Assert.All(
            activity.Calls,
            call => Assert.Equal($"root:{result.RootTurnId}", call.SourceId));
        Assert.Collection(
            activity.Calls,
            call => Assert.Equal(("a", "read_file", """{"path":"a.txt"}""", ToolCallStatus.Succeeded),
                (call.CallId, call.ToolName, call.InputJson, call.Status)),
            call => Assert.Equal(("b", "read_file", """{"path":"b.txt"}""", ToolCallStatus.Succeeded),
                (call.CallId, call.ToolName, call.InputJson, call.Status)));

        var server = Assert.Single(mcpSnapshot.Servers);
        Assert.Equal(new McpServerKey(McpConfigScope.Project, "physical"), server.Key);
        Assert.True(server.IsEffective);
    }
}

internal sealed class CliTuiMcpEndToEndFixture : IAsyncDisposable
{
    private readonly string root;
    private readonly ScriptedCapturingClient client;

    private CliTuiMcpEndToEndFixture(
        string root,
        CodaSession session,
        TuiAgentSink sink,
        ReducingPublisher publisher,
        ScriptedCapturingClient client,
        IMcpManagementService mcpManagement)
    {
        this.root = root;
        this.Session = session;
        this.Sink = sink;
        this.publisher = publisher;
        this.client = client;
        this.McpManagement = mcpManagement;
    }

    private readonly ReducingPublisher publisher;

    public CodaSession Session { get; }

    public TuiAgentSink Sink { get; }

    public ChatRequest LastProviderRequest => this.client.LastRequest
        ?? throw new InvalidOperationException("The scripted provider received no request.");

    public UiSessionSnapshot UiSnapshot => this.publisher.State;

    public IMcpManagementService McpManagement { get; }

    public static Task<CliTuiMcpEndToEndFixture> CreateAsync(
        string? systemPromptOverride,
        IReadOnlyList<IReadOnlyList<ToolUseBlock>> toolBatches)
    {
        var root = Directory.CreateTempSubdirectory("coda_cli_tui_mcp_end_to_end_").FullName;
        try
        {
            var client = new ScriptedCapturingClient(toolBatches);
            var publisher = new ReducingPublisher();
            var session = new CodaSession(
                CredentialFixtures.SignedInClaude(),
                new SessionOptions
                {
                    ProviderId = ClaudeAiProvider.Id,
                    Model = "claude-sonnet-4-6",
                    WorkingDirectory = root,
                    PermissionMode = PermissionMode.BypassPermissions,
                    SystemPromptOverride = systemPromptOverride,
                    ExtraTools = [new DeterministicReadFileTool()],
                    TelemetryOverride = new TelemetrySettings { Enabled = false },
                },
                llmClientFactory: new FixedClientFactory(client));
            var userMcpDir = Path.Combine(root, "user");
            Directory.CreateDirectory(userMcpDir);
            var projectConfig = Path.Combine(root, ".mcp.json");
            File.WriteAllText(projectConfig, """{"mcpServers":{"physical":{"type":"http","url":"https://physical.test/mcp"}}}""");
            IMcpManagementService management = new McpManagementService(
                root,
                userMcpDir,
                runtime: null,
                new InMemoryTokenStore(),
                new NoOpOAuthReauthenticator(),
                publisher);

            return Task.FromResult(new CliTuiMcpEndToEndFixture(
                root,
                session,
                new TuiAgentSink(publisher),
                publisher,
                client,
                management));
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.Session.DisposeAsync();
        Directory.Delete(this.root, recursive: true);
    }

    private sealed class FixedClientFactory(ILlmClient client) : ILlmClientFactory
    {
        public ILlmClient Create(
            string providerId,
            CredentialManager credentials,
            ClientFingerprint fingerprint,
            HttpClient httpClient,
            ILoggerFactory? loggerFactory = null,
            LlmHttpTimeoutConfig? timeoutConfig = null,
            IStreamProgressSink? progressSink = null) => client;
    }

    private sealed class ScriptedCapturingClient(
        IReadOnlyList<IReadOnlyList<ToolUseBlock>> toolBatches) : ILlmClient
    {
        private int turn;

        public string ProviderId => ClaudeAiProvider.Id;

        public ChatRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.LastRequest = request;
            if (this.turn < toolBatches.Count)
            {
                foreach (var tool in toolBatches[this.turn++])
                {
                    yield return AssistantStreamEvent.Tool(tool);
                }

                yield return AssistantStreamEvent.Finished("tool_use");
                yield break;
            }

            this.turn++;
            yield return AssistantStreamEvent.Delta("done");
            yield return AssistantStreamEvent.Finished("end_turn");
            await Task.CompletedTask;
        }
    }

    private sealed class DeterministicReadFileTool : ITool
    {
        public string Name => "read_file";

        public string Description => "Deterministic test-only file reader.";

        public string InputSchemaJson => """{"type":"object","required":["path"]}""";

        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(
            JsonElement input,
            ToolContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult(input.GetProperty("path").GetString()!));
    }

    private sealed class NoOpOAuthReauthenticator : IMcpOAuthReauthenticator
    {
        public Task<McpAuthResult> ReauthenticateAsync(
            McpHttpServerConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpAuthResult(true, null));
    }
}

internal sealed class ReducingPublisher : IUiEventPublisher
{
    public UiSessionSnapshot State { get; private set; } = UiSessionSnapshot.Empty;

    public void Publish(UiEvent uiEvent) => this.State = UiReducer.Reduce(this.State, uiEvent);
}
