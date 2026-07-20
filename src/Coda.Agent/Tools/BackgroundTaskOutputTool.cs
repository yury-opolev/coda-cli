using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>
/// Read incremental output from a background task (cursor-based; each call
/// returns only text appended since the previous read).
/// </summary>
public sealed class BackgroundTaskOutputTool : ITool
{
    public string Name => "task_output";

    public string Description =>
        "Read new output from a background task started with task_start. " +
        "Each call returns only text produced since the previous read (cursor-based). " +
        "Check the status line to know when the task has finished.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The task id returned by task_start"}},"required":["task_id"]}
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
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

        var (found, newText, truncated, status) = context.Tasks.ReadForMainAgent(taskId);
        if (!found)
        {
            return Task.FromResult(new ToolResult($"Task '{taskId}' not found."));
        }

        var outputSection = newText.Length > 0
            ? newText
            : status == TaskRunStatus.Running
                ? "(no new output yet; still running)"
                : "(no new output since last read)";

        if (truncated)
        {
            outputSection = "[earlier output truncated]\n" + outputSection;
        }

        var statusLabel = status switch
        {
            TaskRunStatus.Running => "running",
            TaskRunStatus.Completed => "completed",
            TaskRunStatus.Failed => "failed",
            TaskRunStatus.Stopped => "stopped",
            _ => status.ToString().ToLowerInvariant(),
        };

        return Task.FromResult(new ToolResult($"{outputSection}\n[status: {statusLabel}]"));
    }
}
