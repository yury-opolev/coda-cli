using System.Text;
using System.Text.Json;
using Coda.Agent;

namespace Coda.Mcp;

/// <summary>
/// Agent tool that lists MCP prompts available across all connected servers
/// (or a single named server when the optional <c>server</c> argument is supplied).
/// </summary>
public sealed class ListMcpPromptsTool : ITool
{
    private static readonly string schema = """
        {
          "type": "object",
          "properties": {
            "server": {
              "type": "string",
              "description": "Optional name of a specific MCP server to list prompts from. Omit to list from all servers."
            }
          }
        }
        """;

    private readonly McpClientManager manager;

    public ListMcpPromptsTool(McpClientManager manager)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public string Name => "list_mcp_prompts";

    public string Description => "List prompts available from connected MCP servers.";

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

        var allPrompts = await this.manager.ListPromptsAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<McpPromptInfo> prompts = allPrompts;
        if (!string.IsNullOrEmpty(serverFilter))
        {
            prompts = prompts.Where(p => p.ServerName == serverFilter);
        }

        var list = prompts.ToList();
        if (list.Count == 0)
        {
            return new ToolResult("No MCP prompts available.");
        }

        var builder = new StringBuilder();
        foreach (var prompt in list)
        {
            builder.Append(prompt.ServerName)
                   .Append(": ")
                   .Append(prompt.Name);
            if (!string.IsNullOrEmpty(prompt.Description))
            {
                builder.Append(" — ")
                       .Append(prompt.Description);
            }

            builder.AppendLine();
        }

        return new ToolResult(builder.ToString().TrimEnd());
    }
}
