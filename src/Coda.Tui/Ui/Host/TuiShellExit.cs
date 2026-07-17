using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;

namespace Coda.Tui.Ui.Host;

/// <summary>Why an interactive shell run ended.</summary>
public enum TuiShellExitKind
{
    /// <summary>The user asked to leave the application.</summary>
    Exit,

    /// <summary>The user asked to switch to another interactive mode, carrying the composer draft.</summary>
    SwitchMode,

    /// <summary>The shell (or its terminal lifecycle) failed and the host should fall back to a safer mode.</summary>
    Failed,
}

/// <summary>
/// The result of running one interactive shell. It is the only value that crosses the shell/host
/// boundary: a plain exit, a mode switch (carrying the exact <see cref="ComposerState"/> so the draft
/// survives), or a failure (retaining the primary <see cref="Error"/> for diagnostics and preserving
/// the composer where available so a fallback mode can resume the draft).
/// </summary>
public sealed record TuiShellExit(
    TuiShellExitKind Kind,
    TuiRunMode? NextMode,
    ComposerState Composer,
    Exception? Error)
{
    /// <summary>A normal application exit.</summary>
    public static TuiShellExit Exited { get; } = new(TuiShellExitKind.Exit, null, ComposerState.Empty, null);

    /// <summary>Switch to <paramref name="mode"/>, carrying the exported composer <paramref name="state"/>.</summary>
    public static TuiShellExit SwitchTo(TuiRunMode mode, ComposerState state) =>
        new(TuiShellExitKind.SwitchMode, mode, state ?? ComposerState.Empty, null);

    /// <summary>A failure with no recoverable composer state.</summary>
    public static TuiShellExit Failed(Exception error) =>
        new(TuiShellExitKind.Failed, null, ComposerState.Empty, error);

    /// <summary>A failure that retains the composer <paramref name="state"/> so a fallback mode can resume the draft.</summary>
    public static TuiShellExit Failed(Exception error, ComposerState state) =>
        new(TuiShellExitKind.Failed, null, state ?? ComposerState.Empty, error);

    /// <summary>
    /// Attach a cleanup (teardown/disposal) <paramref name="error"/> to a clean Exit/SwitchMode outcome.
    /// The requested outcome is preserved — the host must not fall back or relaunch for a teardown fault —
    /// while the error rides along so the host can emit a single cleanup diagnostic.
    /// </summary>
    public TuiShellExit WithCleanupError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return this with { Error = error };
    }
}
