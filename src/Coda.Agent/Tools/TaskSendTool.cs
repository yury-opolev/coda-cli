using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>
/// Sends a steering message to a running subagent task the caller owns. The main agent may steer
/// any subagent; a subagent may steer only its own running descendants. Shell tasks cannot be
/// steered, and an unauthorized target is reported identically to an unknown one.
/// </summary>
public sealed class TaskSendTool : ITool
{
    public string Name => "task_send";

    public string Description =>
        "Send a steering message to a running subagent task (started with task_start). Shell tasks cannot be steered.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The subagent task id"},"message":{"type":"string","description":"The steering message to deliver"}},"required":["task_id","message"]}
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

        var message = ToolInput.GetString(input, "message");
        if (string.IsNullOrWhiteSpace(message))
        {
            return Task.FromResult(new ToolResult("Missing required 'message'.", IsError: true));
        }

        // Caller-scoped steering: only the main agent or a strict ancestor of the target subagent.
        return Task.FromResult(context.Tasks.Steer(taskId, message, context.CurrentTaskId) switch
        {
            TaskActionResult.Ok => new ToolResult($"Message delivered to task '{taskId}'."),
            // NotFound and Denied share identical wording so a subagent cannot distinguish a task
            // it is not allowed to steer from one that does not exist (no existence leak).
            TaskActionResult.NotFound or TaskActionResult.Denied => new ToolResult($"Task '{taskId}' not found."),
            TaskActionResult.InvalidState => new ToolResult($"Task '{taskId}' is not running and cannot be steered."),
            _ => new ToolResult($"Task '{taskId}' cannot be steered (only running subagents accept messages)."),
        });
    }
}
