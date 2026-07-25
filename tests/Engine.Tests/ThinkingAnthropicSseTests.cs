using System.Text;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Unit tests for Anthropic SSE thinking-block parsing: thinking_delta, signature_delta, and
/// content_block_stop, producing normalized ThinkingDelta / ThinkingComplete events and a
/// ThinkingBlock with the correct text and signature for round-trip history replay.
/// Also covers Finding 2 (ThinkingTokens from output_tokens) and Finding 4 (redacted_thinking).
/// </summary>
public sealed class ThinkingAnthropicSseTests
{
    private static async Task<List<AssistantStreamEvent>> ReadAll(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<AssistantStreamEvent>();
        await foreach (var e in AnthropicSseReader.ReadAsync(stream))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task Thinking_delta_emits_normalized_ThinkingDelta_events()
    {
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me "}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"think."}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        var thinkingDeltas = events.Where(e => e.Kind == AssistantEventKind.ThinkingDelta).ToList();
        Assert.Equal(2, thinkingDeltas.Count);
        Assert.Equal("Let me ", thinkingDeltas[0].Text);
        Assert.Equal("think.", thinkingDeltas[1].Text);
    }

    [Fact]
    public async Task Thinking_block_stop_emits_ThinkingComplete_with_accumulated_text_and_signature()
    {
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"I am thinking."}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig_part1"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"_sig_part2"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        var complete = events.Single(e => e.Kind == AssistantEventKind.ThinkingComplete);
        Assert.NotNull(complete.Thinking);
        Assert.Equal("I am thinking.", complete.Thinking!.Text);
        Assert.Equal("sig_part1_sig_part2", complete.Thinking.Signature);
    }

    [Fact]
    public async Task Thinking_block_without_signature_produces_null_signature()
    {
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hmm"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        var complete = events.Single(e => e.Kind == AssistantEventKind.ThinkingComplete);
        Assert.NotNull(complete.Thinking);
        Assert.Null(complete.Thinking!.Signature);
    }

    [Fact]
    public async Task Thinking_and_text_blocks_at_separate_indices_are_correctly_dispatched()
    {
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"reasoning"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"answer"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        var thinkingDelta = Assert.Single(events, e => e.Kind == AssistantEventKind.ThinkingDelta);
        Assert.Equal("reasoning", thinkingDelta.Text);

        var thinkingComplete = Assert.Single(events, e => e.Kind == AssistantEventKind.ThinkingComplete);
        Assert.Equal("reasoning", thinkingComplete.Thinking!.Text);
        Assert.Equal("SIG", thinkingComplete.Thinking.Signature);

        var textDelta = Assert.Single(events, e => e.Kind == AssistantEventKind.TextDelta);
        Assert.Equal("answer", textDelta.Text);
    }

    [Fact]
    public async Task Thinking_and_tool_use_coexist_in_correct_order()
    {
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"plan"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tu_1","name":"bash"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        // ThinkingDelta events come before tool use (they are in stream order)
        var kindOrder = events.Select(e => e.Kind).ToList();
        var thinkingIdx = kindOrder.IndexOf(AssistantEventKind.ThinkingDelta);
        var toolIdx = kindOrder.IndexOf(AssistantEventKind.ToolUse);
        Assert.True(thinkingIdx < toolIdx, "ThinkingDelta should appear before ToolUse");

        var tool = events.Single(e => e.Kind == AssistantEventKind.ToolUse).ToolUse!;
        Assert.Equal("tu_1", tool.Id);
        Assert.Equal("bash", tool.Name);
    }

    [Fact]
    public async Task Stream_without_thinking_block_produces_no_thinking_events()
    {
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        Assert.DoesNotContain(events, e => e.Kind == AssistantEventKind.ThinkingDelta);
        Assert.DoesNotContain(events, e => e.Kind == AssistantEventKind.ThinkingComplete);
    }

    // -- Finding 2: ThinkingTokens from output_tokens in message_delta ---

    [Fact]
    public async Task Thinking_tokens_from_message_delta_are_carried_in_ThinkingComplete()
    {
        // Anthropic's message_delta event carries output_tokens at the end of the stream.
        // The SSE reader must associate that count with the thinking block so the caller can
        // display "N tok" in the thinking block header. ThinkingComplete must be deferred to
        // message_delta/message_stop time so the token count is available.
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"my reasoning"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":42}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        var complete = events.Single(e => e.Kind == AssistantEventKind.ThinkingComplete);
        Assert.NotNull(complete.Thinking);
        Assert.Equal("my reasoning", complete.Thinking!.Text);
        Assert.Equal("SIG", complete.Thinking.Signature);
        Assert.Equal(42, complete.ThinkingTokens);
    }

    // -- Finding 4: redacted_thinking support ---

    [Fact]
    public async Task Redacted_thinking_block_emits_ThinkingComplete_with_RedactedThinking_set()
    {
        // Anthropic emits redacted_thinking blocks with an opaque encrypted "data" field
        // at content_block_start (no deltas follow). The SSE reader must parse the data
        // from content_block_start and emit ThinkingComplete with RedactedThinking at stop.
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"OPAQUE_ENCRYPTED_DATA"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        var complete = events.Single(e => e.Kind == AssistantEventKind.ThinkingComplete);
        Assert.Null(complete.Thinking);
        Assert.NotNull(complete.RedactedThinking);
        Assert.Equal("OPAQUE_ENCRYPTED_DATA", complete.RedactedThinking!.Data);
    }

    [Fact]
    public async Task Redacted_thinking_emits_no_ThinkingDelta_events()
    {
        // redacted_thinking blocks are fully opaque; no thinking text is streamed to the UI.
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"OPAQUE"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        Assert.DoesNotContain(events, e => e.Kind == AssistantEventKind.ThinkingDelta);
    }

    [Fact]
    public async Task Redacted_thinking_block_is_serialized_for_Anthropic_history_replay()
    {
        // A ChatMessage that contains a RedactedThinkingBlock must be serialized by
        // AnthropicMessagesClient.BuildBody as {"type":"redacted_thinking","data":"..."} so
        // Anthropic does not reject the turn when replaying history after tool use.
        var request = new ChatRequest
        {
            Model = "claude-opus-4-5",
            MaxTokens = 1024,
            Messages =
            [
                new ChatMessage(ChatRole.Assistant,
                [
                    new RedactedThinkingBlock("OPAQUE_ENCRYPTED_DATA"),
                    new ToolUseBlock("tu_1", "bash", "{}"),
                ]),
            ],
        };

        var body = AnthropicMessagesClient.BuildBody(request);
        var messagesJson = body["messages"]!.ToJsonString();

        // The assistant message content must contain a redacted_thinking block
        Assert.Contains("\"type\":\"redacted_thinking\"", messagesJson);
        Assert.Contains("\"data\":\"OPAQUE_ENCRYPTED_DATA\"", messagesJson);
    }
}