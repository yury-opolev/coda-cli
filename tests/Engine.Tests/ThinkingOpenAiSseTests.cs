using System.Text;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Unit tests for OpenAI Responses SSE reasoning-summary parsing:
/// response.reasoning_summary_text.delta emits normalized ThinkingDelta events, and
/// response.completed emits a ThinkingComplete event with the accumulated reasoning text.
/// Also covers Finding 2 (ThinkingTokens) and Finding 3 (encrypted reasoning round-trip).
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

        var thinkingDelta = Assert.Single(events, e => e.Kind == AssistantEventKind.ThinkingDelta);
        Assert.Equal("my plan", thinkingDelta.Text);

        var textDelta = Assert.Single(events, e => e.Kind == AssistantEventKind.TextDelta);
        Assert.Equal("my answer", textDelta.Text);

        Assert.Single(events, e => e.Kind == AssistantEventKind.ThinkingComplete);
        Assert.Single(events, e => e.Kind == AssistantEventKind.Done);
    }

    // -- Finding 2: ThinkingTokens from output_tokens at response.completed ---

    [Fact]
    public async Task Reasoning_output_tokens_are_carried_as_ThinkingTokens_in_ThinkingComplete()
    {
        // When the Responses API emits reasoning summary deltas and then completes with
        // output_tokens in the usage, the ThinkingComplete event must carry that count so
        // callers can display "N tok" in the thinking block header.
        const string sse = """
            data: {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","output_index":0,"summary_index":0,"delta":"my reasoning"}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":5,"output_tokens":99}}}

            """;

        var events = await ReadAll(sse);

        var complete = events.Single(e => e.Kind == AssistantEventKind.ThinkingComplete);
        Assert.NotNull(complete.Thinking);
        Assert.Equal("my reasoning", complete.Thinking!.Text);
        Assert.Equal(99, complete.ThinkingTokens);
    }

    // -- Finding 3: OpenAI encrypted reasoning content round-trip ---

    [Fact]
    public async Task Reasoning_item_with_encrypted_content_carries_signature_in_ThinkingComplete()
    {
        // When the Responses API returns a reasoning output_item with an encrypted_content field
        // (used for stateless/store=false requests), the SSE reader must capture it and convey
        // it as ThinkingBlock.Signature so the caller can replay the reasoning across turns.
        const string sse = """
            data: {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","output_index":0,"summary_index":0,"delta":"my reasoning"}

            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","encrypted_content":"SYNTHETIC_ENCRYPTED_BLOB"}}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":5,"output_tokens":3}}}

            """;

        var events = await ReadAll(sse);

        var complete = events.Single(e => e.Kind == AssistantEventKind.ThinkingComplete);
        Assert.NotNull(complete.Thinking);
        Assert.Equal("my reasoning", complete.Thinking!.Text);
        Assert.NotNull(complete.Thinking.Signature);
        // Signature must contain the encrypted content so it can be replayed
        Assert.Contains("SYNTHETIC_ENCRYPTED_BLOB", complete.Thinking.Signature);
    }

    [Fact]
    public async Task ThinkingBlock_with_signature_is_serialized_as_reasoning_input_item()
    {
        // When an assistant turn contains a ThinkingBlock with a non-null Signature
        // (captured from an earlier reasoning output_item), OpenAiResponsesRequest.Build
        // must include a reasoning input item so the model retains its reasoning state.
        const string signatureJson = """{"id":"rs_1","encrypted_content":"SYNTHETIC_ENCRYPTED_BLOB"}""";
        var request = new ChatRequest
        {
            Model = "o3",
            Messages =
            [
                new ChatMessage(ChatRole.Assistant,
                [
                    new ThinkingBlock("my reasoning", signatureJson),
                    new ToolUseBlock("call_1", "bash", "{}"),
                ]),
            ],
        };

        var body = OpenAiResponsesRequest.Build(request);
        var inputJson = body["input"]!.ToJsonString();

        Assert.Contains("\"type\":\"reasoning\"", inputJson);
        Assert.Contains("\"id\":\"rs_1\"", inputJson);
        Assert.Contains("SYNTHETIC_ENCRYPTED_BLOB", inputJson);
    }
}