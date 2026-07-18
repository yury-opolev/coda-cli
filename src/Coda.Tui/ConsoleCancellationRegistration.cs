namespace Coda.Tui;

/// <summary>
/// Bridges the console Ctrl-C signal to non-retained fallback hosts. Retained Terminal.Gui shells handle
/// their own double-key chords; this registration keeps legacy/plain cancellation separate from the
/// explicit exit action wired through <see cref="RequestExit"/>.
/// </summary>
internal sealed class ConsoleCancellationRegistration : IDisposable
{
    private readonly CancellationTokenSource? source;
    private readonly Func<bool>? tryInterrupt;
    private readonly Action? requestExit;
    private bool disposed;

    /// <summary>
    /// Legacy form: a Ctrl-C cancels <paramref name="source"/>. Preserved so the current composition
    /// root compiles; the interruption form below is the one the host uses.
    /// </summary>
    public ConsoleCancellationRegistration(CancellationTokenSource source)
    {
        this.source = source;
        Console.CancelKeyPress += this.HandleCancelKeyPress;
    }

    /// <summary>
    /// Interruption form: a Ctrl-C calls <paramref name="tryInterrupt"/> to interrupt an active fallback
    /// turn but never exits. <paramref name="requestExit"/> backs the explicit exit action and is never
    /// invoked by the Ctrl-C handler.
    /// </summary>
    public ConsoleCancellationRegistration(Func<bool> tryInterrupt, Action requestExit)
    {
        this.tryInterrupt = tryInterrupt ?? throw new ArgumentNullException(nameof(tryInterrupt));
        this.requestExit = requestExit ?? throw new ArgumentNullException(nameof(requestExit));
        Console.CancelKeyPress += this.HandleInterruptKeyPress;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        Console.CancelKeyPress -= this.HandleCancelKeyPress;
        Console.CancelKeyPress -= this.HandleInterruptKeyPress;
        this.disposed = true;
    }

    /// <summary>Invoke the explicit exit action. Used by the shell's exit action, never by Ctrl-C.</summary>
    public void RequestExit() => this.requestExit?.Invoke();

    internal static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A queued Ctrl+C can race with shutdown after the source is disposed.
        }
    }

    /// <summary>Run the interruption handler as a real Ctrl-C would; returns whether a turn was interrupted.</summary>
    internal bool HandleForTest() => this.tryInterrupt?.Invoke() ?? false;

    private void HandleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (this.source is not null)
        {
            TryCancel(this.source);
        }
    }

    private void HandleInterruptKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Never let Ctrl-C terminate the fallback process: interrupt active work when possible and keep
        // the application alive.
        e.Cancel = true;
        this.tryInterrupt?.Invoke();
    }
}
