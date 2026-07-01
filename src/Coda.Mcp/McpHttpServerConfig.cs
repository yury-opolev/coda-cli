namespace Coda.Mcp;

/// <summary>
/// A remote MCP server reached over Streamable HTTP (<c>type:"http"</c> or
/// <c>"streamable-http"</c>). <see cref="Headers"/> are static headers sent on every
/// request; <see cref="Auth"/> controls OAuth/bearer behavior.
/// </summary>
public sealed record McpHttpServerConfig(
    Uri Url,
    IReadOnlyDictionary<string, string> Headers,
    McpAuthConfig Auth) : McpServerConfig;
