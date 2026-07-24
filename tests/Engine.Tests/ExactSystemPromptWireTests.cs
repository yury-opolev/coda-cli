using LlmClient;

namespace Engine.Tests;

public sealed class ExactSystemPromptWireTests
{
    [Fact]
    public void Request_builders_omit_an_empty_Anthropic_system_field_but_preserve_empty_OpenAI_values()
    {
        var empty = new ChatRequest
        {
            Model = "test-model",
            System = string.Empty,
            Messages = [ChatMessage.UserText("hello")],
        };
        var absent = empty with { System = null };

        var anthropicEmpty = AnthropicMessagesClient.BuildBody(empty);
        var anthropicAbsent = AnthropicMessagesClient.BuildBody(absent);
        Assert.False(anthropicEmpty.ContainsKey("system"));
        Assert.False(anthropicAbsent.ContainsKey("system"));

        var chatEmpty = OpenAiRequest.Build(empty);
        var chatAbsent = OpenAiRequest.Build(absent);
        Assert.Equal("system", (string?)chatEmpty["messages"]![0]!["role"]);
        Assert.Equal(string.Empty, (string?)chatEmpty["messages"]![0]!["content"]);
        Assert.Equal("user", (string?)chatAbsent["messages"]![0]!["role"]);

        var responsesEmpty = OpenAiResponsesRequest.Build(empty);
        var responsesAbsent = OpenAiResponsesRequest.Build(absent);
        Assert.Equal(string.Empty, (string?)responsesEmpty["instructions"]);
        Assert.False(responsesAbsent.ContainsKey("instructions"));
    }

    [Fact]
    public void Anthropic_request_preserves_a_non_empty_cache_controlled_system_text_block()
    {
        var body = AnthropicMessagesClient.BuildBody(new ChatRequest
        {
            Model = "test-model",
            System = "system prompt",
            Messages = [ChatMessage.UserText("hello")],
        });

        Assert.Equal("text", (string?)body["system"]![0]!["type"]);
        Assert.Equal("system prompt", (string?)body["system"]![0]!["text"]);
        Assert.Equal("ephemeral", (string?)body["system"]![0]!["cache_control"]!["type"]);
    }
}
