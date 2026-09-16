using System.Text.Json.Nodes;

using Coda.Client.Transport;

namespace Coda.Client;

/// <summary>
/// A one-way <c>event/*</c> notification from the engine, surfaced on the event
/// stream. The raw <see cref="Params"/> node is kept intact so an event method
/// this build has never seen — or a known event carrying a new field — reaches
/// the consumer without loss. Typed access is opt-in through <see cref="As{T}"/>.
/// </summary>
public sealed class CodaEvent
{
    internal CodaEvent(string method, JsonNode? @params)
    {
        this.Method = method;
        this.Params = @params;
    }

    /// <summary>The event method, e.g. <c>event/assistantText</c>. Preserved even
    /// when unknown to this build.</summary>
    public string Method { get; }

    /// <summary>The raw params payload.</summary>
    public JsonNode? Params { get; }

    /// <summary>The bus-injected sequence number, when present. Absent (null) means
    /// the engine did not carry it — treat that as unknown, not zero.</summary>
    public long? Seq => ReadLong("seq");

    /// <summary>The engine instance that emitted the event, when present.</summary>
    public string? EngineInstanceId => this.Params?["engineInstanceId"]?.GetValue<string>();

    /// <summary>Deserialises the params into a typed payload. Unknown fields are
    /// ignored rather than throwing.</summary>
    public T? As<T>() => CodaJson.FromNode<T>(this.Params);

    private long? ReadLong(string name)
    {
        JsonNode? value = this.Params?[name];
        if (value is null)
        {
            return null;
        }

        try
        {
            return value.GetValue<long>();
        }
        catch
        {
            return null;
        }
    }
}
