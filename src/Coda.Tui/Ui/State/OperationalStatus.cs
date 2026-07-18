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

internal sealed record OperationalStatus(string Text, OperationalTone Tone, bool Animated);
