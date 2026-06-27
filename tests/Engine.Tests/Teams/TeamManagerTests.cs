using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Teams;
using Coda.Agent.Tools;

namespace Engine.Tests.Teams;

/// <summary>
/// Tests for TeamManager, TeamCreateTool, SpawnTeammateTool, TeamDeleteTool.
/// All awaits are guarded by WaitAsync(5s) to surface hangs early.
/// No real LLM; fake ITeammateAgent records calls and completes them quickly.
/// </summary>
public sealed class TeamManagerTests : IDisposable
{
    private readonly string tempDir;

    public TeamManagerTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    // ── Fake agent ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fake ITeammateAgent that records the first prompt it receives, completes a
    /// TaskCompletionSource on the first RunTurnAsync, then idles on subsequent calls.
    /// </summary>
    private sealed class FakeTeammateAgent : ITeammateAgent
    {
        private readonly TaskCompletionSource<string> firstPromptTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> FirstPromptTask => this.firstPromptTcs.Task;

        private int callCount;

        public async Task<string> RunTurnAsync(string prompt, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref this.callCount);

            if (count == 1)
            {
                this.firstPromptTcs.TrySetResult(prompt);
                return "done";
            }

            // On subsequent calls: block until cancelled (simulates teammate alive and waiting).
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            return "cancelled";
        }
    }

    private TeamManager MakeManager(
        Func<TeammateIdentity, string, ITeammateAgent>? factory = null)
    {
        factory ??= (identity, prompt) => new FakeTeammateAgent();
        return new TeamManager(this.tempDir, factory);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTeam_writes_config_and_leader()
    {
        await using var manager = MakeManager();

        var (ok, msg) = manager.CreateTeam("t", "desc");

        Assert.True(ok, msg);
        Assert.Contains("t", msg);

        var store = new TeamStore(this.tempDir);
        var file = store.Read("t");
        Assert.NotNull(file);
        Assert.NotNull(file.LeadAgentId);
        Assert.NotEmpty(file.LeadAgentId);

        var lead = file.Members.FirstOrDefault(m => m.Name == TeamConstants.TeamLeadName);
        Assert.NotNull(lead);
        Assert.True(lead.IsActive);
    }

    [Fact]
    public async Task SpawnTeammate_adds_member_and_runs_initial_prompt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        FakeTeammateAgent? capturedFake = null;
        await using var manager = new TeamManager(this.tempDir, (identity, prompt) =>
        {
            capturedFake = new FakeTeammateAgent();
            return capturedFake;
        });

        manager.CreateTeam("t", null);

        var (ok, msg) = await manager
            .SpawnTeammateAsync("alice", "do X", null, null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        Assert.True(ok, msg);

        var members = manager.ListMembers();
        Assert.Contains(members, m => m.Name == "alice");

        Assert.NotNull(capturedFake);
        var firstPrompt = await capturedFake!.FirstPromptTask
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.Contains("do X", firstPrompt);
    }

    [Fact]
    public async Task SpawnTeammate_dedupes_name()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        FakeTeammateAgent? firstFake = null;
        await using var manager = new TeamManager(this.tempDir, (identity, prompt) =>
        {
            var fake = new FakeTeammateAgent();
            firstFake ??= fake;
            return fake;
        });

        manager.CreateTeam("t", null);

        await manager.SpawnTeammateAsync("alice", "prompt1", null, null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        // Wait for alice's first turn to complete before spawning a second alice.
        // This ensures SetMemberActive has been called so the store is quiescent.
        Assert.NotNull(firstFake);
        await firstFake!.FirstPromptTask.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        // Brief pause to let SetMemberActive write complete.
        await Task.Delay(50, cts.Token);

        var (ok2, _) = await manager.SpawnTeammateAsync("alice", "prompt2", null, null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        Assert.True(ok2);

        var members = manager.ListMembers();
        Assert.Contains(members, m => m.Name == "alice");
        Assert.Contains(members, m => m.Name == "alice-2");
    }

    [Fact]
    public async Task SpawnTeammate_without_team_errors()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var manager = MakeManager();

        var (ok, msg) = await manager.SpawnTeammateAsync("alice", "p", null, null, cts.Token);

        Assert.False(ok);
        Assert.NotEmpty(msg);
    }

    [Fact]
    public async Task DrainLeaderInbox_surfaces_teammate_message()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var manager = MakeManager();

        manager.CreateTeam("t", null);

        // Write a plain message from "alice" to team-lead inbox.
        var mailbox = new Mailbox(this.tempDir);
        var msg = new TeammateMessage(
            From: "alice",
            Text: "hello from alice",
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Read: false,
            Color: "blue",
            Summary: null);
        await mailbox.WriteAsync(TeamConstants.TeamLeadName, "t", msg, cts.Token);

        var blocks = await manager.DrainLeaderInboxAsync(cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        Assert.Single(blocks);
        Assert.Contains("<teammate_message", blocks[0]);
        Assert.Contains("hello from alice", blocks[0]);

        // Second drain must return empty (messages marked read).
        var blocks2 = await manager.DrainLeaderInboxAsync(cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.Empty(blocks2);
    }

    [Fact]
    public async Task DrainLeaderInbox_idle_marks_member_inactive()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var manager = MakeManager();

        manager.CreateTeam("t", null);

        // Spawn alice so she is a registered member.
        await manager.SpawnTeammateAsync("alice", "do X", null, null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        // Write an idle_notification from alice to team-lead.
        var mailbox = new Mailbox(this.tempDir);
        var idleText = TeamMessages.BuildIdleNotification("alice");
        var idleMsg = new TeammateMessage(
            From: "alice",
            Text: idleText,
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Read: false,
            Color: null,
            Summary: null);
        await mailbox.WriteAsync(TeamConstants.TeamLeadName, "t", idleMsg, cts.Token);

        await manager.DrainLeaderInboxAsync(cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        var members = manager.ListMembers();
        var alice = members.FirstOrDefault(m => m.Name == "alice");
        Assert.NotNull(alice);
        Assert.False(alice!.IsActive);
    }

    [Fact]
    public async Task SignalShutdownApproved_exits_runner()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        FakeTeammateAgent? capturedFake = null;
        string? capturedAgentId = null;

        await using var manager = new TeamManager(this.tempDir, (identity, prompt) =>
        {
            capturedAgentId = identity.AgentId;
            capturedFake = new FakeTeammateAgent();
            return capturedFake;
        });

        manager.CreateTeam("t", null);

        await manager.SpawnTeammateAsync("alice", "do X", null, null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        Assert.NotNull(capturedFake);
        Assert.NotNull(capturedAgentId);

        // Wait for the first prompt to be processed (runner is now idle/polling).
        await capturedFake!.FirstPromptTask
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        // Signal shutdown — the runner should exit.
        var signaled = manager.SignalShutdownApproved(capturedAgentId!);
        Assert.True(signaled);

        // DisposeAsync must complete quickly since all runners have been killed.
        await manager.DisposeAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    }

    [Fact]
    public async Task DeleteTeam_kills_all_and_removes_dir()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var manager = MakeManager();

        manager.CreateTeam("t", null);

        await manager.SpawnTeammateAsync("alice", "p1", null, null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        await manager.SpawnTeammateAsync("bob", "p2", null, null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        var (ok, msg) = await manager.DeleteTeamAsync(cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        Assert.True(ok, msg);

        var store = new TeamStore(this.tempDir);
        Assert.Null(store.Read("t"));
    }

    [Fact]
    public async Task DisposeAsync_stops_everything()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var manager = MakeManager();

        manager.CreateTeam("t", null);
        await manager.SpawnTeammateAsync("alice", "p", null, null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        // DisposeAsync must complete within 5 seconds (no hang).
        await manager.DisposeAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    }

    // ── Tool tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TeamCreateTool_creates_team_and_returns_success()
    {
        var tool = new TeamCreateTool();
        await using var manager = MakeManager();
        var context = new ToolContext(this.tempDir) { Teams = manager };

        var input = JsonSerializer.Deserialize<JsonElement>("""{"team_name":"myteam","description":"My team"}""");
        var result = await tool.ExecuteAsync(input, context);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("myteam", result.Content);
    }

    [Fact]
    public async Task TeamCreateTool_returns_error_when_no_teams_context()
    {
        var tool = new TeamCreateTool();
        var context = new ToolContext(this.tempDir);

        var input = JsonSerializer.Deserialize<JsonElement>("""{"team_name":"x"}""");
        var result = await tool.ExecuteAsync(input, context);

        Assert.True(result.IsError);
        Assert.Contains("not available", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpawnTeammateTool_spawns_and_returns_success()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var tool = new SpawnTeammateTool();
        await using var manager = MakeManager();
        manager.CreateTeam("t", null);
        var context = new ToolContext(this.tempDir) { Teams = manager };

        var input = JsonSerializer.Deserialize<JsonElement>("""{"name":"alice","prompt":"do Z"}""");
        var result = await tool.ExecuteAsync(input, context, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("alice", result.Content);
    }

    [Fact]
    public async Task TeamDeleteTool_deletes_team()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var tool = new TeamDeleteTool();
        await using var manager = MakeManager();
        manager.CreateTeam("t", null);
        var context = new ToolContext(this.tempDir) { Teams = manager };

        var input = JsonSerializer.Deserialize<JsonElement>("""{}""");
        var result = await tool.ExecuteAsync(input, context, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        Assert.False(result.IsError, result.Content);
    }
}
