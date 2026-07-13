using System.Text.Json.Nodes;
using LlmClient;

namespace Coda.Sdk;

/// <summary>
/// Single source of truth for the on-disk JSON shape of an audit turn's tool calls and tool defs,
/// shared by <see cref="SessionAuditStore"/> (the <c>.audit.jsonl</c> sidecar) and
/// <see cref="SessionBundleService"/> (the portable bundle's <c>auditTurns</c>), so the bundle
/// round-trips the sidecar exactly and a field added to <see cref="ToolCallRecord"/> or
/// <see cref="ToolDefinition"/> cannot drift between the two encodings.
/// </summary>
internal static class AuditJson
{
    public static JsonArray SerializeToolCalls(IReadOnlyList<ToolCallRecord> toolCalls)
    {
        var array = new JsonArray();
        foreach (var call in toolCalls)
        {
            array.Add(new JsonObject
            {
                ["name"] = call.Name,
                ["input"] = call.Input,
                ["result"] = call.Result,
                ["isError"] = call.IsError,
            });
        }

        return array;
    }

    public static IReadOnlyList<ToolCallRecord> DeserializeToolCalls(JsonArray array)
    {
        var list = new List<ToolCallRecord>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            list.Add(new ToolCallRecord(
                obj["name"]?.GetValue<string>() ?? string.Empty,
                obj["input"]?.GetValue<string>() ?? string.Empty,
                obj["result"]?.GetValue<string>(),
                obj["isError"]?.GetValue<bool>() ?? false));
        }

        return list;
    }

    public static JsonArray SerializeToolDefs(IReadOnlyList<ToolDefinition> toolDefs)
    {
        var array = new JsonArray();
        foreach (var def in toolDefs)
        {
            array.Add(new JsonObject
            {
                ["name"] = def.Name,
                ["description"] = def.Description,
                ["inputSchema"] = def.InputSchemaJson,
            });
        }

        return array;
    }

    public static IReadOnlyList<ToolDefinition> DeserializeToolDefs(JsonArray array)
    {
        var list = new List<ToolDefinition>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            list.Add(new ToolDefinition(
                obj["name"]?.GetValue<string>() ?? string.Empty,
                obj["description"]?.GetValue<string>() ?? string.Empty,
                obj["inputSchema"]?.GetValue<string>() ?? string.Empty));
        }

        return list;
    }
}
