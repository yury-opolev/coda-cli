using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// Wire representation of a single scheduled task definition returned by
/// <c>session/scheduleList</c> and <c>session/scheduleCreate</c>. A positional record with
/// explicit camelCase <c>[JsonPropertyName]</c> attributes keeps the JSON shape stable —
/// never a ValueTuple <c>Item1</c>/<c>Item2</c> projection. Optional fields are omitted from
/// the wire when null.
/// </summary>
/// <param name="Id">The definition's persisted short identifier.</param>
/// <param name="Name">Optional human-readable label.</param>
/// <param name="Kind">Schedule kind as a lowercase string: <c>interval</c>, <c>at</c>, <c>cron</c>.</param>
/// <param name="Prompt">The prompt executed on each firing.</param>
/// <param name="Rule">Human-readable rule description (e.g. <c>interval (every 5m)</c>).</param>
/// <param name="TimeZone">The timezone the definition is interpreted in.</param>
/// <param name="NextRunUtc">Next scheduled execution time (UTC).</param>
/// <param name="State">Runtime execution state: <c>idle</c>, <c>running</c>, or <c>pending</c>.</param>
/// <param name="ActiveTaskId">The running/pending task id, when one exists.</param>
/// <param name="LastOutcome">Short human-readable summary of the last terminal outcome, when one exists.</param>
public sealed record ScheduledTaskDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("timeZone")] string TimeZone,
    [property: JsonPropertyName("nextRunUtc")] DateTimeOffset NextRunUtc,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("activeTaskId")] string? ActiveTaskId,
    [property: JsonPropertyName("lastOutcome")] string? LastOutcome);
