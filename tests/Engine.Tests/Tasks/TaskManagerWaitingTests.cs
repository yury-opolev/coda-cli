using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskManagerWaitingTests : IDisposable
{
    // Hermetic log root (mirrors TaskLogWriterChannelTests) so File.Exists assertions don't touch ~/.coda.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coda-wait-" + Guid.NewGuid().ToString("N"));
    public TaskManagerWaitingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ } }

    private TaskManager NewManager() => new(sessionId: "sess-waiting", logRoot: _dir);

    [Fact]
    public async Task WaitForTerminal_AlreadyTerminal_ReturnsTerminal()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        Assert.True(mgr.Complete(t.Id, "done"));

        var outcome = await mgr.WaitForTerminalAsync(t.Id, callerTaskId: null, CancellationToken.None);

        Assert.Equal(TaskWaitOutcome.Terminal, outcome);
    }

    [Fact]
    public async Task WaitForTerminal_UnknownOrUnauthorized_ReturnsNotFound_WithoutWaiting()
    {
        var mgr = NewManager();
        var a = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        var b = mgr.Register(TaskKind.Subagent, "b", parentTaskId: null); // running, but not a's descendant

        var unknown = await mgr.WaitForTerminalAsync("task-9999", callerTaskId: null, CancellationToken.None);
        // b is still Running; an unauthorized caller must return immediately (no hang), indistinguishable from unknown.
        var unauthorized = await mgr.WaitForTerminalAsync(b.Id, callerTaskId: a.Id, CancellationToken.None);

        Assert.Equal(TaskWaitOutcome.NotFound, unknown);
        Assert.Equal(TaskWaitOutcome.NotFound, unauthorized);
    }

    [Fact]
    public async Task WaitForTerminal_CompletesWhenTaskFinishes()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);

        var wait = mgr.WaitForTerminalAsync(t.Id, callerTaskId: null, CancellationToken.None);
        Assert.False(wait.IsCompleted);

        mgr.Complete(t.Id, "ok");

        Assert.Equal(TaskWaitOutcome.Terminal, await wait);
    }

    [Fact]
    public async Task WaitForTerminal_HonorsCancellation()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        using var cts = new CancellationTokenSource();

        var wait = mgr.WaitForTerminalAsync(t.Id, callerTaskId: null, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
    }

    [Fact]
    public void Remove_Unauthorized_IsDeniedNotRemoved()
    {
        var mgr = NewManager();
        var a = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        var b = mgr.Register(TaskKind.Subagent, "b", parentTaskId: null);
        mgr.Complete(b.Id, "done");

        var result = mgr.Remove(b.Id, callerTaskId: a.Id);

        Assert.Equal(TaskActionResult.Denied, result);
        Assert.NotNull(mgr.Get(b.Id)); // still present
    }

    [Fact]
    public void Remove_TerminalAuthorized_RemovesAndPreservesLog()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        var logPath = t.LogPath;
        mgr.AppendOutput(t.Id, "some output");
        mgr.Complete(t.Id, "done");

        var result = mgr.Remove(t.Id, callerTaskId: null);

        Assert.Equal(TaskActionResult.Ok, result);
        Assert.Null(mgr.Get(t.Id));
        Assert.True(File.Exists(logPath)); // persistent log preserved
    }

    [Fact]
    public void TryDetach_Unauthorized_IsDenied()
    {
        var mgr = NewManager();
        var a = mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        var shell = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);

        Assert.Equal(TaskActionResult.Denied, mgr.TryDetach(shell.Id, callerTaskId: a.Id));
    }
}
