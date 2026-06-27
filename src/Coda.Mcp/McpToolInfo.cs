using System.Text;
using System.Text.Json;

namespace Coda.Mcp;

/// <summary>A tool advertised by an MCP server (from a <c>tools/list</c> result).</summary>
public sealed record McpToolInfo(string Name, string Description, string InputSchemaJson, bool ReadOnly)
{
    /// <summary>Parse the <c>tools/list</c> result into tool infos.</summary>
    public static IReadOnlyList<McpToolInfo> ParseList(JsonElement toolsResult)
    {
        var tools = new List<McpToolInfo>();
        if (!toolsResult.TryGetProperty("tools", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return tools;
        }

        foreach (var tool in array.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            var schema = tool.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object
                ? s.GetRawText()
                : """{"type":"object"}""";

            var readOnly = tool.TryGetProperty("annotations", out var ann)
                && ann.ValueKind == JsonValueKind.Object
                && ann.TryGetProperty("readOnlyHint", out var ro)
                && ro.ValueKind == JsonValueKind.True;

            tools.Add(new McpToolInfo(name!, description, schema, readOnly));
        }

        return tools;
    }

    /// <summary>Format a <c>tools/call</c> result's content array into plain text + error flag.</summary>
    public static (string Text, bool IsError) FormatCallResult(JsonElement callResult)
    {
        var isError = callResult.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;

        var builder = new StringBuilder();
        if (callResult.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "text" && part.TryGetProperty("text", out var text))
                {
                    builder.Append(text.GetString());
                    builder.Append('\n');
                }
                else if (type is not null)
                {
                    builder.Append('[').Append(type).Append(" content]").Append('\n');
                }
            }
        }

        var result = builder.ToString().TrimEnd('\n');
        return (result.Length == 0 ? "(no content)" : result, isError);
    }
}
