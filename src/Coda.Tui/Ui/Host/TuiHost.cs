using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;

namespace Coda.Tui.Ui.Host;

/// <summary>How the host's mode lifecycle ended.</summary>
public enum TuiHostOutcome
{
    /// <summary>A mode exited cleanly at the user's request — the only outcome that shows the exit card.</summary>
    Exited,

    /// <summary>
    /// The host stopped without a clean exit: the fallback ladder was exhausted after failures, the
    /// mode-switch budget/target ran out, or cancellation was requested. No success card is shown.
    /// </summary>
    Exhausted,
}

/// <summary>
/// Owns the interactive-mode lifecycle above a single <see cref="ITuiModeRunner"/>: it starts the
/// requested mode, honors mode-switch requests, and walks the migration fallback ladder
/// (full-screen → inline → Spectre → plain) when a mode fails, writing one concise diagnostic line to
/// stderr after the failed runner has returned and restored the terminal. The controller, session, and
/// composer draft are carried across every switch and fallback so nothing is rebuilt between modes.
/// </summary>
public sealed class TuiHost
{
    // A defensive cap so a shell that keeps requesting a mode switch cannot loop forever.
    private const int MaxModeSwitches = 64;

    private readonly ITuiModeRunner runner;
    private readonly TextWriter error;
    private readonly IUiEventPublisher? publisher;

    /// <summary>Create a host that drives <paramref name="runner"/> and reports failures to <paramref name="error"/>.</summary>
    /// <param name="runner">The mode runner that owns each mode's terminal lifecycle.</param>
    /// <param name="error">Where the single-line fallback diagnostic is written.</param>
    /// <param name="publisher">Optional event publisher used to announce the active mode before each attempt.</param>
    public TuiHost(ITuiModeRunner runner, TextWriter error, IUiEventPublisher? publisher = null)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.error = error ?? throw new ArgumentNullException(nameof(error));
        this.publisher = publisher;
    }

    /// <summary>
    /// Run <paramref name="initial"/> and, on failure, fall back through safer modes; on a mode switch,
    /// run exactly the requested mode carrying <paramref name="composer"/>. Returns
    /// <see cref="TuiHostOutcome.Exited"/> once a mode exits cleanly, or
    /// <see cref="TuiHostOutcome.Exhausted"/> when no safer mode remains, the switch budget/target runs
    /// out, or cancellation is requested.
    /// </summary>
    public async Task<TuiHostOutcome> RunAsync(TuiRunMode initial, ComposerState composer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(composer);

        var mode = initial;
        var current = composer;
        var switches = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            this.publisher?.Publish(new ModeChangedEvent(mode.ToString().ToLowerInvariant()));

            var exit = await this.runner.RunAsync(mode, current, cancellationToken).ConfigureAwait(false);

            switch (exit.Kind)
            {
                case TuiShellExitKind.Exit:
                    this.ReportCleanup(mode, exit);
                    return TuiHostOutcome.Exited;

                case TuiShellExitKind.SwitchMode:
                    this.ReportCleanup(mode, exit);
                    if (++switches > MaxModeSwitches || exit.NextMode is not { } next)
                    {
                        return TuiHostOutcome.Exhausted;
                    }

                    current = exit.Composer;
                    mode = next;
                    continue;

                case TuiShellExitKind.Failed:
                    // The runner has already returned and restored the terminal; only now write the
                    // diagnostic. Preserve a recovered composer draft for the fallback mode.
                    this.error.WriteLine($"{Label(mode)} failed: {Describe(exit.Error)}");
                    if (!IsEmpty(exit.Composer))
                    {
                        current = exit.Composer;
                    }

                    if (NextSaferMode(mode) is not { } safer)
                    {
                        return TuiHostOutcome.Exhausted;
                    }

                    mode = safer;
                    continue;

                default:
                    return TuiHostOutcome.Exhausted;
            }
        }

        return TuiHostOutcome.Exhausted;
    }

    /// <summary>
    /// Emit a single cleanup diagnostic when a clean Exit/SwitchMode outcome carried a teardown fault.
    /// The outcome itself is still honored by the caller (no fallback for a disposal fault).
    /// </summary>
    private void ReportCleanup(TuiRunMode mode, TuiShellExit exit)
    {
        if (exit.Error is not null)
        {
            this.error.WriteLine($"{Label(mode)} cleanup warning: {Describe(exit.Error)}");
        }
    }

    private static TuiRunMode? NextSaferMode(TuiRunMode mode)
    {
        var ladder = TuiModePolicy.FallbacksFrom(mode);
        for (var i = 0; i < ladder.Count - 1; i++)
        {
            if (ladder[i] == mode)
            {
                return ladder[i + 1];
            }
        }

        return null;
    }

    private static bool IsEmpty(ComposerState composer) => composer == ComposerState.Empty;

    private static string Describe(Exception? error) => error?.Message ?? "unknown error";

    private static string Label(TuiRunMode mode) => mode switch
    {
        TuiRunMode.Fullscreen => "full-screen",
        _ => mode.ToString().ToLowerInvariant(),
    };
}
