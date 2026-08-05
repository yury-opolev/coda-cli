using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tools;

/// <summary>
/// Contract tests for <see cref="TaskListTool.ExecuteAsync"/>. Verifies that the resolved model is
/// always present in the output so the main agent can tell which LLM tier each subagent is using,
/// and that tasks without a resolved model render an unambiguous placeholder rather than a blank.
/// </summary>
public sealed class TaskListToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "coda-tasklisttool-" + Guid.NewGuid().ToString("N"));

    public TaskListToolTests() => Directory.CreateDirectory(this._dir);

    public void Dispose()
    {
        try { Directory.Delete(this._dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private TaskManager NewManager() => new(sessionId: "sess-tlt", logRoot: this._dir);

    private static ToolContext MakeContext(TaskManager mgr) =>
        new(WorkingDirectory: Path.GetTempPath()) { Tasks = mgr };

    private static JsonElement EmptyInput() =>
        JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task Output_IncludesResolvedModel_WhenSet()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "worker", parentTaskId: null);
        mgr.SetTaskResolvedModel(t.Id, "claude-opus-4-8");

        var tool = new TaskListTool();
        var result = await tool.ExecuteAsync(EmptyInput(), MakeContext(mgr));

        // The resolved model must appear so the caller can assess which LLM tier the subagent uses.
        Assert.Contains("claude-opus-4-8", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_ShowsUnambiguousPlaceholder_WhenResolvedModelAbsent()
    {
        var mgr = NewManager();
        mgr.Register(TaskKind.Subagent, "worker", parentTaskId: null);

        var tool = new TaskListTool();
        var result = await tool.ExecuteAsync(EmptyInput(), MakeContext(mgr));

        // Tasks with no resolved model must render a visible placeholder (—), not a blank that would
        // produce ragged columns and make it hard to parse the output programmatically.
        Assert.Contains("—", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_ShowsResolvedModelAndPlaceholder_WhenMixed()
    {
        var mgr = NewManager();
        var t1 = mgr.Register(TaskKind.Subagent, "has-model", parentTaskId: null);
        mgr.SetTaskResolvedModel(t1.Id, "gpt-5-codex");
        mgr.Register(TaskKind.Subagent, "no-model", parentTaskId: null);

        var tool = new TaskListTool();
        var result = await tool.ExecuteAsync(EmptyInput(), MakeContext(mgr));

        Assert.Contains("gpt-5-codex", result.Content, StringComparison.Ordinal);
        Assert.Contains("—", result.Content, StringComparison.Ordinal);
    }
}
