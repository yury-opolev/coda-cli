using System.Text;
using System.Text.Json.Nodes;
using LlmClient;

namespace Engine.Tests;

public sealed class OpenAiSseReaderTests
{
    private static async Task<List<AssistantStreamEvent>> ReadAll(string sse)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<AssistantStreamEvent>();
        await foreach (var e in OpenAiSseReader.ReadAsync(stream, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task Streams_content_and_maps_stop()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"Hello "},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"world"},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var events = await ReadAll(sse);
        var text = string.Concat(events.Where(e => e.Kind == AssistantEventKind.TextDelta).Select(e => e.Text));
        Assert.Equal("Hello world", text);
        Assert.Equal("end_turn", events.Single(e => e.Kind == AssistantEventKind.Done).StopReason);
    }

    [Fact]
    public async Task Accumulates_streamed_tool_call()
    {
        const string sse = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":""}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.txt\"}"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;

        var events = await ReadAll(sse);
        var tool = events.Single(e => e.Kind == AssistantEventKind.ToolUse).ToolUse!;
        Assert.Equal("call_1", tool.Id);
        Assert.Equal("read_file", tool.Name);
        Assert.Equal("{\"path\":\"a.txt\"}", tool.InputJson);
        Assert.Equal("tool_use", events.Single(e => e.Kind == AssistantEventKind.Done).StopReason);
    }

    [Fact]
    public async Task Empty_or_usage_only_chunks_are_ignored()
    {
        const string sse = """
            data: {"choices":[]}

            data: {"usage":{"total_tokens":5}}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var events = await ReadAll(sse);
        Assert.DoesNotContain(events, e => e.Kind == AssistantEventKind.TextDelta);
        Assert.Equal("end_turn", events.Single(e => e.Kind == AssistantEventKind.Done).StopReason);
    }
}

public sealed class OpenAiRequestTests
{
    [Fact]
    public void Maps_system_user_and_tools()
    {
        var request = new ChatRequest
        {
            Model = "gpt-4o",
            System = "be brief",
            Messages = [ChatMessage.UserText("hi")],
            Tools = [new ToolDefinition("read_file", "Read a file", """{"type":"object"}""")],
        };

        var body = OpenAiRequest.Build(request);

        Assert.Equal("gpt-4o", (string?)body["model"]);
        Assert.True((bool)body["stream"]!);
        var messages = body["messages"]!.AsArray();
        Assert.Equal("system", (string?)messages[0]!["role"]);
        Assert.Equal("be brief", (string?)messages[0]!["content"]);
        Assert.Equal("user", (string?)messages[1]!["role"]);
        Assert.Equal("hi", (string?)messages[1]!["content"]);
        var tool = body["tools"]!.AsArray()[0]!;
        Assert.Equal("function", (string?)tool["type"]);
        Assert.Equal("read_file", (string?)tool["function"]!["name"]);
    }

    [Fact]
    public void Omits_max_tokens_so_copilot_uses_its_own_default()
    {
        // Copilot's OpenAI-compatible API makes max_tokens optional. Sending an explicit
        // cap caused premature stop=max_tokens truncations (the cap bounded reasoning too),
        // so — like opencode for github-copilot — coda leaves it unset and lets Copilot decide.
        var request = new ChatRequest
        {
            Model = "claude-opus-4-8",
            MaxTokens = 16000,
            Messages = [ChatMessage.UserText("hi")],
        };

        var body = OpenAiRequest.Build(request);

        Assert.False(body.ContainsKey("max_tokens"));
    }

    [Fact]
    public void Maps_assistant_tool_call_and_tool_result()
    {
        var request = new ChatRequest
        {
            Model = "gpt-4o",
            Messages =
            [
                ChatMessage.UserText("read it"),
                new ChatMessage(ChatRole.Assistant,
                [
                    new TextBlock("ok"),
                    new ToolUseBlock("call_1", "read_file", """{"path":"a.txt"}"""),
                ]),
                new ChatMessage(ChatRole.User, [new ToolResultBlock("call_1", "file contents")]),
            ],
        };

        var messages = OpenAiRequest.Build(request)["messages"]!.AsArray();

        // [0] user, [1] assistant with tool_calls, [2] tool result
        var assistant = messages[1]!;
        Assert.Equal("assistant", (string?)assistant["role"]);
        Assert.Equal("ok", (string?)assistant["content"]);
        var call = assistant["tool_calls"]!.AsArray()[0]!;
        Assert.Equal("call_1", (string?)call["id"]);
        Assert.Equal("read_file", (string?)call["function"]!["name"]);
        Assert.Equal("""{"path":"a.txt"}""", (string?)call["function"]!["arguments"]);

        var toolMsg = messages[2]!;
        Assert.Equal("tool", (string?)toolMsg["role"]);
        Assert.Equal("call_1", (string?)toolMsg["tool_call_id"]);
        Assert.Equal("file contents", (string?)toolMsg["content"]);
    }
}
