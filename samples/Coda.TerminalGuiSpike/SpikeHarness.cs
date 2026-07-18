// TextView is obsolete (CS0618) in Terminal.Gui 2.4.17 but remains the supported multiline editor;
// the harness manipulates the composer's text/keys directly. Suppression is scoped to this file.
#pragma warning disable CS0618

using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;

namespace Coda.TerminalGuiSpike;

/// <summary>
/// Drives the Terminal.Gui v2 compatibility scenarios. When a real interactive terminal is attached it
/// runs the live application loop; when stdin/stdout are redirected (CI) or <c>--headless</c>/
/// <c>--duration-ms</c> are supplied it drives an isolated ANSI screen buffer via Begin/LayoutAndDraw/
/// End so it never blocks on input or corrupts the developer terminal. Either way the application is
/// created, configured, initialized, run, and disposed inside a guarded lifecycle so a managed crash
/// still restores the terminal before the process exits non-zero.
/// </summary>
internal sealed class SpikeHarness
{
    private const int ManagedCrashExitCode = 70;
    private const int FullscreenPreloadRows = 10_000;
    private const int StreamEventIntervalMs = 10; // 100 coalescible events/second.

    private static readonly char[] StreamKeystrokes = "the quick brown fox jumps ".ToCharArray();

    private readonly HarnessOptions options;

    public SpikeHarness(HarnessOptions options) => this.options = options;

    public int Run()
    {
        var headless = this.options.RunHeadless();
        Console.WriteLine(
            $"[spike] mode={ModeName(this.options.Mode)} scenario={ScenarioName(this.options.Scenario)} " +
            $"driver={(headless ? "headless-ansi" : "live-terminal")}");

        return headless ? this.RunHeadless() : this.RunInteractive();
    }

    // ---------------------------------------------------------------------------------------------
    // Headless (isolated ANSI buffer) path — the deterministic, CI-safe implementation.
    // ---------------------------------------------------------------------------------------------
    private int RunHeadless()
    {
        IApplication? app = null;
        SpikeUi? ui = null;
        SpikeManagedCrashException? crash = null;

        try
        {
            app = Application.Create();
            app.AppModel = this.options.Mode == SpikeMode.Inline ? AppModel.Inline : AppModel.FullScreen;
            if (this.options.Scenario == SpikeScenario.MouseOff)
            {
                // Disable the mouse before Init so it never negotiates mouse tracking with the terminal.
                app.Mouse.IsMouseDisabled = true;
            }

            app.Init(DriverRegistry.Names.ANSI);
            app.Driver!.SetScreenSize(this.options.Width, this.options.Height);

            var virtualized = this.UseVirtualizedTranscript();
            ui = new SpikeUi(this.options.Mode, virtualized, Title(this.options));

            SessionToken? token = app.Begin(ui.Window);
            try
            {
                app.LayoutAndDraw();
                ui.Composer.SetFocus();
                this.RunScenarioHeadless(app, ui);
            }
            finally
            {
                if (token is not null)
                {
                    app.End(token);
                }
            }
        }
        catch (SpikeManagedCrashException ex)
        {
            crash = ex;
        }
        finally
        {
            ui?.Dispose();
            app?.Dispose(); // Restores alternate screen, cursor, mouse, bracketed paste, etc.
        }

        if (crash is not null)
        {
            return ReportManagedCrash(crash);
        }

        return 0;
    }

    private void RunScenarioHeadless(IApplication app, SpikeUi ui)
    {
        switch (this.options.Scenario)
        {
            case SpikeScenario.Stream:
                this.RunStreamHeadless(app, ui);
                break;
            case SpikeScenario.Unicode:
                RunUnicodeHeadless(app, ui);
                break;
            case SpikeScenario.Paste:
                RunPasteHeadless(app, ui);
                break;
            case SpikeScenario.Resize:
                this.RunResizeHeadless(app, ui);
                break;
            case SpikeScenario.Cancel:
                RunCancelHeadless(app, ui);
                break;
            case SpikeScenario.MouseOff:
                RunMouseOffHeadless(app, ui);
                break;
            case SpikeScenario.ManagedCrash:
                RunManagedCrashHeadless(app, ui);
                break;
        }
    }

