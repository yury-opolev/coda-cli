using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>Params for <c>session/scheduleDelete</c>.</summary>
/// <param name="Id">The definition id to delete, as returned by <c>session/scheduleList</c>
/// or <c>session/scheduleCreate</c>.</param>
public sealed record ScheduleDeleteParams(
    [property: JsonPropertyName("id")] string? Id = null);
