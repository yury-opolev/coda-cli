using System.Text.Json;
using Coda.Agent.Scheduling;

namespace Coda.Agent.Tools;

/// <summary>
/// Creates a new scheduled task. Validates the cron expression and adds the task
/// to the session's <see cref="ScheduledTaskStore"/>. Bookkeeping-only (no file/system
/// side effects), so it runs without a user permission prompt.
/// </summary>
public sealed class ScheduleCreateTool : ITool
{
    private readonly Func<DateTime> nowUtcFactory;

    public ScheduleCreateTool(Func<DateTime> nowUtcFactory)
    {
        this.nowUtcFactory = nowUtcFactory ?? throw new ArgumentNullException(nameof(nowUtcFactory));
    }

    public string Name => "schedule_create";

    public string Description =>
        "Create a scheduled task that runs a prompt on a recurring or one-shot cron schedule. " +
        "Provide a standard 5-field cron expression (min hour dom month dow), the prompt to run, " +
        "and whether the task should recur (default true). Returns the task id and next run time.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "cron":      { "type": "string",  "description": "5-field cron expression, e.g. \"*/5 * * * *\"" },
            "prompt":    { "type": "string",  "description": "Prompt to execute when the task fires" },
            "recurring": { "type": "boolean", "description": "If true (default), reschedule after each firing; if false, run once" }
          },
          "required": ["cron", "prompt"]
        }
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var cron = input.TryGetProperty("cron", out var c) ? c.GetString() : null;
        var prompt = input.TryGetProperty("prompt", out var p) ? p.GetString() : null;
        var recurring = !input.TryGetProperty("recurring", out var r) || r.ValueKind != JsonValueKind.False;

        if (string.IsNullOrWhiteSpace(cron))
        {
            return Task.FromResult(new ToolResult("schedule_create requires a 'cron' field.", IsError: true));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Task.FromResult(new ToolResult("schedule_create requires a 'prompt' field.", IsError: true));
        }

        if (!CronExpression.TryParse(cron, out _, out var error))
        {
            return Task.FromResult(new ToolResult($"Invalid cron expression: {error}", IsError: true));
        }

        if (context.Schedules is null)
        {
            return Task.FromResult(new ToolResult(
                "No schedule store is available in this context (e.g. running as a subagent). " +
                "Task was not persisted."));
        }

        var now = this.nowUtcFactory();
        var task = context.Schedules.Add(cron, prompt, recurring, now);

        var recurringLabel = task.Recurring ? "recurring" : "one-shot";
        var content = $"Scheduled task created.\n" +
                      $"  Id:       {task.Id}\n" +
                      $"  Cron:     {task.Cron}\n" +
                      $"  Type:     {recurringLabel}\n" +
                      $"  Next run: {task.NextRunUtc:yyyy-MM-dd HH:mm} UTC\n" +
                      $"  Prompt:   {task.Prompt}";

        return Task.FromResult(new ToolResult(content));
    }
}
