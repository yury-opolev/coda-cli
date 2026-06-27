using System.Text.Json;
using Coda.Agent;

namespace Coda.Mcp;

/// <summary>
/// Agent tool that retrieves the rendered text of a specific MCP prompt identified
/// by server name and prompt name.
/// </summary>
public sealed class GetMcpPromptTool : ITool
{
    private static readonly string schema = """
        {
          "type": "object",
          "properties": {
            "server": {
              "type": "string",
              "description": "Name of the MCP server that owns the prompt."
            },
            "name": {
              "type": "string",
              "description": "Name of the prompt to get."
            }
          },
          "required": ["server", "name"]
        }
        """;

    private readonly McpClientManager manager;

    public GetMcpPromptTool(McpClientManager manager)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public string Name => "get_mcp_prompt";

    public string Description => "Get the rendered text of a prompt from a connected MCP server.";

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

        if (!input.TryGetProperty("name", out var nameProp)
            || nameProp.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("Missing required argument: name", IsError: true);
        }

        var serverName = serverProp.GetString()!;
        var promptName = nameProp.GetString()!;

        try
        {
            var text = await this.manager.GetPromptAsync(serverName, promptName, cancellationToken).ConfigureAwait(false);
            return new ToolResult(text);
        }
        catch (McpException ex)
        {
            return new ToolResult($"MCP prompt error: {ex.Message}", IsError: true);
        }
    }
}