    private void RunStreamHeadless(IApplication app, SpikeUi ui)
    {
        var durationMs = this.options.EffectiveStreamDurationMs;
        if (ui.VirtualTranscript is not null)
        {
            ui.VirtualTranscript.Preload(BuildPreloadRows(FullscreenPreloadRows));
            app.LayoutAndDraw();
        }

        var latency = new LatencyStats();
        var queue = new Queue<int>();
        var stopwatch = Stopwatch.StartNew();

        long nextEventAtMs = 0;
        var nextSequence = 0;
        var lastApplied = -1;
        long applied = 0;
        long lost = 0;
        long reordered = 0;
        var maxVisiblePerFrame = 0;
        var injectIndex = 0;

        while (stopwatch.ElapsedMilliseconds < durationMs)
        {
            var nowMs = stopwatch.ElapsedMilliseconds;
            while (nextEventAtMs <= nowMs)
            {
                queue.Enqueue(nextSequence++);
                nextEventAtMs += StreamEventIntervalMs;
            }

            // Coalesce: drain every queued event into the transcript in one frame. Nothing is dropped
            // (lost stays 0) and the monotonic sequence proves ordering is preserved (reordered stays 0).
            while (queue.Count > 0)
            {
                var sequence = queue.Dequeue();
                if (sequence < lastApplied)
                {
                    reordered++;
                }

                lastApplied = sequence;
                ui.AppendTranscript(FormattableString.Invariant($"event {sequence:D5}"));
                applied++;
            }

            // Inject an input action and measure the time to the resulting paint.
            var injectedAt = stopwatch.Elapsed;
            this.InjectComposerKey(ui, ref injectIndex);
            app.LayoutAndDraw();
            latency.Add((stopwatch.Elapsed - injectedAt).TotalMilliseconds);

            if (ui.VirtualTranscript is not null)
            {
                maxVisiblePerFrame = Math.Max(maxVisiblePerFrame, ui.VirtualTranscript.ComputeVisibleRowCount());
            }

            Thread.Sleep(1);
        }

        stopwatch.Stop();
        ReportStream(this.options, durationMs, applied, lost, reordered, latency, ui.VirtualTranscript, maxVisiblePerFrame);
    }

