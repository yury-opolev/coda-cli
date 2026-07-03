using Coda.Agent;

namespace Coda.Mcp;

/// <summary>Pure filtering of an aggregate tool list down to one MCP server's tools.</summary>
public static class McpServerTools
{
    /// <summary>The <see cref="McpTool"/>s in <paramref name="tools"/> that belong to
    /// <paramref name="serverName"/> (non-MCP tools and other servers' tools are excluded).</summary>
    public static IReadOnlyList<McpTool> ForServer(IReadOnlyList<ITool> tools, string serverName)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools
            .OfType<McpTool>()
            .Where(t => string.Equals(t.ServerName, serverName, StringComparison.Ordinal))
            .ToList();
    }
}
