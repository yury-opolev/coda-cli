using System.Text.Json.Nodes;

using Coda.Client.Transport;

namespace Coda.Client.Protocol;

/// <summary>
/// The authoritative <c>session/getState</c> snapshot. Its full shape is large,
/// deeply nested and additive across contract versions, so it is exposed as the
/// raw <see cref="Raw"/> node plus a few convenience accessors for the fields a
/// client reconstructs a view from. Nothing is dropped, and
/// <see cref="As{T}"/> lets a caller bind its own subset when it wants to.
/// </summary>
public sealed class StateSnapshot
{
    internal StateSnapshot(JsonNode? raw)
    {
        this.Raw = raw ?? new JsonObject();
    }

    /// <summary>The complete snapshot exactly as the engine sent it.</summary>
    public JsonNode Raw { get; }

    /// <summary>The engine instance this snapshot came from; the identity to fence
    /// later reads against.</summary>
    public string? EngineInstanceId => this.Raw["engineInstanceId"]?.GetValue<string>();

    public string? SessionId => this.Raw["sessionId"]?.GetValue<string>();

    /// <summary>The event cursor valid at the snapshot, or null when the engine did
    /// not report one.</summary>
    public long? Cursor => ReadLong("cursor");

    /// <summary>The lifecycle string (<c>initializing</c>, <c>ready</c>, and so
    /// on), or null when absent.</summary>
    public string? Lifecycle => this.Raw["lifecycle"]?.GetValue<string>();

    /// <summary>Binds the whole snapshot to a caller-defined type. Unknown fields
    /// are ignored, not rejected.</summary>
    public T? As<T>() => CodaJson.FromNode<T>(this.Raw);

    private long? ReadLong(string name)
    {
        JsonNode? value = this.Raw[name];
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

/// <summary>
/// The <c>session/getHistory</c> result. Like <see cref="StateSnapshot"/> the
/// full shape is large and paged, so it is surfaced raw with convenience
/// accessors for the consistency fences a paging client must carry between
/// reads: the engine instance, history epoch and committed length.
/// </summary>
public sealed class HistoryResult
{
    internal HistoryResult(JsonNode? raw)
    {
        this.Raw = raw ?? new JsonObject();
    }

    /// <summary>The complete result exactly as the engine sent it.</summary>
    public JsonNode Raw { get; }

    public string? SessionId => this.Raw["sessionId"]?.GetValue<string>();

    public string? EngineInstanceId => this.Raw["engineInstanceId"]?.GetValue<string>();

    public long? HistoryEpoch => ReadLong("historyEpoch");

    public long? HistoryLength => ReadLong("historyLength");

    public long? NextIndex => ReadLong("nextIndex");

    public bool? Truncated => this.Raw["truncated"]?.GetValue<bool>();

    /// <summary>Binds the whole result to a caller-defined type.</summary>
    public T? As<T>() => CodaJson.FromNode<T>(this.Raw);

    private long? ReadLong(string name)
    {
        JsonNode? value = this.Raw[name];
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
