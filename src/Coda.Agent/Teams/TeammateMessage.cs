namespace Coda.Agent.Teams;

public sealed record TeammateMessage(
    string From,
    string Text,
    string Timestamp,
    bool Read,
    string? Color,
    string? Summary);
