namespace Coda.Mcp;

/// <summary>A prompt advertised by an MCP server (from a <c>prompts/list</c> result).</summary>
public sealed record McpPromptInfo(string ServerName, string Name, string? Description);
