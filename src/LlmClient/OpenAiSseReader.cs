using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace LlmClient;

/// <summary>
/// Parses an OpenAI-style chat-completions streaming (SSE) body — used by GitHub
/// Copilot — into <see cref="AssistantStreamEvent"/>s. Pure w.r.t. HTTP (give it
/// any stream) so it is unit-testable. Handles <c>choices[].delta.content</c>,
/// <c>delta.tool_calls[]</c> accumulation (by index, with streamed
/// <c>function.arguments</c>), and <c>finish_reason</c>.
/// </summary>
public static class OpenAiSseReader
{
    public static IAsyncEnumerable<AssistantStreamEvent> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var reader = new StreamReader(stream, Encoding.UTF8);
        return ReadAsync(reader, cancellationToken);
    }

    public static async IAsyncEnumerable<AssistantStreamEvent> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Accumulate streamed tool calls by their position index.
        var toolCalls = new SortedDictionary<int, ToolCallAccumulator>();
        string? stopReason = null;
        TokenUsage? usage = null;

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
                using var doc = JsonDocument.Parse(payload);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            // A usage-only chunk (when stream_options.include_usage=true) has no choices.
            if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                var promptTokens = usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : 0;
                var completionTokens = usageEl.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : 0;
                if (promptTokens > 0 || completionTokens > 0)
                {
                    usage = new TokenUsage(promptTokens, completionTokens);
                }
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                stopReason = MapFinishReason(fr.GetString());
            }

            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 } text)
            {
                yield return AssistantStreamEvent.Delta(text);
            }

            if (delta.TryGetProperty("tool_calls", out var deltaToolCalls) && deltaToolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in deltaToolCalls.EnumerateArray())
                {
                    AccumulateToolCall(toolCalls, tc);
                }
            }
        }

        // Flush completed tool calls (in index order) then signal done.
        foreach (var (_, acc) in toolCalls)
        {
            var input = acc.Arguments.Length > 0 ? acc.Arguments.ToString() : "{}";
            yield return AssistantStreamEvent.Tool(new ToolUseBlock(acc.Id, acc.Name, input));
        }

        yield return AssistantStreamEvent.Finished(stopReason, usage);
    }

    private static void AccumulateToolCall(SortedDictionary<int, ToolCallAccumulator> toolCalls, JsonElement tc)
    {
        var index = tc.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
        if (!toolCalls.TryGetValue(index, out var acc))
        {
            acc = new ToolCallAccumulator();
            toolCalls[index] = acc;
        }

        if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            acc.Id = id.GetString() ?? acc.Id;
        }

        if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
        {
            if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                acc.Name = name.GetString() ?? acc.Name;
            }

            if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
            {
                acc.Arguments.Append(args.GetString());
            }
        }
    }

    private static string? MapFinishReason(string? finishReason) => finishReason switch
    {
        "tool_calls" => "tool_use",
        "stop" => "end_turn",
        "length" => "max_tokens",
        null => null,
        _ => finishReason,
    };

    private sealed class ToolCallAccumulator
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }
}
