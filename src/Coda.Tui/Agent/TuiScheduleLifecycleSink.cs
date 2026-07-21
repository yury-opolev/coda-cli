using Coda.Sdk;
using Coda.Sdk.Scheduling;
using Coda.Tui.Ui.Events;

namespace Coda.Tui.Agent;

/// <summary>
/// Bridges schedule-runtime lifecycle notifications into the semantic UI. On every event it publishes
/// a typed <see cref="ScheduleLifecycleChangedEvent"/> (a compact notice) IMMEDIATELY followed by a
/// fresh <see cref="SessionRuntimeChangedEvent"/> so the <c>/tasks</c> browser and status bar reflect
/// the new schedule state. The runtime invokes this on its background firing path — never on the
/// main turn thread — so publish faults during host shutdown (the mailbox is disposed) are swallowed
/// narrowly (only <see cref="ObjectDisposedException"/>, never broad exceptions that would hide bugs).
/// </summary>
public sealed class TuiScheduleLifecycleSink : IScheduleLifecycleSink
{
    private readonly IUiEventPublisher events;
    private readonly Func<SessionRuntimeSnapshot> snapshotProvider;

    /// <summary>
    /// Creates a sink bound to the CURRENT event <paramref name="events"/> publisher and a
    /// fresh-<paramref name="snapshotProvider"/> callback (e.g. <c>session.GetRuntimeSnapshot</c>), so a
    /// stale session/snapshot is never captured.
    /// </summary>
    public TuiScheduleLifecycleSink(IUiEventPublisher events, Func<SessionRuntimeSnapshot> snapshotProvider)
    {
        this.events = events ?? throw new ArgumentNullException(nameof(events));
        this.snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(ScheduleLifecycleEvent value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            // Concise lifecycle notice first, then a fresh runtime snapshot so /tasks + status reflect
            // the new schedule state. The snapshot is pulled at publish time (never captured stale).
            this.events.Publish(new ScheduleLifecycleChangedEvent(value));
            this.events.Publish(new SessionRuntimeChangedEvent(this.snapshotProvider()));
        }
        catch (ObjectDisposedException)
        {
            // The UI mailbox is being torn down during host shutdown: a lifecycle event landing now has
            // nowhere to go, and dropping it is safe. Only this shutdown-specific fault is swallowed —
            // any other exception surfaces so genuine bugs are not hidden.
        }

        return ValueTask.CompletedTask;
    }
}
