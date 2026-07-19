using Coda.Tui;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class InteractiveProgramTests
{
    [Fact]
    public void Semantic_startup_renders_the_helpful_banner_into_the_transcript()
    {
        var events = new RecordingUiEvents();
        var built = TestAppBuilder.BuildApp(events: events, workingDirectory: @"C:\work");
        var adapter = new UiAnsiConsoleAdapter(events, width: 100, height: 30);
        built.Context.SetModeEnvironment(
            adapter,
            PlainUiPromptService.Instance,
            events,
            semanticUiEnabled: true);

        InteractiveProgram.RenderStartupBanner(
            built.Context,
            TuiRunMode.Fullscreen,
            connectedProvider: "github-copilot");

        var text = string.Join(
            Environment.NewLine,
            events.Events.OfType<CommandOutputEvent>().Select(item => item.Text));
        Assert.Contains("Welcome to Coda", text);
        Assert.Contains($"v{Branding.Version}", text);
        Assert.Contains(@"cwd: C:\work", text);
        Assert.Contains("provider: github-copilot", text);
        Assert.Contains($"model: {built.Context.Session.Model}", text);
        Assert.Contains("/help", text);
        Assert.Contains("/exit", text);
    }

    [Fact]
    public void Plain_startup_remains_banner_free()
    {
        var events = new RecordingUiEvents();
        var built = TestAppBuilder.BuildApp(events: events);
        var adapter = new UiAnsiConsoleAdapter(events, width: 100, height: 30);
        built.Context.SetModeEnvironment(
            adapter,
            PlainUiPromptService.Instance,
            events,
            semanticUiEnabled: true);

        InteractiveProgram.RenderStartupBanner(
            built.Context,
            TuiRunMode.Plain,
            connectedProvider: "github-copilot");

        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Redirected_output_uses_plain_and_preserves_script_text()
    {
        var input = new StringReader("hello\n");
        var output = new StringWriter();
        var error = new StringWriter();
        var caps = new TerminalCapabilities(false, true, 120, 40, true);
        var runner = new RecordingInteractiveSessionRunner(output);

        var code = await InteractiveProgram.RunAsync(
            [],
            input,
            output,
            error,
            new FixedCapabilitiesProvider(caps),
            CancellationToken.None,
            runner);

        Assert.Equal(0, code);
        Assert.Equal(TuiRunMode.Plain, runner.Mode);
        Assert.Equal("plain hello" + Environment.NewLine, output.ToString());
        Assert.DoesNotContain("\u001b[", output.ToString());
    }

    [Fact]
    public async Task Explicit_mode_too_small_returns_usage_error_without_starting_terminal()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new RecordingInteractiveSessionRunner(output);

        var code = await InteractiveProgram.RunAsync(
            ["--tui=fullscreen"],
            new StringReader(string.Empty),
            output,
            error,
            new FixedCapabilitiesProvider(new(false, false, 50, 10, true)),
            CancellationToken.None,
            runner);

        Assert.Equal(2, code);
        Assert.Contains("at least 60 columns by 12 rows", error.ToString());
        Assert.Null(runner.Mode);
    }

    [Fact]
    public async Task Invalid_tui_value_returns_usage_error_without_starting_terminal()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new RecordingInteractiveSessionRunner(output);

        var code = await InteractiveProgram.RunAsync(
            ["--tui=windowed"],
            new StringReader(string.Empty),
            output,
            error,
            new FixedCapabilitiesProvider(new(false, false, 120, 40, true)),
            CancellationToken.None,
            runner);

        Assert.Equal(2, code);
        Assert.Contains("Invalid --tui value 'windowed'", error.ToString());
        Assert.Null(runner.Mode);
    }

    [Fact]
    public async Task Explicit_plain_wins_over_interactive_terminal()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new RecordingInteractiveSessionRunner(output);

        var code = await InteractiveProgram.RunAsync(
            ["--plain"],
            new StringReader("hi\n"),
            output,
            error,
            new FixedCapabilitiesProvider(new(false, false, 120, 40, true)),
            CancellationToken.None,
            runner);

        Assert.Equal(0, code);
        Assert.Equal(TuiRunMode.Plain, runner.Mode);
    }

    [Fact]
    public async Task Plain_composition_serializes_dispatched_command_output_without_escapes()
    {
        // Exercises the real plain-mode composition wiring: the command console publishes into the
        // shared mailbox, the single UiActor drains it, and the PlainOutputRenderer serializes the
        // output — proving slash dispatch flows through the actor with no direct Console writes and
        // no ANSI escapes.
        var output = new StringWriter();
        using var mailbox = new UiEventMailbox(64);
        var actorPrompts = new ActorUiPromptService(mailbox);
        var console = new UiAnsiConsoleAdapter(mailbox, 80, 24);

        var (app, _) = PlainCompositionFactory.Build(console, mailbox);

        using var actorCts = new CancellationTokenSource();
        var actor = new UiActor(
            mailbox,
            NullUiFrameSink.Instance,
            UiSessionSnapshot.Empty,
            new PlainOutputRenderer(output),
            actorPrompts);
        var actorTask = actor.RunAsync(actorCts.Token);

        await app.RunPlainAsync(new StringReader("/version\n"), CancellationToken.None);

        // Let the actor drain the mailbox before tearing it down.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (mailbox.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(50);
        actorCts.Cancel();
        await actorTask;
        app.Dispose();

        var text = output.ToString();
        Assert.Contains(Branding.ProductName, text);
        Assert.Contains(Branding.Version, text);
        Assert.DoesNotContain("\u001b[", text);
    }

    [Fact]
    public async Task Plain_composition_drains_final_command_output_before_clean_shutdown()
    {
        // Reproduces the production plain-mode clean shutdown: after RunPlainAsync returns on EOF the
        // runner must drain the actor before cancelling it, otherwise the just-published /version output
        // (queued or mid-observer) is dropped. This test performs NO manual mailbox-empty wait; it relies
        // solely on the FlushAsync barrier and uses a scheduling-delayed observer plus many iterations so
        // that a naive Cancel-then-await shutdown would drop output. Every iteration must emit /version.
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var output = new StringWriter();
            using var mailbox = new UiEventMailbox(64);
            var actorPrompts = new ActorUiPromptService(mailbox);
            var console = new UiAnsiConsoleAdapter(mailbox, 80, 24);

            var (app, _) = PlainCompositionFactory.Build(console, mailbox);

            using var actorCts = new CancellationTokenSource();
            var actor = new UiActor(
                mailbox,
                NullUiFrameSink.Instance,
                UiSessionSnapshot.Empty,
                new SchedulingDelayedObserver(new PlainOutputRenderer(output)),
                actorPrompts);
            var actorTask = actor.RunAsync(actorCts.Token);

            await app.RunPlainAsync(new StringReader("/version\n"), CancellationToken.None);

            // No mailbox-empty polling: drain deterministically through the barrier, exactly as the
            // production runner does before cancelling the actor on a clean exit.
            using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await actor.FlushAsync(flushCts.Token);

            actorCts.Cancel();
            await actorTask;
            app.Dispose();

            var text = output.ToString();
            Assert.Contains(Branding.ProductName, text);
            Assert.Contains(Branding.Version, text);
            Assert.DoesNotContain("\u001b[", text);
        }
    }

    /// <summary>Yields before delegating so events are dequeued but the observer is still running.</summary>
    private sealed class SchedulingDelayedObserver(IUiEventObserver inner) : IUiEventObserver
    {
        public async ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken)
        {
            await Task.Yield();
            await inner.ApplyEventAsync(uiEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FixedCapabilitiesProvider(TerminalCapabilities capabilities)
        : ITerminalCapabilitiesProvider
    {
        public TerminalCapabilities Get() => capabilities;
    }

    private sealed class RecordingInteractiveSessionRunner(TextWriter output)
        : IInteractiveSessionRunner
    {
        public TuiRunMode? Mode { get; private set; }

        public async Task<int> RunAsync(
            TuiRunMode mode,
            TuiLaunchOptions options,
            TextReader input,
            TextWriter error,
            CancellationToken cancellationToken)
        {
            this.Mode = mode;
            var line = await input.ReadLineAsync(cancellationToken);
            output.WriteLine($"{mode.ToString().ToLowerInvariant()} {line}");
            return 0;
        }
    }
}

/// <summary>
/// Lifecycle tests for the centralized clean-exit session card: it renders once, only after a clean
/// host return (never during a mode switch or a faulted host run), uses the injected
/// <see cref="TimeProvider"/> for the duration, degrades to a "not saved" card without a session id,
/// and treats a render/output failure as best-effort so a clean exit still returns 0.
/// </summary>
public sealed class DefaultInteractiveSessionRunnerExitSummaryTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    private static DefaultInteractiveSessionRunner NewRunner(
        Action<IAnsiConsole, SessionExitSnapshot> renderSeam, TimeProvider? time = null) =>
        new(TextWriter.Null, new TerminalCapabilities(false, true, 120, 40, true), time ?? TimeProvider.System, renderSeam);

    private static Task<int> RunHostAsync(
        DefaultInteractiveSessionRunner runner,
        TuiHost host,
        CommandContext context,
        IAnsiConsole exitConsole,
        TuiRunMode mode = TuiRunMode.Plain,
        DateTimeOffset? startedAt = null,
        Func<Task>? finalize = null) =>
        runner.RunHostToCleanExitAsync(
            host,
            mode,
            ComposerState.Empty,
            context,
            exitConsole,
            startedAt ?? Start,
            drainDispatch: _ => Task.CompletedTask,
            flushUi: _ => Task.CompletedTask,
            finalize: finalize ?? (() => Task.CompletedTask),
            hostToken: CancellationToken.None,
            cancellationToken: CancellationToken.None);

    [Fact]
    public async Task Clean_host_exit_renders_the_session_card_once()
    {
        var renderer = new RecordingExitRenderer();
        var runner = NewRunner(renderer.Render);
        var modeRunner = new ScriptedModeRunner(TuiShellExit.Exited);
        var host = new TuiHost(modeRunner, TextWriter.Null);
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var code = await RunHostAsync(runner, host, context, console);

        Assert.Equal(0, code);
        Assert.Equal(1, renderer.Count);
    }

    [Fact]
    public async Task Mode_switches_do_not_render_intermediate_cards()
    {
        var renderer = new RecordingExitRenderer();
        var runner = NewRunner(renderer.Render);
        var modeRunner = new ScriptedModeRunner(
            TuiShellExit.SwitchTo(TuiRunMode.Inline, ComposerState.Empty),
            TuiShellExit.SwitchTo(TuiRunMode.Spectre, ComposerState.Empty),
            TuiShellExit.Exited);
        var host = new TuiHost(modeRunner, TextWriter.Null);
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        await RunHostAsync(runner, host, context, console, mode: TuiRunMode.Fullscreen);

        Assert.Equal(3, modeRunner.Runs);
        Assert.Equal(1, renderer.Count);
    }

    [Theory]
    [InlineData("eof")]
    [InlineData("command")]
    [InlineData("chord")]
    public async Task Clean_exit_seams_all_render_exactly_one_card(string seam)
    {
        var renderer = new RecordingExitRenderer();
        var runner = NewRunner(renderer.Render);
        var modeRunner = seam == "chord"
            ? new ScriptedModeRunner(
                TuiShellExit.SwitchTo(TuiRunMode.Plain, ComposerState.Empty), TuiShellExit.Exited)
            : new ScriptedModeRunner(TuiShellExit.Exited);
        var host = new TuiHost(modeRunner, TextWriter.Null);
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var code = await RunHostAsync(runner, host, context, console);

        Assert.Equal(0, code);
        Assert.Equal(1, renderer.Count);
    }

    [Fact]
    public async Task Faulted_host_run_does_not_render_a_card_but_still_finalizes()
    {
        var renderer = new RecordingExitRenderer();
        var runner = NewRunner(renderer.Render);
        var modeRunner = new ScriptedModeRunner(new InvalidOperationException("host fault"));
        var host = new TuiHost(modeRunner, TextWriter.Null);
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        var finalized = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunHostAsync(runner, host, context, console, finalize: () => { finalized = true; return Task.CompletedTask; }));

        Assert.Empty(renderer.Snapshots);
        Assert.True(finalized);
    }

    [Fact]
    public void Exit_summary_for_unsaved_session_says_not_saved()
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var runner = NewRunner(ExitSummaryRenderer.Render);
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.SessionId = null;

        runner.RenderExitSummary(context, console, Start);

        Assert.Contains("not saved", console.Output);
        Assert.DoesNotContain("--resume", console.Output);
    }

    [Fact]
    public void Exit_summary_uses_injected_time_provider_for_duration()
    {
        var renderer = new RecordingExitRenderer();
        var runner = NewRunner(renderer.Render, new FixedTimeProvider(Start.AddSeconds(125)));
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        runner.RenderExitSummary(context, new TestConsole(), Start);

        Assert.Single(renderer.Snapshots);
        Assert.Equal(TimeSpan.FromSeconds(125), renderer.Snapshots[0].Duration);
    }

    [Fact]
    public void Exit_summary_render_failure_is_best_effort()
    {
        var runner = NewRunner((_, _) => throw new IOException("console gone"));
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        var exception = Record.Exception(() => runner.RenderExitSummary(context, new TestConsole(), Start));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Render_failure_keeps_the_exit_code_zero()
    {
        var runner = NewRunner((_, _) => throw new IOException("console gone"));
        var modeRunner = new ScriptedModeRunner(TuiShellExit.Exited);
        var host = new TuiHost(modeRunner, TextWriter.Null);
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var code = await RunHostAsync(runner, host, context, console);

        Assert.Equal(0, code);
    }

    private sealed class RecordingExitRenderer
    {
        public List<SessionExitSnapshot> Snapshots { get; } = [];

        public int Count => this.Snapshots.Count;

        public void Render(IAnsiConsole console, SessionExitSnapshot snapshot) => this.Snapshots.Add(snapshot);
    }

    private sealed class ScriptedModeRunner : ITuiModeRunner
    {
        private readonly Queue<TuiShellExit> outcomes;
        private readonly Exception? fault;

        public ScriptedModeRunner(params TuiShellExit[] outcomes) => this.outcomes = new Queue<TuiShellExit>(outcomes);

        public ScriptedModeRunner(Exception fault)
        {
            this.fault = fault;
            this.outcomes = new Queue<TuiShellExit>();
        }

        public int Runs { get; private set; }

        public Task<TuiShellExit> RunAsync(TuiRunMode mode, ComposerState composer, CancellationToken cancellationToken)
        {
            this.Runs++;
            return this.fault is not null
                ? Task.FromException<TuiShellExit>(this.fault)
                : Task.FromResult(this.outcomes.Dequeue());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
