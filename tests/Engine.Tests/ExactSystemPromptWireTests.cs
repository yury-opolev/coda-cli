using LlmClient;

namespace Engine.Tests;

public sealed class ExactSystemPromptWireTests
{
    [Fact]
    public void Request_builders_preserve_an_explicit_empty_system_value_and_omit_null()
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
        Assert.Equal(string.Empty, (string?)anthropicEmpty["system"]![0]!["text"]);
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
}
