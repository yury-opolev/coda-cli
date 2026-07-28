namespace Coda.Tui.Ui.State;

internal enum OperationalTone
{
    Ready,
    Initializing,
    Working,
    Thinking,
    Waiting,
    Approval,
    Warning,
    Error,
}

/// <summary>
/// The operational status shown on the status row above the composer. When <see cref="StartedAt"/>
/// is non-null the view appends a live elapsed-seconds suffix on every redraw tick so the user can
/// see how long the current operation has been running without an additional timer.
/// </summary>
internal sealed record OperationalStatus(string Text, OperationalTone Tone, bool Animated, DateTimeOffset? StartedAt = null);
