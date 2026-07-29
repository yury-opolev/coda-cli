namespace Coda.Agent.Hooks;

/// <summary>
/// Records the outcome of a single hook execution. Stored in <see cref="HookRunLog"/>
/// in memory for the lifetime of the session; never persisted.
/// </summary>
/// <param name="RanAt">UTC time the hook completed.</param>
/// <param name="Outcome">
/// Human-readable outcome: <c>"allow"</c>, <c>"blocked"</c>, <c>"abort"</c>,
/// <c>"timeout"</c>, <c>"error"</c>, or <c>"skipped"</c> (untrusted or disabled).
/// </param>
/// <param name="DurationMs">Wall-clock execution time in milliseconds.</param>
public sealed record HookRunEntry(DateTimeOffset RanAt, string Outcome, long DurationMs);
