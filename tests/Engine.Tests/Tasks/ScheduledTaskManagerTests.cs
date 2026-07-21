using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Scheduling;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tasks;

/// <summary>
/// Task 4: covers <see cref="TaskManager.StartScheduledBackground"/>, the
/// <see cref="IScheduledAgentHost"/> contract, scheduled-root registration, output streaming,
/// terminal-state transitions with a once-only terminal callback, steering, and authorization.
/// </summary>
public class ScheduledTaskManagerTests : IDisposable
{
    private readonly string _logRoot =
        Path.Combine(Path.GetTempPath(), "coda-scheduled-" + Guid.NewGuid().ToString("N"));

    public ScheduledTaskManagerTests() => Directory.CreateDirectory(_logRoot);

    public void Dispose()
    {
        try { Directory.Delete(_logRoot, recursive: true); } catch { /* best-effort */ }
    }

    private TaskManager NewManager() => new(sessionId: "sess-sched", logRoot: _logRoot);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolContext Ctx(TaskManager mgr, string? callerTaskId = null) =>
        new(Directory.GetCurrentDirectory()) { Tasks = mgr, CurrentTaskId = callerTaskId };

    /// <summary>
    /// A configurable scheduled host: records what it was handed, optionally emits output, can be
    /// held open on a gate or on token cancellation, and can complete, throw, or return a result.
    /// </summary>
    private sealed class RecordingHost : IScheduledAgentHost
    {
        private readonly string _result;
        private readonly Exception? _throw;
        private readonly TaskCompletionSource? _gate;
        private readonly bool _emitOutput;
        private readonly bool _blockOnToken;

        public RecordingHost(
            string result = "ok",
            Exception? toThrow = null,
            TaskCompletionSource? gate = null,
            bool emitOutput = false,
            bool blockOnToken = false)
        {
            _result = result;
            _throw = toThrow;
            _gate = gate;
            _emitOutput = emitOutput;
            _blockOnToken = blockOnToken;
        }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? SeenPrompt { get; private set; }
        public string? SeenTaskId { get; private set; }
        public int SeenDepth { get; private set; }
        public SteeringInbox? SeenSteering { get; private set; }
        public List<string> SeenSteers { get; } = new();

        public async Task<string> RunScheduledAsync(
            string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken)
        {
            SeenPrompt = prompt;
            SeenTaskId = taskId;
            SeenDepth = depth;
            SeenSteering = steering;
            Started.TrySetResult();

            if (_emitOutput)
            {
                sink.OnAssistantText("scheduled-output");
                sink.OnAssistantTextComplete();
            }

            if (_gate is not null)
            {
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_blockOnToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (_throw is not null)
            {
                throw _throw;
            }

            SeenSteers.AddRange(steering.DrainAll());
            return _result;
        }
    }

    /// <summary>Captures terminal-callback invocations and the final snapshot delivered.</summary>
    private sealed class TerminalRecorder
    {
        private int _calls;
        private readonly TaskCompletionSource<TaskSnapshot> _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);
        public TaskSnapshot? Last { get; private set; }
        public Task<TaskSnapshot> Completed => _done.Task;
        public Action<TaskSnapshot>? OnInvoke { get; set; }

        public void Handle(TaskSnapshot snapshot)
        {
            Interlocked.Increment(ref _calls);
            Last = snapshot;
            // Signal completion BEFORE the (optionally throwing) OnInvoke hook so a callback that
            // throws still lets awaiters observe the terminal snapshot; the throw then propagates
            // into the production continuation, which must isolate it.
            _done.TrySetResult(snapshot);
            OnInvoke?.Invoke(snapshot);
        }
    }

