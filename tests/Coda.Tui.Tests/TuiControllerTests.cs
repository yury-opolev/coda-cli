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
    public void Idle_ctrl_c_publishes_a_notification_and_does_not_request_exit()
    {
        var events = new RecordingUiEvents();
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: events,
            initialSnapshot: UiSessionSnapshot.Empty);

        var interrupted = controller.HandleCtrlC();

        Assert.False(interrupted);
        Assert.False(controller.ExitRequested);
        var note = Assert.IsType<NotificationEvent>(Assert.Single(events.Events));
        Assert.Equal("Nothing is running; use /exit or Ctrl+D to exit.", note.Message);
    }

    [Fact]
    public void Active_ctrl_c_interrupts_without_notifying_or_exiting()
    {
        var events = new RecordingUiEvents();
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => true,
            publisher: events,
            initialSnapshot: UiSessionSnapshot.Empty);

        var interrupted = controller.HandleCtrlC();

        Assert.True(interrupted);
        Assert.Empty(events.Events);
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

    private sealed class FakeShellHandle(ComposerState composer) : ITuiShellHandle
    {
        public TuiShellExit? LastStop { get; private set; }

        public ComposerState ExportComposerState() => composer;

        public void RequestStop(TuiShellExit outcome) => this.LastStop = outcome;
    }
}
