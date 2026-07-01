using System.Text.Json;
using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// Covers <see cref="McpResultParsers"/>, the shared <c>resources/*</c> and <c>prompts/*</c>
/// result parsers that the stdio and HTTP transports both depend on to produce identical shapes.
/// </summary>
public sealed class McpResultParsersTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParseResourceList_reads_uri_name_and_optional_mimetype()
    {
        var resources = McpResultParsers.ParseResourceList(Json("""
            {"resources":[
                {"uri":"file:///a.txt","name":"A","mimeType":"text/plain"},
                {"uri":"file:///b.bin","name":"B"}
            ]}
            """), "srv");

        Assert.Equal(2, resources.Count);
        Assert.Equal(new McpResourceInfo("srv", "file:///a.txt", "A", "text/plain"), resources[0]);
        // mimeType is optional and surfaces as null.
        Assert.Equal(new McpResourceInfo("srv", "file:///b.bin", "B", null), resources[1]);
    }

    [Fact]
    public void ParseResourceList_skips_entries_missing_uri_or_name()
    {
        var resources = McpResultParsers.ParseResourceList(Json("""
            {"resources":[
                {"uri":"file:///ok","name":"keep"},
                {"name":"no-uri"},
                {"uri":"file:///no-name"},
                {"uri":"","name":"empty-uri"}
            ]}
            """), "srv");

        Assert.Equal("file:///ok", Assert.Single(resources).Uri);
    }

    [Fact]
    public void ParseResourceList_returns_empty_when_property_missing_or_not_array()
    {
        Assert.Empty(McpResultParsers.ParseResourceList(Json("""{}"""), "srv"));
        Assert.Empty(McpResultParsers.ParseResourceList(Json("""{"resources":"nope"}"""), "srv"));
    }

    [Fact]
    public void ParseResourceContents_concatenates_text_and_marks_binary()
    {
        var text = McpResultParsers.ParseResourceContents(Json("""
            {"contents":[
                {"text":"hello "},
                {"blob":"AAAA"},
                {"text":"world"}
            ]}
            """));

        Assert.Equal("hello [binary content]world", text);
    }

    [Fact]
    public void ParseResourceContents_returns_empty_when_no_contents()
    {
        Assert.Equal(string.Empty, McpResultParsers.ParseResourceContents(Json("""{}""")));
    }

    [Fact]
    public void ParsePromptList_reads_name_and_optional_description()
    {
        var prompts = McpResultParsers.ParsePromptList(Json("""
            {"prompts":[
                {"name":"review","description":"Review code"},
                {"name":"summarize"}
            ]}
            """), "srv");

        Assert.Equal(2, prompts.Count);
        Assert.Equal(new McpPromptInfo("srv", "review", "Review code"), prompts[0]);
        Assert.Equal(new McpPromptInfo("srv", "summarize", null), prompts[1]);
    }

    [Fact]
    public void ParsePromptList_skips_entries_without_a_name()
    {
        var prompts = McpResultParsers.ParsePromptList(Json("""
            {"prompts":[{"description":"nameless"},{"name":"keep"}]}
            """), "srv");

        Assert.Equal("keep", Assert.Single(prompts).Name);
    }

    [Fact]
    public void ParsePromptMessages_formats_role_and_text_one_per_line()
    {
        var messages = McpResultParsers.ParsePromptMessages(Json("""
            {"messages":[
                {"role":"user","content":{"type":"text","text":"hi"}},
                {"role":"assistant","content":{"type":"text","text":"hello"}}
            ]}
            """));

        Assert.Equal("user: hi\nassistant: hello", messages);
    }

    [Fact]
    public void ParsePromptMessages_skips_messages_missing_role_or_text()
    {
        var messages = McpResultParsers.ParsePromptMessages(Json("""
            {"messages":[
                {"role":"user","content":{"type":"text","text":"kept"}},
                {"content":{"type":"text","text":"no-role"}},
                {"role":"assistant","content":{"type":"image"}}
            ]}
            """));

        Assert.Equal("user: kept", messages);
    }
}
