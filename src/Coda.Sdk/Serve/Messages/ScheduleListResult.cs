using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>Result of <c>session/scheduleList</c>.</summary>
/// <param name="Schedules">All currently scheduled task definitions.</param>
public sealed record ScheduleListResult(
    [property: JsonPropertyName("schedules")] IReadOnlyList<ScheduledTaskDto> Schedules);
