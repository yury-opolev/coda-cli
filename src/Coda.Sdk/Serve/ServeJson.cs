using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve;

public static class ServeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonNode ToNode<T>(T value)
    {
        return JsonSerializer.SerializeToNode(value, Options)!;
    }

    public static T? FromNode<T>(JsonNode? node)
    {
        if (node is null)
        {
            return default;
        }

        return node.Deserialize<T>(Options);
    }
}
