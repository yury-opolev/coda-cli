using LlmClient;

namespace Coda.Sdk;

/// <summary>
/// One turn within a <see cref="SessionBundle"/>: the lean transcript's role/blocks, optionally
/// enriched with the matching audit turn's usage/stop-reason/timestamp when the sidecar was
/// available at export time.
/// </summary>
public sealed record SessionBundleTurn
{
    public required string Role { get; init; }                 // "user" | "assistant"

    public DateTime? TsUtc { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public string? StopReason { get; init; }

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
}
