using System.Reflection;
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskManagerTests
{
    private static TaskManager NewManager() =>
        new(sessionId: "sess-test", logRoot: null);

    [Fact]
    public void Register_AssignsSequentialPaddedIds()
    {
        var mgr = NewManager();
        var a = mgr.Register(TaskKind.Subagent, "first", parentTaskId: null);
        var b = mgr.Register(TaskKind.Shell, "second", parentTaskId: null);

        Assert.Equal("task-0001", a.Id);
        Assert.Equal("task-0002", b.Id);
    }

    [Fact]
    public void Register_TopLevelTask_HasDepthOne()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "top", parentTaskId: null);
        Assert.Equal(1, t.Depth);
        Assert.Null(t.ToSnapshot().ParentId);
    }

    [Fact]
    public void Register_ChildTask_HasParentDepthPlusOne()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "c", parentTaskId: parent.Id);
        Assert.Equal(2, child.Depth);
        Assert.Equal(parent.Id, child.ToSnapshot().ParentId);
    }

    [Fact]
    public void Register_SubagentBeyondMaxDepth_Throws()
    {
        var mgr = NewManager();
        var d1 = mgr.Register(TaskKind.Subagent, "d1", parentTaskId: null);
        var d2 = mgr.Register(TaskKind.Subagent, "d2", parentTaskId: d1.Id);
        var ex = Assert.Throws<InvalidOperationException>(
            () => mgr.Register(TaskKind.Subagent, "d3", parentTaskId: d2.Id));
        Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_ShellBeyondMaxDepth_IsAllowed()
    {
        var mgr = NewManager();
        var d1 = mgr.Register(TaskKind.Subagent, "d1", parentTaskId: null);
        var d2 = mgr.Register(TaskKind.Subagent, "d2", parentTaskId: d1.Id);
        var shell = mgr.Register(TaskKind.Shell, "sh", parentTaskId: d2.Id);
        Assert.Equal(3, shell.Depth);
    }

    [Fact]
    public void Register_UnknownParent_Throws()
    {
        var mgr = NewManager();
        Assert.Throws<InvalidOperationException>(
            () => mgr.Register(TaskKind.Subagent, "x", parentTaskId: "task-9999"));
    }

    [Fact]
    public void NewTask_StartsRunningWithVersionZero()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var snap = t.ToSnapshot();
        Assert.Equal(TaskRunStatus.Running, snap.Status);
        Assert.Equal(0, snap.Version);
        Assert.Null(snap.EndedAt);
    }

    [Fact]
    public void TryComplete_MovesToCompletedAndBumpsVersion()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(t.TryComplete("done"));
        var snap = t.ToSnapshot();
        Assert.Equal(TaskRunStatus.Completed, snap.Status);
        Assert.Equal("done", snap.Result);
        Assert.Equal(1, snap.Version);
        Assert.NotNull(snap.EndedAt);
    }

    [Fact]
    public void TryStop_UsesStoppedTerminology()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        Assert.True(t.TryStop());
        Assert.Equal(TaskRunStatus.Stopped, t.ToSnapshot().Status);
    }

    [Fact]
    public void Transition_OnTerminalTask_ReturnsFalseAndKeepsState()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(t.TryComplete("ok"));
        Assert.False(t.TryFail("late"));
        Assert.False(t.TryStop());
        var snap = t.ToSnapshot();
        Assert.Equal(TaskRunStatus.Completed, snap.Status);
        Assert.Equal(1, snap.Version);
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var mgr = NewManager();
        Assert.Null(mgr.Get("task-0001"));
    }

    [Fact]
    public void List_ReturnsAllRegisteredTasksInOrder()
    {
        var mgr = NewManager();
        mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        mgr.Register(TaskKind.Shell, "b", parentTaskId: null);
        var ids = mgr.List().Select(s => s.Id).ToList();
        Assert.Equal(new[] { "task-0001", "task-0002" }, ids);
    }

    [Fact]
    public void CancelToken_IsSignalledOnStop()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        Assert.False(t.Token.IsCancellationRequested);
        t.Cancel();
        Assert.True(t.Token.IsCancellationRequested);
    }

    [Fact]
    public void Register_ConcurrentStarts_AssignUniqueSequentialIds()
    {
        var mgr = NewManager();

        // 100 parallel registrations must each get a distinct id and all appear in the list.
        Parallel.For(0, 100, _ => mgr.Register(TaskKind.Shell, "s", parentTaskId: null));

        var ids = mgr.List().Select(s => s.Id).ToList();
        Assert.Equal(100, ids.Count);
        Assert.Equal(100, ids.Distinct().Count());
    }

    [Fact]
    public void TryFail_MovesToFailedWithErrorAndBumpsVersion()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(t.TryFail("boom"));
        var snap = t.ToSnapshot();
        Assert.Equal(TaskRunStatus.Failed, snap.Status);
        Assert.Equal("boom", snap.Error);
        Assert.Null(snap.Result);
        Assert.Equal(1, snap.Version);
        Assert.NotNull(snap.EndedAt);
    }

    [Fact]
    public void Register_LogPath_IsComposedUnderSessionLogRoot()
    {
        var mgr = new TaskManager(sessionId: "sess-test", logRoot: "logroot");
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var expected = Path.Combine("logroot", "sess-test", t.Id + ".log");
        Assert.Equal(expected, t.ToSnapshot().LogPath);
    }

    [Fact]
    public void AppendOutput_ThenReadIncremental_RoundTrips()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        mgr.AppendOutput(t.Id, "line1\n");
        mgr.AppendOutput(t.Id, "line2\n");
        var read = mgr.TryReadIncremental(t.Id, 0);
        Assert.NotNull(read);
        Assert.Equal("line1\nline2\n", read!.Value.Text);
        Assert.False(read.Value.Truncated);
    }

    [Fact]
    public void AppendOutput_BumpsVersion()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var before = t.ToSnapshot().Version;
        mgr.AppendOutput(t.Id, "x");
        Assert.True(t.ToSnapshot().Version > before);
    }

    [Fact]
    public void TryPeek_UnknownId_ReturnsNull()
    {
        var mgr = NewManager();
        Assert.Null(mgr.TryPeek("task-0001", 10));
    }

    [Fact]
    public void TryReadIncremental_UnknownId_ReturnsNull()
    {
        var mgr = NewManager();
        Assert.Null(mgr.TryReadIncremental("task-0001", 0));
    }

    [Fact]
    public void Constructor_NonPositiveRingBytes_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TaskManager(sessionId: "sess-test", logRoot: null, outputRingBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TaskManager(sessionId: "sess-test", logRoot: null, outputRingBytes: -1));
    }

    [Fact]
    public void ManagedTask_IsNotPublic()
    {
        var type = typeof(ManagedTask);
        Assert.False(type.IsPublic, "ManagedTask must not be public; it is an internal lifecycle type.");
        Assert.True(type.IsNotPublic);
    }

    [Fact]
    public void AppendOutput_WritesToPersistentLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "coda-mgrlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new TaskManager(sessionId: "sess-log", logRoot: root);
            var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
            mgr.AppendOutput(t.Id, "persist me\n");
            mgr.Dispose(); // flush + close writers

            var logPath = t.ToSnapshot().LogPath;
            Assert.True(File.Exists(logPath));
            Assert.Contains("persist me", File.ReadAllText(logPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void AppendOutput_RedactsSecretsInPersistentLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "coda-mgrlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new TaskManager(sessionId: "sess-redact", logRoot: root);
            var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
            mgr.AppendOutput(t.Id, "auth token=sk-abcdefghijklmnop trailing\n");
            mgr.Dispose();

            var text = File.ReadAllText(t.ToSnapshot().LogPath);
            Assert.DoesNotContain("sk-abcdefghijklmnop", text);
            Assert.Contains("***redacted***", text);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Constructor_RunsRetentionCleanup_DeletingAgedLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), "coda-mgrclean-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sessionDir = Path.Combine(root, "sess-old");
            Directory.CreateDirectory(sessionDir);
            var aged = Path.Combine(sessionDir, "task-9999.log");
            File.WriteAllBytes(aged, new byte[10]);
            File.SetLastWriteTimeUtc(aged, DateTime.UtcNow.AddDays(-30));

            using var mgr = new TaskManager(sessionId: "sess-new", logRoot: root);

            Assert.False(File.Exists(aged), "constructor should run retention cleanup on aged logs.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Terminal_ClosesAndRemovesTaskLogWriter()
    {
        var root = Path.Combine(Path.GetTempPath(), "coda-term-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new TaskManager(sessionId: "sess-term", logRoot: root);
            var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
            mgr.AppendOutput(t.Id, "before-terminal\n");
            Assert.True(mgr.HasLogWriter(t.Id), "writer should exist while running.");

            Assert.True(t.TryComplete("done"));

            Assert.False(mgr.HasLogWriter(t.Id), "writer should be removed on terminal state.");

            var logPath = t.ToSnapshot().LogPath;
            Assert.Contains("before-terminal", File.ReadAllText(logPath));

            // The writer handle is closed, so post-terminal output does not reach the log.
            mgr.AppendOutput(t.Id, "after-terminal\n");
            Assert.DoesNotContain("after-terminal", File.ReadAllText(logPath));

            mgr.Dispose();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Terminal_FlushesFinalPartialOutputBeforeClosing()
    {
        var root = Path.Combine(Path.GetTempPath(), "coda-term-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new TaskManager(sessionId: "sess-flush", logRoot: root);
            var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
            mgr.AppendOutput(t.Id, "tail-without-newline");
            Assert.True(t.TryFail("boom"));

            Assert.Contains("tail-without-newline", File.ReadAllText(t.ToSnapshot().LogPath));
            mgr.Dispose();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Deliberately blocking waits with timeouts keep this concurrency regression bounded.
#pragma warning disable xUnit1031
    [Fact]
    public void Terminal_HookRunsOutsideTaskLock()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        ManagedTask? task = null;
        task = new ManagedTask(
            "task-x", parentId: null, depth: 1, TaskKind.Shell, "d", "unused.log", 1024,
            onTerminal: _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            });

        var completeTask = Task.Run(() => task!.TryComplete("x"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "terminal hook did not run.");

        // If the hook were invoked while holding the task lock, ToSnapshot (which takes the
        // same lock) would block until the hook returns and deadlock this test.
        var snapshotTask = Task.Run(() => task!.ToSnapshot());
        Assert.True(
            snapshotTask.Wait(TimeSpan.FromSeconds(5)),
            "ToSnapshot blocked: terminal hook held the task lock.");

        release.Set();
        Assert.True(completeTask.Wait(TimeSpan.FromSeconds(5)), "transition did not complete.");
    }

    [Fact]
    public void Terminal_ConcurrentWithRegistryReaders_NoDeadlock()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);

        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                _ = mgr.List();
            }
        });
        var completer = Task.Run(() => t.TryComplete("done"));

        Assert.True(Task.WaitAll(new[] { reader, completer }, TimeSpan.FromSeconds(5)),
            "terminal transition deadlocked against registry readers.");
    }

    [Fact]
    public void Dispose_DisposesTasksOutsideManagerLock_NoDeadlockWithConcurrentReaders()
    {
        var mgr = NewManager();
        var task = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);

        using var callbackEntered = new ManualResetEventSlim(false);
        using var listCompleted = new ManualResetEventSlim(false);

        // This cancellation callback fires synchronously from ManagedTask.Dispose ->
        // _cts.Cancel(). It blocks until another thread completes a manager.List() call.
        // If TaskManager.Dispose holds the manager lock while cancelling, the reader
        // cannot acquire that lock and the two threads deadlock.
        using var registration = task.Token.Register(() =>
        {
            callbackEntered.Set();
            listCompleted.Wait(TimeSpan.FromSeconds(5));
        });

        var disposeTask = Task.Run(() => mgr.Dispose());

        Assert.True(
            callbackEntered.Wait(TimeSpan.FromSeconds(5)),
            "cancellation callback did not start");

        var readerTask = Task.Run(() =>
        {
            _ = mgr.List();
            listCompleted.Set();
        });

        Assert.True(
            readerTask.Wait(TimeSpan.FromSeconds(5)),
            "manager.List() blocked: Dispose held the manager lock while cancelling tasks.");
        Assert.True(
            disposeTask.Wait(TimeSpan.FromSeconds(5)),
            "Dispose did not complete.");
    }
    [Fact]
    public void ReadFromMainCursor_ConcurrentReaders_DeliverEachChunkExactlyOnce()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);

        // A large body of fixed-width, uniquely numbered tokens so any duplicate delivery
        // is detectable by counting token occurrences across all readers' combined output.
        const int tokenCount = 4000;
        var expected = new System.Text.StringBuilder();
        for (var i = 0; i < tokenCount; i++)
        {
            expected.Append($"<{i:D6}>");
        }
        var expectedText = expected.ToString();

        using var stop = new ManualResetEventSlim(false);
        var buffers = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Multiple readers hammer the shared main cursor while a producer streams output.
        var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                var (_, text, _, _) = mgr.ReadForMainAgent(t.Id);
                sb.Append(text);
                if (stop.IsSet && text.Length == 0) break;
            }
            buffers.Add(sb.ToString());
        })).ToArray();

        var producer = Task.Run(() =>
        {
            for (var i = 0; i < tokenCount; i++)
            {
                mgr.AppendOutput(t.Id, $"<{i:D6}>");
            }
        });

        Assert.True(producer.Wait(TimeSpan.FromSeconds(10)), "producer did not finish.");
        // Give readers a moment to drain, then signal completion.
        Thread.Sleep(50);
        stop.Set();
        Assert.True(Task.WaitAll(readers, TimeSpan.FromSeconds(10)), "readers did not finish.");

        var combined = string.Concat(buffers);

        // No duplicate delivery: total delivered length equals appended length exactly.
        Assert.Equal(expectedText.Length, combined.Length);

        // Every token was delivered exactly once across all readers.
        var matches = System.Text.RegularExpressions.Regex.Matches(combined, "<\\d{6}>");
        Assert.Equal(tokenCount, matches.Count);
        Assert.Equal(tokenCount, matches.Select(m => m.Value).Distinct().Count());
    }
#pragma warning restore xUnit1031
}
