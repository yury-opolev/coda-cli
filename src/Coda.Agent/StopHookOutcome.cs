namespace Coda.Agent;

/// <summary>
/// Aggregate of all stop hooks for a single stop point. When
/// <see cref="ShouldContinue"/> is true, <see cref="InjectedMessage"/> is appended
/// as a user turn and the loop keeps going; otherwise the agent stops.
/// </summary>
public sealed record StopHookOutcome(bool ShouldContinue, string InjectedMessage)
{
    public static StopHookOutcome Stop { get; } = new(false, string.Empty);
}
