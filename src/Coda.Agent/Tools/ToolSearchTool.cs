using System.Text;
using System.Text.Json;
using Coda.Agent.ToolSearch;

namespace Coda.Agent.Tools;

/// <summary>
/// Fetches full schema definitions for deferred tools so they can be called.
/// </summary>
public sealed class ToolSearchTool : ITool
{
    /// <summary>The canonical name of this tool (matches <see cref="ToolSearchToolNames.ToolSearch"/>).</summary>
    public const string ToolName = ToolSearchToolNames.ToolSearch;

    public string Name => ToolName;

    public bool IsReadOnly => true;

    public bool ShouldDefer => false;

    public string Description =>
        """
        Fetches full schema definitions for deferred tools so they can be called.

        Deferred tools appear by name in <system-reminder> messages. Until fetched, only the name is known — there is no parameter schema, so the tool cannot be invoked. This tool takes a query, matches it against the deferred tool list, and returns the matched tools' complete JSONSchema definitions inside a <functions> block. Once a tool's schema appears in that result, it is callable exactly like any tool defined at the top of the prompt.

        Result format: each matched tool appears as one <function>{"description": "...", "name": "...", "parameters": {...}}</function> line inside the <functions> block — the same encoding as the tool list at the top of this prompt.

        Query forms:
        - "select:Read,Edit,Grep" — fetch these exact tools by name
        - "notebook jupyter" — keyword search, up to max_results best matches
        - "+slack send" — require "slack" in the name, rank by remaining terms
        """;

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Query to find deferred tools. Use \"select:<tool_name>\" for direct selection, or keywords to search."
            },
            "max_results": {
              "type": "integer",
              "default": 5,
              "description": "Maximum number of results to return (default: 5)."
            }
          },
          "required": ["query"]
        }
        """;

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Parse query — required
            if (!input.TryGetProperty("query", out var queryElement) ||
                queryElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(queryElement.GetString()))
            {
                return Task.FromResult(new ToolResult("query is required", IsError: true));
            }

            var query = queryElement.GetString()!;

            // Parse max_results — optional, default 5, clamp to >= 1
            var maxResults = 5;
            if (input.TryGetProperty("max_results", out var maxResultsElement) &&
                maxResultsElement.ValueKind == JsonValueKind.Number &&
                maxResultsElement.TryGetInt32(out var parsedMax))
            {
                maxResults = Math.Max(1, parsedMax);
            }

            var all = context.AllTools ?? [];
            var deferred = all.Where(DeferredTools.IsDeferred).ToList();

            var matches = ToolSearchEngine.Search(query, deferred, all, maxResults);

            // Invoke the discovery callback (even when matches is empty)
            context.OnToolsDiscovered?.Invoke(matches);

            if (matches.Count == 0)
            {
                return Task.FromResult(new ToolResult("No matching deferred tools found."));
            }

            // Build the <functions> block
            var toolsByName = all.ToDictionary(t => t.Name, StringComparer.Ordinal);
            var builder = new StringBuilder();
            builder.AppendLine("<functions>");

            foreach (var matchedName in matches)
            {
                if (!toolsByName.TryGetValue(matchedName, out var matchedTool))
                {
                    continue;
                }

                var descJson = JsonSerializer.Serialize(matchedTool.Description);
                var nameJson = JsonSerializer.Serialize(matchedTool.Name);
                builder.Append("<function>{\"description\": ");
                builder.Append(descJson);
                builder.Append(", \"name\": ");
                builder.Append(nameJson);
                builder.Append(", \"parameters\": ");
                builder.Append(matchedTool.InputSchemaJson);
                builder.AppendLine("}</function>");
            }

            builder.Append("</functions>");

            return Task.FromResult(new ToolResult(builder.ToString()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Unexpected error in tool_search: {ex.Message}", IsError: true));
        }
    }
}
