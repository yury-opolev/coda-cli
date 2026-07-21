using Coda.JsonRpc;
using Coda.Sdk.Scheduling;
using WireScheduleLifecycleEvent = Coda.Sdk.Serve.Messages.ScheduleLifecycleEvent;

namespace Coda.Sdk.Serve;

/// <summary>
/// Forwards schedule-runtime lifecycle notifications to the orchestrator as
/// <c>event/scheduleLifecycle</c> JSON-RPC notifications. Maps the host-neutral
/// <see cref="ScheduleLifecycleKind"/> to a deterministic lower-case wire <c>state</c>
/// (<c>started</c> | <c>completed</c> | <c>failed</c> | <c>stopped</c>) and serializes the event
/// through the shared <see cref="IJsonRpcConnection"/> (whose write lock keeps a lifecycle
/// notification serialized against a concurrently streaming turn — see
/// <see cref="WireStreamProgressSink"/>).
///
/// <para>Unlike the fire-and-forget liveness sinks (which swallow a dead pipe so a mid-turn write
/// never crashes the agent), this sink intentionally does NOT swallow connection faults: the
/// <see cref="ScheduleRuntime"/> owns sink isolation — it catches and logs a faulting publish so a
/// bad sink can never stall or stop scheduling — so surfacing the fault here keeps that ownership in
/// exactly one place and never hides a genuine transport error behind a second empty catch.</para>
/// </summary>
public sealed class WireScheduleLifecycleSink : IScheduleLifecycleSink
{
    private readonly IJsonRpcConnection connection;

    /// <summary>Creates a sink that publishes over the given JSON-RPC <paramref name="connection"/>.</summary>
    public WireScheduleLifecycleSink(IJsonRpcConnection connection) =>
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <inheritdoc />
    public async ValueTask PublishAsync(ScheduleLifecycleEvent value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        var wire = new WireScheduleLifecycleEvent(
            value.DefinitionId,
            value.DefinitionName,
            value.TaskId,
            MapState(value.Kind),
            value.Timestamp,
            value.Summary);

        // Honor cancellation (e.g. host shutdown) and let any transport fault propagate to the
        // runtime's isolating catch — never swallowed here.
        await this.connection
            .SendNotificationAsync(ServeMethods.EventScheduleLifecycle, ServeJson.ToNode(wire), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Deterministic lower-case wire <c>state</c> for each lifecycle transition.</summary>
    internal static string MapState(ScheduleLifecycleKind kind) => kind switch
    {
        ScheduleLifecycleKind.Started => "started",
        ScheduleLifecycleKind.Completed => "completed",
        ScheduleLifecycleKind.Failed => "failed",
        ScheduleLifecycleKind.Stopped => "stopped",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown schedule lifecycle kind"),
    };
}
