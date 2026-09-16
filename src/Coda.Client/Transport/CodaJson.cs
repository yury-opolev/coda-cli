using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Coda.Client.Transport;

/// <summary>
/// The single serializer configuration used for every payload on the wire.
///
/// The engine emits and expects camelCase names and omits optional fields
/// entirely rather than writing <c>null</c>, so we mirror both: <see
/// cref="JsonIgnoreCondition.WhenWritingNull"/> means a null DTO field is
/// absent on the wire, which is exactly how the engine reads "use the default"
/// for a field like a goal budget. Unknown fields are ignored on read (never a
/// throw), which is what lets an older client tolerate a newer engine.
/// </summary>
public static class CodaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Read tolerance: an additive numeric field the engine starts sending
        // as a string, or vice versa, must not tear a session down.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    public static T? FromNode<T>(JsonNode? node) =>
        node is null ? default : node.Deserialize<T>(Options);

    public static byte[] ToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
}
