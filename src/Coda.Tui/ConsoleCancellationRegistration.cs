namespace Coda.Tui;

internal sealed class ConsoleCancellationRegistration : IDisposable
{
    private readonly CancellationTokenSource source;
    private bool disposed;

    public ConsoleCancellationRegistration(CancellationTokenSource source)
    {
        this.source = source;
        Console.CancelKeyPress += this.HandleCancelKeyPress;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        Console.CancelKeyPress -= this.HandleCancelKeyPress;
        this.disposed = true;
    }

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

    private void HandleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        TryCancel(this.source);
    }
}
