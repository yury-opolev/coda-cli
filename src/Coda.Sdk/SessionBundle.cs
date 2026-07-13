using LlmClient;

namespace Coda.Sdk;

/// <summary>
/// One transcript message within a <see cref="SessionBundle"/>: a role plus its content blocks.
/// Audit metadata (usage, stop reason, timestamps, system prompt, tool defs) lives separately in
/// <see cref="SessionBundle.AuditTurns"/> — one entry per user turn — because the agent loop
/// appends multiple assistant messages per audit turn, so message-to-turn alignment is not 1:1.
/// </summary>
public sealed record SessionBundleTurn
{
    public required string Role { get; init; }                 // "user" | "assistant"

    public required IReadOnlyList<ContentBlock> Blocks { get; init; }
}

/// <summary>
/// A portable, self-contained session export: the lean transcript merged with the audit sidecar
/// (when available). Written to a single file by <see cref="SessionBundleService.WriteAsync"/> and
/// read back by <see cref="SessionBundleService.ImportAsync"/>.
/// </summary>
public sealed record SessionBundle
{
    public string Schema { get; init; } = "coda.session/1";

    public required string CodaVersion { get; init; }

    public required DateTime ExportedUtc { get; init; }

    public required string Id { get; init; }

    public DateTime CreatedUtc { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public bool AuditAvailable { get; init; }

    public string? SystemPrompt { get; init; }

    public IReadOnlyList<ToolDefinition> ToolDefs { get; init; } = [];

    public required IReadOnlyList<SessionBundleTurn> Turns { get; init; }

    /// <summary>
    /// The audit sidecar's turns, carried verbatim (one per user turn, with effective per-turn
    /// system prompt / tool defs). Empty when no sidecar was available at export time. Replayed
    /// on import to reconstruct the sidecar exactly.
    /// </summary>
    public IReadOnlyList<SessionAuditTurn> AuditTurns { get; init; } = [];
}
