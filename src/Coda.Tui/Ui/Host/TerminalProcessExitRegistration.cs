namespace Coda.Tui.Ui.Host;

/// <summary>
/// Subscribes to <see cref="AppDomain.ProcessExit"/> so a managed process exit (Ctrl-Break, host
/// shutdown, container stop) can trigger a bounded, idempotent stop of the running Terminal.Gui
/// application before the runtime tears the process down. The stop callback runs at most once and any
/// secondary failure it raises is swallowed — the process is already leaving, so a throw here must not
/// crash shutdown. The registration unsubscribes on <see cref="Dispose"/> so a completed run leaves no
/// dangling handler.
/// </summary>
public sealed class TerminalProcessExitRegistration : IDisposable
{
    private readonly Action stop;
    private readonly EventHandler handler;
    private int invoked;
    private bool disposed;

    /// <summary>Register <paramref name="stop"/> to run once on process exit.</summary>
    public TerminalProcessExitRegistration(Action stop)
    {
        this.stop = stop ?? throw new ArgumentNullException(nameof(stop));
        this.handler = (_, _) => this.Invoke();
        AppDomain.CurrentDomain.ProcessExit += this.handler;
    }

    /// <summary>Invoke the stop callback exactly as a real process exit would; used by tests only.</summary>
    internal void InvokeForTest() => this.Invoke();

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= this.handler;
    }

    private void Invoke()
    {
        // Idempotent: a queued ProcessExit and an explicit InvokeForTest must never run the callback
        // twice.
        if (Interlocked.Exchange(ref this.invoked, 1) != 0)
        {
            return;
        }

        try
        {
            this.stop();
        }
        catch
        {
            // The process is exiting; a secondary failure in the stop callback must not surface here.
        }
    }
}
