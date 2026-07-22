using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>Recalls still-pending steering messages from an authorized running agent task.</summary>
public sealed class TaskRecallTool : ITool
{
    public string Name => "task_recall";
    public string Description => "Recall pending steering messages from a running subagent or scheduled task you own.";
    public string InputSchemaJson => """{"type":"object","properties":{"task_id":{"type":"string","description":"The agent task id"}},"required":["task_id"]}""";
    public bool IsReadOnly => true;
    public bool ShouldDefer => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
        {
            return Task.FromResult(new ToolResult("Tasks are not available in this context."));
        }

        var taskId = ToolInput.GetString(input, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(new ToolResult("Missing required 'task_id'.", IsError: true));
        }

        var (status, messages) = context.Tasks.RecallSteering(taskId, context.CurrentTaskId);
        var result = status switch
        {
            TaskActionResult.Ok when messages.Count == 0 => new ToolResult($"No pending steering messages for task '{taskId}'."),
            TaskActionResult.Ok => new ToolResult($"Recalled {messages.Count} message(s) from task '{taskId}':\n" +
                string.Join("\n", messages.Select(message => $"- {message.Text}"))),
            TaskActionResult.NotFound or TaskActionResult.Denied => new ToolResult($"Task '{taskId}' not found."),
            TaskActionResult.InvalidState => new ToolResult($"Task '{taskId}' is not running and has no steering to recall."),
            _ => new ToolResult($"Task '{taskId}' cannot be steered (only running agent tasks accept messages)."),
        };
        return Task.FromResult(result);
    }
}
