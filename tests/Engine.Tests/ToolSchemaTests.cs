using System.Text.Json;
using System.Text.Json.Nodes;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// The wire-layer invariant: whatever a tool advertises as its input schema, the body we send
/// carries a JSON object with <c>"type": "object"</c>. Both target APIs reject a typeless tool
/// schema by failing the <em>entire</em> request, so one bad tool would otherwise break every turn.
/// </summary>
public sealed class ToolSchemaTests
{
    // ── ParseSafe ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]                       // malformed
    [InlineData("[]")]                      // not an object
    [InlineData("\"a string\"")]            // not an object
    [InlineData("42")]                      // not an object
    [InlineData("null")]                    // JSON null
    [InlineData("{}")]                      // object, but no type
    [InlineData("""{"$schema":"http://json-schema.org/draft-07/schema#"}""")]
    [InlineData("""{"type":123}""")]        // type present but not a string
    [InlineData("""{"type":null}""")]
    public void ParseSafe_always_yields_an_object_schema_with_a_type(string? json)
    {
        var node = ToolSchema.ParseSafe(json);

        var obj = Assert.IsType<JsonObject>(node);
        Assert.Equal("object", obj["type"]!.GetValue<string>());
    }

    [Fact]
    public void ParseSafe_preserves_advertised_keys_when_coercing()
    {
        var node = ToolSchema.ParseSafe("""{"$schema":"http://json-schema.org/draft-07/schema#"}""");

        var obj = Assert.IsType<JsonObject>(node);
        Assert.Equal("http://json-schema.org/draft-07/schema#", obj["$schema"]!.GetValue<string>());
        Assert.Equal("object", obj["type"]!.GetValue<string>());
    }

    [Fact]
    public void ParseSafe_leaves_a_valid_schema_untouched()
    {
        const string Valid = """{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""";

        var obj = Assert.IsType<JsonObject>(ToolSchema.ParseSafe(Valid));

        Assert.Equal("object", obj["type"]!.GetValue<string>());
        Assert.NotNull(obj["properties"]);
        Assert.NotNull(obj["required"]);
    }

    [Fact]
    public void ParseSafe_forces_a_non_object_declared_type_to_object()
    {
        // Tool arguments are always a name/value map: no API accepts a non-object tool schema, so
        // passing one through would reproduce the very rejection this helper prevents.
        var obj = Assert.IsType<JsonObject>(ToolSchema.ParseSafe("""{"type":"string"}"""));

        Assert.Equal("object", obj["type"]!.GetValue<string>());
    }

    [Fact]
    public void ParseSafe_returns_an_independent_node_each_call()
    {
        const string Json = """{"type":"object","properties":{}}""";

        var first = ToolSchema.ParseSafe(Json);
        var second = ToolSchema.ParseSafe(Json);

        // Each result must be unparented so it can be assigned into a request body without
        // "node already has a parent" failures when the same schema is serialised twice.
        _ = new JsonObject { ["a"] = first };
        _ = new JsonObject { ["b"] = second };
    }

    // ── the three serialisers ───────────────────────────────────────────────

    private const string TypelessSchema = """{"$schema":"http://json-schema.org/draft-07/schema#"}""";

    private static ChatRequest RequestWith(string schemaJson) => new()
    {
        Model = "test-model",
        Messages = [new ChatMessage(ChatRole.User, [new TextBlock("hi")])],
        Tools = [new ToolDefinition("browser_navigate", "Navigate", schemaJson)],
    };

    [Fact]
    public void Anthropic_body_never_emits_a_typeless_tool_schema()
    {
        var body = AnthropicMessagesClient.BuildBody(RequestWith(TypelessSchema));

        var schema = body["tools"]!.AsArray()[0]!["input_schema"]!.AsObject();
        Assert.Equal("object", schema["type"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_chat_body_never_emits_a_typeless_tool_schema()
    {
        var body = OpenAiRequest.Build(RequestWith(TypelessSchema));

        var schema = body["tools"]!.AsArray()[0]!["function"]!["parameters"]!.AsObject();
        Assert.Equal("object", schema["type"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_responses_body_never_emits_a_typeless_tool_schema()
    {
        var body = OpenAiResponsesRequest.Build(RequestWith(TypelessSchema));

        var schema = body["tools"]!.AsArray()[0]!["parameters"]!.AsObject();
        Assert.Equal("object", schema["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    public void All_serialisers_survive_a_hostile_schema(string schemaJson)
    {
        var request = RequestWith(schemaJson);

        Assert.Equal("object", AnthropicMessagesClient.BuildBody(request)["tools"]!
            .AsArray()[0]!["input_schema"]!["type"]!.GetValue<string>());
        Assert.Equal("object", OpenAiRequest.Build(request)["tools"]!
            .AsArray()[0]!["function"]!["parameters"]!["type"]!.GetValue<string>());
        Assert.Equal("object", OpenAiResponsesRequest.Build(request)["tools"]!
            .AsArray()[0]!["parameters"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_count_tokens_body_never_emits_a_typeless_tool_schema()
    {
        var body = AnthropicMessagesClient.BuildCountTokensBody(RequestWith(TypelessSchema));

        var tools = Assert.IsType<JsonArray>(body["tools"]);
        Assert.NotEmpty(tools);
        Assert.Equal("object", tools[0]!["input_schema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Tool_input_arguments_are_not_coerced_by_the_schema_helper()
    {
        // ParseSafe is for schemas only. Tool *inputs* must keep their own shape, so the
        // tool-use serialisation path must not be routed through it.
        var request = new ChatRequest
        {
            Model = "test-model",
            Messages =
            [
                new ChatMessage(ChatRole.Assistant, [new ToolUseBlock("id-1", "browser_navigate", """{"url":"https://x"}""")]),
            ],
        };

        var body = AnthropicMessagesClient.BuildBody(request);
        var json = body.ToJsonString();

        Assert.Contains("https://x", json, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }
}
