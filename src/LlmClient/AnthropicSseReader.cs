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
        // Accumulator for the in-flight thinking block, keyed by content index.
        var thinkingBlocks = new Dictionary<int, (StringBuilder Text, StringBuilder Signature)>();
        // Data for in-flight redacted_thinking blocks (complete in content_block_start, no deltas).
        var redactedThinkingData = new Dictionary<int, string>();
        // Thinking/redacted-thinking events deferred until message_stop so output_tokens is available.
        var pendingThinkingDones = new List<AssistantStreamEvent>();
        string? stopReason = null;
        var inputTokens = 0;
        var outputTokens = 0;
        var cacheReadTokens = 0;
        var cacheWrite5mTokens = 0;
        var cacheWrite1hTokens = 0;

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
                        // The three input counters in message_start are DISJOINT:
                        //   input_tokens              — uncached tokens only (the remainder after the last cache breakpoint)
                        //   cache_read_input_tokens   — tokens served from an existing cache entry
                        //   cache_creation_input_tokens — tokens written to a new cache entry
                        // total_input_tokens = input_tokens + cache_read_input_tokens + cache_creation_input_tokens
                        // Use assignment (=), not +=; message_start arrives exactly once per request.
                        inputTokens = startUsage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number
                            ? it.GetInt32()
                            : 0;

                        cacheReadTokens = startUsage.TryGetProperty("cache_read_input_tokens", out var crt) && crt.ValueKind == JsonValueKind.Number
                            ? crt.GetInt32()
                            : 0;

                        var totalCacheCreate = startUsage.TryGetProperty("cache_creation_input_tokens", out var cct) && cct.ValueKind == JsonValueKind.Number
                            ? cct.GetInt32()
                            : 0;

                        // When the cache_creation sub-object is present it splits writes by TTL.
                        // Otherwise, fall back to attributing all writes to the 5m bucket (the default TTL).
                        if (startUsage.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
                        {
                            cacheWrite5mTokens = cc.TryGetProperty("ephemeral_5m_input_tokens", out var c5m) && c5m.ValueKind == JsonValueKind.Number
                                ? c5m.GetInt32()
                                : 0;
                            cacheWrite1hTokens = cc.TryGetProperty("ephemeral_1h_input_tokens", out var c1h) && c1h.ValueKind == JsonValueKind.Number
                                ? c1h.GetInt32()
                                : 0;
                        }
                        else
                        {
                            cacheWrite5mTokens = totalCacheCreate;
                            cacheWrite1hTokens = 0;
                        }
                    }

                    break;

                case "content_block_start":
                    {
                        var index = GetIndex(root);
                        if (root.TryGetProperty("content_block", out var block)
                            && block.TryGetProperty("type", out var bt))
                        {
                            var blockType = bt.GetString();
                            if (blockType == "tool_use")
                            {
                                var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                                var name = block.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
                                toolUses[index] = (id, name, new StringBuilder());
                            }
                            else if (blockType == "thinking")
                            {
                                // Register an accumulator for the thinking block at this index.
                                thinkingBlocks[index] = (new StringBuilder(), new StringBuilder());
                            }
                            else if (blockType == "redacted_thinking")
                            {
                                // The opaque encrypted data is complete in content_block_start.
                                // No deltas follow; capture verbatim for round-trip replay.
                                var data = block.TryGetProperty("data", out var dataEl)
                                    ? dataEl.GetString() ?? string.Empty
                                    : string.Empty;
                                redactedThinkingData[index] = data;
                            }
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

                                case "thinking_delta":
                                    // Emit a normalized thinking-text delta and accumulate in the block.
                                    var thinkingText = delta.TryGetProperty("thinking", out var tt) ? tt.GetString() : null;
                                    if (!string.IsNullOrEmpty(thinkingText) && thinkingBlocks.TryGetValue(index, out var thacc))
                                    {
                                        thacc.Text.Append(thinkingText);
                                        yield return AssistantStreamEvent.ThinkingDelta(thinkingText!);
                                    }

                                    break;

                                case "signature_delta":
                                    // Accumulate the Anthropic signed-thinking signature (not streamed to the UI).
                                    var sig = delta.TryGetProperty("signature", out var sv) ? sv.GetString() : null;
                                    if (sig is not null && thinkingBlocks.TryGetValue(index, out var sigacc))
                                    {
                                        sigacc.Signature.Append(sig);
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
                        else if (thinkingBlocks.Remove(index, out var completedThinking))
                        {
                            // Defer ThinkingDone to message_stop so output_tokens from message_delta
                            // can be attached as ThinkingTokens (Anthropic reports tokens at the end).
                            var thinkingText = completedThinking.Text.ToString();
                            var signature = completedThinking.Signature.Length > 0
                                ? completedThinking.Signature.ToString()
                                : null;
                            pendingThinkingDones.Add(AssistantStreamEvent.ThinkingDone(new ThinkingBlock(thinkingText, signature)));
                        }
                        else if (redactedThinkingData.Remove(index, out var redactedData))
                        {
                            // Defer redacted-thinking to message_stop for consistency (tokens not applicable,
                            // but emitting here and there would cause ordering differences).
                            pendingThinkingDones.Add(AssistantStreamEvent.RedactedThinkingDone(new RedactedThinkingBlock(redactedData)));
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
                    // Emit all deferred thinking/redacted-thinking events now that output_tokens
                    // (from message_delta) is available. Regular ThinkingDone events carry the count;
                    // RedactedThinkingDone events do not (no user-visible burst to attribute).
                    foreach (var pending in pendingThinkingDones)
                    {
                        if (pending.Thinking is { } block)
                        {
                            yield return AssistantStreamEvent.ThinkingDone(
                                block,
                                thinkingTokens: outputTokens > 0 ? outputTokens : null);
                        }
                        else
                        {
                            yield return pending;
                        }
                    }

                    var usage = (inputTokens > 0 || outputTokens > 0 || cacheReadTokens > 0 || cacheWrite5mTokens > 0 || cacheWrite1hTokens > 0)
                        ? new TokenUsage(inputTokens, outputTokens, cacheReadTokens, cacheWrite5mTokens, cacheWrite1hTokens)
                        : null;
                    yield return AssistantStreamEvent.Finished(stopReason, usage);
                    break;
            }
        }
    }

    private static int GetIndex(JsonElement root) =>
        root.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
}
