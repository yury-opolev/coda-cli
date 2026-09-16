using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Coda.Client.Protocol;

/// <summary>
/// One stored/replayed notification, exactly as <c>session/getEvents</c> returns
/// it. <see cref="Params"/> is the whole original payload — already carrying its
/// own <c>seq</c> and <c>engineInstanceId</c> because the bus injects them before
/// storing — kept as a raw node so a replayed frame is never truncated or
/// re-summarised, and an unknown event type still round-trips intact.
/// </summary>
public sealed record EventEnvelope
{
    public long Seq { get; init; }

    public string EngineInstanceId { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public JsonNode? Params { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>Parameters for <c>session/getEvents</c>: a bounded replay for a
/// matching engine instance, starting after <see cref="AfterCursor"/>.</summary>
public sealed record GetEventsParams
{
    /// <summary>Fences the read to one engine process; a cursor minted by a
    /// replacement instance is refused rather than answered from the wrong bus.</summary>
    public string EngineInstanceId { get; init; } = string.Empty;

    public long AfterCursor { get; init; }

    public long? Limit { get; init; }
}

/// <summary>
/// The <c>session/getEvents</c> result. <see cref="Truncated"/> means more events
/// exist past this page; <see cref="OldestAvailableCursor"/> exposes ring
/// eviction so a gap is explicit rather than a silent hole.
/// </summary>
public sealed record GetEventsResult
{
    public string EngineInstanceId { get; init; } = string.Empty;

    public IReadOnlyList<EventEnvelope> Events { get; init; } = Array.Empty<EventEnvelope>();

    public long NextCursor { get; init; }

    public bool Truncated { get; init; }

    public long OldestAvailableCursor { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>
/// Parameters for <c>session/getHistory</c>. The instance/epoch/length fields are
/// consistency fences: supply the ones from a prior read to detect a moved
/// history rather than silently combining pages from different conversations.
/// </summary>
public sealed record GetHistoryParams
{
    /// <summary>Absent means the live session; a saved id is scoped to the
    /// engine workspace.</summary>
    public string? SessionId { get; init; }

    public long? HistoryEpoch { get; init; }

    public long? ExpectedHistoryLength { get; init; }

    public string? EngineInstanceId { get; init; }

    public long? SinceIndex { get; init; }

    public long? Limit { get; init; }

    public bool? IncludeLive { get; init; }
}
