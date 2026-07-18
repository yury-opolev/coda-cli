using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

public sealed class TuiHostTests
{
    [Fact]
    public async Task Fullscreen_failure_falls_back_in_order_and_reports_diagnostic()
    {
        var runner = new ScriptedRunner(
            new Dictionary<TuiRunMode, Queue<TuiShellExit>>
            {
                [TuiRunMode.Fullscreen] = new([TuiShellExit.Failed(new InvalidOperationException("full"))]),
                [TuiRunMode.Inline] = new([TuiShellExit.Failed(new InvalidOperationException("inline"))]),
                [TuiRunMode.Spectre] = new([TuiShellExit.Exited]),
            });
        var error = new StringWriter();
        var host = new TuiHost(runner, error);

        await host.RunAsync(TuiRunMode.Fullscreen, ComposerState.Empty);

        Assert.Equal([TuiRunMode.Fullscreen, TuiRunMode.Inline, TuiRunMode.Spectre], runner.Attempts);
        Assert.Contains("full-screen failed", error.ToString());
        Assert.Contains("inline failed", error.ToString());
    }

    [Fact]
    public async Task Mode_switch_preserves_composer_state()
    {
        var draft = new ComposerState("draft", 2, ["one"], 1, false);
        var runner = new ScriptedRunner(
            new Dictionary<TuiRunMode, Queue<TuiShellExit>>
            {
                [TuiRunMode.Inline] = new([TuiShellExit.SwitchTo(TuiRunMode.Fullscreen, draft)]),
                [TuiRunMode.Fullscreen] = new([TuiShellExit.Exited]),
            });
        var host = new TuiHost(runner, new StringWriter());

        await host.RunAsync(TuiRunMode.Inline, ComposerState.Empty);

        Assert.Equal([TuiRunMode.Inline, TuiRunMode.Fullscreen], runner.Attempts);
        Assert.Equal(draft, runner.States[1]);
        Assert.Same(runner.SessionIdentities[0], runner.SessionIdentities[1]);
    }

    [Fact]
    public async Task Switch_to_a_failing_mode_falls_back_from_the_requested_mode()
    {
        var draft = new ComposerState("carried", 3, [], 0, false);
        var runner = new ScriptedRunner(
            new Dictionary<TuiRunMode, Queue<TuiShellExit>>
            {
                [TuiRunMode.Inline] = new([
                    TuiShellExit.SwitchTo(TuiRunMode.Fullscreen, draft),
                    TuiShellExit.Exited,
                ]),
                [TuiRunMode.Fullscreen] = new([TuiShellExit.Failed(new InvalidOperationException("boom"))]),
            });
        var error = new StringWriter();
        var host = new TuiHost(runner, error);

        await host.RunAsync(TuiRunMode.Inline, ComposerState.Empty);

        // Inline -> switch Fullscreen -> Fullscreen fails -> fall back to Inline (from Fullscreen ladder).
        Assert.Equal([TuiRunMode.Inline, TuiRunMode.Fullscreen, TuiRunMode.Inline], runner.Attempts);
        Assert.Contains("full-screen failed", error.ToString());
        Assert.Equal(draft, runner.States[2]);
    }

