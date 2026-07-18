namespace Coda.Mcp;

/// <summary>A failure talking to an MCP server (JSON-RPC error or transport loss).</summary>
public class McpException : Exception
{
    public McpException(string message, Exception? inner = null) : base(message, inner) { }
}
