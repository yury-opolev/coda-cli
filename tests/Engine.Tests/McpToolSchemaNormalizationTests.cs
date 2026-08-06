using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// Ingestion-time normalisation of MCP <c>inputSchema</c>s. MCP servers are untrusted
/// third-party input — a published build can advertise a schema the model APIs reject, and such
/// a rejection fails the whole request, not just that tool. Nothing may enter the registry
/// without a usable <c>"type": "object"</c>.
/// </summary>
public sealed class McpToolSchemaNormalizationTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static McpToolInfo ParseOne(string toolJson) =>
        Assert.Single(McpToolInfo.ParseList(Json($$"""{"tools":[{{toolJson}}]}""")));

    private static JsonObject SchemaOf(McpToolInfo info) =>
        JsonNode.Parse(info.InputSchemaJson)!.AsObject();

    // ── the normalisation table ─────────────────────────────────────────────

    [Fact]
    public void Absent_schema_becomes_an_empty_object_schema()
    {
        var tool = ParseOne("""{"name":"t","description":"d"}""");

        var schema = SchemaOf(tool);
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.NotNull(schema["properties"]);
        Assert.False(tool.SchemaCoerced);
    }

    [Theory]
    [InlineData("\"not an object\"")]
    [InlineData("[]")]
    [InlineData("42")]
    public void A_present_but_non_object_schema_is_flattened_and_flagged(string schemaJson)
    {
        // A JSON-stringified schema loses every advertised parameter, so it must be visible to the
        // user and to the skip/strict policies — unlike an absent schema, which is legitimate.
        var tool = ParseOne($$"""{"name":"t","description":"d","inputSchema":{{schemaJson}}}""");

        var schema = SchemaOf(tool);
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.NotNull(schema["properties"]);
        Assert.True(tool.SchemaCoerced);
    }

    [Fact]
    public void An_explicitly_null_schema_means_no_arguments_and_is_not_flagged()
    {
        var tool = ParseOne("""{"name":"t","description":"d","inputSchema":null}""");

        Assert.Equal("object", SchemaOf(tool)["type"]!.GetValue<string>());
        Assert.False(tool.SchemaCoerced);
    }

    [Fact]
    public void Empty_object_schema_gains_a_type()
    {
        var tool = ParseOne("""{"name":"t","description":"d","inputSchema":{}}""");

        Assert.Equal("object", SchemaOf(tool)["type"]!.GetValue<string>());
        Assert.True(tool.SchemaCoerced);
    }

    [Fact]
    public void Schema_with_only_dollar_schema_keeps_it_and_gains_a_type()
    {
        // The @playwright/mcp@1.52.0-alpha-2025-03-26 shape that caused the incident.
        var tool = ParseOne(
            """{"name":"browser_navigate","description":"d","inputSchema":{"$schema":"http://json-schema.org/draft-07/schema#"}}""");

        var schema = SchemaOf(tool);
        Assert.Equal("http://json-schema.org/draft-07/schema#", schema["$schema"]!.GetValue<string>());
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.NotNull(schema["properties"]);
        Assert.True(tool.SchemaCoerced);
    }

    [Fact]
    public void Properties_survive_when_type_is_missing()
    {
        var tool = ParseOne(
            """{"name":"t","description":"d","inputSchema":{"properties":{"a":{"type":"string"}},"required":["a"]}}""");

        var schema = SchemaOf(tool);
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Equal("string", schema["properties"]!["a"]!["type"]!.GetValue<string>());
        Assert.Equal("a", schema["required"]!.AsArray()[0]!.GetValue<string>());
        Assert.True(tool.SchemaCoerced);
    }

    [Fact]
    public void A_valid_schema_is_returned_unchanged()
    {
        const string Valid = """{"type":"object","properties":{"a":{"type":"string"}}}""";
        var tool = ParseOne($$"""{"name":"t","description":"d","inputSchema":{{Valid}}}""");

        Assert.Equal(Valid, tool.InputSchemaJson);
        Assert.False(tool.SchemaCoerced);
    }

    [Fact]
    public void A_non_object_declared_type_is_still_forced_to_object()
    {
        // Unlike the wire layer (which only guarantees *a* type), a tool's input schema must be
        // an object: MCP arguments are always a name/value map.
        var tool = ParseOne("""{"name":"t","description":"d","inputSchema":{"type":"string"}}""");

        Assert.Equal("object", SchemaOf(tool)["type"]!.GetValue<string>());
        Assert.True(tool.SchemaCoerced);
    }

    [Fact]
    public void Coercion_does_not_add_properties_when_the_server_already_supplied_them()
    {
        var tool = ParseOne(
            """{"name":"t","description":"d","inputSchema":{"properties":{"a":{"type":"string"}}}}""");

        var schema = SchemaOf(tool);
        Assert.Single(schema["properties"]!.AsObject());
    }

    // ── policy ──────────────────────────────────────────────────────────────

    [Fact]
    public void Coerce_policy_keeps_the_tool()
    {
        var tools = McpToolInfo.ParseList(
            Json("""{"tools":[{"name":"t","inputSchema":{"$schema":"x"}}]}"""));

        var kept = McpSchemaPolicyFilter.Apply(tools, McpSchemaPolicy.Coerce, "playwright");

        Assert.Single(kept);
        Assert.True(kept[0].SchemaCoerced);
    }

    [Fact]
    public void Skip_policy_drops_only_the_coerced_tools()
    {
        var tools = McpToolInfo.ParseList(Json("""
            {"tools":[
              {"name":"bad","inputSchema":{"$schema":"x"}},
              {"name":"good","inputSchema":{"type":"object","properties":{}}}
            ]}
            """));

        var kept = McpSchemaPolicyFilter.Apply(tools, McpSchemaPolicy.Skip, "playwright");

        Assert.Equal("good", Assert.Single(kept).Name);
    }

    [Fact]
    public void Strict_policy_refuses_the_whole_server()
    {
        var tools = McpToolInfo.ParseList(Json("""
            {"tools":[
              {"name":"bad","inputSchema":{"$schema":"x"}},
              {"name":"good","inputSchema":{"type":"object"}}
            ]}
            """));

        var ex = Assert.Throws<McpException>(
            () => McpSchemaPolicyFilter.Apply(tools, McpSchemaPolicy.Strict, "playwright"));

        Assert.Contains("playwright", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bad", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_policy_accepts_a_server_with_no_coerced_schemas()
    {
        var tools = McpToolInfo.ParseList(Json("""{"tools":[{"name":"good","inputSchema":{"type":"object"}}]}"""));

        var kept = McpSchemaPolicyFilter.Apply(tools, McpSchemaPolicy.Strict, "playwright");

        Assert.Single(kept);
    }

    [Theory]
    [InlineData(null, McpSchemaPolicy.Coerce)]
    [InlineData("", McpSchemaPolicy.Coerce)]
    [InlineData("nonsense", McpSchemaPolicy.Coerce)]
    [InlineData("coerce", McpSchemaPolicy.Coerce)]
    [InlineData("skip", McpSchemaPolicy.Skip)]
    [InlineData("SKIP", McpSchemaPolicy.Skip)]
    [InlineData("Strict", McpSchemaPolicy.Strict)]
    public void Policy_parses_from_settings_text(string? raw, McpSchemaPolicy expected)
    {
        Assert.Equal(expected, McpSchemaPolicyFilter.Parse(raw));
    }

    // ── reporting ───────────────────────────────────────────────────────────

    [Fact]
    public void Coercion_summary_names_the_server_and_the_count()
    {
        var tools = McpToolInfo.ParseList(Json("""
            {"tools":[
              {"name":"a","inputSchema":{"$schema":"x"}},
              {"name":"b","inputSchema":{"$schema":"x"}},
              {"name":"c","inputSchema":{"type":"object"}}
            ]}
            """));

        var summary = McpSchemaPolicyFilter.DescribeCoercions("playwright", tools);

        Assert.NotNull(summary);
        Assert.Contains("playwright", summary, StringComparison.Ordinal);
        Assert.Contains("2", summary, StringComparison.Ordinal);
        Assert.Contains("coerced", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Skip_summary_says_dropped_not_coerced()
    {
        // Claiming a repair that did not happen is worse than saying nothing: under skip the
        // tools are gone, and the message must not imply they are present-but-repaired.
        var tools = McpToolInfo.ParseList(Json("""{"tools":[{"name":"a","inputSchema":{"$schema":"x"}}]}"""));

        var summary = McpSchemaPolicyFilter.DescribeCoercions("playwright", tools, McpSchemaPolicy.Skip);

        Assert.Contains("dropped", summary!, StringComparison.Ordinal);
        Assert.DoesNotContain("coerced", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void No_summary_when_every_schema_is_valid()
    {
        var tools = McpToolInfo.ParseList(Json("""{"tools":[{"name":"a","inputSchema":{"type":"object"}}]}"""));

        Assert.Null(McpSchemaPolicyFilter.DescribeCoercions("playwright", tools));
    }

    [Fact]
    public void Summary_hints_at_a_broken_package_when_every_schema_is_invalid()
    {
        var tools = McpToolInfo.ParseList(Json("""
            {"tools":[
              {"name":"a","inputSchema":{"$schema":"x"}},
              {"name":"b","inputSchema":{"$schema":"x"}}
            ]}
            """));

        var summary = McpSchemaPolicyFilter.DescribeCoercions("playwright", tools);

        Assert.Contains("every", summary, StringComparison.OrdinalIgnoreCase);
    }
}
