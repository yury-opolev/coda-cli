using System.Text;
using System.Text.Json;
using Coda.Agent.Scheduling;

namespace Coda.Agent.Tools;

/// <summary>
/// Lists all currently scheduled tasks, combining the persisted store snapshot with the live
/// runtime-state view (idle/running/pending). Delegates projection to
/// <see cref="ScheduleControlService.List"/> so the display is identical across tools, serve, and
/// TUI. Read-only; runs without a permission prompt.
/// </summary>
public sealed class ScheduleListTool : ITool
{
    public string Name => "schedule_list";

    public string Description => "List all scheduled tasks, showing each task's id, name, schedule " +
                                 "rule, timezone, next run time (local and UTC), current runtime state " +
                                 "(idle/running/pending), active task id, prompt preview, and last outcome.";

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

        var service = new ScheduleControlService(context.Schedules, context.ScheduleRuntime);
        var items = service.List();

        if (items.Count == 0)
        {
            return Task.FromResult(new ToolResult("No scheduled tasks."));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Scheduled tasks ({items.Count}):");
        foreach (var task in items)
        {
            var label = string.IsNullOrWhiteSpace(task.Name) ? task.Id : $"{task.Id} \"{task.Name}\"";
            var promptPreview = task.Prompt.Length > 60 ? task.Prompt[..57] + "..." : task.Prompt;

            sb.AppendLine($"  [{label}] {StatusLabel(task.State)}");
            sb.AppendLine($"     Schedule: {task.Rule}");
            sb.AppendLine($"     Timezone: {task.TimeZone}");
            sb.AppendLine($"     Next:     {task.NextRunLocal} ({task.NextRunLocalLabel}) / {task.NextRunUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
            if (!string.IsNullOrEmpty(task.ActiveTaskId))
            {
                sb.AppendLine($"     Active:   task {task.ActiveTaskId}");
            }

            if (task.LastOutcome is { } outcome)
            {
                var summary = string.IsNullOrWhiteSpace(outcome.Summary) ? string.Empty : $" — {outcome.Summary}";
                sb.AppendLine($"     Last:     {outcome.Outcome} at {outcome.CompletedAtUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC{summary}");
            }

            sb.AppendLine($"     Prompt:   {promptPreview}");
        }

        return Task.FromResult(new ToolResult(sb.ToString().TrimEnd()));
    }

    private static string StatusLabel(ScheduleRuntimeStatus status) => status switch
    {
        ScheduleRuntimeStatus.Running => "running",
        ScheduleRuntimeStatus.Pending => "pending",
        _ => "idle",
    };
}
