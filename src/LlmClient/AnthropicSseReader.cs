using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace LlmClient;

/// <summary>
/// Parses an Anthropic Messages streaming (SSE) body into
/// <see cref="AssistantStreamEvent"/>s. Pure with respect to HTTP — give it any
/// stream — so it is unit-testable with canned streams. Handles
/// content_block_start/delta(text_delta, input_json_delta)/stop,
/// message_delta(stop_reason), and message_stop.
/// </summary>
public static class AnthropicSseReader
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
        // Accumulator for the in-flight tool_use block, keyed by content index.
        var toolUses = new Dictionary<int, (string Id, string Name, StringBuilder Input)>();
        string? stopReason = null;
        var inputTokens = 0;
        var outputTokens = 0;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue; // ignore "event:" lines and blank separators
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
                continue; // skip malformed events rather than abort the stream
            }

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var msg)
                        && msg.TryGetProperty("usage", out var startUsage))
                    {
                        // Anthropic sends input_tokens exactly once in message_start as a
                        // cumulative total for the entire request (including cache tokens).
                        // Use assignment (=), not +=, so this is last-wins and never double-counted.
                        var rawInput = startUsage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number
                            ? it.GetInt32()
                            : 0;

                        // cache_creation_input_tokens and cache_read_input_tokens are billed as
                        // input tokens and are part of the same single message_start usage event.
                        var cacheCreate = startUsage.TryGetProperty("cache_creation_input_tokens", out var cct) && cct.ValueKind == JsonValueKind.Number
                            ? cct.GetInt32()
                            : 0;

                        var cacheRead = startUsage.TryGetProperty("cache_read_input_tokens", out var crt) && crt.ValueKind == JsonValueKind.Number
                            ? crt.GetInt32()
                            : 0;

                        inputTokens = rawInput + cacheCreate + cacheRead;
                    }

                    break;

                case "content_block_start":
                    {
                        var index = GetIndex(root);
                        if (root.TryGetProperty("content_block", out var block)
                            && block.TryGetProperty("type", out var bt)
                            && bt.GetString() == "tool_use")
                        {
                            var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                            var name = block.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
                            toolUses[index] = (id, name, new StringBuilder());
                        }

                        break;
                    }

                case "content_block_delta":
                    {
                        var index = GetIndex(root);
                        if (root.TryGetProperty("delta", out var delta) && delta.TryGetProperty("type", out var dt))
                        {
                            switch (dt.GetString())
                            {
                                case "text_delta":
                                    var text = delta.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        yield return AssistantStreamEvent.Delta(text!);
                                    }

                                    break;

                                case "input_json_delta":
                                    if (toolUses.TryGetValue(index, out var acc)
                                        && delta.TryGetProperty("partial_json", out var pj))
                                    {
                                        acc.Input.Append(pj.GetString());
                                    }

                                    break;
                            }
                        }

                        break;
                    }

                case "content_block_stop":
                    {
                        var index = GetIndex(root);
                        if (toolUses.Remove(index, out var finished))
                        {
                            var input = finished.Input.Length > 0 ? finished.Input.ToString() : "{}";
                            yield return AssistantStreamEvent.Tool(new ToolUseBlock(finished.Id, finished.Name, input));
                        }

                        break;
                    }

                case "message_delta":
                    if (root.TryGetProperty("delta", out var md))
                    {
                        if (md.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                        {
                            stopReason = sr.GetString();
                        }
                    }

                    // output_tokens in message_delta is a cumulative total for the entire
                    // response so far. Anthropic sends one message_delta per stream, so
                    // last-wins (=) is correct — never use += here or you'd double-count.
                    if (root.TryGetProperty("usage", out var deltaUsage)
                        && deltaUsage.TryGetProperty("output_tokens", out var ot)
                        && ot.ValueKind == JsonValueKind.Number)
                    {
                        outputTokens = ot.GetInt32();
                    }

                    break;

                case "message_stop":
                    var usage = (inputTokens > 0 || outputTokens > 0)
                        ? new TokenUsage(inputTokens, outputTokens)
                        : null;
                    yield return AssistantStreamEvent.Finished(stopReason, usage);
                    break;
            }
        }
    }

    private static int GetIndex(JsonElement root) =>
        root.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
}
