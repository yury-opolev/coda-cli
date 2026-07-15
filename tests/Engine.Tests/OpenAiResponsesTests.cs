using System.Text;
using LlmClient;

namespace Engine.Tests;

public sealed class OpenAiResponsesTests
{
    [Fact]
    public void Request_maps_conversation_and_tools()
    {
        var request = new ChatRequest
        {
            Model = "gpt-5.6-sol",
            System = "be brief",
            Messages =
            [
                ChatMessage.UserText("read it"),
                new ChatMessage(ChatRole.Assistant,
                [
                    new TextBlock("ok"),
                    new ToolUseBlock("call_1", "read_file", """{"path":"a.txt"}"""),
                ]),
                new ChatMessage(ChatRole.User, [new ToolResultBlock("call_1", "contents")]),
            ],
            Tools = [new ToolDefinition("read_file", "Read a file", """{"type":"object"}""")],
            Effort = "high",
        };

        var body = OpenAiResponsesRequest.Build(request);

        Assert.Equal("gpt-5.6-sol", (string?)body["model"]);
        Assert.Equal("be brief", (string?)body["instructions"]);
        Assert.True((bool)body["stream"]!);
        Assert.Equal("high", (string?)body["reasoning"]?["effort"]);
        var input = body["input"]!.AsArray();
        Assert.Equal("user", (string?)input[0]!["role"]);
        Assert.Equal("input_text", (string?)input[0]!["content"]![0]!["type"]);
        Assert.Equal("assistant", (string?)input[1]!["role"]);
        Assert.Equal("function_call", (string?)input[2]!["type"]);
        Assert.Equal("""{"path":"a.txt"}""", (string?)input[2]!["arguments"]);
        Assert.Equal("function_call_output", (string?)input[3]!["type"]);
        Assert.Equal("contents", (string?)input[3]!["output"]);
        Assert.Equal("function", (string?)body["tools"]![0]!["type"]);
        Assert.Equal("read_file", (string?)body["tools"]![0]!["name"]);
    }

    [Fact]
    public async Task Reader_streams_text_tool_calls_and_usage()
    {
        const string sse = """
            data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"hello"}

            data: {"type":"response.output_item.added","output_index":1,"item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"read_file","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":1,"delta":"{\"path\":\"a.txt\"}"}

            data: {"type":"response.output_item.done","output_index":1,"item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}

            data: {"type":"response.completed","response":{"status":"completed","incomplete_details":null,"usage":{"input_tokens":12,"output_tokens":7}}}

            """;
        var events = new List<AssistantStreamEvent>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));

        await foreach (var streamEvent in OpenAiResponsesSseReader.ReadAsync(stream))
        {
            events.Add(streamEvent);
        }

        Assert.Equal("hello", string.Concat(events.Where(streamEvent => streamEvent.Kind == AssistantEventKind.TextDelta).Select(streamEvent => streamEvent.Text)));
        var tool = events.Single(streamEvent => streamEvent.Kind == AssistantEventKind.ToolUse).ToolUse!;
        Assert.Equal("call_1", tool.Id);
        Assert.Equal("read_file", tool.Name);
        Assert.Equal("""{"path":"a.txt"}""", tool.InputJson);
        var done = events.Single(streamEvent => streamEvent.Kind == AssistantEventKind.Done);
        Assert.Equal("tool_use", done.StopReason);
        Assert.Equal(new TokenUsage(12, 7), done.Usage);
    }

    [Fact]
    public async Task Reader_surfaces_nested_response_failure_message()
    {
        const string sse = """
            data: {"type":"response.failed","response":{"error":{"message":"encrypted content could not be verified"}}}

            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in OpenAiResponsesSseReader.ReadAsync(stream))
            {
            }
        });

        Assert.Contains("encrypted content could not be verified", exception.Message);
    }

    [Fact]
    public async Task Reader_rejects_stream_that_ends_without_terminal_event()
    {
        const string sse = """
            data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"partial"}

            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in OpenAiResponsesSseReader.ReadAsync(stream))
            {
            }
        });

        Assert.Contains("terminal event", exception.Message);
    }
}
