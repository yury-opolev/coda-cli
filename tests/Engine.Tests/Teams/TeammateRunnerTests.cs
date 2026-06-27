using Coda.Agent.Teams;

namespace Engine.Tests.Teams;

/// <summary>
/// Tests for TeammateRunner — the in-process run loop.
/// Every await is guarded with WaitAsync(TimeSpan.FromSeconds(5)) to surface hangs early.
/// </summary>
public sealed class TeammateRunnerTests : IDisposable
{
    private readonly string tempDir;
    private readonly Mailbox mailbox;
    private readonly TeamStore store;
    private readonly TaskBoard board;
    private readonly string teamName = "myteam";
    private readonly string agentName = "alice";
    private readonly string agentId = "alice@myteam";

    public TeammateRunnerTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(this.tempDir);

        this.mailbox = new Mailbox(this.tempDir);
        this.store = new TeamStore(this.tempDir);
        this.board = new TaskBoard(this.tempDir);

        // Pre-create the team with the teammate as a member
        var teamFile = new TeamFile(
            Name: this.teamName,
            Description: null,
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LeadAgentId: "team-lead@myteam",
            Members:
            [
                new TeamMember("team-lead@myteam", TeamConstants.TeamLeadName, null, null, null, null, 0, true, []),
                new TeamMember(this.agentId, this.agentName, null, null, null, "blue", 0, true, []),
            ]);
        this.store.Write(this.teamName, teamFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    private TeammateIdentity MakeIdentity() =>
        new TeammateIdentity(this.agentId, this.agentName, this.teamName, "blue");

    // ── Helper: poll the team-lead inbox until a message matching a predicate arrives ──

    private async Task<TeammateMessage> PollLeaderInboxAsync(
        Func<TeammateMessage, bool> predicate,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var messages = await this.mailbox.ReadAsync(TeamConstants.TeamLeadName, this.teamName, ct)
                ;
            var match = messages.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(50, ct);
        }
    }

    // ── Test 1: Initial prompt runs then idle notification sent ──────────────────────────

