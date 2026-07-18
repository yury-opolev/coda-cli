namespace Coda.Tui;

/// <summary>
/// Bridges the console Ctrl-C signal to the interactive host. In its interruption form, a first Ctrl-C
/// interrupts the active turn and never exits; the explicit exit action is a separate concern wired
/// through <see cref="RequestExit"/>. The legacy cancellation-token form is retained so the current
/// composition root keeps compiling until the host takes over cancellation wiring.
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
    /// Interruption form: a Ctrl-C calls <paramref name="tryInterrupt"/> to interrupt the active turn
    /// (and, when idle, publish a notice) but never exits. <paramref name="requestExit"/> backs the
    /// explicit exit action and is never invoked by the Ctrl-C handler.
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
        // Never let Ctrl-C terminate the process: interrupt the active turn (the callback publishes the
        // idle notice itself when nothing is running) and keep the application alive.
        e.Cancel = true;
        this.tryInterrupt?.Invoke();
    }
}
