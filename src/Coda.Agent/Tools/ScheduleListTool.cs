using System.Text;
using System.Text.Json;
using Coda.Agent.Scheduling;

namespace Coda.Agent.Tools;

/// <summary>
/// Lists all currently scheduled tasks, combining the persisted store snapshot with the live
/// runtime-state view (idle/running/pending). Read-only; runs without a permission prompt.
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

        var snapshot = context.Schedules.GetSnapshot();
        if (snapshot.Items.Count == 0)
        {
            return Task.FromResult(new ToolResult("No scheduled tasks."));
        }

        var runtime = context.ScheduleRuntime;

        var sb = new StringBuilder();
        sb.AppendLine($"Scheduled tasks ({snapshot.Items.Count}):");
        foreach (var task in snapshot.Items)
        {
            var state = ResolveState(runtime, task.Id);
            var label = string.IsNullOrWhiteSpace(task.Name) ? task.Id : $"{task.Id} \"{task.Name}\"";
            var localDisplay = ScheduleDisplay.FormatLocal(task.NextRunUtc, task.TimeZoneId, out var zoneLabel);
            var promptPreview = task.Prompt.Length > 60 ? task.Prompt[..57] + "..." : task.Prompt;

            sb.AppendLine($"  [{label}] {StatusLabel(state.Status)}");
            sb.AppendLine($"     Schedule: {ScheduleDisplay.DescribeRule(task)}");
            sb.AppendLine($"     Timezone: {task.TimeZoneId}");
            sb.AppendLine($"     Next:     {localDisplay} ({zoneLabel}) / {task.NextRunUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
            if (!string.IsNullOrEmpty(state.ActiveTaskId))
            {
                sb.AppendLine($"     Active:   task {state.ActiveTaskId}");
            }

            if (task.LastTerminalOutcome is { } outcome)
            {
                var summary = string.IsNullOrWhiteSpace(outcome.Summary) ? string.Empty : $" — {outcome.Summary}";
                sb.AppendLine($"     Last:     {outcome.Outcome} at {outcome.CompletedAtUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC{summary}");
            }

            sb.AppendLine($"     Prompt:   {promptPreview}");
        }

        return Task.FromResult(new ToolResult(sb.ToString().TrimEnd()));
    }

    private static ScheduleRuntimeState ResolveState(IScheduleRuntimeView? runtime, string id)
    {
        if (runtime is not null && runtime.TryGetState(id, out var state) && state is not null)
        {
            return state;
        }

        return new ScheduleRuntimeState(ScheduleRuntimeStatus.Idle, null);
    }

    private static string StatusLabel(ScheduleRuntimeStatus status) => status switch
    {
        ScheduleRuntimeStatus.Running => "running",
        ScheduleRuntimeStatus.Pending => "pending",
        _ => "idle",
    };
}
