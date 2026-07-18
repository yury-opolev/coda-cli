using Coda.Tui;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

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
