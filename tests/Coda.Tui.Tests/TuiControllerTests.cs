using Coda.Tui.Repl;
using Coda.Tui.Ui;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class TuiControllerTests
{
    [Fact]
    public async Task Interrupt_action_calls_interrupt_without_requesting_exit()
    {
        var interrupts = 0;
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () =>
            {
                interrupts++;
                return true;
            },
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        await controller.HandleActionAsync(UiAction.Interrupt);

        Assert.Equal(1, interrupts);
        Assert.False(controller.ExitRequested);
    }

    [Fact]
    public async Task Submit_schedules_dispatch_off_the_ui_thread_and_disables_submit_while_active()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0;
        var controller = new TuiController(
            dispatch: async (_, _) =>
            {
                Interlocked.Increment(ref dispatches);
                started.TrySetResult();
                await release.Task;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("hello");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(controller.HasActiveWork);

        // A second submit while the first is in-flight is rejected coherently.
        controller.OnSubmitted("second");
        Assert.Equal(1, dispatches);

        release.SetResult();
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(controller.HasActiveWork);
    }

    [Fact]
    public async Task Dispatch_failure_publishes_an_error_and_re_enables_submit()
    {
        var events = new RecordingUiEvents();
        var controller = new TuiController(
            dispatch: (_, _) => Task.FromException(new InvalidOperationException("dispatch failed")),
            tryInterrupt: () => false,
            publisher: events,
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("boom");
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(controller.HasActiveWork);
        var error = Assert.IsType<AgentErrorEvent>(Assert.Single(events.Events));
        Assert.Contains("dispatch failed", error.Message);
    }

    [Fact]
    public async Task Submit_is_blocked_while_startup_pending_and_re_enabled_after_completion()
    {
        var dispatched = 0;
        var controller = new TuiController(
            dispatch: (_, _) =>
            {
                Interlocked.Increment(ref dispatched);
                return Task.CompletedTask;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        // While startup is pending the composer/plain loop cannot submit a turn, so the bounded mailbox
        // cannot fill before its actor is running and no turn races MCP/setup initialization.
        controller.BeginStartup();
        Assert.True(controller.StartupPending);

        controller.OnSubmitted("blocked");
        Assert.Null(controller.CurrentDispatch);
        Assert.Equal(0, dispatched);

        // Once startup completes, submission is re-enabled.
        controller.CompleteStartup();
        Assert.False(controller.StartupPending);

        controller.OnSubmitted("allowed");
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, dispatched);
    }

    [Fact]
    public async Task Exit_action_sets_exit_request_and_stops_the_shell()
    {
        var shell = new FakeShellHandle(ComposerState.Empty);
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.AttachShell(shell, TuiRunMode.Inline);

        await controller.HandleActionAsync(UiAction.Exit);

        Assert.True(controller.ExitRequested);
        Assert.Equal(TuiShellExitKind.Exit, shell.LastStop!.Kind);
    }

    [Fact]
    public async Task Toggle_mode_exports_the_composer_and_requests_a_mode_switch()
    {
        var draft = new ComposerState("draft-text", 5, ["prev"], 1, false);
        var shell = new FakeShellHandle(draft);
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.AttachShell(shell, TuiRunMode.Inline);

        await controller.HandleActionAsync(UiAction.ToggleMode);

        Assert.Equal(TuiRunMode.Fullscreen, controller.PendingModeSwitch);
        Assert.Equal(draft, controller.CurrentComposer);
        Assert.Equal(TuiShellExitKind.SwitchMode, shell.LastStop!.Kind);
        Assert.Equal(TuiRunMode.Fullscreen, shell.LastStop.NextMode);
        Assert.Equal(draft, shell.LastStop.Composer);
    }

    [Fact]
    public async Task Toggle_mode_from_full_screen_returns_to_inline()
    {
        var shell = new FakeShellHandle(ComposerState.Empty);
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.AttachShell(shell, TuiRunMode.Fullscreen);

        await controller.HandleActionAsync(UiAction.ToggleMode);

        Assert.Equal(TuiRunMode.Inline, controller.PendingModeSwitch);
    }

    [Fact]
    public void Captured_snapshot_survives_shell_recreation()
    {
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        var snapshot = UiSessionSnapshot.Empty with { Model = "captured-model" };

        controller.CaptureSnapshot(snapshot);

        Assert.Same(snapshot, controller.CurrentSnapshot);
        Assert.Equal("captured-model", controller.CurrentSnapshot.Model);
    }

    [Fact]
    public async Task Pending_prompt_survives_a_mode_switch()
    {
        var prompt = UiPromptRequest.Confirm("Proceed?", true);
        var snapshot = UiSessionSnapshot.Empty with { PendingPrompt = prompt };
        var shell = new FakeShellHandle(ComposerState.Empty);
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.CaptureSnapshot(snapshot);
        controller.AttachShell(shell, TuiRunMode.Inline);

        await controller.HandleActionAsync(UiAction.ToggleMode);

        Assert.Same(prompt, controller.CurrentSnapshot.PendingPrompt);
    }

    [Fact]
    public void Begin_startup_publishes_starting_indicator_and_complete_clears_it()
    {
        var events = new RecordingUiEvents();
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: events,
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.BeginStartup();
        controller.CompleteStartup();

        var set = Assert.IsType<ActiveOperationChangedEvent>(events.Events[0]);
        Assert.NotNull(set.Operation);
        Assert.Equal("startup", set.Operation!.Kind);

        var clear = Assert.IsType<ActiveOperationChangedEvent>(events.Events[1]);
        Assert.Null(clear.Operation);
    }

    [Fact]
    public async Task Dispatch_result_exit_stops_the_attached_shell_with_exit()
    {
        // The Terminal.Gui shells submit `/exit` through OnSubmitted, whose dispatch returns
        // CommandResult.Exit; the controller must honor that result and stop the shell, exactly as the
        // plain/Spectre loops already break on ShouldExit.
        var shell = new FakeShellHandle(ComposerState.Empty);
        var controller = new TuiController(
            dispatch: (_, _) => Task.FromResult(CommandResult.Exit),
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.AttachShell(shell, TuiRunMode.Inline);

        controller.OnSubmitted("/exit");
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(controller.ExitRequested);
        Assert.Equal(TuiShellExitKind.Exit, shell.LastStop!.Kind);
    }

    [Fact]
    public async Task Dispatch_result_continue_neither_exits_nor_stops_the_shell()
    {
        var shell = new FakeShellHandle(ComposerState.Empty);
        var controller = new TuiController(
            dispatch: (_, _) => Task.FromResult(CommandResult.Continue),
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.AttachShell(shell, TuiRunMode.Inline);

        controller.OnSubmitted("just a prompt");
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(controller.ExitRequested);
        Assert.Null(shell.LastStop);
    }

    [Fact]
    public async Task WaitForDispatch_observes_a_delayed_dispatch_before_returning()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While the dispatch is blocked, the wait must not complete — shutdown must observe it first.
        var wait = controller.WaitForDispatchAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        release.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitForDispatch_returns_immediately_when_idle()
    {
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        await controller.WaitForDispatchAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitForDispatch_from_within_the_dispatch_does_not_deadlock()
    {
        TuiController controller = null!;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller = new TuiController(
            dispatch: async (_, ct) =>
            {
                // Self-join guard: awaiting our own dispatch would deadlock, so it must return at once.
                await controller.WaitForDispatchAsync(ct);
                completed.TrySetResult();
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run");

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Faulting_dispatch_during_shutdown_never_publishes_into_the_disposed_mailbox()
    {
        using var hostCts = new CancellationTokenSource();
        var publisher = new LateThrowingPublisher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var controller = new TuiController(
            dispatch: async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("boom after shutdown");
            },
            tryInterrupt: () => false,
            publisher: publisher,
            initialSnapshot: UiSessionSnapshot.Empty,
            hostCancellationToken: hostCts.Token);

        controller.OnSubmitted("run");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispatch = controller.CurrentDispatch;
        Assert.NotNull(dispatch);

        // Shutdown ordering: host cancelled and the mailbox disposed, THEN the dispatch faults.
        hostCts.Cancel();
        publisher.MarkDisposed();
        release.SetResult();

        await dispatch!.WaitAsync(TimeSpan.FromSeconds(5));

        // The faulting dispatch swallowed the fault without publishing into the disposed mailbox.
        Assert.Equal(TaskStatus.RanToCompletion, dispatch.Status);
        Assert.DoesNotContain(publisher.Published, e => e is AgentErrorEvent);
    }

    [Fact]
    public async Task Faulting_dispatch_swallows_object_disposed_from_a_late_publish()
    {
        var publisher = new LateThrowingPublisher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // No host cancellation: exercise the narrow ObjectDisposedException race where the mailbox is
        // disposed while the host token has not yet been observed as cancelled.
        var controller = new TuiController(
            dispatch: async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("boom");
            },
            tryInterrupt: () => false,
            publisher: publisher,
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispatch = controller.CurrentDispatch;
        Assert.NotNull(dispatch);

        publisher.MarkDisposed();
        release.SetResult();

        await dispatch!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TaskStatus.RanToCompletion, dispatch.Status);
    }

    private sealed class LateThrowingPublisher : IUiEventPublisher
    {
        private volatile bool disposed;

        public List<UiEvent> Published { get; } = new();

        public void MarkDisposed() => this.disposed = true;

        public void Publish(UiEvent uiEvent)
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(nameof(LateThrowingPublisher));
            }

            this.Published.Add(uiEvent);
        }
    }

    private sealed class FakeShellHandle(ComposerState composer) : ITuiShellHandle
    {
        public TuiShellExit? LastStop { get; private set; }

        public ComposerState ExportComposerState() => composer;

        public void RequestStop(TuiShellExit outcome) => this.LastStop = outcome;
    }
}
