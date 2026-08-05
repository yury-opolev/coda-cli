using Coda.Agent.Tasks;
using LlmClient;
using Xunit;

namespace Engine.Tests.Tasks;

/// <summary>
/// Verifies the per-owner completion outbox on <see cref="TaskManager"/>:
/// - Enqueued on all three terminal transitions for background tasks only.
/// - Exactly-once drain.
/// - Owner-scoped (no cross-subtree leaks).
/// - Orphan roll-up when the owner itself terminates before the child.
/// - Bounded capacity per owner.
/// - <see cref="TaskManager.ConsumeCompletion"/> removes exactly one entry.
/// </summary>
public class TaskCompletionOutboxTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coda-outbox-" + Guid.NewGuid().ToString("N"));

    public TaskCompletionOutboxTests() => Directory.CreateDirectory(_dir);

    private TaskManager NewManager() => new(sessionId: "sess-outbox", logRoot: _dir);

    // -------------------------------------------------------------------------
    // Enqueue on all three terminal transitions (invariant 2)
    // -------------------------------------------------------------------------

    [Fact]
    public void Complete_Background_EnqueuesEntry()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "worker", parentTaskId: null, mode: TaskExecutionMode.Background);

        mgr.Complete(t.Id, "the result");

        var entries = mgr.DrainCompletions(ownerTaskId: null);
        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal(t.Id, e.TaskId);
        Assert.Equal("worker", e.Description);
        Assert.Equal(TaskRunStatus.Completed, e.Status);
        Assert.Equal("the result", e.Report);
    }

    [Fact]
    public void Fail_Background_EnqueuesEntry()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "failing-worker", parentTaskId: null, mode: TaskExecutionMode.Background);

        mgr.Fail(t.Id, "it broke");

        var entries = mgr.DrainCompletions(ownerTaskId: null);
        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal(t.Id, e.TaskId);
        Assert.Equal(TaskRunStatus.Failed, e.Status);
        Assert.Equal("it broke", e.Report);
    }

    [Fact]
    public void Stop_Background_EnqueuesEntry()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "stopped-worker", parentTaskId: null, mode: TaskExecutionMode.Background);

        mgr.Stop(t.Id);

        var entries = mgr.DrainCompletions(ownerTaskId: null);
        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal(t.Id, e.TaskId);
        Assert.Equal(TaskRunStatus.Stopped, e.Status);
        Assert.Null(e.Report);
    }

    // -------------------------------------------------------------------------
    // Foreground task enqueues nothing
    // -------------------------------------------------------------------------

    [Fact]
    public void Complete_Foreground_EnqueuesNothing()
    {
        var mgr = NewManager();
        // Default mode is Foreground — the tool result already carries the report.
        var t = mgr.Register(TaskKind.Subagent, "fg", parentTaskId: null);

        mgr.Complete(t.Id, "done");

        var entries = mgr.DrainCompletions(ownerTaskId: null);
        Assert.Empty(entries);
    }

    [Fact]
    public void Fail_Foreground_EnqueuesNothing()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "fg", parentTaskId: null);

        mgr.Fail(t.Id, "error");

        var entries = mgr.DrainCompletions(ownerTaskId: null);
        Assert.Empty(entries);
    }

    [Fact]
    public void Stop_Foreground_EnqueuesNothing()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "fg", parentTaskId: null);

        mgr.Stop(t.Id);

        var entries = mgr.DrainCompletions(ownerTaskId: null);
        Assert.Empty(entries);
    }

    // -------------------------------------------------------------------------
    // Exactly-once drain
    // -------------------------------------------------------------------------

    [Fact]
    public void Drain_IsExactlyOnce()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "w", parentTaskId: null, mode: TaskExecutionMode.Background);
        mgr.Complete(t.Id, "ok");

        var first = mgr.DrainCompletions(ownerTaskId: null);
        var second = mgr.DrainCompletions(ownerTaskId: null);

        Assert.Single(first);
        Assert.Empty(second);
    }

    // -------------------------------------------------------------------------
    // Owner-scoped: agent only sees its own tasks
    // -------------------------------------------------------------------------

    [Fact]
    public void Drain_OwnerScoped_NoLeakBetweenAgents()
    {
        var mgr = NewManager();
        // agentA and agentB are both top-level tasks (owned by main agent = null).
        // agentA starts a background child; agentB starts a different background child.
        var agentA = mgr.Register(TaskKind.Subagent, "agent-a", parentTaskId: null);
        var agentB = mgr.Register(TaskKind.Subagent, "agent-b", parentTaskId: null);
        var childA = mgr.Register(TaskKind.Subagent, "child-a", parentTaskId: agentA.Id, mode: TaskExecutionMode.Background);
        var childB = mgr.Register(TaskKind.Subagent, "child-b", parentTaskId: agentB.Id, mode: TaskExecutionMode.Background);

        mgr.Complete(childA.Id, "a-result");
        mgr.Complete(childB.Id, "b-result");

        // agentA's outbox should only contain childA's entry.
        var forA = mgr.DrainCompletions(ownerTaskId: agentA.Id);
        Assert.Single(forA);
        Assert.Equal(childA.Id, forA[0].TaskId);

        // agentB's outbox should only contain childB's entry.
        var forB = mgr.DrainCompletions(ownerTaskId: agentB.Id);
        Assert.Single(forB);
        Assert.Equal(childB.Id, forB[0].TaskId);
    }

    // -------------------------------------------------------------------------
    // Orphan roll-up: owner terminates before child completes
    // -------------------------------------------------------------------------

    [Fact]
    public void OrphanRollUp_DeadOwner_EntryLandsOnLiveAncestor()
    {
        // Hierarchy: main agent → agentA (background) → childB (background).
        // agentA dies. When childB completes, the entry must land on the main agent (null),
        // NOT on the dead agentA.
        var mgr = NewManager();
        var agentA = mgr.Register(TaskKind.Subagent, "agent-a", parentTaskId: null, mode: TaskExecutionMode.Background);
        var childB = mgr.Register(TaskKind.Subagent, "child-b", parentTaskId: agentA.Id, mode: TaskExecutionMode.Background);

        // agentA reaches terminal state while childB is still running.
        mgr.Complete(agentA.Id, "agent-a done");
        // Drain agentA's own completion from the main agent's outbox (it was a background task).
        mgr.DrainCompletions(ownerTaskId: null);

        // Now childB finishes — its owner agentA is dead.
        mgr.Complete(childB.Id, "child-b done");

        // childB's entry should roll up to main agent (the nearest live ancestor of agentA).
        var mainEntries = mgr.DrainCompletions(ownerTaskId: null);
        Assert.Single(mainEntries);
        Assert.Equal(childB.Id, mainEntries[0].TaskId);

        // Nothing should be in agentA's outbox.
        var aEntries = mgr.DrainCompletions(ownerTaskId: agentA.Id);
        Assert.Empty(aEntries);
    }

    // -------------------------------------------------------------------------
    // Bounded outbox
    // -------------------------------------------------------------------------

    [Fact]
    public void Outbox_Bounded_DoesNotGrowWithoutLimit()
    {
        // Fill more than the capacity (64) — the outbox must not throw and must stay bounded.
        var mgr = NewManager();
        for (var i = 0; i < 80; i++)
        {
            var t = mgr.Register(TaskKind.Subagent, $"w{i}", parentTaskId: null, mode: TaskExecutionMode.Background);
            mgr.Complete(t.Id, $"result-{i}");
        }

        var entries = mgr.DrainCompletions(ownerTaskId: null);
        Assert.True(entries.Count <= TaskManager.CompletionOutboxCapacity,
            $"Outbox capacity {TaskManager.CompletionOutboxCapacity} exceeded: got {entries.Count}");
        Assert.True(entries.Count > 0, "At least some entries should be present.");
    }

    // -------------------------------------------------------------------------
    // ConsumeCompletion — targeted single-entry removal
    // -------------------------------------------------------------------------

    [Fact]
    public void ConsumeCompletion_RemovesExactlyOneEntry()
    {
        var mgr = NewManager();
        var t1 = mgr.Register(TaskKind.Subagent, "w1", parentTaskId: null, mode: TaskExecutionMode.Background);
        var t2 = mgr.Register(TaskKind.Subagent, "w2", parentTaskId: null, mode: TaskExecutionMode.Background);
        mgr.Complete(t1.Id, "r1");
        mgr.Complete(t2.Id, "r2");

        // Consume t1's entry specifically.
        var consumed = mgr.ConsumeCompletion(t1.Id, ownerTaskId: null);
        Assert.True(consumed);

        // t2's entry must still be there.
        var remaining = mgr.DrainCompletions(ownerTaskId: null);
        Assert.Single(remaining);
        Assert.Equal(t2.Id, remaining[0].TaskId);
    }

    [Fact]
    public void ConsumeCompletion_ReturnsFalse_WhenNoEntry()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "w", parentTaskId: null, mode: TaskExecutionMode.Background);
        mgr.Complete(t.Id, "r");

        // Consume it once — should succeed.
        Assert.True(mgr.ConsumeCompletion(t.Id, ownerTaskId: null));
        // Second consume — no entry, returns false.
        Assert.False(mgr.ConsumeCompletion(t.Id, ownerTaskId: null));
    }
}
