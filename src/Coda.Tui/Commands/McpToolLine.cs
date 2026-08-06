namespace Coda.Tui.Commands;

/// <summary>One tool row for the <c>/mcp info</c> view: the agent-facing tool name + its description.</summary>
/// <param name="SchemaCoerced">
/// True when the server advertised an invalid input schema for this tool and coda repaired it at
/// ingestion; rendered as a warning marker so a half-broken tool is never silently presented as
/// healthy.
/// </param>
public sealed record McpToolLine(string Name, string Description, bool SchemaCoerced = false);
