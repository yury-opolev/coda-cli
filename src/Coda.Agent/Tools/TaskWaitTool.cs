using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>
/// Blocks the calling agent until an authorized task becomes terminal, or an optional timeout
/// (default 10 minutes) elapses. A timeout reports the task is still running and never stops it.
/// Unknown and unauthorized tasks are reported identically so a subagent cannot probe outside its subtree.
/// </summary>
public sealed class TaskWaitTool : ITool
{
    private const int DefaultTimeoutSeconds = 600;

    public string Name => "task_wait";

    public string Description =>
        "Wait until a task finishes (completed, failed, or stopped). Accepts an optional timeout_seconds "
        + "(default 600). On timeout the task keeps running and you are told it is still running.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The task id to wait for"},"timeout_seconds":{"type":"integer","description":"Maximum seconds to wait before returning still-running (default 600)"}},"required":["task_id"]}
        """;

    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
        {
            return new ToolResult("Tasks are not available in this context.", IsError: false);
        }

        var taskId = ToolInput.GetString(input, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return new ToolResult("Missing required 'task_id'.", IsError: true);
        }

        var timeoutSeconds = ToolInput.GetInt(input, "timeout_seconds") ?? DefaultTimeoutSeconds;
        if (timeoutSeconds <= 0) timeoutSeconds = DefaultTimeoutSeconds;

        // Own linked token: the turn token unwinds the tool; the CancelAfter is the tool's own timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var outcome = await context.Tasks
                .WaitForTerminalAsync(taskId, context.CurrentTaskId, timeoutCts.Token)
                .ConfigureAwait(false);

            if (outcome == TaskWaitOutcome.NotFound)
            {
                return new ToolResult($"Task '{taskId}' not found.");
            }

            var status = context.Tasks.Get(taskId, context.CurrentTaskId)?.Status.ToString().ToLowerInvariant()
                ?? "finished";
            return new ToolResult($"Task '{taskId}' finished with status {status}.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The tool's own timeout fired — NOT the turn. Leave the task running untouched.
            return new ToolResult($"Task '{taskId}' is still running after {timeoutSeconds}s.");
        }
    }
}
