using System.Text.Json;
using System.Text.RegularExpressions;
using Coda.Agent;

namespace Coda.Mcp;

/// <summary>
/// Bridges an MCP server tool to the agent's <see cref="ITool"/> abstraction. The
/// advertised name is <c>mcp__{server}__{tool}</c> (matching the reference client);
/// calls are forwarded to the server via <see cref="McpStdioClient"/>.
/// </summary>
public sealed class McpTool : ITool
{
    private readonly McpStdioClient client;
    private readonly McpToolInfo info;

    public McpTool(McpStdioClient client, string serverName, McpToolInfo info)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.info = info ?? throw new ArgumentNullException(nameof(info));
        this.Name = $"mcp__{Sanitize(serverName)}__{Sanitize(info.Name)}";
    }

    public string Name { get; }

    public string Description => this.info.Description;

    public string InputSchemaJson => this.info.InputSchemaJson;

    public bool IsReadOnly => this.info.ReadOnly;

    public bool ShouldDefer => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var (text, isError) = await this.client.CallToolAsync(this.info.Name, input, cancellationToken).ConfigureAwait(false);
            return new ToolResult(text, isError);
        }
        catch (McpException ex)
        {
            return new ToolResult($"MCP tool error: {ex.Message}", IsError: true);
        }
    }

    /// <summary>Tool names must match the model API charset (^[a-zA-Z0-9_-]+$).</summary>
    private static string Sanitize(string value) => Regex.Replace(value, "[^a-zA-Z0-9_-]", "_");
}