    [Fact]
    public async Task Initial_prompt_runs_then_idle_notification_sent()
    {
        using var cts = new CancellationTokenSource();

        var fake = new ScriptedFakeAgent(
            responses: ["I'm done with the initial task."],
            onRunTurn: null);

        var runner = new TeammateRunner(this.MakeIdentity(), fake, this.mailbox, this.store, this.board);

        var runTask = Task.Run(() => runner.RunAsync("Do the work", cts.Token), cts.Token);

        // The team-lead inbox should receive an idle_notification after the turn.
        using var pollCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var idleMsg = await this.PollLeaderInboxAsync(
            m => m.From == this.agentName && m.Text.Contains("idle_notification"),
            pollCts.Token);

        Assert.NotNull(idleMsg);
        Assert.Equal(this.agentName, idleMsg.From);
        Assert.Contains("idle_notification", idleMsg.Text);

        // Cancel to stop the runner
        await cts.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── Test 2: Queued mailbox message wakes runner ──────────────────────────────────────

    [Fact]
    public async Task Queued_mailbox_message_wakes_runner()
    {
        using var cts = new CancellationTokenSource();

        // Completion source to detect that the runner received "do X"
        var promptReceivedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var fake = new ScriptedFakeAgent(
            responses: ["Turn 1 done.", "Turn 2 done with do X."],
            onRunTurn: (prompt, _) =>
            {
                if (prompt.Contains("do X"))
                {
                    promptReceivedTcs.TrySetResult(prompt);
                }
            });

        var runner = new TeammateRunner(this.MakeIdentity(), fake, this.mailbox, this.store, this.board);
        var runTask = Task.Run(() => runner.RunAsync("Turn 1 work", cts.Token), cts.Token);

        // Wait for first idle notification to confirm first turn is complete
        using var pollCts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await this.PollLeaderInboxAsync(
            m => m.From == this.agentName && m.Text.Contains("idle_notification"),
            pollCts1.Token);

        // Now write a message to the teammate's inbox
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        await this.mailbox.WriteAsync(
            this.agentName,
            this.teamName,
            new TeammateMessage(TeamConstants.TeamLeadName, "do X", nowIso, false, null, null))
            ;

        // The runner should pick it up and call RunTurnAsync with a prompt containing "do X"
        var receivedPrompt = await promptReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("do X", receivedPrompt);

        await cts.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── Test 3: Claims available board task when idle ─────────────────────────────────────

    [Fact]
    public async Task Claims_available_board_task_when_idle()
    {
        using var cts = new CancellationTokenSource();

        // Pre-create a task on the board
        var boardTask = await this.board.CreateAsync(this.teamName, "Implement feature Z", "Details here", null)
            ;

        var taskPromptTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var fake = new ScriptedFakeAgent(
            responses: ["Initial done.", "Feature Z done."],
            onRunTurn: (prompt, _) =>
            {
                if (prompt.Contains("feature Z") || prompt.Contains("Implement feature Z") || prompt.Contains("t1"))
                {
                    taskPromptTcs.TrySetResult(prompt);
                }
            });

        var runner = new TeammateRunner(this.MakeIdentity(), fake, this.mailbox, this.store, this.board);
        var runTask = Task.Run(() => runner.RunAsync("Initial prompt", cts.Token), cts.Token);

        // Runner should claim the task and call RunTurnAsync with the task's subject
        var taskPrompt = await taskPromptTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Implement feature Z", taskPrompt);

        // The task should be InProgress and owned by the teammate
        var updatedTask = await this.board.GetAsync(this.teamName, boardTask.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TeamTaskStatus.InProgress, updatedTask!.Status);
        Assert.Equal(this.agentName, updatedTask.Owner);

        await cts.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── Test 4: Shutdown request then approve exits ───────────────────────────────────────

    [Fact]
    public async Task Shutdown_request_then_approve_exits()
    {
        using var cts = new CancellationTokenSource();

        TeammateRunner? runnerRef = null;

        var fake = new ScriptedFakeAgent(
            responses: ["Initial done.", "Shutdown acknowledged."],
            onRunTurn: (prompt, _) =>
            {
                if (prompt.Contains("shutdown_request") && runnerRef is not null)
                {
                    runnerRef.SignalShutdownApproved();
                }
            });

        var identity = this.MakeIdentity();
        var runner = new TeammateRunner(identity, fake, this.mailbox, this.store, this.board);
        runnerRef = runner;

        var runTask = Task.Run(() => runner.RunAsync("Start work", cts.Token), cts.Token);

        // Wait for first idle notification
        using var pollCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await this.PollLeaderInboxAsync(
            m => m.From == this.agentName && m.Text.Contains("idle_notification"),
            pollCts.Token);

        // Write a shutdown_request to the teammate's inbox
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var shutdownText = TeamMessages.BuildShutdownRequest("req-1", TeamConstants.TeamLeadName, "Time to stop");
        await this.mailbox.WriteAsync(
            this.agentName,
            this.teamName,
            new TeammateMessage(TeamConstants.TeamLeadName, shutdownText, nowIso, false, null, null))
            ;

        // RunAsync should complete on its own (shutdown approved by fake)
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Not cancelled — it exited because shutdown was approved
        Assert.False(cts.IsCancellationRequested);
    }

    // ── Test 5: Lifecycle cancel stops promptly ───────────────────────────────────────────

    [Fact]
    public async Task Lifecycle_cancel_stops_promptly()
    {
        using var cts = new CancellationTokenSource();

        // Fake that blocks until cancelled
        var fake = new BlockingFakeAgent();

        var runner = new TeammateRunner(this.MakeIdentity(), fake, this.mailbox, this.store, this.board);
        var runTask = Task.Run(() => runner.RunAsync("Work that blocks", cts.Token), cts.Token);

        // Let the runner start
        await Task.Delay(50);

        // Cancel the lifecycle
        await cts.CancelAsync();

        // RunAsync should stop promptly
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

// ── Fake implementations ──────────────────────────────────────────────────────────────────

/// <summary>
/// A scripted fake ITeammateAgent that returns canned responses in order and
/// optionally invokes a callback each time RunTurnAsync is called.
/// Thread-safe: the fake may be called from a background task.
/// </summary>
internal sealed class ScriptedFakeAgent : ITeammateAgent
{
    private readonly IReadOnlyList<string> responses;
    private readonly Action<string, CancellationToken>? onRunTurn;
    private int callCount;

    public ScriptedFakeAgent(IReadOnlyList<string> responses, Action<string, CancellationToken>? onRunTurn)
    {
        this.responses = responses;
        this.onRunTurn = onRunTurn;
    }

    public Task<string> RunTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.onRunTurn?.Invoke(prompt, cancellationToken);
        var index = Interlocked.Increment(ref this.callCount) - 1;
        var response = index < this.responses.Count
            ? this.responses[index]
            : this.responses[^1];
        return Task.FromResult(response);
    }
}

/// <summary>
/// A fake ITeammateAgent that blocks until the CancellationToken is cancelled.
/// </summary>
internal sealed class BlockingFakeAgent : ITeammateAgent
{
    public async Task<string> RunTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return string.Empty;
    }
}
