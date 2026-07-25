using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// Params for <c>session/scheduleCreate</c>. Mirrors <see cref="Coda.Agent.Scheduling.ScheduleCreateRequest"/>:
/// supply <c>prompt</c> and exactly one of <c>every</c>, <c>at</c>, or <c>cron</c>.
/// </summary>
/// <param name="Name">Optional human-readable label.</param>
/// <param name="Prompt">The prompt to execute on each firing.</param>
/// <param name="Every">Recurring interval such as <c>3m</c>, <c>2h</c>, or <c>1d</c>.</param>
/// <param name="At">One-shot ISO-8601 date-time, with or without an explicit offset.</param>
/// <param name="Cron">Five-field cron expression, e.g. <c>*/5 * * * *</c>.</param>
/// <param name="TimeZone">Optional IANA/Windows timezone id for cron or offset-less <c>at</c> values.</param>
public sealed record ScheduleCreateParams(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("prompt")] string Prompt = "",
    [property: JsonPropertyName("every")] string? Every = null,
    [property: JsonPropertyName("at")] string? At = null,
    [property: JsonPropertyName("cron")] string? Cron = null,
    [property: JsonPropertyName("timeZone")] string? TimeZone = null);
