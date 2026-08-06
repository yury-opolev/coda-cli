using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coda.Mcp;

/// <summary>A tool advertised by an MCP server (from a <c>tools/list</c> result).</summary>
/// <param name="SchemaCoerced">
/// True when the server's advertised <c>inputSchema</c> was not usable as-is and
/// <see cref="ParseList"/> repaired it. Drives the user-facing warning and the
/// <see cref="McpSchemaPolicy"/> decision.
/// </param>
public sealed record McpToolInfo(
    string Name,
    string Description,
    string InputSchemaJson,
    bool ReadOnly,
    bool SchemaCoerced = false)
{
    /// <summary>The schema used when a server advertises none, or advertises a non-object.</summary>
    private const string EmptyObjectSchema = """{"type":"object","properties":{}}""";

    /// <summary>Parse the <c>tools/list</c> result into tool infos.</summary>
    public static IReadOnlyList<McpToolInfo> ParseList(JsonElement toolsResult)
    {
        var tools = new List<McpToolInfo>();
        if (!toolsResult.TryGetProperty("tools", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return tools;
        }

        foreach (var tool in array.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            var (schema, coerced) = NormalizeSchema(tool);

            var readOnly = tool.TryGetProperty("annotations", out var ann)
                && ann.ValueKind == JsonValueKind.Object
                && ann.TryGetProperty("readOnlyHint", out var ro)
                && ro.ValueKind == JsonValueKind.True;

            tools.Add(new McpToolInfo(name!, description, schema, readOnly, coerced));
        }

        return tools;
    }

    /// <summary>
    /// Returns a tool's input schema, guaranteed to be a JSON object carrying
    /// <c>"type": "object"</c>, together with whether it had to be repaired.
    /// </summary>
    /// <remarks>
    /// The model APIs reject a tool whose input schema has no <c>type</c> — Anthropic fails the
    /// whole <em>request</em> with <c>tools.N.custom.input_schema.type: Field required</c>, which
    /// kills every turn, not just the offending tool call. Some published MCP servers ship schemas
    /// that are valid JSON objects yet carry only <c>$schema</c> (e.g.
    /// <c>@playwright/mcp@1.52.0-alpha-2025-03-26</c>). Normalising here keeps the damage local to
    /// the tool.
    /// <para>
    /// The repair is deliberately least-destructive: every advertised key is preserved and only
    /// what is missing is added, so a server that ships <c>properties</c> but forgets <c>type</c>
    /// ends up completely correct. An absent <c>properties</c> is only defaulted when the server
    /// gave none, leaving the tool callable with no arguments — strictly better than unusable.
    /// </para>
    /// </remarks>
    private static (string Schema, bool Coerced) NormalizeSchema(JsonElement tool)
    {
        if (!tool.TryGetProperty("inputSchema", out var s) || s.ValueKind == JsonValueKind.Null)
        {
            // Absent (or explicitly null) is the documented way to say "no arguments", not a defect.
            return (EmptyObjectSchema, false);
        }

        if (s.ValueKind != JsonValueKind.Object)
        {
            // Present but not an object — e.g. a server that JSON-*stringifies* its schema. Every
            // advertised parameter is lost here, so this must be reported and must be visible to
            // the skip/strict policies; it is emphatically not the same as advertising none.
            return (EmptyObjectSchema, true);
        }

        var hasObjectType = s.TryGetProperty("type", out var t)
            && t.ValueKind == JsonValueKind.String
            && t.GetString() == "object";

        if (hasObjectType)
        {
            return (s.GetRawText(), false);
        }

        JsonObject node;
        try
        {
            node = JsonNode.Parse(s.GetRawText())!.AsObject();
        }
        catch (JsonException)
        {
            return (EmptyObjectSchema, true);
        }

        node["type"] = "object";
        node["properties"] ??= new JsonObject();

        // Note this makes the schema *safe*, not necessarily *functional*: a schema whose real
        // shape lives behind a top-level $ref/allOf gains inert type+properties siblings and
        // becomes callable with no arguments. That is the least-destructive outcome available
        // without resolving arbitrary JSON Schema, and it is still strictly better than a tool
        // that poisons every request.
        return (node.ToJsonString(), true);
    }

    /// <summary>Format a <c>tools/call</c> result's content array into plain text + error flag.</summary>
    public static (string Text, bool IsError) FormatCallResult(JsonElement callResult)
    {
        var isError = callResult.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;

        var builder = new StringBuilder();
        if (callResult.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "text" && part.TryGetProperty("text", out var text))
                {
                    builder.Append(text.GetString());
                    builder.Append('\n');
                }
                else if (type is not null)
                {
                    builder.Append('[').Append(type).Append(" content]").Append('\n');
                }
            }
        }

        var result = builder.ToString().TrimEnd('\n');
        return (result.Length == 0 ? "(no content)" : result, isError);
    }
}
