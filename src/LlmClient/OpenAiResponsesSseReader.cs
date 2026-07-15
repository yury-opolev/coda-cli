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

                case "response.output_item.added":
                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var item)
                        && item.ValueKind == JsonValueKind.Object
                        && IsFunctionCall(item))
                    {
                        var index = ReadOutputIndex(root);
                        var accumulator = GetToolCall(toolCalls, index);
                        ReadToolCall(item, accumulator);
                        hasToolCall = true;
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
                    var stopReason = type == "response.incomplete"
                        ? MapIncompleteReason(response)
                        : hasToolCall ? "tool_use" : "end_turn";
                    yield return AssistantStreamEvent.Finished(stopReason, ReadUsage(response));
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
        return inputTokens > 0 || outputTokens > 0
            ? new TokenUsage(inputTokens, outputTokens)
            : null;
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
