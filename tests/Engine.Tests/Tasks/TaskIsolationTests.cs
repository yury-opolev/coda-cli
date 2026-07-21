using Coda.Agent;
using Coda.Agent.Subagents;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tasks;

/// <summary>
/// Isolation coverage for the subagent task runtime: a subagent may read or stop only the
/// descendants it created (its own subtree), while the main agent retains full authority.
/// Also locks per-consumer output cursors (consumers cannot steal one another's incremental
/// output) and the tool-filtering that denies ALL task-management tools to read-only and
/// max-depth children.
/// </summary>
public sealed class TaskIsolationTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-iso", logRoot: null);

    // ─── Manager authorization: RequestStop ─────────────────────────────────

    [Fact]
    public void Main_CanStop_AnyTaskInSession()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "c", parentTaskId: parent.Id);

        // Main agent has null caller task id and full authority over every task.
        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(parent.Id, callerTaskId: null));
        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(child.Id, callerTaskId: null));
    }

    [Fact]
    public void Child_CanStop_ItsOwnDescendant()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "c", parentTaskId: parent.Id);

        // The parent created the child, so the child is in the parent's subtree.
        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(child.Id, callerTaskId: parent.Id));
    }

    [Fact]
    public void Child_CannotStop_ItsParent()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "c", parentTaskId: parent.Id);

        Assert.Equal(TaskActionResult.Denied, mgr.RequestStop(parent.Id, callerTaskId: child.Id));
        Assert.Equal(TaskRunStatus.Running, mgr.Get(parent.Id)!.Status);
    }

    [Fact]
    public void Child_CannotStop_ASibling()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var a = mgr.Register(TaskKind.Subagent, "a", parentTaskId: parent.Id);
        var b = mgr.Register(TaskKind.Subagent, "b", parentTaskId: parent.Id);

        Assert.Equal(TaskActionResult.Denied, mgr.RequestStop(b.Id, callerTaskId: a.Id));
        Assert.Equal(TaskRunStatus.Running, mgr.Get(b.Id)!.Status);
    }

    [Fact]
    public void Child_CannotStop_AnUnrelatedTask()
    {
        var mgr = NewManager();
        var branchA = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        var branchB = mgr.Register(TaskKind.Subagent, "b", parentTaskId: null);

        Assert.Equal(TaskActionResult.Denied, mgr.RequestStop(branchB.Id, callerTaskId: branchA.Id));
    }

    [Fact]
    public void Child_CannotStop_Itself()
    {
        var mgr = NewManager();
        var self = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);

        // A task is never a strict ancestor of itself, so it cannot stop itself via task_stop.
        Assert.Equal(TaskActionResult.Denied, mgr.RequestStop(self.Id, callerTaskId: self.Id));
        Assert.Equal(TaskRunStatus.Running, mgr.Get(self.Id)!.Status);
    }

    [Fact]
    public void Stop_UnknownId_FromSubagent_ReportsNotFound()
    {
        var mgr = NewManager();
        var caller = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);

        // Unknown and unauthorized are both non-revealing, but at the manager level an unknown
        // id is NotFound (there is nothing to authorize against).
        Assert.Equal(TaskActionResult.NotFound, mgr.RequestStop("task-9999", callerTaskId: caller.Id));
    }

    // ─── Manager authorization: output reads ────────────────────────────────

    [Fact]
    public void Main_CanReadOutput_OfAnyTask()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "c", parentTaskId: parent.Id);
        mgr.AppendOutput(child.Id, "child text");

        var read = mgr.ReadOutput(child.Id, callerTaskId: null);
        Assert.True(read.Found);
        Assert.Equal("child text", read.Text);
    }

    [Fact]
    public void Child_CanReadOutput_OfItsDescendant()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "c", parentTaskId: parent.Id);
        mgr.AppendOutput(child.Id, "child text");

        var read = mgr.ReadOutput(child.Id, callerTaskId: parent.Id);
        Assert.True(read.Found);
        Assert.Equal("child text", read.Text);
    }

    [Fact]
    public void Child_CannotReadOutput_OfParentSiblingOrUnrelated()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var a = mgr.Register(TaskKind.Subagent, "a", parentTaskId: parent.Id);
        var b = mgr.Register(TaskKind.Subagent, "b", parentTaskId: parent.Id);
        var unrelated = mgr.Register(TaskKind.Subagent, "u", parentTaskId: null);
        mgr.AppendOutput(parent.Id, "secret parent");
        mgr.AppendOutput(b.Id, "secret sibling");
        mgr.AppendOutput(unrelated.Id, "secret unrelated");

        // Reading anything outside its own subtree is indistinguishable from a missing task.
        Assert.False(mgr.ReadOutput(parent.Id, callerTaskId: a.Id).Found);
        Assert.False(mgr.ReadOutput(b.Id, callerTaskId: a.Id).Found);
        Assert.False(mgr.ReadOutput(unrelated.Id, callerTaskId: a.Id).Found);
        Assert.False(mgr.ReadOutput(a.Id, callerTaskId: a.Id).Found); // not even itself
    }

    // ─── Per-consumer cursors ───────────────────────────────────────────────

    [Fact]
    public void SeparateConsumers_ReceiveTheSameOutputIndependently()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "c", parentTaskId: parent.Id);
        mgr.AppendOutput(child.Id, "shared output");

        // Two distinct authorized consumers (main and the child's parent) each read from their
        // own cursor, so neither steals the other's incremental output.
        var main = mgr.ReadOutput(child.Id, callerTaskId: null);
        var byParent = mgr.ReadOutput(child.Id, callerTaskId: parent.Id);

        Assert.Equal("shared output", main.Text);
        Assert.Equal("shared output", byParent.Text);

        // Each consumer's cursor advanced independently: a second read yields nothing new.
        Assert.Equal(string.Empty, mgr.ReadOutput(child.Id, callerTaskId: null).Text);
        Assert.Equal(string.Empty, mgr.ReadOutput(child.Id, callerTaskId: parent.Id).Text);
    }

    [Fact]
    public void ReadForMainAgent_CompatibilityWrapper_UsesMainCursor()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "t", parentTaskId: null);
        mgr.AppendOutput(t.Id, "hello");

        // The legacy wrapper shares the main sentinel cursor with ReadOutput(id, null).
        var first = mgr.ReadForMainAgent(t.Id);
        Assert.True(first.Found);
        Assert.Equal("hello", first.Text);
        Assert.Equal(string.Empty, mgr.ReadOutput(t.Id, callerTaskId: null).Text);
    }

    // ─── Tool filtering (SubagentHost.ResolveChildTools) ────────────────────

    private static ToolRegistry FullTaskRegistry() => new(
    [
        new TaskTool(),
        new BackgroundTaskStartTool(),
        new BackgroundTaskOutputTool(),
        new BackgroundTaskStopTool(),
        new TaskWaitTool(),
        new TaskBackgroundTool(),
        new TaskRemoveTool(),
        new ReadFileTool(),
    ]);

    [Fact]
    public void ReadOnlyExplore_Child_SeesNoTaskManagementTools()
    {
        var explore = BuiltInAgents.Resolve("explore");
        Assert.True(explore.ReadOnlyToolsOnly);

        var tools = SubagentHost.ResolveChildTools(FullTaskRegistry(), explore.ReadOnlyToolsOnly, depth: 1);
        var names = tools.All.Select(t => t.Name).ToList();

        Assert.DoesNotContain("task", names);
        Assert.DoesNotContain("task_start", names);
        Assert.DoesNotContain("task_output", names);
        Assert.DoesNotContain("task_stop", names);
        Assert.DoesNotContain("task_wait", names);
        Assert.DoesNotContain("task_background", names);
        Assert.DoesNotContain("task_remove", names);
        Assert.Contains("read_file", names);
    }

    [Fact]
    public void MaxDepthChild_SeesNoTaskManagementTools()
    {
        var general = BuiltInAgents.Resolve("general-purpose");

        var tools = SubagentHost.ResolveChildTools(
            FullTaskRegistry(), general.ReadOnlyToolsOnly, depth: TaskManager.MaxSubagentDepth);
        var names = tools.All.Select(t => t.Name).ToList();

        Assert.DoesNotContain("task", names);
        Assert.DoesNotContain("task_start", names);
        Assert.DoesNotContain("task_output", names);
        Assert.DoesNotContain("task_stop", names);
        Assert.DoesNotContain("task_wait", names);
        Assert.DoesNotContain("task_background", names);
        Assert.DoesNotContain("task_remove", names);
        Assert.Contains("read_file", names);
    }

    [Fact]
    public void Depth1GeneralPurposeChild_KeepsAllTaskManagementTools()
    {
        var general = BuiltInAgents.Resolve("general-purpose");

        var tools = SubagentHost.ResolveChildTools(FullTaskRegistry(), general.ReadOnlyToolsOnly, depth: 1);
        var names = tools.All.Select(t => t.Name).ToList();

        // A depth-1 general-purpose child keeps the management tools so it can drive its own
        // depth-2 descendants (create/read/stop), including the new parity tools.
        Assert.Contains("task", names);
        Assert.Contains("task_start", names);
        Assert.Contains("task_output", names);
        Assert.Contains("task_stop", names);
        Assert.Contains("task_wait", names);
        Assert.Contains("task_background", names);
        Assert.Contains("task_remove", names);
    }

    [Fact]
    public void IsTaskManagementTool_MatchesTaskAndTaskPrefixedTools()
    {
        Assert.True(SubagentHost.IsTaskManagementTool("task"));
        Assert.True(SubagentHost.IsTaskManagementTool("task_start"));
        Assert.True(SubagentHost.IsTaskManagementTool("task_output"));
        Assert.True(SubagentHost.IsTaskManagementTool("task_stop"));
        Assert.True(SubagentHost.IsTaskManagementTool("task_send")); // future task_* tool
        // The new parity tools are task_*-prefixed, so read-only/max-depth subagent stripping
        // catches them automatically — no per-tool change to the deny predicate is needed.
        Assert.True(SubagentHost.IsTaskManagementTool("task_wait"));
        Assert.True(SubagentHost.IsTaskManagementTool("task_background"));
        Assert.True(SubagentHost.IsTaskManagementTool("task_remove"));
        Assert.False(SubagentHost.IsTaskManagementTool("read_file"));
        Assert.False(SubagentHost.IsTaskManagementTool("tool_search"));
    }
}
