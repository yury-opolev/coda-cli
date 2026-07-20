namespace Coda.Agent.Tasks;

/// <summary>Immutable point-in-time view of a managed task.</summary>
public sealed record TaskSnapshot(
    string Id,
    string? ParentId,
    int Depth,
    TaskKind Kind,
    string Description,
    TaskRunStatus Status,
    long Version,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string LogPath,
    string? Result,
    string? Error);
