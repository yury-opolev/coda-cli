using System.Text.Json.Nodes;
using LlmClient;

namespace Coda.Sdk;

/// <summary>
/// Shared JSON (de)serialization for <see cref="ChatMessage"/>/<see cref="ContentBlock"/>, used by
/// both <see cref="SessionTranscriptStore"/> (the on-disk transcript) and
/// <see cref="SessionBundleService"/> (the portable export/import bundle) so the two stay
/// wire-compatible. A message serializes as <c>{"role":"user"|"assistant","blocks":[...]}</c>; block
/// shapes are documented on <see cref="SerializeBlocks"/>.
/// </summary>
internal static class ChatMessageJson
{
    public static JsonArray SerializeMessages(IReadOnlyList<ChatMessage> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            var msgObj = new JsonObject
            {
                ["role"] = message.Role == ChatRole.User ? "user" : "assistant",
                ["blocks"] = SerializeBlocks(message.Content),
            };
            array.Add(msgObj);
        }

        return array;
    }

    /// <summary>
    /// Serializes content blocks. Shapes: text → <c>{"type":"text","text":&lt;t&gt;}</c>; tool_use →
    /// <c>{"type":"tool_use","id":&lt;id&gt;,"name":&lt;name&gt;,"input":&lt;inputJson&gt;}</c>
    /// with optional correlation fields;
    /// tool_result → <c>{"type":"tool_result","toolUseId":&lt;id&gt;,"content":&lt;c&gt;,"isError":&lt;bool&gt;}</c>.
    /// </summary>
    public static JsonArray SerializeBlocks(IReadOnlyList<ContentBlock> blocks)
    {
        var array = new JsonArray();
        foreach (var block in blocks)
        {
            JsonObject obj = block switch
            {
                TextBlock tb => new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = tb.Text,
                },
                ToolUseBlock tub => SerializeToolUse(tub),
                ToolResultBlock trb => SerializeToolResult(trb),
                _ => new JsonObject { ["type"] = "unknown" },
            };
            array.Add(obj);
        }

        return array;
    }

    public static IReadOnlyList<ChatMessage> DeserializeMessages(JsonArray array)
    {
        var messages = new List<ChatMessage>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject msgObj)
            {
                continue;
            }

            var roleStr = msgObj["role"]?.GetValue<string>();
            var role = string.Equals(roleStr, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;

            var blocksArray = msgObj["blocks"]?.AsArray();
            var blocks = blocksArray is not null ? DeserializeBlocks(blocksArray) : (IReadOnlyList<ContentBlock>)[];
            messages.Add(new ChatMessage(role, blocks));
        }

        return messages;
    }

    public static IReadOnlyList<ContentBlock> DeserializeBlocks(JsonArray array)
    {
        var blocks = new List<ContentBlock>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var type = obj["type"]?.GetValue<string>();
            ContentBlock? block = type switch
            {
                "text" => new TextBlock(obj["text"]?.GetValue<string>() ?? string.Empty),
                "tool_use" => new ToolUseBlock(
                    obj["id"]?.GetValue<string>() ?? string.Empty,
                    obj["name"]?.GetValue<string>() ?? string.Empty,
                    obj["input"]?.GetValue<string>() ?? string.Empty)
                {
                    RootTurnId = OptionalString(obj, "rootTurnId"),
                    ActivityId = OptionalString(obj, "activityId"),
                    SourceId = OptionalString(obj, "sourceId"),
                },
                "tool_result" => new ToolResultBlock(
                    obj["toolUseId"]?.GetValue<string>() ?? string.Empty,
                    obj["content"]?.GetValue<string>() ?? string.Empty,
                    obj["isError"]?.GetValue<bool>() ?? false)
                {
                    RootTurnId = OptionalString(obj, "rootTurnId"),
                    ActivityId = OptionalString(obj, "activityId"),
                    SourceId = OptionalString(obj, "sourceId"),
                    ToolStatus = OptionalString(obj, "toolStatus"),
                },
                _ => null,
            };

            if (block is not null)
            {
                blocks.Add(block);
            }
        }

        return blocks;
    }

    private static JsonObject SerializeToolUse(ToolUseBlock block)
    {
        var obj = new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = block.Id,
            ["name"] = block.Name,
            ["input"] = block.InputJson,
        };
        AddOptionalString(obj, "rootTurnId", block.RootTurnId);
        AddOptionalString(obj, "activityId", block.ActivityId);
        AddOptionalString(obj, "sourceId", block.SourceId);
        return obj;
    }

    private static JsonObject SerializeToolResult(ToolResultBlock block)
    {
        var obj = new JsonObject
        {
            ["type"] = "tool_result",
            ["toolUseId"] = block.ToolUseId,
            ["content"] = block.Content,
            ["isError"] = block.IsError,
        };
        AddOptionalString(obj, "rootTurnId", block.RootTurnId);
        AddOptionalString(obj, "activityId", block.ActivityId);
        AddOptionalString(obj, "sourceId", block.SourceId);
        AddOptionalString(obj, "toolStatus", block.ToolStatus);
        return obj;
    }

    private static void AddOptionalString(JsonObject obj, string name, string? value)
    {
        if (value is not null)
        {
            obj[name] = value;
        }
    }

    private static string? OptionalString(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
