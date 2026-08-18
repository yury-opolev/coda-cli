using Coda.Tui.Commands;
using Coda.Tui.Ui.Events;
using LlmClient;

namespace Coda.Tui.Repl;

/// <summary>
/// Builds a <see cref="SessionMetadataChangedEvent"/> from the current <see cref="CommandContext"/>,
/// so the agent runner and every state-mutating command construct the event identically (effective
/// effort resolved via <see cref="ReasoningCapabilityResolver"/>, falling back to "auto").
/// </summary>
public static class SessionMetadataEvents
{
    /// <summary>Snapshot the session's provider/model/effort/cwd/permission into a metadata event.</summary>
    public static SessionMetadataChangedEvent Build(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Session.Model;
        var requested = context.Session.Effort;

        // ResolveStoredLevel, not Resolve + ResolveAppliedLevel — the same rule the startup path
        // follows. A Copilot model advertises its levels at runtime, so until a model list has been
        // fetched the capability is INDETERMINATE, not unsupported. Resolving through the plain
        // capability reported it unsupported and dropped the configured level, so the status line
        // read "auto" while /effort correctly showed (and the API correctly received) "high".
        var effective = ReasoningCapabilityResolver.ResolveStoredLevel(
            context.ActiveProvider.Id,
            model,
            requested,
            EffortCommand.CachedReasoningLevels(context)) ?? "auto";

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
