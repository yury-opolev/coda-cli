using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;

namespace Coda.Tui.Ui.Host;

/// <summary>
/// Runs a single interactive mode to completion and reports how it ended. Implementations own the
/// terminal lifecycle for their mode (Terminal.Gui application, Spectre REPL, or plain loop); the
/// <see cref="TuiHost"/> uses the returned <see cref="TuiShellExit"/> to decide whether to stop,
/// switch modes, or fall back to a safer mode.
/// </summary>
public interface ITuiModeRunner
{
    /// <summary>Run <paramref name="mode"/> seeded with <paramref name="composer"/> until it exits, switches, or fails.</summary>
    Task<TuiShellExit> RunAsync(TuiRunMode mode, ComposerState composer, CancellationToken cancellationToken);
}
