using System.Text;
using System.Text.Json.Nodes;

namespace LlmClient;

/// <summary>
/// Maps a provider-neutral <see cref="ChatRequest"/> to an OpenAI chat-completions
/// request body (used by GitHub Copilot). Translates the internal content-block
/// model to OpenAI's shape: assistant <c>tool_calls</c> + <c>role:"tool"</c>
/// result messages, and tools as <c>function</c> definitions.
/// </summary>
public static class OpenAiRequest
{
    public static JsonObject Build(ChatRequest request)
    {
        var messages = new JsonArray();
        if (request.System is not null)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        }

        foreach (var message in request.Messages)
        {
            AppendMessage(messages, message);
        }

        // NOTE: max_tokens is intentionally omitted. Copilot's OpenAI-compatible API makes it
        // optional, and sending an explicit per-response cap caused premature
        // stop=max_tokens truncations (the cap also bounds reasoning tokens, so a turn could
        // hit it before emitting any output). Letting Copilot apply its own server-side default
        // matches the reference implementations (opencode omits it for github-copilot; Claude
        // Code only sends it on the Anthropic path, where the Messages API requires it).
        // coda still sends a real max_tokens on the Anthropic path (AnthropicMessagesClient).
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["messages"] = messages,
        };

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = ParseOrEmpty(tool.InputSchemaJson),
                    },
                });
            }

            body["tools"] = tools;
        }

        return body;
    }

    private static void AppendMessage(JsonArray messages, ChatMessage message)
    {
        if (message.Role == ChatRole.User)
        {
            // Tool results become separate role:"tool" messages; otherwise it's plain user text.
            var toolResults = message.Content.OfType<ToolResultBlock>().ToList();
            if (toolResults.Count > 0)
            {
                foreach (var result in toolResults)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.ToolUseId,
                        ["content"] = result.Content,
                    });
                }

                // Preserve any user text accompanying the tool results.
                var extraText = ConcatText(message.Content);
                if (extraText.Length > 0)
                {
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = extraText });
                }

                return;
            }

            messages.Add(new JsonObject { ["role"] = "user", ["content"] = ConcatText(message.Content) });
            return;
        }

        // Assistant: text content (nullable) + tool_calls.
        var assistant = new JsonObject { ["role"] = "assistant" };
        var text = ConcatText(message.Content);
        assistant["content"] = text.Length > 0 ? text : null;

        var toolUses = message.Content.OfType<ToolUseBlock>().ToList();
        if (toolUses.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var toolUse in toolUses)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = toolUse.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = toolUse.Name,
                        // OpenAI expects arguments as a JSON STRING.
                        ["arguments"] = toolUse.InputJson,
                    },
                });
            }

            assistant["tool_calls"] = calls;
        }

        messages.Add(assistant);
    }

    private static string ConcatText(IReadOnlyList<ContentBlock> content)
    {
        var builder = new StringBuilder();
        foreach (var block in content)
        {
            if (block is TextBlock text)
            {
                builder.Append(text.Text);
            }
            else if (block is ImageBlock image)
            {
                // Copilot (OpenAI-shaped) does not support multimodal images in this
                // integration. Render a placeholder so the model is aware an image was
                // attached rather than silently dropping it.
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append($"[image attached: {image.MediaType}]");
            }
        }

        return builder.ToString();
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
