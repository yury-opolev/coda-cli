using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Tui.Ui.Tasks;
using Xunit;

namespace Coda.Tui.Tests;

public sealed class TaskBrowserControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coda-ctl-" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _mgr;
    private readonly AgentExecutionGate _gate = new();
    private readonly ManualTimeProvider _time = new();
    private readonly TaskBrowserProvider _provider;
    private readonly TaskBrowserController _controller;

    public TaskBrowserControllerTests()
    {
        Directory.CreateDirectory(_dir);
        _mgr = new TaskManager(sessionId: "sess-ctl", logRoot: _dir);
        _provider = new TaskBrowserProvider(_mgr, _gate);
        _controller = new TaskBrowserController(() => _provider, _time);
    }

    /// <summary>Builds a controller bound to the shared manager/gate with an injected log-tail reader.</summary>
    private TaskBrowserController NewController(Func<string, CancellationToken, Task<TaskLogTail>> logReader) =>
        new(() => _provider, _time, logReader);

    public void Dispose()
    {
        _controller.Close();
        _mgr.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Open_SeedsFromInitialSnapshot_AndSyncPicksUpNewTasks()
    {
        var existing = _mgr.Register(TaskKind.Subagent, "first", parentTaskId: null);
        _controller.Open();
        Assert.Equal(existing.Id, _controller.State.SelectedTaskId);

        var added = _mgr.Register(TaskKind.Subagent, "second", parentTaskId: null);
        await _controller.SyncAsync(CancellationToken.None);

        Assert.Contains(added.Id, _controller.State.Projection.AllRows.Select(r => r.Task.Id));
    }

    [Fact]
    public async Task SelectedOutput_ReadsRecentRing_NonConsuming()
    {
        var t = _mgr.Register(TaskKind.Subagent, "worker", parentTaskId: null);
        _mgr.AppendOutput(t.Id, "ring output line");
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);
        await _controller.RefreshOutputAsync(CancellationToken.None);

        Assert.Contains("ring output line", _controller.SelectedOutput);
        // TryPeek is non-consuming: a second read still sees it.
        Assert.Contains("ring output line", _mgr.TryPeek(t.Id, 8000));
    }

    [Fact]
    public async Task DoubleX_WithinWindow_RequestsStop()
    {
        var t = _mgr.Register(TaskKind.Subagent, "runner", parentTaskId: null);
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);

        _controller.RequestStop();
        Assert.Contains("Press x again", _controller.State.StatusMessage);
        Assert.Equal(TaskRunStatus.Running, _mgr.Get(t.Id)!.Status); // first press does not stop

        _time.Advance(TimeSpan.FromSeconds(1)); // still within the 1.5s window
        _controller.RequestStop();
        Assert.Contains("Stopping", _controller.State.StatusMessage!);
    }

    [Fact]
    public async Task DoubleX_AfterWindow_ReArmsInsteadOfStopping()
    {
        var t = _mgr.Register(TaskKind.Subagent, "runner", parentTaskId: null);
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);

        _controller.RequestStop();
        _time.Advance(TimeSpan.FromSeconds(2)); // window expired
        _controller.RequestStop();

        Assert.Contains("Press x again", _controller.State.StatusMessage); // re-armed, not confirmed
    }

    [Fact]
    public async Task DismissSelected_RemovesTerminalTask()
    {
        var t = _mgr.Register(TaskKind.Subagent, "done", parentTaskId: null);
        _mgr.Complete(t.Id, "ok");
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);

        _controller.DismissSelected();
        await _controller.SyncAsync(CancellationToken.None);

        Assert.Null(_mgr.Get(t.Id));
    }

    [Fact]
    public async Task Steering_Submit_DeliversToInboxAndReportsOk()
    {
        var t = _mgr.Register(TaskKind.Subagent, "steerable", parentTaskId: null);
        _mgr.Find(t.Id)!.AttachSteering(new SteeringInbox()); // internal; Coda.Tui.Tests sees Coda.Agent internals
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);

        _controller.OpenDetail();
        _controller.BeginSteering();
        _controller.AppendSteering("adjust the plan");
        var result = _controller.SubmitSteering();

        Assert.Equal(TaskActionResult.Ok, result);
        Assert.Contains("adjust the plan", _mgr.Find(t.Id)!.Steering!.DrainAll());
    }

    [Fact]
    public async Task Attach_WhenIdle_ReachesImmediately_RecordsTarget_ThenReleaseResumes()
    {
        var shell = _mgr.Register(TaskKind.Shell, "bg", parentTaskId: null, TaskExecutionMode.Background);
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);
        _controller.OpenDetail();

        await _controller.AttachAsync(CancellationToken.None);
        Assert.True(_controller.IsAttached);
        Assert.True(_controller.IsComposerLocked);
        Assert.True(_gate.IsPaused);
        Assert.Equal(shell.Id, _controller.AttachedTaskId);

        _controller.ReleaseAttachment();
        Assert.False(_controller.IsAttached);
        Assert.False(_controller.IsComposerLocked);
        Assert.False(_gate.IsPaused);
        Assert.Null(_controller.AttachedTaskId);
    }

    [Fact]
    public async Task Attach_WhileExecuting_StaysAttachingUntilBoundary()
    {
        _mgr.Register(TaskKind.Shell, "bg", parentTaskId: null, TaskExecutionMode.Background);
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);
        _controller.OpenDetail();
        using var exec = _gate.BeginExecution(); // a turn is running

        var attach = _controller.AttachAsync(CancellationToken.None);
        // AttachAsync sets IsAttaching synchronously (before its first await on WaitUntilPaused),
        // so this is deterministic with no sleep: the pause boundary has not been reached yet.
        Assert.True(_controller.IsAttaching);
        Assert.False(_controller.IsAttached);

        _controller.ReleaseAttachment(); // Esc: cancels the pending wait and drops the lease
        Assert.False(_controller.IsComposerLocked);
        await attach;                     // AttachAsync completes via the finally cleanup (OCE swallowed)
        Assert.Null(_controller.AttachedTaskId);
        Assert.False(_controller.IsAttached);
    }

    [Fact]
    public async Task Attach_RejectsSubagentSelection()
    {
        _mgr.Register(TaskKind.Subagent, "sub", parentTaskId: null);
        await AssertSelectedRowIsNotAttachableAsync();
    }

    [Fact]
    public async Task Attach_RejectsForegroundShellSelection()
    {
        // A foreground shell must be backgrounded (Ctrl+B) before it can be attached.
        _mgr.Register(TaskKind.Shell, "fg", parentTaskId: null, TaskExecutionMode.Foreground);
        await AssertSelectedRowIsNotAttachableAsync();
    }

    [Fact]
    public async Task Attach_RejectsTerminalBackgroundShellSelection()
    {
        var t = _mgr.Register(TaskKind.Shell, "bg", parentTaskId: null, TaskExecutionMode.Background);
        _mgr.Complete(t.Id, "done"); // a background shell that is no longer running
        await AssertSelectedRowIsNotAttachableAsync();
    }

    [Fact]
    public async Task Attach_ReleasesGateAndLock_WhenAttachedShellCompletes()
    {
        var id = await AttachToRunningBackgroundShellAsync();
        _mgr.Complete(id, "done");
        await _controller.SyncAsync(CancellationToken.None);

        AssertFullyReleased();
    }

    [Fact]
    public async Task Attach_ReleasesGateAndLock_WhenAttachedShellFails()
    {
        var id = await AttachToRunningBackgroundShellAsync();
        _mgr.Fail(id, "boom");
        await _controller.SyncAsync(CancellationToken.None);

        AssertFullyReleased();
    }

    [Fact]
    public async Task Attach_ReleasesGateAndLock_WhenAttachedShellStops()
    {
        var id = await AttachToRunningBackgroundShellAsync();
        _mgr.Stop(id);
        await _controller.SyncAsync(CancellationToken.None);

        AssertFullyReleased();
    }

    [Fact]
    public async Task Attach_ReleasesWithWarning_WhenAttachedShellPruned()
    {
        var id = await AttachToRunningBackgroundShellAsync();
        _mgr.Complete(id, "done"); // must be terminal before it can be removed
        _mgr.Remove(id);           // auto-pruned from the registry while attached
        await _controller.SyncAsync(CancellationToken.None);

        AssertFullyReleased();
        Assert.Contains("removed", _controller.State.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Background_WhenAttached_ReleasesAttachmentAndResumes()
    {
        await AttachToRunningBackgroundShellAsync();

        var message = _controller.HandleBackgroundChord();

        AssertFullyReleased();
        Assert.Contains("resuming", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Background_PrefersSelectedForegroundShell_OverLatest()
    {
        var selectedShell = _mgr.Register(TaskKind.Shell, "fg-selected", parentTaskId: null, TaskExecutionMode.Foreground);
        _controller.Open(); // selects fg-selected (the only row)
        await _controller.SyncAsync(CancellationToken.None);

        var laterShell = _mgr.Register(TaskKind.Shell, "fg-later", parentTaskId: null, TaskExecutionMode.Foreground);
        await _controller.SyncAsync(CancellationToken.None);
        Assert.Equal(selectedShell.Id, _controller.State.SelectedTaskId); // selection stayed on the first

        var message = _controller.HandleBackgroundChord();

        Assert.Equal(TaskExecutionMode.Background, _mgr.Get(selectedShell.Id)!.Mode); // the *selected* one
        Assert.Equal(TaskExecutionMode.Foreground, _mgr.Get(laterShell.Id)!.Mode);    // later one untouched
        Assert.Contains(selectedShell.Id, message);
    }

    [Fact]
    public async Task Background_WhenSelectionNotForeground_DetachesLatestRunningForegroundShell()
    {
        var subagent = _mgr.Register(TaskKind.Subagent, "sub", parentTaskId: null);
        _controller.Open(); // only the subagent exists → it is selected
        await _controller.SyncAsync(CancellationToken.None);
        Assert.Equal(subagent.Id, _controller.State.SelectedTaskId);

        var older = _mgr.Register(TaskKind.Shell, "fg-older", parentTaskId: null, TaskExecutionMode.Foreground);
        var newer = _mgr.Register(TaskKind.Shell, "fg-newer", parentTaskId: null, TaskExecutionMode.Foreground);
        await _controller.SyncAsync(CancellationToken.None);
        Assert.Equal(subagent.Id, _controller.State.SelectedTaskId); // selection stays on the subagent

        var message = _controller.HandleBackgroundChord();

        Assert.Equal(TaskExecutionMode.Background, _mgr.Get(newer.Id)!.Mode); // latest running fg shell
        Assert.Equal(TaskExecutionMode.Foreground, _mgr.Get(older.Id)!.Mode); // older left running foreground
        Assert.Contains(newer.Id, message);
    }

    [Fact]
    public async Task Background_WhenNoForegroundShell_ReportsNothingToBackground()
    {
        _mgr.Register(TaskKind.Subagent, "sub", parentTaskId: null);
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);

        var message = _controller.HandleBackgroundChord();

        Assert.Contains("No running foreground shell", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseAndClose_AreIdempotent_AfterAttach()
    {
        await AttachToRunningBackgroundShellAsync();

        _controller.ReleaseAttachment();
        _controller.ReleaseAttachment(); // second release is a no-op (mode-switch/Esc path)
        _controller.Close();
        _controller.Close();             // shutdown/Dispose path is idempotent

        AssertFullyReleased();
    }

    // ---- Threading / async serialization contract (Task 5) ----

    [Fact]
    public async Task PersistentLogToggle_LoadsContent_AndFiresChangedAfterAsyncCompletion()
    {
        // Distinct ring vs log content so a Changed observed with the log text can only come from the
        // async persistent-log read completing (not from the synchronous toggle that still shows the ring).
        var t = _mgr.Register(TaskKind.Subagent, "worker", parentTaskId: null);
        _mgr.AppendOutput(t.Id, "RING-CONTENT");
        var logGate = new TaskCompletionSource();
        var controller = NewController(async (path, ct) =>
        {
            await logGate.Task.WaitAsync(ct).ConfigureAwait(false);
            return new TaskLogTail("LOG-ONLY-CONTENT", null);
        });

        try
        {
            controller.Open();
            await controller.SyncAsync(CancellationToken.None);
            controller.OpenDetail();
            await controller.WhenOutputSettledAsync(); // ring load
            Assert.Contains("RING-CONTENT", controller.SelectedOutput);

            var sawLogViaChanged = false;
            controller.Changed += () =>
            {
                if (controller.SelectedOutput.Contains("LOG-ONLY-CONTENT")) sawLogViaChanged = true;
            };

            controller.ToggleOutputSource(); // -> PersistentLog, queues the async read
            Assert.DoesNotContain("LOG-ONLY-CONTENT", controller.SelectedOutput); // not loaded yet
            logGate.SetResult();
            await controller.WhenOutputSettledAsync();

            Assert.Equal(TaskOutputSource.PersistentLog, controller.State.OutputSource);
            Assert.Contains("LOG-ONLY-CONTENT", controller.SelectedOutput);
            Assert.True(sawLogViaChanged, "Changed must fire after the async persistent-log read applies.");
        }
        finally { controller.Close(); }
    }

    [Fact]
    public async Task SlowFirstLogRead_ThenSelectionChange_DiscardsStaleResult()
    {
        var a = _mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        _mgr.Register(TaskKind.Subagent, "b", parentTaskId: null);

        var gateA = new TaskCompletionSource();
        var controller = NewController(async (path, ct) =>
        {
            if (path == a.LogPath) { await gateA.Task; return new TaskLogTail("A-LOG-STALE", null); }
            return new TaskLogTail("B-LOG", null);
        });

        try
        {
            controller.Open();
            await controller.SyncAsync(CancellationToken.None); // selection = a (first row)
            controller.OpenDetail();
            controller.ToggleOutputSource(); // a -> PersistentLog

            var slowA = controller.RefreshOutputAsync(CancellationToken.None); // A's read blocks on gateA
            Assert.False(slowA.IsCompleted);

            controller.MoveSelection(1); // select b: supersedes A's read and queues B's read
            await controller.WhenOutputSettledAsync();
            Assert.Contains("B-LOG", controller.SelectedOutput);

            gateA.SetResult(); // release the stale, now-superseded A read
            await slowA;

            // The stale A result must never overwrite the current B selection's output.
            Assert.Contains("B-LOG", controller.SelectedOutput);
            Assert.DoesNotContain("A-LOG-STALE", controller.SelectedOutput);
        }
        finally { controller.Close(); }
    }

    [Fact]
    public async Task PumpAndRapidSelection_Concurrent_PreserveLatestSelectionAndProjection()
    {
        var first = _mgr.Register(TaskKind.Subagent, "first", parentTaskId: null);
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);

        const int produced = 40;
        const int spins = 400;
        var barrier = new Barrier(3);
        var producerIds = new List<string>();

        var producer = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < produced; i++)
            {
                var id = _mgr.Register(TaskKind.Subagent, $"p{i}", parentTaskId: null).Id;
                lock (producerIds) producerIds.Add(id);
            }
        });

        var pump = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < spins; i++) await _controller.SyncAsync(CancellationToken.None);
        });

        var ui = Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < spins; i++) { _controller.MoveToEnd(); _controller.MoveToStart(); }
        });

        await Task.WhenAll(producer, pump, ui);
        await _controller.SyncAsync(CancellationToken.None); // final authoritative drain

        // The last UI mutation was MoveToStart, which selects the first row; concurrent pump
        // re-projections keep the selection stable by id, so it must survive intact.
        Assert.Equal(first.Id, _controller.State.SelectedTaskId);

        var rows = _controller.State.Projection.AllRows.Select(r => r.Task.Id).ToHashSet();
        List<string> expected;
        lock (producerIds) expected = producerIds.ToList();
        foreach (var id in expected) Assert.Contains(id, rows); // no lost registrations
    }

    [Fact]
    public async Task AttachBoundaryVsRelease_Race_NeverAttachedWithoutPause()
    {
        _mgr.Register(TaskKind.Shell, "bg", parentTaskId: null, TaskExecutionMode.Background);
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);
        _controller.OpenDetail();

        var stop = false;
        var violation = false;
        var observer = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                // Atomic snapshot under the controller's own lock: IsAttached implies the pause lease
                // is still held. A (true, false) reading would mean the agent is "attached" while the
                // gate has already been released — the exact race this test guards against.
                var s = _controller.SnapshotAttach();
                if (s.Attached && !s.HasLease) violation = true;
            }
        });

        for (var i = 0; i < 300; i++)
        {
            using var exec = _gate.BeginExecution(); // attach will park at the pause boundary
            var attach = _controller.AttachAsync(CancellationToken.None);
            Assert.True(_controller.IsAttaching);

            var start = new Barrier(2);
            var releaser = Task.Run(() => { start.SignalAndWait(); _controller.ReleaseAttachment(); });
            var boundary = Task.Run(() => { start.SignalAndWait(); exec.Dispose(); }); // reach paused

            await Task.WhenAll(releaser, boundary);
            await attach;

            _controller.ReleaseAttachment(); // guarantee a clean slate for the next iteration
            Assert.False(_controller.IsAttached);
            Assert.False(_gate.IsPaused);
        }

        Volatile.Write(ref stop, true);
        await observer;
        Assert.False(violation, "IsAttached must never be observed while the pause lease is gone.");
    }

    [Fact]
    public async Task OpenTwice_LeavesSingleLiveSubscription_AndStaysFunctional()
    {
        _mgr.Register(TaskKind.Subagent, "x", parentTaskId: null);
        _controller.Open();
        _controller.Open(); // a defensive re-open must not leak the first subscription

        Assert.Equal(1, _mgr.SubscriptionCount);

        var added = _mgr.Register(TaskKind.Subagent, "y", parentTaskId: null);
        await _controller.SyncAsync(CancellationToken.None);
        Assert.Contains(added.Id, _controller.State.Projection.AllRows.Select(r => r.Task.Id));
    }

    [Fact]
    public async Task Close_StopsPump_WithoutSpinning()
    {
        _mgr.Register(TaskKind.Subagent, "x", parentTaskId: null);
        _controller.Open();
        var pump = _controller.PumpAsync(CancellationToken.None);

        _controller.Close(); // disposes the subscription, which wakes and exits the pump

        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pump.IsCompleted);
    }

    [Fact]
    public async Task ProviderLossAtOpen_PumpExitsImmediately_WithoutSpinning()
    {
        var controller = new TaskBrowserController(() => null, _time);
        controller.Open(); // null provider -> no subscription
        var pump = controller.PumpAsync(CancellationToken.None);

        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pump.IsCompleted);
    }

    [Fact]
    public async Task NavigationRefresh_DoesNotMarkNewOutput_ButRealOutputWhileScrolledDoes()
    {
        var t = _mgr.Register(TaskKind.Subagent, "worker", parentTaskId: null);
        _mgr.AppendOutput(t.Id, "line-1");
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);
        _controller.OpenDetail();
        await _controller.WhenOutputSettledAsync();

        _controller.Scroll(-1); // scroll up: auto-follow turns off
        Assert.False(_controller.State.AutoFollow);

        // A navigation/toggle-style output refresh (not a real Output event) must NOT flag new output.
        await _controller.RefreshOutputAsync(CancellationToken.None);
        Assert.False(_controller.State.HasNewOutput);

        // A real Output event on the selected task while scrolled DOES flag new output.
        _mgr.AppendOutput(t.Id, "line-2");
        await _controller.SyncAsync(CancellationToken.None);
        Assert.True(_controller.State.HasNewOutput);
    }

    private async Task<string> AttachToRunningBackgroundShellAsync()
    {
        var shell = _mgr.Register(TaskKind.Shell, "bg", parentTaskId: null, TaskExecutionMode.Background);
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);
        _controller.OpenDetail();
        await _controller.AttachAsync(CancellationToken.None);
        Assert.True(_controller.IsAttached);
        Assert.True(_gate.IsPaused);
        Assert.Equal(shell.Id, _controller.AttachedTaskId);
        return shell.Id;
    }

    private async Task AssertSelectedRowIsNotAttachableAsync()
    {
        _controller.Open();
        await _controller.SyncAsync(CancellationToken.None);
        _controller.OpenDetail();

        await _controller.AttachAsync(CancellationToken.None);

        Assert.False(_controller.IsAttaching);
        Assert.False(_controller.IsAttached);
        Assert.False(_controller.IsComposerLocked);
        Assert.False(_gate.IsPaused);
        Assert.Null(_controller.AttachedTaskId);
        Assert.Contains("background shell", _controller.State.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    private void AssertFullyReleased()
    {
        Assert.False(_controller.IsAttaching);
        Assert.False(_controller.IsAttached);
        Assert.False(_controller.IsComposerLocked);
        Assert.False(_gate.IsPaused);
        Assert.Null(_controller.AttachedTaskId);
    }
}
