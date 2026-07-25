using System.Text;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Unit tests for Anthropic SSE thinking-block parsing: thinking_delta, signature_delta, and
/// content_block_stop, producing normalized ThinkingDelta / ThinkingComplete events and a
/// ThinkingBlock with the correct text and signature for round-trip history replay.
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

        // Thinking events come before tool use
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
}
