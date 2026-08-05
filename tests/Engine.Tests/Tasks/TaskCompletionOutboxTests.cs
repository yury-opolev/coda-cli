using System.Linq;
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

    public TaskCompletionOutboxTests() => Directory.CreateDirectory(this._dir);

    private TaskManager NewManager() => new(sessionId: "sess-outbox", logRoot: this._dir);

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

    [Fact]
    public void Complete_Foreground_EnqueuesNothing()
    {
        var mgr = NewManager();
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

    [Fact]
    public void Drain_OwnerScoped_NoLeakBetweenAgents()
    {
        var mgr = NewManager();
        var agentA = mgr.Register(TaskKind.Subagent, "agent-a", parentTaskId: null);
        var agentB = mgr.Register(TaskKind.Subagent, "agent-b", parentTaskId: null);
        var childA = mgr.Register(TaskKind.Subagent, "child-a", parentTaskId: agentA.Id, mode: TaskExecutionMode.Background);
        var childB = mgr.Register(TaskKind.Subagent, "child-b", parentTaskId: agentB.Id, mode: TaskExecutionMode.Background);

        mgr.Complete(childA.Id, "a-result");
        mgr.Complete(childB.Id, "b-result");

        var forA = mgr.DrainCompletions(ownerTaskId: agentA.Id);
        Assert.Single(forA);
        Assert.Equal(childA.Id, forA[0].TaskId);

        var forB = mgr.DrainCompletions(ownerTaskId: agentB.Id);
        Assert.Single(forB);
        Assert.Equal(childB.Id, forB[0].TaskId);
    }

    [Fact]
    public void OrphanRollUp_DeadOwner_EntryLandsOnLiveAncestor()
    {
        var mgr = NewManager();
        var agentA = mgr.Register(TaskKind.Subagent, "agent-a", parentTaskId: null, mode: TaskExecutionMode.Background);
        var childB = mgr.Register(TaskKind.Subagent, "child-b", parentTaskId: agentA.Id, mode: TaskExecutionMode.Background);

        mgr.Complete(agentA.Id, "agent-a done");
        mgr.DrainCompletions(ownerTaskId: null);

        mgr.Complete(childB.Id, "child-b done");

        var mainEntries = mgr.DrainCompletions(ownerTaskId: null);
        Assert.Single(mainEntries);
        Assert.Equal(childB.Id, mainEntries[0].TaskId);

        var aEntries = mgr.DrainCompletions(ownerTaskId: agentA.Id);
        Assert.Empty(aEntries);
    }

    [Fact]
    public void Outbox_Bounded_DoesNotGrowWithoutLimit()
    {
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

    [Fact]
    public void ConsumeCompletion_RemovesExactlyOneEntry()
    {
        var mgr = NewManager();
        var t1 = mgr.Register(TaskKind.Subagent, "w1", parentTaskId: null, mode: TaskExecutionMode.Background);
        var t2 = mgr.Register(TaskKind.Subagent, "w2", parentTaskId: null, mode: TaskExecutionMode.Background);
        mgr.Complete(t1.Id, "r1");
        mgr.Complete(t2.Id, "r2");

        var consumed = mgr.ConsumeCompletion(t1.Id, ownerTaskId: null);
        Assert.NotNull(consumed);

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

        Assert.NotNull(mgr.ConsumeCompletion(t.Id, ownerTaskId: null));
        Assert.Null(mgr.ConsumeCompletion(t.Id, ownerTaskId: null));
    }

    [Fact]
    public void OutboxSweep_TerminatingOwner_MovesLateArrivingEntries()
    {
        var mgr = NewManager();
        var agentA = mgr.Register(TaskKind.Subagent, "agent-a", parentTaskId: null, mode: TaskExecutionMode.Background);
        var childB = mgr.Register(TaskKind.Subagent, "child-b", parentTaskId: agentA.Id, mode: TaskExecutionMode.Background);

        mgr.Complete(childB.Id, "child-b-result");

        var pendingForA = mgr.DrainCompletions(ownerTaskId: agentA.Id);
        Assert.Single(pendingForA);
        Assert.Equal(childB.Id, pendingForA[0].TaskId);
        Assert.Empty(mgr.DrainCompletions(ownerTaskId: null));

        // Re-create the naturally pre-loaded state after verifying it so terminating agentA
        // can prove the sweep moves childB's already-enqueued completion to main.
        var childBAgain = mgr.Register(TaskKind.Subagent, "child-b-2", parentTaskId: agentA.Id, mode: TaskExecutionMode.Background);
        mgr.Complete(childBAgain.Id, "child-b-result");
        mgr.Complete(agentA.Id, "agent-a-done");

        var mainEntries = mgr.DrainCompletions(ownerTaskId: null);
        Assert.Equal(2, mainEntries.Count);
        Assert.Contains(mainEntries, e => e.TaskId == agentA.Id);
        Assert.Contains(mainEntries, e => e.TaskId == childBAgain.Id);
        Assert.Empty(mgr.DrainCompletions(ownerTaskId: agentA.Id));
    }

    [Fact]
    public async Task OutboxRace_ConcurrentParentChildTermination_NoEntryStranded()
    {
        const int iterations = 500;

        for (var i = 0; i < iterations; i++)
        {
            var mgr = NewManager();
            var agentA = mgr.Register(
                TaskKind.Subagent, $"agent-a-{i}", parentTaskId: null, mode: TaskExecutionMode.Background);
            var childB = mgr.Register(
                TaskKind.Subagent, $"child-b-{i}", parentTaskId: agentA.Id, mode: TaskExecutionMode.Background);

            var t1 = Task.Run(() => mgr.Complete(agentA.Id, "a-done"));
            var t2 = Task.Run(() => mgr.Complete(childB.Id, "b-done"));
            await Task.WhenAll(t1, t2);

            var fromMain = mgr.DrainCompletions(ownerTaskId: null);
            var fromA = mgr.DrainCompletions(ownerTaskId: agentA.Id);
            var fromB = mgr.DrainCompletions(ownerTaskId: childB.Id);

            var childBCount =
                fromMain.Count(e => e.TaskId == childB.Id) +
                fromA.Count(e => e.TaskId == childB.Id) +
                fromB.Count(e => e.TaskId == childB.Id);

            Assert.True(
                childBCount == 1,
                $"Iteration {i}: childB completion appeared {childBCount} times (expected exactly 1). " +
                $"fromMain={fromMain.Count}, fromA={fromA.Count}, fromB={fromB.Count}");
        }
    }
}
