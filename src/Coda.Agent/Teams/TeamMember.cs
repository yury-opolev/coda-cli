namespace Coda.Agent.Teams;

public sealed record TeamMember(
    string AgentId,
    string Name,
    string? AgentType,
    string? Model,
    string? Prompt,
    string? Color,
    long JoinedAt,
    bool IsActive,
    IReadOnlyList<string> Subscriptions);
