using System.Text;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Unit tests for OpenAI Responses SSE reasoning-summary parsing:
/// response.reasoning_summary_text.delta emits normalized ThinkingDelta events, and
/// response.completed emits a ThinkingComplete event with the accumulated reasoning text.
/// </summary>
public sealed class ThinkingOpenAiSseTests
{
    private static async Task<List<AssistantStreamEvent>> ReadAll(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<AssistantStreamEvent>();
        await foreach (var e in OpenAiResponsesSseReader.ReadAsync(stream))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task Reasoning_summary_delta_emits_normalized_ThinkingDelta_events()
    {
        const string sse = """
            data: {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","output_index":0,"summary_index":0,"delta":"Let me "}

            data: {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","output_index":0,"summary_index":0,"delta":"think."}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":10,"output_tokens":5}}}

            """;

        var events = await ReadAll(sse);

        var thinkingDeltas = events.Where(e => e.Kind == AssistantEventKind.ThinkingDelta).ToList();
        Assert.Equal(2, thinkingDeltas.Count);
        Assert.Equal("Let me ", thinkingDeltas[0].Text);
        Assert.Equal("think.", thinkingDeltas[1].Text);
    }

    [Fact]
    public async Task Reasoning_summary_emits_ThinkingComplete_at_response_completed()
    {
        const string sse = """
            data: {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","output_index":0,"summary_index":0,"delta":"I reasoned carefully."}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":8,"output_tokens":4}}}

            """;

        var events = await ReadAll(sse);

        var complete = events.Single(e => e.Kind == AssistantEventKind.ThinkingComplete);
        Assert.NotNull(complete.Thinking);
        Assert.Equal("I reasoned carefully.", complete.Thinking!.Text);
        // OpenAI reasoning is server-side; no client replay signature.
        Assert.Null(complete.Thinking.Signature);
    }

    [Fact]
    public async Task Reasoning_summary_ThinkingComplete_emitted_before_Done()
    {
        const string sse = """
            data: {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","output_index":0,"summary_index":0,"delta":"think"}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":8,"output_tokens":4}}}

            """;

        var events = await ReadAll(sse);

        var kindOrder = events.Select(e => e.Kind).ToList();
        var thinkingCompleteIdx = kindOrder.IndexOf(AssistantEventKind.ThinkingComplete);
        var doneIdx = kindOrder.IndexOf(AssistantEventKind.Done);
        Assert.True(thinkingCompleteIdx < doneIdx, "ThinkingComplete should appear before Done");
    }

    [Fact]
    public async Task Stream_without_reasoning_produces_no_thinking_events()
    {
        const string sse = """
            data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"hello"}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":5,"output_tokens":1}}}

            """;

        var events = await ReadAll(sse);

        Assert.DoesNotContain(events, e => e.Kind == AssistantEventKind.ThinkingDelta);
        Assert.DoesNotContain(events, e => e.Kind == AssistantEventKind.ThinkingComplete);
    }

    [Fact]
    public async Task Reasoning_summary_and_text_both_parsed_correctly()
    {
        const string sse = """
            data: {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","output_index":0,"summary_index":0,"delta":"my plan"}

            data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":1,"content_index":0,"delta":"my answer"}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":10,"output_tokens":5}}}

            """;

        var events = await ReadAll(sse);

        var thinkingDelta = Assert.Single(events.Where(e => e.Kind == AssistantEventKind.ThinkingDelta));
        Assert.Equal("my plan", thinkingDelta.Text);

        var textDelta = Assert.Single(events.Where(e => e.Kind == AssistantEventKind.TextDelta));
        Assert.Equal("my answer", textDelta.Text);

        Assert.Single(events.Where(e => e.Kind == AssistantEventKind.ThinkingComplete));
        Assert.Single(events.Where(e => e.Kind == AssistantEventKind.Done));
    }
}
