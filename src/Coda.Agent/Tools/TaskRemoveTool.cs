using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>
/// Removes a finished (terminal) task from the in-memory registry while preserving its persistent log
/// on disk. Running tasks must be stopped first. Unknown and unauthorized ids are indistinguishable.
/// </summary>
public sealed class TaskRemoveTool : ITool
{
    public string Name => "task_remove";

    public string Description =>
        "Remove a finished task (completed, failed, or stopped) from the task list. Its log file is preserved.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The terminal task id to remove"}},"required":["task_id"]}
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
        {
            return Task.FromResult(new ToolResult("Tasks are not available in this context.", IsError: false));
        }

        var taskId = ToolInput.GetString(input, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(new ToolResult("Missing required 'task_id'.", IsError: true));
        }

        var outcome = context.Tasks.Remove(taskId, context.CurrentTaskId);
        return Task.FromResult(outcome switch
        {
            TaskActionResult.Ok => new ToolResult($"Task '{taskId}' removed."),
            TaskActionResult.NotFound or TaskActionResult.Denied => new ToolResult($"Task '{taskId}' not found."),
            TaskActionResult.Rejected => new ToolResult($"Task '{taskId}' is still running; stop it before removing."),
            _ => new ToolResult($"Task '{taskId}' cannot be removed."),
        });
    }
}
