using System.Text;
using System.Text.Json;
using Coda.Agent.Scheduling;

namespace Coda.Agent.Tools;

/// <summary>
/// Lists all currently scheduled tasks. Bookkeeping-only; runs without a permission prompt.
/// </summary>
public sealed class ScheduleListTool : ITool
{
    public string Name => "schedule_list";

    public string Description => "List all scheduled tasks, showing their id, cron expression, " +
                                 "next run time, recurrence type, and prompt preview.";

    public string InputSchemaJson => """{"type":"object","properties":{}}""";

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Schedules is null)
        {
            return Task.FromResult(new ToolResult("No schedule store is available in this context."));
        }

        var items = context.Schedules.Items;
        if (items.Count == 0)
        {
            return Task.FromResult(new ToolResult("No scheduled tasks."));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Scheduled tasks ({items.Count}):");
        foreach (var task in items)
        {
            var recurringLabel = task.Kind == ScheduleKind.Cron ? "recurring" : "one-shot";
            var promptPreview = task.Prompt.Length > 60
                ? task.Prompt[..57] + "..."
                : task.Prompt;

            sb.AppendLine($"  [{task.Id}] {task.Cron} | {recurringLabel} | next: {task.NextRunUtc:yyyy-MM-dd HH:mm} UTC | {promptPreview}");
        }

        return Task.FromResult(new ToolResult(sb.ToString().TrimEnd()));
    }
}
