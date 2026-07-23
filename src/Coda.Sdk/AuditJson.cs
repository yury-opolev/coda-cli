using System.Text.Json.Nodes;
using Coda.Agent;
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
            var obj = new JsonObject
            {
                ["name"] = call.Name,
                ["input"] = call.Input,
                ["result"] = call.Result,
                ["isError"] = call.IsError,
            };
            if (call.RootTurnId is not null)
            {
                obj["rootTurnId"] = call.RootTurnId;
            }

            if (call.ActivityId is not null)
            {
                obj["activityId"] = call.ActivityId;
            }

            if (call.CallId is not null)
            {
                obj["callId"] = call.CallId;
            }

            if (call.SourceId is not null)
            {
                obj["sourceId"] = call.SourceId;
            }

            if (call.Status is { } status)
            {
                obj["status"] = status.ToString();
            }

            array.Add(obj);
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
                obj["isError"]?.GetValue<bool>() ?? false)
            {
                RootTurnId = OptionalString(obj["rootTurnId"]),
                ActivityId = OptionalString(obj["activityId"]),
                CallId = OptionalString(obj["callId"]),
                SourceId = OptionalString(obj["sourceId"]),
                Status = OptionalStatus(obj["status"]),
            });
        }

        return list;
    }

    private static string? OptionalString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static ToolCallStatus? OptionalStatus(JsonNode? node)
    {
        var text = OptionalString(node);
        if (text is null)
        {
            return null;
        }

        foreach (var name in Enum.GetNames<ToolCallStatus>())
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<ToolCallStatus>(name);
            }
        }

        return null;
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
