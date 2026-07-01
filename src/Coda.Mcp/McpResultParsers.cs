using System.Text;
using System.Text.Json;

namespace Coda.Mcp;

/// <summary>
/// Shared parsers for <c>resources/*</c> and <c>prompts/*</c> results, used by both the
/// stdio and HTTP transports so the two produce identical shapes.
/// </summary>
internal static class McpResultParsers
{
    public static IReadOnlyList<McpResourceInfo> ParseResourceList(JsonElement result, string serverName)
    {
        var resources = new List<McpResourceInfo>();
        if (!result.TryGetProperty("resources", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return resources;
        }

        foreach (var item in array.EnumerateArray())
        {
            var uri = item.TryGetProperty("uri", out var u) ? u.GetString() : null;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            var mimeType = item.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
            resources.Add(new McpResourceInfo(serverName, uri!, name!, mimeType));
        }

        return resources;
    }

    public static string ParseResourceContents(JsonElement result)
    {
        if (!result.TryGetProperty("contents", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var text))
            {
                builder.Append(text.GetString());
            }
            else if (item.TryGetProperty("blob", out _))
            {
                builder.Append("[binary content]");
            }
        }

        return builder.ToString();
    }

    public static IReadOnlyList<McpPromptInfo> ParsePromptList(JsonElement result, string serverName)
    {
        var prompts = new List<McpPromptInfo>();
        if (!result.TryGetProperty("prompts", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return prompts;
        }

        foreach (var item in array.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var description = item.TryGetProperty("description", out var d) ? d.GetString() : null;
            prompts.Add(new McpPromptInfo(serverName, name!, description));
        }

        return prompts;
    }

    public static string ParsePromptMessages(JsonElement result)
    {
        if (!result.TryGetProperty("messages", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var role = item.TryGetProperty("role", out var r) ? r.GetString() : null;
            string? text = null;
            if (item.TryGetProperty("content", out var content) && content.TryGetProperty("text", out var t))
            {
                text = t.GetString();
            }

            if (role is not null && text is not null)
            {
                lines.Add($"{role}: {text}");
            }
        }

        return string.Join('\n', lines);
    }
}
