namespace Coda.Tui.Commands;

/// <summary>One tool row for the <c>/mcp info</c> view: the agent-facing tool name + its description.</summary>
public sealed record McpToolLine(string Name, string Description);
