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
