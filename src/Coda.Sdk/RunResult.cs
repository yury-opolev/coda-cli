using Coda.Agent.Goals;
using LlmClient;

namespace Coda.Sdk;

/// <summary>A record of one tool the agent called during a run.</summary>
public sealed record ToolCallRecord(string Name, string Input, string? Result, bool IsError);

/// <summary>The outcome of <see cref="CodaSession.RunAsync"/>.</summary>
public sealed record RunResult(
    bool Success,
    string FinalText,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    string? StopReason,
    string? Error)
{
    /// <summary>Token usage accumulated across all sampling iterations in this run. Zero when the provider did not report usage.</summary>
    public TokenUsage Usage { get; init; } = TokenUsage.Zero;

    /// <summary>Goal-run outcome snapshot. Non-null only when a goal was active for this run.</summary>
    public GoalStatus? Goal { get; init; }
}
