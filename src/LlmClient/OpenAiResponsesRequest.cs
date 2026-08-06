using System.Text.Json;
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

        if (request.System is not null)
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
                    ["parameters"] = ToolSchema.ParseSafe(tool.InputSchemaJson),
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
        // Reasoning blocks with a signature (encrypted_content from a prior stateless turn)
        // must be replayed before text/tool-call items so the model retains reasoning state.
        foreach (var thinking in content.OfType<ThinkingBlock>())
        {
            if (thinking.Signature is not { Length: > 0 } signatureJson)
            {
                continue;
            }

            // The signature is a JSON object with "id" and "encrypted_content" fields, stored
            // by OpenAiResponsesSseReader when the Responses API returned an encrypted_content.
            try
            {
                using var doc = JsonDocument.Parse(signatureJson);
                var sig = doc.RootElement;
                var id = sig.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? string.Empty
                    : string.Empty;
                var encContent = sig.TryGetProperty("encrypted_content", out var encEl) && encEl.ValueKind == JsonValueKind.String
                    ? encEl.GetString()
                    : null;

                if (encContent is not null)
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "reasoning",
                        ["id"] = id,
                        ["encrypted_content"] = encContent,
                    });
                }
            }
            catch (JsonException)
            {
                // Malformed signature: skip safely rather than send an invalid request.
            }
        }

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

}
