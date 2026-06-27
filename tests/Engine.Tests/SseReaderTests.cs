using System.Text;
using LlmClient;

namespace Engine.Tests;

public sealed class SseReaderTests
{
    private static async Task<List<AssistantStreamEvent>> ReadAll(string sse)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<AssistantStreamEvent>();
        await foreach (var e in AnthropicSseReader.ReadAsync(stream, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task Parses_text_deltas_and_stop()
    {
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello "}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"world"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        var text = string.Concat(events.Where(e => e.Kind == AssistantEventKind.TextDelta).Select(e => e.Text));
        Assert.Equal("Hello world", text);

        var done = events.Single(e => e.Kind == AssistantEventKind.Done);
        Assert.Equal("end_turn", done.StopReason);
    }

    [Fact]
    public async Task Accumulates_tool_use_input_json()
    {
        const string sse = """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"tu_1","name":"read_file"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"a.txt\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAll(sse);

        var tool = events.Single(e => e.Kind == AssistantEventKind.ToolUse).ToolUse!;
        Assert.Equal("tu_1", tool.Id);
        Assert.Equal("read_file", tool.Name);
        Assert.Equal("{\"path\":\"a.txt\"}", tool.InputJson);

        Assert.Equal("tool_use", events.Single(e => e.Kind == AssistantEventKind.Done).StopReason);
    }
}
