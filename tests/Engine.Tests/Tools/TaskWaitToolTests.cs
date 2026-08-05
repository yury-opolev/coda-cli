using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tools;

/// <summary>
/// End-to-end tests for <see cref="TaskWaitTool.ExecuteAsync"/>. Drives the tool itself
/// (not just TaskManager methods) to verify the real contract: a terminal wait consumes the
/// outbox entry and carries the report in the result; a timeout leaves the entry untouched.
/// </summary>
public class TaskWaitToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "coda-taskwait-" + Guid.NewGuid().ToString("N"));

    public TaskWaitToolTests() => Directory.CreateDirectory(this._dir);

    public void Dispose()
    {
        try { Directory.Delete(this._dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private TaskManager NewManager() => new(sessionId: "sess-twt", logRoot: this._dir);

    private static ToolContext MakeContext(TaskManager mgr) =>
        new(WorkingDirectory: Path.GetTempPath()) { Tasks = mgr };

    private static JsonElement MakeInput(string taskId, int? timeoutSeconds = null)
    {
        var json = timeoutSeconds.HasValue
            ? string.Format("{{\"task_id\":\"{0}\",\"timeout_seconds\":{1}}}", taskId, timeoutSeconds.Value)
            : string.Format("{{\"task_id\":\"{0}\"}}", taskId);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task ExecuteAsync_Terminal_ConsumesOutboxEntry()
    {
        var mgr = NewManager();
        var t = mgr.Register(
            TaskKind.Subagent, "worker", parentTaskId: null, mode: TaskExecutionMode.Background);
        mgr.Complete(t.Id, "result text");

        var tool = new TaskWaitTool();
        var result = await tool.ExecuteAsync(MakeInput(t.Id), MakeContext(mgr));

        Assert.Contains("finished", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mgr.DrainCompletions(ownerTaskId: null));
    }

    [Fact]
    public async Task ExecuteAsync_Terminal_IncludesReportInResult()
    {
        var mgr = NewManager();
        var t = mgr.Register(
            TaskKind.Subagent, "reporter", parentTaskId: null, mode: TaskExecutionMode.Background);
        const string expectedReport = "the final answer is 42";
        mgr.Complete(t.Id, expectedReport);

        var tool = new TaskWaitTool();
        var result = await tool.ExecuteAsync(MakeInput(t.Id), MakeContext(mgr));

        Assert.Contains(expectedReport, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_Terminal_WithoutReport_DoesNotAddSpuriousNewline()
    {
        var mgr = NewManager();
        var t = mgr.Register(
            TaskKind.Subagent, "silent", parentTaskId: null, mode: TaskExecutionMode.Background);
        mgr.Stop(t.Id);

        var tool = new TaskWaitTool();
        var result = await tool.ExecuteAsync(MakeInput(t.Id), MakeContext(mgr));

        Assert.DoesNotContain("\n", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_DoesNotConsumeOutboxEntry()
    {
        var mgr = NewManager();
        var t = mgr.Register(
            TaskKind.Subagent, "slow", parentTaskId: null, mode: TaskExecutionMode.Background);

        var tool = new TaskWaitTool();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        try
        {
            await tool.ExecuteAsync(MakeInput(t.Id, timeoutSeconds: 1), MakeContext(mgr), cts.Token);
        }
        catch (OperationCanceledException) { }

        mgr.Complete(t.Id, "late");

        var entries = mgr.DrainCompletions(ownerTaskId: null);
        Assert.Single(entries);
        Assert.Equal(t.Id, entries[0].TaskId);
    }
}
