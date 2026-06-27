namespace Coda.Agent.Teams;

public sealed record TeamFile(
    string Name,
    string? Description,
    long CreatedAt,
    string LeadAgentId,
    IReadOnlyList<TeamMember> Members);