    private static async Task WaitUntil(Func<bool> predicate, string message = "condition not met")
    {
        for (var i = 0; i < 300; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException(message);
    }

    // ── Registration: Scheduled kind, Background mode, Running, depth 1 before host enters ──

    [Fact]
    public async Task StartScheduled_RegistersScheduledBackgroundRunningDepth1_BeforeHostEnters()
    {
        var mgr = NewManager();
        var gate = new TaskCompletionSource();
        var host = new RecordingHost(gate: gate);
        var recorder = new TerminalRecorder();

        var id = mgr.StartScheduledBackground(host, "do it", "nightly", recorder.Handle);

        // The snapshot must exist and be authoritative immediately — registration happens BEFORE
        // any host work is scheduled — so it is observable while the host is still gated.
        var snap = mgr.Get(id);
        Assert.NotNull(snap);
        Assert.Equal(TaskKind.Scheduled, snap!.Kind);
        Assert.Equal(TaskExecutionMode.Background, snap.Mode);
        Assert.Equal(TaskRunStatus.Running, snap.Status);
        Assert.Null(snap.ParentId);
        Assert.Equal(1, snap.Depth);

        gate.SetResult();
        await recorder.Completed;
    }

    [Fact]
    public async Task StartScheduled_HostReceivesPromptTaskIdDepth1AndSteeringInbox()
    {
        var mgr = NewManager();
        var host = new RecordingHost();
        var recorder = new TerminalRecorder();

        var id = mgr.StartScheduledBackground(host, "the-prompt", "nightly", recorder.Handle);
        await recorder.Completed;

        Assert.Equal("the-prompt", host.SeenPrompt);
        Assert.Equal(id, host.SeenTaskId);
        Assert.Equal(1, host.SeenDepth);
        Assert.NotNull(host.SeenSteering);
    }

    [Fact]
    public async Task StartScheduled_StreamsOutputIntoRingAndPersistentLog()
    {
        var mgr = NewManager();
        var host = new RecordingHost(emitOutput: true);
        var recorder = new TerminalRecorder();

        var id = mgr.StartScheduledBackground(host, "go", "nightly", recorder.Handle);
        var snap = await recorder.Completed;

        Assert.Contains("scheduled-output", mgr.TryPeek(id, 4000) ?? string.Empty);

        var log = File.ReadAllText(snap.LogPath);
        Assert.Contains("scheduled-output", log);
    }

    // ── Terminal transitions + callback fires exactly once ──

    [Fact]
    public async Task StartScheduled_Success_CompletesWithResult_AndFiresCallbackOnce()
    {
        var mgr = NewManager();
        var host = new RecordingHost(result: "scheduled report");
        var recorder = new TerminalRecorder();

        var id = mgr.StartScheduledBackground(host, "go", "nightly", recorder.Handle);
        var snap = await recorder.Completed;

        Assert.Equal(TaskRunStatus.Completed, snap.Status);
        Assert.Equal("scheduled report", snap.Result);

        await Task.Delay(100);
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(TaskRunStatus.Completed, mgr.Get(id)!.Status);
    }

    [Fact]
    public async Task StartScheduled_Exception_FailsWithError_AndFiresCallbackOnce()
    {
        var mgr = NewManager();
        var host = new RecordingHost(toThrow: new InvalidOperationException("boom"));
        var recorder = new TerminalRecorder();

        var id = mgr.StartScheduledBackground(host, "go", "nightly", recorder.Handle);
        var snap = await recorder.Completed;

        Assert.Equal(TaskRunStatus.Failed, snap.Status);
        Assert.Contains("boom", snap.Error);

        await Task.Delay(100);
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(TaskRunStatus.Failed, mgr.Get(id)!.Status);
    }

    [Fact]
    public async Task StartScheduled_RequestStop_StopsTask_AndFiresCallbackOnce()
    {
        var mgr = NewManager();
        var host = new RecordingHost(blockOnToken: true);
        var recorder = new TerminalRecorder();

        var id = mgr.StartScheduledBackground(host, "go", "nightly", recorder.Handle);
        await host.Started.Task;

        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(id));
        var snap = await recorder.Completed;

        Assert.Equal(TaskRunStatus.Stopped, snap.Status);

        await Task.Delay(100);
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(TaskRunStatus.Stopped, mgr.Get(id)!.Status);
    }

    [Fact]
    public async Task StartScheduled_ShutdownRace_FiresCallbackOnce_NoUnobservedFault()
    {
        var faulted = false;
        void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) => faulted = true;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var mgr = NewManager();
            var host = new RecordingHost(blockOnToken: true);
            var recorder = new TerminalRecorder();

            var id = mgr.StartScheduledBackground(host, "go", "nightly", recorder.Handle);
            await host.Started.Task;

            await mgr.ShutdownAsync(TimeSpan.FromSeconds(5));

            var snap = await recorder.Completed;
            Assert.NotEqual(TaskRunStatus.Running, snap.Status);

            await Task.Delay(100);
            Assert.Equal(1, recorder.Calls);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(faulted, "no scheduled worker exception should go unobserved");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task StartScheduled_AfterShutdown_Throws()
    {
        var mgr = NewManager();
        await mgr.ShutdownAsync(TimeSpan.FromSeconds(1));

        Assert.Throws<InvalidOperationException>(
            () => mgr.StartScheduledBackground(new RecordingHost(), "go", "nightly", _ => { }));
    }

