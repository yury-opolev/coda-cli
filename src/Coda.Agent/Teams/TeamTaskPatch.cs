namespace Coda.Agent.Teams;

public sealed record TeamTaskPatch
{
    public TeamTaskStatus? Status { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? BlockedBy { get; init; }
    public string? Owner { get; init; }
    public bool ClearOwner { get; init; }
}
