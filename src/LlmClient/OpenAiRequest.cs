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
    /// <summary>
    /// Copilot proprietary cache-control field. Applied only on Anthropic-family models
    /// (those whose model ID contains "claude"). This changes <em>latency only</em> —
    /// Copilot bills premium requests, not tokens, so tool calls do not count and there
    /// is no per-token cost saving from caching on this path.
    /// </summary>
    private const string CopilotCacheControlField = "copilot_cache_control";

    public static JsonObject Build(ChatRequest request)
    {
        var isAnthropicFamily = IsAnthropicFamily(request.Model);

        // Run the planner once; used for both tools and messages markers below.
        var plan = isAnthropicFamily ? PromptCachePlanner.Plan(request) : CachePlan.None;

        var messages = new JsonArray();
        if (request.System is not null)
        {
            var systemMsg = new JsonObject { ["role"] = "system", ["content"] = request.System };
            if (isAnthropicFamily)
            {
                // Slot 2 (system) is always marked on the Anthropic-family path.
                systemMsg[CopilotCacheControlField] = new JsonObject { ["type"] = "ephemeral" };
            }

            messages.Add(systemMsg);
        }

        // Track the last OpenAI message index that was emitted for each ChatMessage index,
        // so we can add copilot_cache_control to the right position for anchor/rolling slots.
        var lastOpenAiIdxForChatMsg = isAnthropicFamily
            ? new Dictionary<int, int>()
            : null;

        for (var i = 0; i < request.Messages.Count; i++)
        {
            var before = messages.Count;
            AppendMessage(messages, request.Messages[i]);
            if (lastOpenAiIdxForChatMsg is not null && messages.Count > before)
            {
                lastOpenAiIdxForChatMsg[i] = messages.Count - 1;
            }
        }

        // Apply copilot_cache_control to message-level breakpoints (slots 3 and 4).
        if (isAnthropicFamily && lastOpenAiIdxForChatMsg is not null)
        {
            AddCopilotCacheMark(messages, plan.AnchorMessageIndex, lastOpenAiIdxForChatMsg);
            AddCopilotCacheMark(messages, plan.RollingMessageIndex, lastOpenAiIdxForChatMsg);
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
            for (var i = 0; i < request.Tools.Count; i++)
            {
                var tool = request.Tools[i];
                var toolNode = new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = ToolSchema.ParseSafe(tool.InputSchemaJson),
                    },
                };

                // Slot 1 (last tool): mark only when the planner placed a tools breakpoint.
                if (isAnthropicFamily && plan.ToolsBreakpoint && i == request.Tools.Count - 1)
                {
                    toolNode[CopilotCacheControlField] = new JsonObject { ["type"] = "ephemeral" };
                }

                tools.Add(toolNode);
            }

            body["tools"] = tools;
        }

        return body;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the model ID indicates an Anthropic (Claude) model.
    /// Case-insensitive containment check on "claude".
    /// </summary>
    internal static bool IsAnthropicFamily(string model) =>
        model.Contains("claude", StringComparison.OrdinalIgnoreCase);

    private static void AddCopilotCacheMark(
        JsonArray messages,
        int? chatMsgIndex,
        Dictionary<int, int> lastOpenAiIdxForChatMsg)
    {
        if (chatMsgIndex is null)
        {
            return;
        }

        if (!lastOpenAiIdxForChatMsg.TryGetValue(chatMsgIndex.Value, out var openAiIdx))
        {
            return;
        }

        if (messages[openAiIdx] is JsonObject msgNode
            && msgNode[CopilotCacheControlField] is null)
        {
            msgNode[CopilotCacheControlField] = new JsonObject { ["type"] = "ephemeral" };
        }
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

}