    [Fact]
    public async Task StartScheduled_CallbackThrows_DoesNotAlterTerminalStateOrCrash()
    {
        var mgr = NewManager();
        var host = new RecordingHost(result: "done");
        var recorder = new TerminalRecorder { OnInvoke = _ => throw new InvalidOperationException("callback blew up") };

        var id = mgr.StartScheduledBackground(host, "go", "nightly", recorder.Handle);
        var snap = await recorder.Completed;

        // The throwing callback must not corrupt the authoritative terminal state.
        Assert.Equal(TaskRunStatus.Completed, snap.Status);
        await Task.Delay(100);
        Assert.Equal(TaskRunStatus.Completed, mgr.Get(id)!.Status);
        Assert.Equal(1, recorder.Calls);
    }

    // ── Steering: scheduled tasks are steerable; shell still rejected ──

    [Fact]
    public async Task Steer_ScheduledTask_DeliversFifoMessage()
    {
        var mgr = NewManager();
        var gate = new TaskCompletionSource();
        var host = new RecordingHost(gate: gate);
        var recorder = new TerminalRecorder();

        var id = mgr.StartScheduledBackground(host, "go", "nightly", recorder.Handle);
        await host.Started.Task;

        Assert.Equal(TaskActionResult.Ok, mgr.Steer(id, "first"));
        Assert.Equal(TaskActionResult.Ok, mgr.Steer(id, "second"));

        gate.SetResult();
        await recorder.Completed;

        Assert.Equal(new[] { "first", "second" }, host.SeenSteers);
    }

    [Fact]
    public void Steer_ShellTask_StillRejected_WhenScheduledAccepted()
    {
        var mgr = NewManager();
        var shell = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);
        Assert.Equal(TaskActionResult.Rejected, mgr.Steer(shell.Id, "x"));

        var scheduled = mgr.Register(TaskKind.Scheduled, "s", parentTaskId: null);
        scheduled.AttachSteering(new SteeringInbox());
        Assert.Equal(TaskActionResult.Ok, mgr.Steer(scheduled.Id, "y"));
    }

    [Fact]
    public void Steer_TerminalScheduledTask_ReturnsInvalidState()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Scheduled, "s", parentTaskId: null);
        t.AttachSteering(new SteeringInbox());
        mgr.Complete(t.Id, "done");
        Assert.Equal(TaskActionResult.InvalidState, mgr.Steer(t.Id, "x"));
    }

    // ── Authorization unchanged: main all; scheduled root cannot reach unrelated tasks ──

    [Fact]
    public void Steer_Main_CanSteerScheduledRoot()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Scheduled, "s", parentTaskId: null);
        t.AttachSteering(new SteeringInbox());

        Assert.Equal(TaskActionResult.Ok, mgr.Steer(t.Id, "hi", callerTaskId: null));
    }

    [Fact]
    public void Steer_ScheduledRoot_CannotSteerUnrelatedTask()
    {
        var mgr = NewManager();
        var scheduled = mgr.Register(TaskKind.Scheduled, "s", parentTaskId: null);
        var other = mgr.Register(TaskKind.Subagent, "unrelated", parentTaskId: null);
        other.AttachSteering(new SteeringInbox());

        // The scheduled root is not an ancestor of the unrelated task, so it is denied.
        Assert.Equal(TaskActionResult.Denied, mgr.Steer(other.Id, "x", scheduled.Id));
    }

    [Fact]
    public void Steer_ScheduledRoot_CanSteerItsOwnDescendant()
    {
        var mgr = NewManager();
        var scheduled = mgr.Register(TaskKind.Scheduled, "s", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "child", parentTaskId: scheduled.Id);
        child.AttachSteering(new SteeringInbox());

        Assert.Equal(TaskActionResult.Ok, mgr.Steer(child.Id, "refine", scheduled.Id));
    }

    // ── TaskSendTool wording/behavior for scheduled tasks ──

    [Fact]
    public async Task TaskSend_RunningScheduled_DeliversMessage()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Scheduled, "s", parentTaskId: null);
        t.AttachSteering(new SteeringInbox());

        var result = await new TaskSendTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}","message":"adjust the run"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("delivered", result.Content);
        Assert.Contains("adjust the run", t.Steering!.DrainAll());
    }

    [Fact]
    public async Task TaskSend_ShellTask_StillRejected()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);

        var result = await new TaskSendTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}","message":"x"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("cannot be steered", result.Content);
    }

    [Fact]
    public async Task TaskSend_UnknownId_ReportsNotFoundSafely()
    {
        var mgr = NewManager();

        var result = await new TaskSendTool().ExecuteAsync(
            Input("""{"task_id":"task-9999","message":"x"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("not found", result.Content);
    }
}
