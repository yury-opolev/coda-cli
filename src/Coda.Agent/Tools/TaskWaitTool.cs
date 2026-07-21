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

    // Ceiling aligned with AgentLoop.DefaultToolMaxDuration (30 minutes): a single tool call may not
    // outlive the agent's own tool-duration ceiling, and CancelAfter would overflow for larger values.
    private const int MaxTimeoutSeconds = 1800;

    public string Name => "task_wait";

    public string Description =>
        "Wait until a task finishes (completed, failed, or stopped). Accepts an optional timeout_seconds "
        + "(default 600, maximum 1800). On timeout the task keeps running and you are told it is still running.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The task id to wait for"},"timeout_seconds":{"type":"integer","description":"Maximum seconds to wait before returning still-running (default 600, maximum 1800)"}},"required":["task_id"]}
        """;

    public bool IsReadOnly => true;

    /// <summary>
    /// Clamps a requested timeout into the tool's supported range: non-positive values fall back to the
    /// default, and anything above the 30-minute ceiling is capped (also keeping CancelAfter from overflowing).
    /// </summary>
    internal static int NormalizeTimeoutSeconds(int requested)
    {
        if (requested <= 0) return DefaultTimeoutSeconds;
        return requested > MaxTimeoutSeconds ? MaxTimeoutSeconds : requested;
    }

    /// <summary>
    /// Renders the terminal-completion message. A null <paramref name="status"/> (the task was concurrently
    /// pruned after reaching a terminal state) omits the status clause rather than reporting "status finished".
    /// </summary>
    internal static string FormatFinished(string taskId, string? status) =>
        status is null
            ? $"Task '{taskId}' finished."
            : $"Task '{taskId}' finished with status {status}.";

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

        var timeoutSeconds = NormalizeTimeoutSeconds(ToolInput.GetInt(input, "timeout_seconds") ?? DefaultTimeoutSeconds);

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

            var status = context.Tasks.Get(taskId, context.CurrentTaskId)?.Status.ToString().ToLowerInvariant();
            return new ToolResult(FormatFinished(taskId, status));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The tool's own timeout fired — NOT the turn. Leave the task running untouched.
            return new ToolResult($"Task '{taskId}' is still running after {timeoutSeconds}s.");
        }
    }
}
