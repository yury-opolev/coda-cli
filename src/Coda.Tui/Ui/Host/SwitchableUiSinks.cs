using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Host;

/// <summary>
/// An <see cref="IUiFrameSink"/> whose concrete target the interactive host swaps in place as it moves
/// between modes and fallbacks, so one shared <see cref="UiActor"/> drives every mode without being
/// rebuilt. A target fault does not fault the shared actor: the faulted target is cleared (the sink
/// becomes a no-op until the fallback mode re-points it) and the fault is handed to the current mode's
/// handler, which stops that mode's shell so <see cref="TuiHost"/> can fall back. Cancellation is host
/// shutdown, not a fault, so it propagates untouched.
/// </summary>
internal sealed class SwitchableUiFrameSink : IUiFrameSink
{
    private readonly object gate = new();
    private IUiFrameSink? target;
    private Action<Exception>? onFault;

    /// <summary>Point the sink at <paramref name="sink"/>, routing its faults to <paramref name="fault"/>.</summary>
    public void Set(IUiFrameSink? sink, Action<Exception>? fault)
    {
        lock (this.gate)
        {
            this.target = sink;
            this.onFault = fault;
        }
    }

    /// <inheritdoc />
    public async ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        IUiFrameSink? sink;
        Action<Exception>? fault;
        lock (this.gate)
        {
            sink = this.target;
            fault = this.onFault;
        }

        if (sink is null)
        {
            return;
        }

        try
        {
            await sink.ApplyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Clear the faulted target so the shared actor keeps running for the fallback mode, then
            // hand the fault to the current mode's handler (which stops the app and falls back).
            lock (this.gate)
            {
                if (ReferenceEquals(this.target, sink))
                {
                    this.target = null;
                }
            }

            fault?.Invoke(ex);
        }
    }
}

/// <summary>
/// An <see cref="IUiEventObserver"/> whose concrete target the interactive host swaps in place per mode.
/// A single mode observer failure must never fault the shared actor, so non-cancellation exceptions are
/// swallowed; cancellation still propagates as host shutdown.
/// </summary>
internal sealed class SwitchableUiEventObserver : IUiEventObserver
{
    private readonly object gate = new();
    private IUiEventObserver? target;

    /// <summary>Point the observer at <paramref name="next"/> (or clear it with <c>null</c>).</summary>
    public void Set(IUiEventObserver? next)
    {
        lock (this.gate)
        {
            this.target = next;
        }
    }

    /// <inheritdoc />
    public async ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken)
    {
        IUiEventObserver? obs;
        lock (this.gate)
        {
            obs = this.target;
        }

        if (obs is null)
        {
            return;
        }

        try
        {
            await obs.ApplyEventAsync(uiEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A single mode observer failure must never fault the shared actor.
        }
    }
}
