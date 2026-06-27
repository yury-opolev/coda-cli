namespace Coda.Mcp;

/// <summary>A resource advertised by an MCP server (from a <c>resources/list</c> result).</summary>
public sealed record McpResourceInfo(string ServerName, string Uri, string Name, string? MimeType);
