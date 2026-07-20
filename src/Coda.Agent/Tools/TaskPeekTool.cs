using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>
/// Peeks the most recent output of an authorized task without advancing the incremental read
/// cursor. Unauthorized/unknown targets are reported identically so no existence leaks.
/// </summary>
public sealed class TaskPeekTool : ITool
{
    public string Name => "task_peek";

    public string Description =>
        "Show the most recent output of a task without consuming it (task_output's incremental cursor is unaffected).";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The task id"},"max_chars":{"type":"integer","description":"Maximum characters of trailing output to show (default 2000)."}},"required":["task_id"]}
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

        // Caller-scoped existence check: an unauthorized or unknown id is reported as not found.
        if (context.Tasks.Get(taskId, context.CurrentTaskId) is null)
        {
            return Task.FromResult(new ToolResult($"Task '{taskId}' not found."));
        }

        var maxChars = TryGetMaxChars(input) ?? 2000;
        var text = context.Tasks.TryPeek(taskId, maxChars, context.CurrentTaskId) ?? string.Empty;
        return Task.FromResult(new ToolResult(text.Length == 0 ? "(no output yet)" : text));
    }

    private static int? TryGetMaxChars(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("max_chars", out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var n)
        && n > 0
            ? n
            : null;
}
