namespace Coda.Agent.Teams;

public sealed record TeamTask(
    string Id,
    string Subject,
    string? Description,
    TeamTaskStatus Status,
    string? Owner,
    IReadOnlyList<string> BlockedBy,
    long CreatedAt);
