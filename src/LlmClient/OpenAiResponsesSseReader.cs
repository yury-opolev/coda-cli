using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace LlmClient;

/// <summary>Parses OpenAI Responses API streaming events into provider-neutral events.</summary>
public static class OpenAiResponsesSseReader
{
    public static IAsyncEnumerable<AssistantStreamEvent> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var reader = new StreamReader(stream, Encoding.UTF8);
        return ReadAsync(reader, cancellationToken);
    }

    public static async IAsyncEnumerable<AssistantStreamEvent> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var toolCalls = new SortedDictionary<int, ToolCallAccumulator>();
        var hasToolCall = false;
        var hasFinished = false;
        // Accumulate reasoning summary text for ThinkingBlock history replay.
        var reasoningText = new StringBuilder();
        // Capture the reasoning item's id and encrypted_content for stateless replay (store=false).
        // Both are null when the Responses API does not return an encrypted_content field.
        string? reasoningItemId = null;
        string? reasoningEncryptedContent = null;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                continue;
            }

            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(payload);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            var type = root.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;
            switch (type)
            {
                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var delta)
                        && delta.ValueKind == JsonValueKind.String
                        && delta.GetString() is { Length: > 0 } text)
                    {
                        yield return AssistantStreamEvent.Delta(text);
                    }

                    break;

                case "response.reasoning_summary_text.delta":
                    // Normalized thinking-text delta: emit to the sink and accumulate for history replay.
                    if (root.TryGetProperty("delta", out var reasoningDelta)
                        && reasoningDelta.ValueKind == JsonValueKind.String
                        && reasoningDelta.GetString() is { Length: > 0 } reasoningChunk)
                    {
                        reasoningText.Append(reasoningChunk);
                        yield return AssistantStreamEvent.ThinkingDelta(reasoningChunk);
                    }

                    break;

                case "response.output_item.added":
                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var item)
                        && item.ValueKind == JsonValueKind.Object)
                    {
                        if (IsFunctionCall(item))
                        {
                            var index = ReadOutputIndex(root);
                            var accumulator = GetToolCall(toolCalls, index);
                            ReadToolCall(item, accumulator);
                            hasToolCall = true;
                        }
                        else if (IsReasoningItem(item))
                        {
                            // Capture the reasoning item id and encrypted_content (if present) for
                            // stateless replay. The encrypted_content field is only present when the
                            // request uses store=false; omitting it is safe (no client-side replay).
                            if (item.TryGetProperty("id", out var itemId) && itemId.ValueKind == JsonValueKind.String)
                            {
                                reasoningItemId = itemId.GetString();
                            }

                            if (item.TryGetProperty("encrypted_content", out var enc) && enc.ValueKind == JsonValueKind.String)
                            {
                                reasoningEncryptedContent = enc.GetString();
                            }
                        }
                    }

                    break;

                case "response.function_call_arguments.delta":
                    var argumentsIndex = ReadOutputIndex(root);
                    var argumentsAccumulator = GetToolCall(toolCalls, argumentsIndex);
                    if (root.TryGetProperty("delta", out var argumentsDelta)
                        && argumentsDelta.ValueKind == JsonValueKind.String)
                    {
                        argumentsAccumulator.Arguments.Append(argumentsDelta.GetString());
                    }

                    hasToolCall = true;
                    break;

                case "response.completed":
                case "response.incomplete":
                    foreach (var toolCall in FlushToolCalls(toolCalls))
                    {
                        yield return AssistantStreamEvent.Tool(toolCall);
                    }

                    var response = root.TryGetProperty("response", out var responseElement)
                        ? responseElement
                        : default;
                    var turnUsage = ReadUsage(response);

                    // Emit the complete thinking block if any reasoning was accumulated.
                    // Signature carries the reasoning item id + encrypted_content (JSON) when the
                    // provider returned an encrypted_content field; null otherwise (server-side only).
                    // ThinkingTokens is set to output_tokens so the block header can display "N tok".
                    if (reasoningText.Length > 0)
                    {
                        string? signature = null;
                        if (reasoningEncryptedContent is not null)
                        {
                            // Store as a small JSON object so AppendAssistantInput can reconstruct
                            // the full reasoning input item (needs both id and encrypted_content).
                            signature = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                id = reasoningItemId ?? string.Empty,
                                encrypted_content = reasoningEncryptedContent,
                            });
                        }

                        var thinkingTokens = turnUsage?.OutputTokens is { } ot && ot > 0 ? ot : (int?)null;
                        yield return AssistantStreamEvent.ThinkingDone(
                            new ThinkingBlock(reasoningText.ToString(), signature),
                            thinkingTokens: thinkingTokens);
                    }

                    var stopReason = type == "response.incomplete"
                        ? MapIncompleteReason(response)
                        : hasToolCall ? "tool_use" : "end_turn";
                    yield return AssistantStreamEvent.Finished(stopReason, turnUsage);
                    hasFinished = true;
                    break;

                case "response.failed":
                case "error":
                    throw new InvalidDataException(ReadError(root));
            }
        }

        if (!hasFinished)
        {
            throw new InvalidDataException("The Responses API stream ended without a terminal event.");
        }
    }

    private static bool IsFunctionCall(JsonElement item) =>
        item.TryGetProperty("type", out var itemType)
        && itemType.ValueKind == JsonValueKind.String
        && itemType.GetString() == "function_call";

    private static bool IsReasoningItem(JsonElement item) =>
        item.TryGetProperty("type", out var itemType)
        && itemType.ValueKind == JsonValueKind.String
        && itemType.GetString() == "reasoning";

    private static int ReadOutputIndex(JsonElement root) =>
        root.TryGetProperty("output_index", out var index)
        && index.ValueKind == JsonValueKind.Number
            ? index.GetInt32()
            : 0;

    private static ToolCallAccumulator GetToolCall(
        SortedDictionary<int, ToolCallAccumulator> toolCalls,
        int index)
    {
        if (!toolCalls.TryGetValue(index, out var accumulator))
        {
            accumulator = new ToolCallAccumulator();
            toolCalls[index] = accumulator;
        }

        return accumulator;
    }

    private static void ReadToolCall(JsonElement item, ToolCallAccumulator accumulator)
    {
        if (item.TryGetProperty("call_id", out var callId) && callId.ValueKind == JsonValueKind.String)
        {
            accumulator.Id = callId.GetString() ?? accumulator.Id;
        }

        if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            accumulator.Name = name.GetString() ?? accumulator.Name;
        }

        if (item.TryGetProperty("arguments", out var arguments)
            && arguments.ValueKind == JsonValueKind.String
            && arguments.GetString() is { Length: > 0 } completeArguments)
        {
            accumulator.Arguments.Clear();
            accumulator.Arguments.Append(completeArguments);
        }
    }

    private static IEnumerable<ToolUseBlock> FlushToolCalls(
        SortedDictionary<int, ToolCallAccumulator> toolCalls)
    {
        foreach (var (_, accumulator) in toolCalls)
        {
            yield return new ToolUseBlock(
                accumulator.Id,
                accumulator.Name,
                accumulator.Arguments.Length > 0 ? accumulator.Arguments.ToString() : "{}");
        }

        toolCalls.Clear();
    }

    private static TokenUsage? ReadUsage(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var inputTokens = usage.TryGetProperty("input_tokens", out var input)
            && input.ValueKind == JsonValueKind.Number
                ? input.GetInt32()
                : 0;
        var outputTokens = usage.TryGetProperty("output_tokens", out var output)
            && output.ValueKind == JsonValueKind.Number
                ? output.GetInt32()
                : 0;

        // OpenAI convention (inverted from Anthropic): input_tokens is the TOTAL;
        // input_tokens_details.cached_tokens (Responses API) or prompt_tokens_details.cached_tokens
        // (Chat Completions, kept as fallback) is a SUBSET. Subtract cached from InputTokens so
        // TotalInputTokens stays correct.
        var cachedTokens = 0;
        if (usage.TryGetProperty("input_tokens_details", out var details1)
            && details1.ValueKind == JsonValueKind.Object
            && details1.TryGetProperty("cached_tokens", out var c1)
            && c1.ValueKind == JsonValueKind.Number)
        {
            cachedTokens = c1.GetInt32();
        }
        else if (usage.TryGetProperty("prompt_tokens_details", out var details2)
            && details2.ValueKind == JsonValueKind.Object
            && details2.TryGetProperty("cached_tokens", out var c2)
            && c2.ValueKind == JsonValueKind.Number)
        {
            cachedTokens = c2.GetInt32();
        }

        if (inputTokens > 0 || outputTokens > 0)
        {
            var clamped = Math.Min(cachedTokens, inputTokens);
            return new TokenUsage(inputTokens - clamped, outputTokens, CacheReadTokens: clamped);
        }

        return null;
    }

    private static string? MapIncompleteReason(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("incomplete_details", out var details)
            || details.ValueKind != JsonValueKind.Object
            || !details.TryGetProperty("reason", out var reason)
            || reason.ValueKind != JsonValueKind.String)
        {
            return "incomplete";
        }

        return reason.GetString() switch
        {
            "max_output_tokens" => "max_tokens",
            { } value => value,
            null => "incomplete",
        };
    }

    private static string ReadError(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? "The Responses API stream failed.";
        }

        if (root.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var nested)
            && nested.ValueKind == JsonValueKind.String)
        {
            return nested.GetString() ?? "The Responses API stream failed.";
        }

        if (root.TryGetProperty("response", out var response)
            && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("error", out var responseError)
            && responseError.ValueKind == JsonValueKind.Object
            && responseError.TryGetProperty("message", out var responseMessage)
            && responseMessage.ValueKind == JsonValueKind.String)
        {
            return responseMessage.GetString() ?? "The Responses API stream failed.";
        }

        return "The Responses API stream failed.";
    }

    private sealed class ToolCallAccumulator
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }
}
