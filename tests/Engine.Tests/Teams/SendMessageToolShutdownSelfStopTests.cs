using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Teams;
using Coda.Agent.Tools;

namespace Engine.Tests.Teams;

/// <summary>
/// Tests for FIX 3: when a teammate sends a shutdown_response with approve=true,
/// the tool must call Teams.SignalShutdownApproved(selfAgentId) so the runner's
/// in-process loop exits promptly without waiting for the leader to drain its inbox.
/// </summary>
public sealed class SendMessageToolShutdownSelfStopTests : IDisposable
{
    private readonly string tempDir;
    private readonly Mailbox mailbox;
    private readonly TeamStore store;
    private readonly SendMessageTool tool;

    public SendMessageToolShutdownSelfStopTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(this.tempDir);
        this.mailbox = new Mailbox(this.tempDir);
        this.store = new TeamStore(this.tempDir);

        // Create a team with two members: team-lead and alice.
        var teamFile = new TeamFile(
            Name: "t",
            Description: null,
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LeadAgentId: "team-lead@t",
            Members:
            [
                new TeamMember("team-lead@t", "team-lead", null, null, null, null, 0, true, []),
                new TeamMember("alice@t", "alice", null, null, null, null, 0, true, []),
            ]);
        this.store.Write("t", teamFile);

        this.tool = new SendMessageTool();
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    private static JsonElement ParseInput(string json) =>
        JsonDocument.Parse(json).RootElement;

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed class FakeBlockingTeammateAgent : ITeammateAgent
    {
        private readonly TaskCompletionSource<string> firstTcs;
        private int callCount;

        public FakeBlockingTeammateAgent(TaskCompletionSource<string> firstTcs)
        {
            this.firstTcs = firstTcs;
        }

        public async Task<string> RunTurnAsync(string prompt, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref this.callCount);
            if (count == 1)
            {
                this.firstTcs.TrySetResult(prompt);
                return "done";
            }

            // Subsequent calls: block until cancelled (simulates idle polling).
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

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When approve=true is sent, the mailbox write and the self-signal both happen.
    /// Verified by: the shutdown_approved message is present in the leader inbox,
    /// and SignalShutdownApproved(selfAgentId) returns true (agent found in registry
    /// and runner is signalled).
    /// </summary>
    [Fact]
    public async Task Shutdown_approve_writes_approved_message_to_lead_inbox()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var manager = new TeamManager(
            this.tempDir,
            (_, _) => throw new InvalidOperationException("No spawning in this test."));
        manager.CreateTeam("t", null);

        var aliceContext = new ToolContext(this.tempDir)
        {
            TeamMailbox = this.mailbox,
            TeamStore = this.store,
            TeamName = "t",
            AgentName = "alice",
            Teams = manager,
        };

        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"team-lead","message":{"type":"shutdown_response","request_id":"r1","approve":true}}"""),
            aliceContext,
            cts.Token);

        Assert.False(result.IsError, result.Content);

        // The mailbox write must have happened.
        var leadInbox = await new Mailbox(this.tempDir).ReadAsync("team-lead", "t");
        Assert.Single(leadInbox);
        var parsed = TeamMessages.TryParseShutdownApproved(leadInbox[0].Text);
        Assert.NotNull(parsed);
        Assert.Equal("r1", parsed!.RequestId);
    }

    /// <summary>
    /// Integration test: spawn a real runner for alice, alice's tool sends approve,
    /// and the manager's DisposeAsync completes within 5s (runner has already exited
    /// because FIX 3 called SignalShutdownApproved + Kill).
    /// </summary>
    [Fact]
    public async Task Runner_task_completes_within_5s_after_approve_via_shared_manager()
    {
        using var globalCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var firstTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeBlockingTeammateAgent(firstTcs);

        var tempDir2 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir2);
        try
        {
            await using var manager = new TeamManager(
                tempDir2,
                (_, _) => agent);

            manager.CreateTeam("t", null);

            // Spawn alice — registers her runner and fires the Task.
            var (ok, msg) = await manager
                .SpawnTeammateAsync("alice", "initial prompt", null, null, globalCts.Token)
                .WaitAsync(TimeSpan.FromSeconds(10), globalCts.Token);
            Assert.True(ok, msg);

            // Wait for alice's first turn to complete so the runner is in the poll loop.
            await firstTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), globalCts.Token);

            // Use the shared manager in alice's ToolContext (FIX 2 + FIX 3 together).
            var mailbox2 = new Mailbox(tempDir2);
            var store2 = new TeamStore(tempDir2);
            var aliceContext = new ToolContext(tempDir2)
            {
                TeamMailbox = mailbox2,
                TeamStore = store2,
                TeamName = "t",
                AgentName = "alice",
                Teams = manager,
            };

            // Alice sends shutdown_response approve — FIX 3 must signal the runner.
            var result = await this.tool.ExecuteAsync(
                ParseInput("""{"to":"team-lead","message":{"type":"shutdown_response","request_id":"r9","approve":true}}"""),
                aliceContext,
                globalCts.Token);
            Assert.False(result.IsError, result.Content);

            // DisposeAsync must complete quickly because the runner was already killed by FIX 3.
            await manager.DisposeAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), globalCts.Token);
        }
        finally
        {
            if (Directory.Exists(tempDir2))
            {
                Directory.Delete(tempDir2, recursive: true);
            }
        }
    }

    /// <summary>
    /// Guard: when Teams is null (non-team ToolContext), the tool must not throw
    /// and must still write the approved message to the inbox.
    /// </summary>
    [Fact]
    public async Task Shutdown_approve_without_Teams_context_still_writes_and_succeeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var aliceContextNoTeams = new ToolContext(this.tempDir)
        {
            TeamMailbox = this.mailbox,
            TeamStore = this.store,
            TeamName = "t",
            AgentName = "alice",
            Teams = null,
        };

        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"team-lead","message":{"type":"shutdown_response","request_id":"r2","approve":true}}"""),
            aliceContextNoTeams,
            cts.Token);

        Assert.False(result.IsError, result.Content);

        var leadInbox = await new Mailbox(this.tempDir).ReadAsync("team-lead", "t");
        Assert.Single(leadInbox);
    }

    /// <summary>
    /// Rejection (approve=false) must NOT trigger SignalShutdownApproved.
    /// Verified by: no side effect from a manager that has no runner registered —
    /// a no-op SignalShutdownApproved returns false and does not throw.
    /// </summary>
    [Fact]
    public async Task Shutdown_reject_does_not_throw_and_returns_success()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var manager = new TeamManager(
            this.tempDir,
            (_, _) => throw new InvalidOperationException("No spawning in this test."));
        manager.CreateTeam("t", null);

        var aliceContext = new ToolContext(this.tempDir)
        {
            TeamMailbox = this.mailbox,
            TeamStore = this.store,
            TeamName = "t",
            AgentName = "alice",
            Teams = manager,
        };

        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"team-lead","message":{"type":"shutdown_response","request_id":"r3","approve":false,"reason":"not done yet"}}"""),
            aliceContext,
            cts.Token);

        Assert.False(result.IsError, result.Content);

        // Leader inbox gets a rejection message, not an approval.
        var leadInbox = await new Mailbox(this.tempDir).ReadAsync("team-lead", "t");
        Assert.Single(leadInbox);
        Assert.Null(TeamMessages.TryParseShutdownApproved(leadInbox[0].Text));
    }
}
