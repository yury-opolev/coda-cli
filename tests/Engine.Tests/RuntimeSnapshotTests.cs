using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Goals;
using Coda.Agent.Lsp;
using Coda.Mcp;
using Coda.Sdk;
using Engine.Tests.Lsp;
using Engine.Tests.TestSupport;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Verifies the immutable runtime snapshot accessors added for the TUI status bar:
/// <see cref="BackgroundTaskRunner.GetSnapshot"/>, <see cref="LspServerManager.GetSnapshot"/>,
/// <see cref="McpClientManager.GetSnapshot"/> and <see cref="CodaSession.GetRuntimeSnapshot"/>.
/// Each returns fresh, copied, engine-instance-free value snapshots.
/// </summary>
public sealed class RuntimeSnapshotTests
{
    // ─── BackgroundTaskRunner ───────────────────────────────────────────────

    [Fact]
    public void BackgroundTaskRunner_GetSnapshot_returns_fresh_copy_each_call()
    {
        using var runner = new BackgroundTaskRunner();

        var first = runner.GetSnapshot();
        var second = runner.GetSnapshot();

        Assert.Empty(first);
        Assert.Empty(second);
        // A fresh copy every call — even when empty — so callers can never alias engine state.
        Assert.NotSame(first, second);
    }

    [Fact]
    public void BackgroundTaskRunner_GetSnapshot_includes_running_task()
    {
        // Not disposed on purpose: the engine's registry outlives the assertion, matching the
        // existing BackgroundTaskTests which never dispose the runner mid-task.
        var runner = new BackgroundTaskRunner();
        var gate = new TaskCompletionSource();
        var host = new GatedSubagentHost(gate);

        var id = runner.Start(host, "explore", "do work");

        var snapshot = runner.GetSnapshot();

        var entry = Assert.Single(snapshot);
        Assert.Equal(id, entry.Id);
        Assert.Equal(BackgroundTaskStatus.Running, entry.Status);

        gate.TrySetResult();
    }

    private sealed class GatedSubagentHost : ISubagentHost
    {
        private readonly TaskCompletionSource gate;

        public GatedSubagentHost(TaskCompletionSource gate) => this.gate = gate;

        public async Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink parentSink,
            SteeringInbox steering, string taskId, int depth,
            CancellationToken cancellationToken = default)
        {
            await this.gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return "done";
        }
    }

    // ─── LspServerManager ───────────────────────────────────────────────────

    [Fact]
    public async Task LspServerManager_GetSnapshot_exposes_name_state_extensions_without_instances()
    {
        var (manager, loop) = LspFakeServerHarness.BuildManager(serverName: "ts-server", ext: ".ts");
        try
        {
            var snapshot = manager.GetSnapshot();

            var entry = Assert.Single(snapshot);
            Assert.Equal("ts-server", entry.Name);
            Assert.Equal(LspServerState.Stopped, entry.State);
            Assert.Equal([".ts"], entry.Extensions);

            // The snapshot must not surface the mutable engine instance.
            Assert.DoesNotContain(
                typeof(LspServerSnapshot).GetProperties(),
                p => p.PropertyType == typeof(LspServerInstance));
        }
        finally
        {
            await loop.DisposeAsync();
            await manager.DisposeAsync();
        }
    }

    // ─── McpClientManager ───────────────────────────────────────────────────

    [Fact]
    public async Task McpClientManager_GetSnapshot_exposes_version_and_immutable_summaries()
    {
        var manager = new McpClientManager();
        await using var _ = manager;
        var info = new McpServerInfo("github-server", "1.2.3", "does things");
        var client = new SnapshotFakeMcpClient("github", info)
        {
            Tools = [new McpToolInfo("echo", "d", "{}", true), new McpToolInfo("ping", "d", "{}", true)],
        };

        await manager.ConnectClientAsync(client, default);

        var snapshot = manager.GetSnapshot();

        Assert.Equal(manager.Version, snapshot.Version);
        var server = Assert.Single(snapshot.Servers);
        Assert.Equal("github", server.Name);
        Assert.Equal(info, server.Info);
        Assert.Equal(2, server.ToolCount);

        // No live client is surfaced through the snapshot record.
        Assert.DoesNotContain(
            typeof(McpServerRuntimeSnapshot).GetProperties(),
            p => p.PropertyType == typeof(IMcpClient));
    }

    private sealed class SnapshotFakeMcpClient : IMcpClient
    {
        public SnapshotFakeMcpClient(string serverName, McpServerInfo? info)
        {
            this.ServerName = serverName;
            this.ServerInfo = info;
        }

        public string ServerName { get; }

        public McpServerInfo? ServerInfo { get; }

        public IReadOnlyList<McpToolInfo> Tools { get; init; } = [];

        public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken ct = default)
            => Task.FromResult(this.Tools);

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

    // ─── CodaSession.GetRuntimeSnapshot ─────────────────────────────────────

    [Fact]
    public async Task CodaSession_GetRuntimeSnapshot_tracks_goal_and_copies_state()
    {
        var root = Directory.CreateTempSubdirectory("coda_runtime_snap_").FullName;
        try
        {
            var goal = new GoalStatus(GoalOutcome.Met, null, 2, TimeSpan.FromSeconds(3), false, false);
            using var http = new HttpClient();
            using var session = new CodaSession(
                CredentialFixtures.SignedInClaude(),
                new SessionOptions
                {
                    ProviderId = ClaudeAiProvider.Id,
                    Model = "claude-sonnet-4-6",
                    WorkingDirectory = root,
                    PermissionMode = PermissionMode.BypassPermissions,
                },
                httpClient: http,
                agentLoopFactory: new GoalLoopFactory(goal));

            var before = session.GetRuntimeSnapshot();
            Assert.Equal(session.SessionId, before.SessionId);
            Assert.Null(before.Goal);
            Assert.Empty(before.Todos);
            Assert.Empty(before.ScheduledTasks);
            Assert.Empty(before.BackgroundTasks);

            await session.RunAsync("hi");

            var after = session.GetRuntimeSnapshot();
            Assert.Equal(goal, after.Goal);
            Assert.Equal(session.SessionUsage, after.Usage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class GoalLoopFactory : IAgentLoopFactory
    {
        private readonly GoalStatus? goal;

        public GoalLoopFactory(GoalStatus? goal) => this.goal = goal;

        public IAgentLoop Create(AgentLoopSpec spec) => new GoalLoop(this.goal);
    }

    private sealed class GoalLoop : IAgentLoop
    {
        public GoalLoop(GoalStatus? goal) => this.LastGoalStatus = goal;

        public GoalStatus? LastGoalStatus { get; }

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            sink.OnAssistantText("ok");
            sink.OnAssistantTextComplete();
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }
}
