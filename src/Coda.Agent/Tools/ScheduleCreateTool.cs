using System.Text.Json;
using Coda.Agent.Scheduling;

namespace Coda.Agent.Tools;

/// <summary>
/// Creates a new scheduled task from an <c>every</c>/<c>at</c>/<c>cron</c> selector. Delegates
/// validation, parsing, and persistence to <see cref="ScheduleControlService.Create"/> so the
/// parse→store→project flow is identical regardless of the calling surface. The tool itself only
/// mutates the schedule store (no file/system side effects), so it runs without a user permission
/// prompt; the session's schedule runtime is what later executes each due definition as a
/// background agent.
/// </summary>
public sealed class ScheduleCreateTool : ITool
{
    private readonly TimeProvider timeProvider;
    private readonly Func<TimeZoneInfo> localTimeZone;

    /// <summary>
    /// Creates the tool. <paramref name="timeProvider"/> defaults to <see cref="TimeProvider.System"/>
    /// and <paramref name="localTimeZone"/> defaults to the machine-local zone; both are injectable
    /// so tests get deterministic, zone-independent results.
    /// </summary>
    public ScheduleCreateTool(TimeProvider? timeProvider = null, Func<TimeZoneInfo>? localTimeZone = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.localTimeZone = localTimeZone ?? (() => TimeZoneInfo.Local);
    }

    public string Name => "schedule_create";

    public string Description =>
        "Create a scheduled task that runs a prompt. Provide the 'prompt' plus exactly one schedule " +
        "selector: 'every' for a repeating interval (e.g. \"3m\", \"2h\", \"1d\"; minimum one minute), " +
        "'at' for a one-shot ISO-8601 time (with or without an offset), or 'cron' for a repeating " +
        "five-field cron rule. Use optional 'timeZone' (e.g. \"America/New_York\") to interpret a cron " +
        "rule, and optional 'name' as a label. Returns the task id and its next run time.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "name":     { "type": "string", "description": "Optional human-readable label" },
            "prompt":   { "type": "string", "description": "Prompt to execute when the task fires" },
            "every":    { "type": "string", "description": "Recurring interval such as \"3m\", \"2h\", or \"1d\" (minimum one minute)" },
            "at":       { "type": "string", "description": "One-shot ISO-8601 date-time, with or without an explicit offset" },
            "cron":     { "type": "string", "description": "Recurring five-field cron expression, e.g. \"*/5 * * * *\"" },
            "timeZone": { "type": "string", "description": "Optional IANA/Windows timezone id used to interpret a cron rule" }
          },
          "required": ["prompt"],
          "description": "Supply the prompt and exactly one of 'every', 'at', or 'cron'."
        }
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var request = new ScheduleCreateRequest(
            Name: GetString(input, "name"),
            Prompt: GetString(input, "prompt") ?? string.Empty,
            Every: GetString(input, "every"),
            At: GetString(input, "at"),
            Cron: GetString(input, "cron"),
            TimeZoneId: GetString(input, "timeZone"));

        var service = new ScheduleControlService(
            context.Schedules,
            context.ScheduleRuntime,
            this.timeProvider,
            this.localTimeZone);

        var result = service.Create(request);

        if (!result.IsSuccess)
        {
            return Task.FromResult(new ToolResult(result.Error!, IsError: true));
        }

        var task = result.Task!;
        var lines = new List<string>
        {
            "Scheduled task created.",
            $"  Id:        {task.Id}",
        };
        if (!string.IsNullOrWhiteSpace(task.Name))
        {
            lines.Add($"  Name:      {task.Name}");
        }

        lines.Add($"  Schedule:  {task.Rule}");
        lines.Add($"  Timezone:  {task.TimeZone}");
        lines.Add($"  Next run:  {task.NextRunLocal} ({task.NextRunLocalLabel})");
        lines.Add($"  Next UTC:  {task.NextRunUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        lines.Add($"  Prompt:    {task.Prompt}");

        return Task.FromResult(new ToolResult(string.Join('\n', lines)));
    }

    private static string? GetString(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
