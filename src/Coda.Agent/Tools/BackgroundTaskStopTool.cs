using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>
/// Cancel a background task that was started with <c>task_start</c>.
/// </summary>
public sealed class BackgroundTaskStopTool : ITool
{
    public string Name => "task_stop";

    public string Description =>
        "Cancel a background task started with task_start. " +
        "The task's status will transition to stopped once it acknowledges the cancellation.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The task id returned by task_start"}},"required":["task_id"]}
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.BackgroundTasks is null)
        {
            return Task.FromResult(new ToolResult(
                "Background tasks are not available in this context.",
                IsError: false));
        }

        var taskId = ToolInput.GetString(input, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(new ToolResult("Missing required 'task_id'.", IsError: true));
        }

        var found = context.BackgroundTasks.Stop(taskId);
        if (!found)
        {
            return Task.FromResult(new ToolResult($"Task '{taskId}' not found."));
        }

        return Task.FromResult(new ToolResult($"Task '{taskId}' has been stopped."));
    }
}
