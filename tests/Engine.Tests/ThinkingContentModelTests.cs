using System.Text.Json.Nodes;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Content-model round-trip tests: a ThinkingBlock with a signature serializes through
/// AnthropicMessagesClient.BuildBody and is included in the assistant turn verbatim so
/// Anthropic can verify the signature on the next request. A block without a signature is
/// silently dropped from history to prevent a 400 error.
/// </summary>
public sealed class ThinkingContentModelTests
{
    [Fact]
    public void ThinkingBlock_with_signature_serializes_to_anthropic_thinking_object()
    {
        var request = new ChatRequest
        {
            Model = "claude-opus-4-5-20251101",
            Messages =
            [
                ChatMessage.UserText("hello"),
                new ChatMessage(ChatRole.Assistant,
                [
                    new ThinkingBlock("my reasoning", "SIG123"),
                    new TextBlock("my answer"),
                ]),
            ],
        };

        var body = AnthropicMessagesClient.BuildBody(request);

        var messages = body["messages"]!.AsArray();
        // user + assistant
        Assert.Equal(2, messages.Count);
        var assistantContent = messages[1]!["content"]!.AsArray();
        // thinking + text
        Assert.Equal(2, assistantContent.Count);

        var thinkingObj = assistantContent[0]!.AsObject();
        Assert.Equal("thinking", (string?)thinkingObj["type"]);
        Assert.Equal("my reasoning", (string?)thinkingObj["thinking"]);
        Assert.Equal("SIG123", (string?)thinkingObj["signature"]);

        var textObj = assistantContent[1]!.AsObject();
        Assert.Equal("text", (string?)textObj["type"]);
        Assert.Equal("my answer", (string?)textObj["text"]);
    }

    [Fact]
    public void ThinkingBlock_without_signature_is_silently_dropped_from_history()
    {
        var request = new ChatRequest
        {
            Model = "claude-opus-4-5-20251101",
            Messages =
            [
                ChatMessage.UserText("hello"),
                new ChatMessage(ChatRole.Assistant,
                [
                    new ThinkingBlock("orphan reasoning", null),  // no signature → must be dropped
                    new TextBlock("my answer"),
                ]),
            ],
        };

        var body = AnthropicMessagesClient.BuildBody(request);

        var messages = body["messages"]!.AsArray();
        var assistantContent = messages[1]!["content"]!.AsArray();
        // Only the TextBlock survives; the ThinkingBlock without a signature is dropped.
        Assert.Single(assistantContent);
        Assert.Equal("text", (string?)assistantContent[0]!["type"]);
    }

    [Fact]
    public void ThinkingBlock_roundtrip_multiple_blocks_preserves_order()
    {
        // Simulate: thinking1 → tool_use → thinking2 → text
        var request = new ChatRequest
        {
            Model = "claude-opus-4-5-20251101",
            Messages =
            [
                ChatMessage.UserText("go"),
                new ChatMessage(ChatRole.Assistant,
                [
                    new ThinkingBlock("first thought", "SIG_A"),
                    new ToolUseBlock("tu_1", "bash", "{}"),
                    new ThinkingBlock("second thought", "SIG_B"),
                    new TextBlock("done"),
                ]),
                new ChatMessage(ChatRole.User, [new ToolResultBlock("tu_1", "ok")]),
            ],
        };

        var body = AnthropicMessagesClient.BuildBody(request);

        var messages = body["messages"]!.AsArray();
        var assistantContent = messages[1]!["content"]!.AsArray();
        Assert.Equal(4, assistantContent.Count);
        Assert.Equal("thinking", (string?)assistantContent[0]!["type"]);
        Assert.Equal("SIG_A", (string?)assistantContent[0]!["signature"]);
        Assert.Equal("tool_use", (string?)assistantContent[1]!["type"]);
        Assert.Equal("thinking", (string?)assistantContent[2]!["type"]);
        Assert.Equal("SIG_B", (string?)assistantContent[2]!["signature"]);
        Assert.Equal("text", (string?)assistantContent[3]!["type"]);
    }

    [Fact]
    public void ThinkingBlock_record_equality_and_properties()
    {
        var block = new ThinkingBlock("text", "sig");
        Assert.Equal("text", block.Text);
        Assert.Equal("sig", block.Signature);

        var noSig = new ThinkingBlock("text", null);
        Assert.Null(noSig.Signature);
        Assert.NotEqual(block, noSig);
    }
}
