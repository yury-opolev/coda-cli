using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmClient;

/// <summary>Helpers for emitting model-API-safe tool input schemas.</summary>
public static class ToolSchema
{
    /// <summary>
    /// Parses a tool input schema, guaranteeing a JSON object carrying <c>"type": "object"</c>.
    /// Never throws.
    /// </summary>
    /// <remarks>
    /// Both model APIs we target require a tool's input schema to be an object schema, and they
    /// enforce it by failing the <em>entire</em> request — Anthropic with
    /// <c>tools.N.custom.input_schema.type: Field required</c>. One malformed tool would therefore
    /// break every turn in the session, not just calls to that tool. Since
    /// <see cref="ToolDefinition.InputSchemaJson"/> is an unvalidated string supplied by MCP
    /// servers, skills, and every built-in tool alike, the serialiser is the last line of defence
    /// and is made unconditionally safe here rather than at each of the three call sites.
    /// <para>
    /// A declared non-object <c>type</c> is overwritten rather than passed through: tool arguments
    /// are always a name/value map, so there is no legitimate non-object top-level tool schema for
    /// either API, and passing one through would reproduce exactly the request-fatal rejection this
    /// helper exists to prevent.
    /// </para>
    /// </remarks>
    public static JsonNode ParseSafe(string? json)
    {
        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (JsonException)
        {
            // Unparseable input is indistinguishable from an absent schema for our purposes.
        }

        if (node is not JsonObject obj)
        {
            return EmptyObjectSchema();
        }

        if (obj["type"] is not JsonValue value
            || value.GetValueKind() != JsonValueKind.String
            || value.GetValue<string>() != "object")
        {
            obj["type"] = "object";
        }

        return obj;
    }

    /// <summary>A fresh, unparented <c>{"type":"object","properties":{}}</c> node.</summary>
    private static JsonObject EmptyObjectSchema() =>
        new() { ["type"] = "object", ["properties"] = new JsonObject() };
}