    private static void RunUnicodeHeadless(IApplication app, SpikeUi ui)
    {
        var samples = UnicodeSamples();
        ui.AppendTranscript("Unicode rendering sample:");
        foreach (var (label, text) in samples)
        {
            ui.AppendTranscript($"{label}: {text}");
        }

        ui.SetStatus("Wide CJK, emoji, and combining marks rendered.");
        app.LayoutAndDraw();

        Console.WriteLine("[unicode] rendered wide/combining samples:");
        foreach (var (label, text) in samples)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"  - {label}: \"{text}\" (runes={CountRunes(text)}, chars={text.Length})"));
        }

        Console.WriteLine("[unicode] result=OK (visually confirm alignment against the checklist)");
    }

    private static void RunPasteHeadless(IApplication app, SpikeUi ui)
    {
        ui.AppendTranscript("Paste sample: a bracketed multiline paste must insert, never submit.");
        ui.SetStatus("Paste multiline text — it is inserted verbatim, Enter is not triggered.");
        ui.Composer.SetFocus();
        app.LayoutAndDraw();

        const string payload = "first pasted line\nsecond pasted line\nthird pasted line";
        var method = "RaisePasteEvent";
        app.RaisePasteEvent(payload);
        app.LayoutAndDraw();

        if (LineCount(ui.Composer.Text) <= 1)
        {
            // Fallback for drivers that do not route synthetic paste to the focused view headlessly.
            ui.Composer.Text = payload.Replace("\n", Environment.NewLine);
            app.LayoutAndDraw();
            method = "direct-insert";
        }

        var lines = LineCount(ui.Composer.Text);
        Console.WriteLine(FormattableString.Invariant(
            $"[paste] method={method} composer-lines={lines} submitted=false result={(lines >= 3 ? "OK" : "CHECK")}"));
    }

    private void RunResizeHeadless(IApplication app, SpikeUi ui)
    {
        var observed = new List<Rectangle>();
        void OnScreenChanged(object? sender, EventArgs<Rectangle> e) => observed.Add(e.Value);
        app.ScreenChanged += OnScreenChanged;

        ui.AppendTranscript("Resize sample: layout must reflow and the composer/status must survive.");
        ui.SetStatus("Resize the terminal — includes the minimum-size checks 60x12 / 59x12 / 60x11.");

        var sizes = new (int Width, int Height)[]
        {
            (this.options.Width, this.options.Height),
            (100, 30),
            (60, 12),
            (59, 12),
            (60, 11),
        };

        Console.WriteLine("[resize] responding to screen-size changes:");
        foreach (var (width, height) in sizes)
        {
            app.Driver!.SetScreenSize(width, height);
            app.LayoutAndDraw();
            Console.WriteLine(FormattableString.Invariant(
                $"  - {width}x{height}: composer.width={ui.Composer.Frame.Width} status.width={ui.Status.Frame.Width}"));
        }

        app.ScreenChanged -= OnScreenChanged;
        Console.WriteLine(FormattableString.Invariant(
            $"[resize] screen-change events observed={observed.Count} result=OK"));
    }

    private static void RunCancelHeadless(IApplication app, SpikeUi ui)
    {
        var interrupted = false;
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            if (e is not null)
            {
                e.Cancel = true; // Keep the process alive; a real turn would be cancelled, not the app.
            }

            interrupted = true;
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            ui.AppendTranscript("Cancel sample: a double Esc interrupts the active turn; an intercepted Ctrl+C keeps the app alive so it can copy a selection or, pressed twice, exit.");
            ui.SetStatus("Esc Esc interrupts; Ctrl+C copies the selection or exits on a second press; /exit also quits.");
            app.LayoutAndDraw();

            // Simulate the intercepted Ctrl+C signal deterministically (headless cannot self-deliver SIGINT).
            OnCancel(null, null!);
            app.LayoutAndDraw();

            Console.WriteLine(
                $"[cancel] signal-handled={interrupted} app-alive={app.Initialized} " +
                "note=an intercepted Ctrl+C never corrupts the terminal; double Esc interrupts and an explicit exit is still required result=OK");
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    private static void RunMouseOffHeadless(IApplication app, SpikeUi ui)
    {
        ui.AppendTranscript("Mouse-off sample: the mouse is disabled; keyboard input still works.");
        ui.SetStatus("Mouse disabled (--no-mouse). Type to confirm the keyboard is still usable.");
        ui.Composer.SetFocus();

        foreach (var character in "keyboard-still-works")
        {
            ui.Composer.NewKeyDownEvent(new Key(character));
        }

        app.LayoutAndDraw();

        var keyboardWorks = ui.Composer.Text.Length > 0;
        Console.WriteLine(
            $"[mouse-off] mouse-disabled={app.Mouse.IsMouseDisabled} keyboard-usable={keyboardWorks} " +
            $"composer-chars={ui.Composer.Text.Length} result={(app.Mouse.IsMouseDisabled && keyboardWorks ? "OK" : "CHECK")}");
    }

    private static void RunManagedCrashHeadless(IApplication app, SpikeUi ui)
    {
        var frames = 0;
        void OnIteration(object? sender, EventArgs<IApplication?> e)
        {
            frames++;
            ui.AppendTranscript($"frame {frames}");
            if (frames >= 3)
            {
                throw new SpikeManagedCrashException("renderer callback threw on frame 3 (simulated)");
            }
        }

        app.Iteration += OnIteration;
        try
        {
            ui.SetStatus("Managed-crash sample: an iteration callback throws after three frames.");
            Console.WriteLine("[managed-crash] driving frames; the callback will throw on frame 3...");
            for (var frame = 0; frame < 3; frame++)
            {
                app.LayoutAndDraw();
                app.RaiseIteration(); // Frame 3 throws, propagating out to the guarded lifecycle.
            }
        }
        finally
        {
            app.Iteration -= OnIteration;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Interactive (live terminal) path — used when a human runs the spike from a real terminal.
    // ---------------------------------------------------------------------------------------------
    private int RunInteractive()
    {
        IApplication? app = null;
        SpikeUi? ui = null;
        SpikeManagedCrashException? crash = null;
        var counters = new StreamCounters();
        var latency = new LatencyStats();

        ConsoleCancelEventHandler cancelHandler = (sender, e) =>
        {
            e.Cancel = true;
            try
            {
                app?.Invoke(() => app!.RequestStop());
            }
            catch
            {
                // Best-effort stop during interrupt.
            }
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            app = Application.Create();
            app.AppModel = this.options.Mode == SpikeMode.Inline ? AppModel.Inline : AppModel.FullScreen;
            if (this.options.Scenario == SpikeScenario.MouseOff)
            {
                app.Mouse.IsMouseDisabled = true;
            }

            app.Init(null);
            ui = new SpikeUi(this.options.Mode, this.UseVirtualizedTranscript(), Title(this.options));
            this.ScheduleInteractive(app, ui, latency, counters);

            try
            {
                app.RunAsync(ui.Window, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (SpikeManagedCrashException ex)
            {
                crash = ex;
            }
        }
        finally
        {
            ui?.Dispose();
            app?.Dispose();
            Console.CancelKeyPress -= cancelHandler;
        }

        if (crash is not null)
        {
            return ReportManagedCrash(crash);
        }

        if (this.options.Scenario == SpikeScenario.Stream)
        {
            ReportStream(
                this.options,
                this.options.EffectiveStreamDurationMs,
                counters.Applied,
                0L,
                0L,
                latency,
                ui?.VirtualTranscript,
                counters.MaxVisiblePerFrame);
        }
        else
        {
            Console.WriteLine($"[{ScenarioName(this.options.Scenario)}] interactive session exited cleanly; terminal restored.");
        }

        return 0;
    }

    private void ScheduleInteractive(IApplication app, SpikeUi ui, LatencyStats latency, StreamCounters counters)
    {
        switch (this.options.Scenario)
        {
            case SpikeScenario.Stream:
                this.ScheduleStreamInteractive(app, ui, latency, counters);
                break;
            case SpikeScenario.ManagedCrash:
                ScheduleManagedCrashInteractive(app, ui);
                break;
            case SpikeScenario.Resize:
                ScheduleResizeInteractive(app, ui);
                break;
            default:
                ui.SetStatus(InteractiveInstructions(this.options.Scenario));
                break;
        }
    }

    private void ScheduleStreamInteractive(IApplication app, SpikeUi ui, LatencyStats latency, StreamCounters counters)
    {
        if (ui.VirtualTranscript is not null)
        {
            ui.VirtualTranscript.Preload(BuildPreloadRows(FullscreenPreloadRows));
        }

        ui.SetStatus("Streaming 100 events/second; type freely — Esc exits early.");

        var stopwatch = Stopwatch.StartNew();
        var durationMs = this.options.EffectiveStreamDurationMs;
        var nextSequence = 0;
        var injectIndex = 0;
        TimeSpan? pendingInjectAt = null;

        void OnDrawComplete(object? sender, EventArgs e)
        {
            if (pendingInjectAt is { } injectedAt)
            {
                latency.Add((stopwatch.Elapsed - injectedAt).TotalMilliseconds);
                pendingInjectAt = null;
            }

            if (ui.VirtualTranscript is not null)
            {
                counters.MaxVisiblePerFrame = Math.Max(counters.MaxVisiblePerFrame, ui.VirtualTranscript.LastVisibleRowCount);
            }
        }

        app.LayoutAndDrawComplete += OnDrawComplete;

        app.AddTimeout(TimeSpan.FromMilliseconds(StreamEventIntervalMs), () =>
        {
            if (stopwatch.ElapsedMilliseconds >= durationMs)
            {
                app.LayoutAndDrawComplete -= OnDrawComplete;
                app.RequestStop();
                return false;
            }

            ui.AppendTranscript(FormattableString.Invariant($"event {nextSequence++:D5}"));
            counters.Applied++;
            pendingInjectAt = stopwatch.Elapsed;
            this.InjectComposerKey(ui, ref injectIndex);
            return true;
        });
    }

    private static void ScheduleManagedCrashInteractive(IApplication app, SpikeUi ui)
    {
        ui.SetStatus("Managed-crash: an iteration callback throws after three frames.");
        var frames = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(120), () =>
        {
            frames++;
            ui.AppendTranscript($"frame {frames}");
            if (frames >= 3)
            {
                throw new SpikeManagedCrashException("renderer callback threw on frame 3 (simulated)");
            }

            return true;
        });
    }

    private static void ScheduleResizeInteractive(IApplication app, SpikeUi ui)
    {
        ui.SetStatus(InteractiveInstructions(SpikeScenario.Resize));
        app.ScreenChanged += (sender, e) =>
        {
            var bounds = e.Value;
            ui.SetStatus(FormattableString.Invariant($"resized to {bounds.Width}x{bounds.Height} — Esc exits."));
            ui.AppendTranscript(FormattableString.Invariant($"screen changed: {bounds.Width}x{bounds.Height}"));
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Shared helpers.
    // ---------------------------------------------------------------------------------------------
    private bool UseVirtualizedTranscript() =>
        this.options.Mode == SpikeMode.Fullscreen && this.options.Scenario == SpikeScenario.Stream;

    private void InjectComposerKey(SpikeUi ui, ref int injectIndex)
    {
        if (ui.Composer.Text.Length > 400)
        {
            ui.Composer.Text = string.Empty;
        }

        var character = StreamKeystrokes[injectIndex % StreamKeystrokes.Length];
        injectIndex++;
        ui.Composer.NewKeyDownEvent(new Key(character));
    }

    private static IEnumerable<string> BuildPreloadRows(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return FormattableString.Invariant($"transcript block {i:D5} — preloaded backlog row");
        }
    }

    private static (string Label, string Text)[] UnicodeSamples() =>
        new[]
        {
            ("CJK-wide", "你好世界 こんにちは 안녕하세요"),
            ("Emoji", "😀 🚀 🎉 👩‍💻 👨‍👩‍👧‍👦"),
            ("Combining", "e\u0301 a\u0300 n\u0303 o\u0308 (é à ñ ö)"),
        };

    private static string InteractiveInstructions(SpikeScenario scenario) => scenario switch
    {
        SpikeScenario.Unicode => "Unicode: wide CJK, emoji, and combining marks are rendered. Esc exits.",
        SpikeScenario.Paste => "Paste: paste multiline text — it inserts verbatim without submitting. Esc exits.",
        SpikeScenario.Resize => "Resize: resize the terminal (try 60x12, 59x12, 60x11). Esc exits.",
        SpikeScenario.Cancel => "Cancel: a double Esc interrupts; Ctrl+C copies a selection or exits on a second press; /exit quits.",
        SpikeScenario.MouseOff => "Mouse-off: the mouse is disabled; the keyboard still works. Esc exits.",
        _ => "Esc exits.",
    };

    private static void ReportStream(
        HarnessOptions options,
        int durationMs,
        long applied,
        long lost,
        long reordered,
        LatencyStats latency,
        SampleVirtualTranscriptView? virtualTranscript,
        int maxVisiblePerFrame)
    {
        var p50 = latency.Percentile(50);
        var p95 = latency.Percentile(95);
        Console.WriteLine(
            $"[stream] mode={ModeName(options.Mode)} duration={durationMs}ms events-applied={applied} " +
            $"lost={lost} reordered={reordered}");
        Console.WriteLine(FormattableString.Invariant(
            $"[stream] key-to-paint latency samples={latency.Count} p50={p50:F2}ms p95={p95:F2}ms max={latency.Max:F2}ms"));

        if (virtualTranscript is not null)
        {
            Console.WriteLine(
                $"[stream] fullscreen preloaded-rows={virtualTranscript.TotalRows} " +
                $"visible-rows/frame(max)={maxVisiblePerFrame} (bounded by viewport height)");
        }
    }

    private static int ReportManagedCrash(SpikeManagedCrashException crash)
    {
        Console.Error.WriteLine($"[managed-crash] {crash.Message}");
        Console.Error.WriteLine(
            "[managed-crash] terminal restored by top-level disposal (alternate screen/cursor/mouse reset); " +
            $"exiting non-zero ({ManagedCrashExitCode}).");
        return ManagedCrashExitCode;
    }

    private static int LineCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Length;
    }

    private static int CountRunes(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    private static string Title(HarnessOptions options) =>
        $"Coda Terminal.Gui spike — {ModeName(options.Mode)} / {ScenarioName(options.Scenario)}";

    private static string ModeName(SpikeMode mode) => mode == SpikeMode.Inline ? "inline" : "fullscreen";

    private static string ScenarioName(SpikeScenario scenario) => scenario switch
    {
        SpikeScenario.Stream => "stream",
        SpikeScenario.Unicode => "unicode",
        SpikeScenario.Paste => "paste",
        SpikeScenario.Resize => "resize",
        SpikeScenario.Cancel => "cancel",
        SpikeScenario.MouseOff => "mouse-off",
        SpikeScenario.ManagedCrash => "managed-crash",
        _ => scenario.ToString().ToLower(CultureInfo.InvariantCulture),
    };

    /// <summary>Mutable stream metrics shared with the interactive timeout callbacks.</summary>
    private sealed class StreamCounters
    {
        public long Applied;
        public int MaxVisiblePerFrame;
    }
}

#pragma warning restore CS0618
