using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Ui.Host;

/// <summary>
/// The only place allowed to create and dispose a Terminal.Gui application. It runs the inline and
/// full-screen shells inside a fully guarded lifecycle (create → set app model/mouse → init driver →
/// register the process-exit stop → build shell → run → dispose in reverse), and delegates the
/// Spectre and plain modes straight to the injected runners without ever touching Terminal.Gui.
/// </summary>
/// <remarks>
/// Cleanup attempts every independent disposal step (process-exit registration, shell, application)
/// even if an earlier one throws, retains the primary failure first, and never writes diagnostics —
/// <see cref="TuiHost"/> owns the single stderr line, and only after cleanup has finished restoring the
/// terminal (alternate screen, input mode, cursor, mouse, bracketed paste, focus reporting,
/// synchronized output, and scroll regions are all restored by Terminal.Gui's own disposal).
/// </remarks>
internal sealed class TerminalGuiModeRunner : ITuiModeRunner
{
    private readonly Func<TuiRunMode, IApplication, ComposerState, TerminalGuiShellBase> shellFactory;
    private readonly Func<ComposerState, CancellationToken, Task<TuiShellExit>> spectreRunner;
    private readonly Func<ComposerState, CancellationToken, Task<TuiShellExit>> plainRunner;
    private readonly Func<IApplication> applicationFactory;
    private readonly string? driverName;
    private readonly bool mouseDisabled;

    public TerminalGuiModeRunner(
        Func<TuiRunMode, IApplication, ComposerState, TerminalGuiShellBase> shellFactory,
        Func<ComposerState, CancellationToken, Task<TuiShellExit>> spectreRunner,
        Func<ComposerState, CancellationToken, Task<TuiShellExit>> plainRunner,
        Func<IApplication>? applicationFactory = null,
        string? driverName = null,
        bool mouseDisabled = false)
    {
        this.shellFactory = shellFactory ?? throw new ArgumentNullException(nameof(shellFactory));
        this.spectreRunner = spectreRunner ?? throw new ArgumentNullException(nameof(spectreRunner));
        this.plainRunner = plainRunner ?? throw new ArgumentNullException(nameof(plainRunner));
        this.applicationFactory = applicationFactory ?? (static () => Application.Create());
        this.driverName = driverName;
        this.mouseDisabled = mouseDisabled;
    }

    /// <inheritdoc />
    public Task<TuiShellExit> RunAsync(TuiRunMode mode, ComposerState composer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(composer);

        return mode switch
        {
            TuiRunMode.Plain => this.plainRunner(composer, cancellationToken),
            TuiRunMode.Spectre => this.spectreRunner(composer, cancellationToken),
            TuiRunMode.Inline or TuiRunMode.Fullscreen => this.RunTerminalGuiAsync(mode, composer, cancellationToken),
            _ => Task.FromResult(TuiShellExit.Exited),
        };
    }

    /// <summary>
    /// Combine the primary run failure with any cleanup failures into a single <see cref="TuiShellExit"/>,
    /// always preserving the primary exception first so diagnostics stay meaningful, and retaining the
    /// composer so a fallback mode can resume the draft.
    /// </summary>
    internal static TuiShellExit BuildFailure(Exception? primary, IReadOnlyList<Exception> cleanup, ComposerState composer)
    {
        if (cleanup.Count == 0)
        {
            return primary is null
                ? TuiShellExit.Exited
                : TuiShellExit.Failed(primary, composer);
        }

        var inner = new List<Exception>(cleanup.Count + 1);
        if (primary is not null)
        {
            inner.Add(primary);
        }

        inner.AddRange(cleanup);

        return TuiShellExit.Failed(new AggregateException(inner), composer);
    }

    /// <summary>
    /// Combine the primary run outcome with any cleanup failures into a single <see cref="TuiShellExit"/>.
    /// A real run failure (<paramref name="primary"/> non-null) still falls back, aggregating cleanup
    /// after the primary. But a clean Exit/SwitchMode whose teardown threw keeps its requested outcome —
    /// a disposal fault must never force an unnecessary fallback/relaunch — while carrying the cleanup
    /// error so the host emits one diagnostic.
    /// </summary>
    internal static TuiShellExit Combine(
        Exception? primary, TuiShellExit? outcome, IReadOnlyList<Exception> cleanup, ComposerState composer)
    {
        if (primary is not null)
        {
            return BuildFailure(primary, cleanup, composer);
        }

        var clean = outcome ?? TuiShellExit.Exited;
        if (cleanup.Count == 0)
        {
            return clean;
        }

        var cleanupError = cleanup.Count == 1 ? cleanup[0] : new AggregateException(cleanup);
        return clean.WithCleanupError(cleanupError);
    }

    internal static string? ResolveDriverName(string? explicitDriverName, Func<string?> autoDriverName)
    {
        ArgumentNullException.ThrowIfNull(autoDriverName);
        return explicitDriverName ?? autoDriverName();
    }

    private async Task<TuiShellExit> RunTerminalGuiAsync(TuiRunMode mode, ComposerState composer, CancellationToken cancellationToken)
    {
        IApplication? app = null;
        TerminalGuiShellBase? shell = null;
        TerminalProcessExitRegistration? processExit = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        TuiShellExit? outcome = null;

        try
        {
            app = this.applicationFactory();
            app.AppModel = mode == TuiRunMode.Inline ? AppModel.Inline : AppModel.FullScreen;
            var mouseService = app.Mouse;
            if (mouseService is not null)
            {
                mouseService.IsMouseDisabled = this.mouseDisabled;
            }

            app.Init(ResolveDriverName(this.driverName, TerminalInputCompatibility.SelectDriverName));

            // Register the process-exit stop after Init (so there is a live application to stop) and
            // dispose it before the application itself, below.
            var initialized = app;
            processExit = new TerminalProcessExitRegistration(() =>
            {
                try
                {
                    initialized.RequestStop();
                }
                catch
                {
                    // Bounded, best-effort stop during process exit.
                }
            });

            shell = this.shellFactory(mode, app, composer);
            await app.RunAsync(shell, cancellationToken).ConfigureAwait(false);

            // Cancellation cleanly stops the loop with no RequestedExit; treat that as a normal exit.
            outcome = shell.RequestedExit ?? TuiShellExit.Exited;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown: a cancelled run is a clean exit, not a failure.
            outcome = TuiShellExit.Exited;
        }
        catch (Exception ex)
        {
            primary = ex;
        }
        finally
        {
            TryCleanup(() => processExit?.Dispose(), cleanup);
            TryCleanup(() => shell?.Dispose(), cleanup);
            TryCleanup(() => app?.Dispose(), cleanup);
        }

        // Fold the run outcome and any cleanup faults into a single result: a clean Exit/SwitchMode is
        // preserved (a teardown fault only rides along as a diagnostic), while a real run failure falls
        // back with the primary exception first.
        return Combine(primary, outcome, cleanup, composer);
    }

    private static void TryCleanup(Action action, List<Exception> cleanup)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            cleanup.Add(ex);
        }
    }
}
