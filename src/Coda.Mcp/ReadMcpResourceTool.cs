using System.Text.Json;
using Coda.Agent;

namespace Coda.Mcp;

/// <summary>
/// Agent tool that reads the content of a specific MCP resource identified by server
/// name and URI.
/// </summary>
public sealed class ReadMcpResourceTool : ITool
{
    private static readonly string schema = """
        {
          "type": "object",
          "properties": {
            "server": {
              "type": "string",
              "description": "Name of the MCP server that owns the resource."
            },
            "uri": {
              "type": "string",
              "description": "URI of the resource to read."
            }
          },
          "required": ["server", "uri"]
        }
        """;

    private readonly McpClientManager manager;

    public ReadMcpResourceTool(McpClientManager manager)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public string Name => "read_mcp_resource";

    public string Description => "Read the content of a resource from a connected MCP server.";

    public string InputSchemaJson => schema;

    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("server", out var serverProp)
            || serverProp.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("Missing required argument: server", IsError: true);
        }

        if (!input.TryGetProperty("uri", out var uriProp)
            || uriProp.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("Missing required argument: uri", IsError: true);
        }

        var serverName = serverProp.GetString()!;
        var uri = uriProp.GetString()!;

        try
        {
            var content = await this.manager.ReadResourceAsync(serverName, uri, cancellationToken).ConfigureAwait(false);
            return new ToolResult(content);
        }
        catch (McpException ex)
        {
            return new ToolResult($"MCP resource error: {ex.Message}", IsError: true);
        }
    }
}
