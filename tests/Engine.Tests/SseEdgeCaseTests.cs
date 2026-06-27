using System.Text;
using LlmClient;

namespace Engine.Tests;

public sealed class SseEdgeCaseTests
{
    private static async Task<List<AssistantStreamEvent>> ReadAll(string sse)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<AssistantStreamEvent>();
        await foreach (var streamEvent in AnthropicSseReader.ReadAsync(stream, CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    [Fact]
    public async Task Empty_stream_yields_no_events()
    {
        var events = await ReadAll(string.Empty);

        Assert.Empty(events);
    }

    [Fact]
    public async Task Only_message_stop_yields_single_done_with_null_stop_reason()
    {
        const string sse = """
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        var done = Assert.Single(events);
        Assert.Equal(AssistantEventKind.Done, done.Kind);
        Assert.Null(done.StopReason);
    }

    [Fact]
    public async Task Malformed_json_data_lines_are_skipped_without_throwing()
    {
        const string sse = """
            data: {not valid json

            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}

            data: also broken }

            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        var text = string.Concat(events.Where(e => e.Kind == AssistantEventKind.TextDelta).Select(e => e.Text));
        Assert.Equal("ok", text);
        Assert.Single(events, e => e.Kind == AssistantEventKind.Done);
    }
}
