namespace Coda.TerminalGuiSpike;

/// <summary>The two Terminal.Gui host models the spike can run under.</summary>
internal enum SpikeMode
{
    Inline,
    Fullscreen,
}

/// <summary>The compatibility scenarios exercised by the harness.</summary>
internal enum SpikeScenario
{
    Stream,
    Unicode,
    Paste,
    Resize,
    Cancel,
    MouseOff,
    ManagedCrash,
}

/// <summary>
/// Parsed command-line options for the compatibility spike. Parsing is strict: unknown flags,
/// missing values, and out-of-range enum values all fail with <see cref="Error"/> set so the
/// entry point can exit with code 2.
/// </summary>
internal sealed class HarnessOptions
{
    /// <summary>The default streaming duration when <c>--duration-ms</c> is not supplied (ten seconds).</summary>
    public const int DefaultStreamDurationMs = 10_000;

    public SpikeMode Mode { get; private init; } = SpikeMode.Inline;

    public SpikeScenario Scenario { get; private init; } = SpikeScenario.Stream;

    /// <summary>
    /// Optional test-only override for the streaming duration. The documented default remains
    /// ten seconds; a short value lets an automated smoke exercise the same code path quickly.
    /// </summary>
    public int? DurationMs { get; private init; }

    /// <summary>Screen width used for the headless (isolated ANSI-buffer) run.</summary>
    public int Width { get; private init; } = 100;

    /// <summary>Screen height used for the headless (isolated ANSI-buffer) run.</summary>
    public int Height { get; private init; } = 30;

    /// <summary>Forces the isolated headless driver even when a real terminal is attached.</summary>
    public bool ForceHeadless { get; private init; }

    /// <summary>True when <c>--help</c>/<c>-h</c> was requested.</summary>
    public bool ShowHelp { get; private init; }

    /// <summary>Non-null when parsing failed; the message is printed to stderr before exit code 2.</summary>
    public string? Error { get; private init; }

    /// <summary>The effective streaming duration in milliseconds.</summary>
    public int EffectiveStreamDurationMs => this.DurationMs ?? DefaultStreamDurationMs;

    public static HarnessOptions Parse(string[] args)
    {
        var mode = SpikeMode.Inline;
        var scenario = SpikeScenario.Stream;
        int? durationMs = null;
        var width = 100;
        var height = 30;
        var forceHeadless = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                case "/?":
                    return new HarnessOptions { ShowHelp = true };

                case "--mode":
                    if (!TryTakeValue(args, ref i, out var modeValue))
                    {
                        return Fail("--mode requires a value (inline|fullscreen).");
                    }

                    if (!TryParseMode(modeValue, out mode))
                    {
                        return Fail($"invalid --mode '{modeValue}'. Expected inline|fullscreen.");
                    }

                    break;

                case "--scenario":
                    if (!TryTakeValue(args, ref i, out var scenarioValue))
                    {
                        return Fail("--scenario requires a value.");
                    }

                    if (!TryParseScenario(scenarioValue, out scenario))
                    {
                        return Fail(
                            $"invalid --scenario '{scenarioValue}'. Expected " +
                            "stream|unicode|paste|resize|cancel|mouse-off|managed-crash.");
                    }

                    break;

                case "--duration-ms":
                    if (!TryTakeValue(args, ref i, out var durationValue))
                    {
                        return Fail("--duration-ms requires an integer value.");
                    }

                    if (!int.TryParse(durationValue, out var parsedDuration) || parsedDuration <= 0)
                    {
                        return Fail($"invalid --duration-ms '{durationValue}'. Expected a positive integer.");
                    }

                    durationMs = parsedDuration;
                    break;

                case "--width":
                    if (!TryTakeValue(args, ref i, out var widthValue) ||
                        !int.TryParse(widthValue, out width) || width <= 0)
                    {
                        return Fail("--width requires a positive integer.");
                    }

                    break;

                case "--height":
                    if (!TryTakeValue(args, ref i, out var heightValue) ||
                        !int.TryParse(heightValue, out height) || height <= 0)
                    {
                        return Fail("--height requires a positive integer.");
                    }

                    break;

                case "--headless":
                    forceHeadless = true;
                    break;

                default:
                    return Fail($"unknown argument '{arg}'. Run with --help for usage.");
            }
        }

        return new HarnessOptions
        {
            Mode = mode,
            Scenario = scenario,
            DurationMs = durationMs,
            Width = width,
            Height = height,
            ForceHeadless = forceHeadless,
        };
    }

    /// <summary>Whether the harness should use the isolated headless driver rather than a live terminal.</summary>
    public bool RunHeadless() =>
        this.ForceHeadless || this.DurationMs.HasValue || Console.IsInputRedirected || Console.IsOutputRedirected;

    public static string HelpText() =>
        """
        Coda Terminal.Gui v2 compatibility spike

        Usage:
          dotnet run --project samples/Coda.TerminalGuiSpike -- --mode <mode> --scenario <scenario> [options]

        Modes (--mode):
          inline        Inline host model (primary buffer / terminal history); the acceptance default.
          fullscreen    Full-screen host model with a virtualized transcript viewport.

        Scenarios (--scenario):
          stream        Stream 100 coalescible events/second for 10s; report p50/p95 key-to-paint
                        latency and lost/reordered action counts after a clean exit.
          unicode       Render wide CJK, emoji, and combining-mark sequences.
          paste         Exercise multiline bracketed paste inserted without submitting.
          resize        Respond to and log screen-size changes (including 60x12/59x12/60x11).
          cancel        Demonstrate Ctrl-C interruption without corrupting the terminal.
          mouse-off     Disable the mouse before Init; keyboard input remains usable.
          managed-crash Throw from an iteration callback after three frames; the terminal is
                        restored by top-level disposal and the process exits non-zero.

        Options:
          --duration-ms <n>   Test-only override for the stream duration (documented default: 10000).
          --width <n>         Screen width for the headless isolated run (default 100).
          --height <n>        Screen height for the headless isolated run (default 30).
          --headless          Force the isolated ANSI-buffer driver even with a live terminal.
          -h, --help          Show this help and exit.

        Notes:
          * No credentials or network access are used.
          * When stdin/stdout are redirected the harness runs headlessly against an isolated
            ANSI screen buffer, so it never blocks on input or corrupts the developer terminal.
        """;

    private static HarnessOptions Fail(string message) => new() { Error = message };

    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static bool TryParseMode(string value, out SpikeMode mode)
    {
        switch (value.ToLowerInvariant())
        {
            case "inline":
                mode = SpikeMode.Inline;
                return true;
            case "fullscreen":
                mode = SpikeMode.Fullscreen;
                return true;
            default:
                mode = SpikeMode.Inline;
                return false;
        }
    }

    private static bool TryParseScenario(string value, out SpikeScenario scenario)
    {
        switch (value.ToLowerInvariant())
        {
            case "stream":
                scenario = SpikeScenario.Stream;
                return true;
            case "unicode":
                scenario = SpikeScenario.Unicode;
                return true;
            case "paste":
                scenario = SpikeScenario.Paste;
                return true;
            case "resize":
                scenario = SpikeScenario.Resize;
                return true;
            case "cancel":
                scenario = SpikeScenario.Cancel;
                return true;
            case "mouse-off":
                scenario = SpikeScenario.MouseOff;
                return true;
            case "managed-crash":
                scenario = SpikeScenario.ManagedCrash;
                return true;
            default:
                scenario = SpikeScenario.Stream;
                return false;
        }
    }
}
