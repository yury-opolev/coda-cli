using Coda.Tui.Ui.Events;
using LlmClient;

namespace Coda.Tui.Repl;

/// <summary>
/// Builds a <see cref="SessionMetadataChangedEvent"/> from the current <see cref="CommandContext"/>,
/// so the agent runner and every state-mutating command construct the event identically (effective
/// effort resolved via <see cref="EffortSupport.ResolveAppliedEffort"/>, falling back to "auto").
/// </summary>
public static class SessionMetadataEvents
{
    /// <summary>Snapshot the session's provider/model/effort/cwd/permission into a metadata event.</summary>
    public static SessionMetadataChangedEvent Build(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Session.Model;
        var requested = context.Session.Effort;
        var effective = EffortSupport.ResolveAppliedEffort(model, requested) ?? "auto";
        var connected = context.UiSnapshotProvider?.Invoke().Connected ?? false;

        return new SessionMetadataChangedEvent(
            context.Session.SessionId,
            context.ActiveProvider.Id,
            model,
            requested,
            effective,
            context.Session.WorkingDirectory,
            context.Session.PermissionMode,
            connected);
    }

    /// <summary>Publish a <see cref="SessionMetadataChangedEvent"/> for the current session state.</summary>
    public static void Publish(CommandContext context) => context.Events.Publish(Build(context));
}
