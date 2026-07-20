using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

/// <summary>
/// Bounded retention of terminal tasks: the manager keeps at most
/// <c>maxRetainedTerminalTasks</c> terminal tasks, auto-pruning the oldest terminal tasks as
/// new ones finish. Running tasks are never pruned, pruning publishes a contiguous
/// <see cref="TaskChangeKind.Removed"/> change, and persistent log files are preserved.
/// </summary>
public class TaskRetentionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "coda-retention-mgr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void DefaultCap_Is256()
    {
        Assert.Equal(256, TaskManager.DefaultMaxRetainedTerminalTasks);
    }

    [Fact]
    public void NegativeCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TaskManager(sessionId: "s", logRoot: null, maxRetainedTerminalTasks: -1));
    }

    [Fact]
    public void ManyTerminalTasks_OnlyNewestCapRetained()
    {
        const int cap = 10;
        const int total = 300;
        using var mgr = new TaskManager(sessionId: "sess-cap", logRoot: null, maxRetainedTerminalTasks: cap);

        var ids = new List<string>();
        for (var i = 0; i < total; i++)
        {
            var t = mgr.Register(TaskKind.Shell, "c" + i, parentTaskId: null);
            ids.Add(t.Id);
            Assert.True(mgr.Complete(t.Id, "done"));
        }

        var listed = mgr.List();
        Assert.Equal(cap, listed.Count);

        // The retained tasks are exactly the newest `cap` completed tasks, in order.
        var expectedNewest = ids.Skip(total - cap).ToList();
        Assert.Equal(expectedNewest, listed.Select(s => s.Id).ToList());

        // Older tasks are gone from the registry.
        foreach (var old in ids.Take(total - cap))
        {
            Assert.Null(mgr.Get(old));
        }
    }

    [Fact]
    public void RunningTasks_AreNeverPruned()
    {
        const int cap = 5;
        using var mgr = new TaskManager(sessionId: "sess-run", logRoot: null, maxRetainedTerminalTasks: cap);

        // A long-lived running task registered first must survive an avalanche of terminal tasks.
        var running = mgr.Register(TaskKind.Subagent, "runner", parentTaskId: null);

        for (var i = 0; i < 100; i++)
        {
            var t = mgr.Register(TaskKind.Shell, "c" + i, parentTaskId: null);
            Assert.True(mgr.Complete(t.Id, "done"));
        }

        Assert.NotNull(mgr.Get(running.Id));
        Assert.Equal(TaskRunStatus.Running, mgr.Get(running.Id)!.Status);

        // The list holds the running task plus at most `cap` terminal tasks.
        var listed = mgr.List();
        Assert.Contains(listed, s => s.Id == running.Id);
        Assert.True(listed.Count <= cap + 1, $"expected <= {cap + 1} entries, got {listed.Count}.");
        var terminalCount = listed.Count(s => s.Status != TaskRunStatus.Running);
        Assert.Equal(cap, terminalCount);
    }

    [Fact]
    public void Prune_PublishesRemovedChange_WithContiguousVersion()
    {
        const int cap = 1;
        using var mgr = new TaskManager(sessionId: "sess-ev", logRoot: null, maxRetainedTerminalTasks: cap);

        var first = mgr.Register(TaskKind.Shell, "first", parentTaskId: null);
        Assert.True(mgr.Complete(first.Id, "done"));
        var firstTerminalVersion = mgr.Get(first.Id)!.Version;

        using var sub = mgr.Subscribe();

        // Completing a second task pushes the terminal count to 2 > cap(1) -> prune `first`.
        var second = mgr.Register(TaskKind.Shell, "second", parentTaskId: null);
        Assert.True(mgr.Complete(second.Id, "done"));

        var (changes, _) = sub.Drain();
        var removedChanges = changes.Where(c => c.Kind == TaskChangeKind.Removed).ToList();
        var removed = Assert.Single(removedChanges);
        Assert.Equal(first.Id, removed.TaskId);
        // Pruning bumps the version N -> N+1 so the Removed change is contiguous for a subscriber.
        Assert.Equal(firstTerminalVersion + 1, removed.Version);

        Assert.Null(mgr.Get(first.Id));
        Assert.NotNull(mgr.Get(second.Id));
    }

    [Fact]
    public void CapZero_PrunesEveryTerminalTaskImmediately()
    {
        using var mgr = new TaskManager(sessionId: "sess-zero", logRoot: null, maxRetainedTerminalTasks: 0);

        var running = mgr.Register(TaskKind.Subagent, "runner", parentTaskId: null);
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(mgr.Complete(t.Id, "done"));

        // The terminal task is removed immediately; the running one stays.
        Assert.Null(mgr.Get(t.Id));
        Assert.NotNull(mgr.Get(running.Id));
        Assert.DoesNotContain(mgr.List(), s => s.Status != TaskRunStatus.Running);
    }

    [Fact]
    public void Prune_PreservesPersistentLogFile()
    {
        const int cap = 1;
        using var mgr = new TaskManager(sessionId: "sess-log", logRoot: _root, maxRetainedTerminalTasks: cap);

        var first = mgr.Register(TaskKind.Shell, "first", parentTaskId: null);
        mgr.AppendOutput(first.Id, "first output\n");
        var firstLog = first.ToSnapshot().LogPath;
        Assert.True(mgr.Complete(first.Id, "done"));

        var second = mgr.Register(TaskKind.Shell, "second", parentTaskId: null);
        Assert.True(mgr.Complete(second.Id, "done")); // prunes `first`

        Assert.Null(mgr.Get(first.Id));
        // Pruning must NOT delete the persistent log file.
        Assert.True(File.Exists(firstLog), "pruned task's log file must be preserved.");
        Assert.Contains("first output", File.ReadAllText(firstLog));
    }

    [Fact]
    public void ExplicitRemove_StillWorks_UnderRetention()
    {
        using var mgr = new TaskManager(sessionId: "sess-rm", logRoot: null, maxRetainedTerminalTasks: 256);
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(mgr.Complete(t.Id, "done"));
        Assert.Equal(TaskActionResult.Ok, mgr.Remove(t.Id));
        Assert.Null(mgr.Get(t.Id));
    }

    [Fact]
    public async Task ForegroundCaller_StillGetsResult_WhenItsTaskAutoPruned()
    {
        // With cap 0 the completing task is pruned the instant it goes terminal. A foreground
        // shell caller must still return its ShellRunResult safely from locally-captured state.
        using var mgr = new TaskManager(sessionId: "sess-fg", logRoot: null, maxRetainedTerminalTasks: 0);

        var result = await mgr.RunShellAsync(
            EchoCommand("hello-fg"), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-fg", result.Stdout);
        // Its task was auto-pruned on completion.
        Assert.Null(mgr.Get(result.TaskId));
    }

    private static string EchoCommand(string text) =>
        OperatingSystem.IsWindows() ? $"Write-Output {text}" : $"echo {text}";
}
