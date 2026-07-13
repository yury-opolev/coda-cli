using LlmClient;

namespace Coda.Sdk;

/// <summary>
/// One turn of an append-only per-session audit trail: what was sent to the model (system
/// prompt, tool definitions) and what came back (usage, stop reason, tool calls).
/// </summary>
/// <remarks>
/// On disk, <see cref="SystemPrompt"/> and <see cref="ToolDefs"/> are written only when they
/// differ from the last emitted values (change-only emission). On <see cref="SessionAuditStore.LoadAsync"/>
/// they are always populated, carried forward from the most recent turn that emitted them.
/// </remarks>
public sealed record SessionAuditTurn
{
    public required int TurnIndex { get; init; }

    public required DateTime TsUtc { get; init; }

    public required string Provider { get; init; }

    public required string Model { get; init; }

    public required int InputTokens { get; init; }

    public required int OutputTokens { get; init; }

    public string? StopReason { get; init; }

    public IReadOnlyList<ToolCallRecord> ToolCalls { get; init; } = [];

    public string? SystemPrompt { get; init; }

    public IReadOnlyList<ToolDefinition> ToolDefs { get; init; } = [];
}
