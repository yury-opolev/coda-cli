using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>
/// Promotes an authorized, still-running foreground shell task to the background so the agent no
/// longer awaits it. Only shell tasks originally started in the foreground can be detached. Unknown
/// and unauthorized ids are indistinguishable.
/// </summary>
public sealed class TaskBackgroundTool : ITool
{
    public string Name => "task_background";

    public string Description =>
        "Move a running foreground shell task to the background so you stop waiting on it. "
        + "Poll its output later with task_output.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The foreground shell task id"}},"required":["task_id"]}
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

        var outcome = context.Tasks.TryDetach(taskId, context.CurrentTaskId);
        return Task.FromResult(outcome switch
        {
            TaskActionResult.Ok => new ToolResult($"Task '{taskId}' moved to the background."),
            // NotFound and Denied share wording so an unauthorized caller cannot probe existence.
            TaskActionResult.NotFound or TaskActionResult.Denied => new ToolResult($"Task '{taskId}' not found."),
            TaskActionResult.Rejected => new ToolResult($"Task '{taskId}' is not a shell task and cannot be backgrounded."),
            TaskActionResult.InvalidState => new ToolResult($"Task '{taskId}' is not a running foreground task."),
            _ => new ToolResult($"Task '{taskId}' cannot be backgrounded."),
        });
    }
}
