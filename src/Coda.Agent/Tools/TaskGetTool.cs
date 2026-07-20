using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>
/// Returns the full status snapshot for a single authorized task. An unauthorized target is
/// reported identically to an unknown one so a subagent cannot probe tasks outside its subtree.
/// </summary>
public sealed class TaskGetTool : ITool
{
    public string Name => "task_get";

    public string Description =>
        "Get the full status of one task by id: kind, status, depth, start/end time, and its result or error.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The task id"}},"required":["task_id"]}
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

        // Caller-scoped Get returns null for both unknown and unauthorized ids (indistinguishable).
        if (context.Tasks.Get(taskId, context.CurrentTaskId) is not { } s)
        {
            return Task.FromResult(new ToolResult($"Task '{taskId}' not found."));
        }

        var detail = s.Error is not null ? $"\nerror: {s.Error}"
            : s.Result is not null ? $"\nresult: {s.Result}"
            : string.Empty;

        var body = $"id: {s.Id}\nkind: {s.Kind.ToString().ToLowerInvariant()}\nstatus: {s.Status.ToString().ToLowerInvariant()}"
            + $"\ndepth: {s.Depth}\nstarted: {s.StartedAt:o}"
            + (s.EndedAt is { } ended ? $"\nended: {ended:o}" : string.Empty)
            + $"\nlog: {s.LogPath}"
            + detail;

        return Task.FromResult(new ToolResult(body));
    }
}
