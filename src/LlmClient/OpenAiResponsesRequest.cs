using System.Text.Json.Nodes;

namespace LlmClient;

/// <summary>Maps a provider-neutral chat request to OpenAI's Responses API shape.</summary>
public static class OpenAiResponsesRequest
{
    public static JsonObject Build(ChatRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["input"] = BuildInput(request.Messages),
        };

        if (!string.IsNullOrEmpty(request.System))
        {
            body["instructions"] = request.System;
        }

        if (!string.IsNullOrEmpty(request.Effort))
        {
            body["reasoning"] = new JsonObject { ["effort"] = request.Effort };
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(
                [.. request.Tools.Select(tool => (JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ParseOrEmpty(tool.InputSchemaJson),
                })]);
        }

        return body;
    }

    private static JsonArray BuildInput(IReadOnlyList<ChatMessage> messages)
    {
        var input = new JsonArray();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                AppendUserInput(input, message.Content);
            }
            else
            {
                AppendAssistantInput(input, message.Content);
            }
        }

        return input;
    }

    private static void AppendUserInput(JsonArray input, IReadOnlyList<ContentBlock> content)
    {
        foreach (var result in content.OfType<ToolResultBlock>())
        {
            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = result.ToolUseId,
                ["output"] = result.Content,
            });
        }

        var parts = new JsonArray();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextBlock text:
                    parts.Add(new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = text.Text,
                    });
                    break;

                case ImageBlock image:
                    parts.Add(new JsonObject
                    {
                        ["type"] = "input_image",
                        ["image_url"] = $"data:{image.MediaType};base64,{image.Base64Data}",
                    });
                    break;
            }
        }

        if (parts.Count > 0)
        {
            input.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = parts,
            });
        }
    }

    private static void AppendAssistantInput(JsonArray input, IReadOnlyList<ContentBlock> content)
    {
        var textParts = new JsonArray(
            [.. content.OfType<TextBlock>().Select(text => (JsonNode)new JsonObject
            {
                ["type"] = "output_text",
                ["text"] = text.Text,
            })]);
        if (textParts.Count > 0)
        {
            input.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = textParts,
            });
        }

        foreach (var toolUse in content.OfType<ToolUseBlock>())
        {
            input.Add(new JsonObject
            {
                ["type"] = "function_call",
                ["call_id"] = toolUse.Id,
                ["name"] = toolUse.Name,
                ["arguments"] = toolUse.InputJson,
            });
        }
    }

    private static JsonNode ParseOrEmpty(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }
}
