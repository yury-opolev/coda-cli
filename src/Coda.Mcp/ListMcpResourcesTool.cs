using System.Text;
using System.Text.Json;
using Coda.Agent;

namespace Coda.Mcp;

/// <summary>
/// Agent tool that lists MCP resources available across all connected servers
/// (or a single named server when the optional <c>server</c> argument is supplied).
/// </summary>
public sealed class ListMcpResourcesTool : ITool
{
    private static readonly string schema = """
        {
          "type": "object",
          "properties": {
            "server": {
              "type": "string",
              "description": "Optional name of a specific MCP server to list resources from. Omit to list from all servers."
            }
          }
        }
        """;

    private readonly McpClientManager manager;

    public ListMcpResourcesTool(McpClientManager manager)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public string Name => "list_mcp_resources";

    public string Description => "List resources available from connected MCP servers.";

    public string InputSchemaJson => schema;

    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        string? serverFilter = null;
        if (input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("server", out var serverProp)
            && serverProp.ValueKind == JsonValueKind.String)
        {
            serverFilter = serverProp.GetString();
        }

        var allResources = await this.manager.ListResourcesAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<McpResourceInfo> resources = allResources;
        if (!string.IsNullOrEmpty(serverFilter))
        {
            resources = resources.Where(r => r.ServerName == serverFilter);
        }

        var list = resources.ToList();
        if (list.Count == 0)
        {
            return new ToolResult("No MCP resources available.");
        }

        var builder = new StringBuilder();
        foreach (var resource in list)
        {
            builder.Append(resource.ServerName)
                   .Append(": ")
                   .Append(resource.Uri)
                   .Append(" (")
                   .Append(resource.Name)
                   .AppendLine(")");
        }

        return new ToolResult(builder.ToString().TrimEnd());
    }
}
