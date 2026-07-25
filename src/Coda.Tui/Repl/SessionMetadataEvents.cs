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

        // Resolve the effective effort using the full capability resolver (handles provider- and
        // model-specific rules, including the Anthropic max→high clamp for non-Opus models).
        var capability = EffortCommand.ResolveCapability(context);
        var effective = ReasoningCapabilityResolver.ResolveAppliedLevel(capability, requested) ?? "auto";

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