    [Fact]
    public async Task Cancelled_host_exits_without_running_a_mode()
    {
        var runner = new ScriptedRunner(new Dictionary<TuiRunMode, Queue<TuiShellExit>>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var host = new TuiHost(runner, new StringWriter());

        await host.RunAsync(TuiRunMode.Inline, ComposerState.Empty, cts.Token);

        Assert.Empty(runner.Attempts);
    }

    [Fact]
    public async Task Mode_changed_event_is_published_before_each_attempt()
    {
        var runner = new ScriptedRunner(
            new Dictionary<TuiRunMode, Queue<TuiShellExit>>
            {
                [TuiRunMode.Inline] = new([TuiShellExit.Failed(new InvalidOperationException("x"))]),
                [TuiRunMode.Spectre] = new([TuiShellExit.Exited]),
            });
        var events = new RecordingUiEvents();
        var host = new TuiHost(runner, new StringWriter(), events);

        await host.RunAsync(TuiRunMode.Inline, ComposerState.Empty);

        var modes = events.Events.OfType<Coda.Tui.Ui.Events.ModeChangedEvent>().Select(e => e.Mode).ToList();
        Assert.Equal(["inline", "spectre"], modes);
    }

    [Fact]
    public async Task Terminal_gui_runner_disposes_application_after_shell_factory_failure()
    {
        var disposed = false;
        EventHandler<EventArgs<IApplication>> handler = (_, _) => disposed = true;
        Application.InstanceDisposed += handler;
        try
        {
            var runner = new TerminalGuiModeRunner(
                shellFactory: (_, _, _) => throw new InvalidOperationException("render setup failed"),
                spectreRunner: (_, _) => Task.FromResult(TuiShellExit.Exited),
                plainRunner: (_, _) => Task.FromResult(TuiShellExit.Exited),
                applicationFactory: () =>
                {
                    var app = Application.Create();
                    app.ForceInlinePosition = new Point(0, 0);
                    return app;
                },
                driverName: DriverRegistry.Names.ANSI);

            var result = await runner.RunAsync(TuiRunMode.Inline, ComposerState.Empty, CancellationToken.None);

            Assert.Equal(TuiShellExitKind.Failed, result.Kind);
            Assert.True(disposed);
        }
        finally
        {
            Application.InstanceDisposed -= handler;
        }
    }

    [Fact]
    public void Managed_process_exit_requests_terminal_stop()
    {
        var requested = false;
        using var registration = new TerminalProcessExitRegistration(() => requested = true);

        registration.InvokeForTest();

        Assert.True(requested);
    }

    [Fact]
    public void Managed_process_exit_callback_is_idempotent_and_swallows_secondary_failures()
    {
        var calls = 0;
        using var registration = new TerminalProcessExitRegistration(() =>
        {
            calls++;
            throw new InvalidOperationException("secondary");
        });

        registration.InvokeForTest();
        registration.InvokeForTest();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Ctrl_c_interrupts_active_turn_before_exit()
    {
        var active = true;
        var exited = false;
        using var registration = new ConsoleCancellationRegistration(
            () =>
            {
                if (!active)
                {
                    return false;
                }

                active = false;
                return true;
            },
            () => exited = true);

        Assert.True(registration.HandleForTest());
        Assert.False(active);
        Assert.False(exited);
    }

    [Fact]
    public void Idle_ctrl_c_does_not_interrupt_and_never_exits()
    {
        var exited = false;
        using var registration = new ConsoleCancellationRegistration(
            () => false,
            () => exited = true);

        Assert.False(registration.HandleForTest());
        Assert.False(exited);
    }

    [Fact]
    public async Task Clean_exit_with_a_cleanup_error_reports_a_diagnostic_without_falling_back()
    {
        var cleanup = new InvalidOperationException("teardown blew up");
        var runner = new ScriptedRunner(
            new Dictionary<TuiRunMode, Queue<TuiShellExit>>
            {
                [TuiRunMode.Inline] = new([TuiShellExit.Exited.WithCleanupError(cleanup)]),
            });
        var error = new StringWriter();
        var host = new TuiHost(runner, error);

        await host.RunAsync(TuiRunMode.Inline, ComposerState.Empty);

        Assert.Equal([TuiRunMode.Inline], runner.Attempts);
        Assert.Contains("teardown blew up", error.ToString());
        Assert.DoesNotContain("failed", error.ToString());
    }

    [Fact]
    public async Task Switch_with_a_cleanup_error_still_switches_and_reports_a_diagnostic()
    {
        var cleanup = new InvalidOperationException("teardown blew up");
        var draft = new ComposerState("draft", 2, [], 0, false);
        var runner = new ScriptedRunner(
            new Dictionary<TuiRunMode, Queue<TuiShellExit>>
            {
                [TuiRunMode.Inline] = new([TuiShellExit.SwitchTo(TuiRunMode.Fullscreen, draft).WithCleanupError(cleanup)]),
                [TuiRunMode.Fullscreen] = new([TuiShellExit.Exited]),
            });
        var error = new StringWriter();
        var host = new TuiHost(runner, error);

        await host.RunAsync(TuiRunMode.Inline, ComposerState.Empty);

        Assert.Equal([TuiRunMode.Inline, TuiRunMode.Fullscreen], runner.Attempts);
        Assert.Equal(draft, runner.States[1]);
        Assert.Contains("teardown blew up", error.ToString());
    }

    private sealed class ScriptedRunner(Dictionary<TuiRunMode, Queue<TuiShellExit>> scripts) : ITuiModeRunner
    {
        private readonly object identity = new();

        public List<TuiRunMode> Attempts { get; } = [];

        public List<ComposerState> States { get; } = [];

        public List<object> SessionIdentities { get; } = [];

        public Task<TuiShellExit> RunAsync(TuiRunMode mode, ComposerState composer, CancellationToken cancellationToken)
        {
            this.Attempts.Add(mode);
            this.States.Add(composer);
            this.SessionIdentities.Add(this.identity);
            return Task.FromResult(scripts[mode].Dequeue());
        }
    }
}
