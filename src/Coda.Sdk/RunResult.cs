using Coda.Agent;
using Coda.Agent.Goals;
using LlmClient;

namespace Coda.Sdk;

/// <summary>A record of one tool the agent called during a run.</summary>
public sealed record ToolCallRecord(string Name, string Input, string? Result, bool IsError)
{
    public string? RootTurnId { get; init; }

    public string? ActivityId { get; init; }

    public string? CallId { get; init; }

    public string? SourceId { get; init; }

    public ToolCallStatus? Status { get; init; }
}

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

    /// <summary>Root identity for the tool activity associated with this run, when one was created.</summary>
    public string? RootTurnId { get; init; }

    /// <summary>Terminal tool activity summary, when this run executed correlated tool calls.</summary>
    public ToolActivitySummary? ToolActivity { get; init; }
}
