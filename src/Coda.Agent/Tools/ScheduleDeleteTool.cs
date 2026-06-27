using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>
/// Deletes a scheduled task by its Id. Bookkeeping-only; runs without a permission prompt.
/// </summary>
public sealed class ScheduleDeleteTool : ITool
{
    public string Name => "schedule_delete";

    public string Description => "Delete a scheduled task by its id. Use schedule_list to discover task ids.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string", "description": "The task id returned by schedule_create or schedule_list" }
          },
          "required": ["id"]
        }
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var id = input.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult(new ToolResult("schedule_delete requires an 'id' field.", IsError: true));
        }

        if (context.Schedules is null)
        {
            return Task.FromResult(new ToolResult("No schedule store is available in this context."));
        }

        var removed = context.Schedules.Remove(id);
        return removed
            ? Task.FromResult(new ToolResult($"Scheduled task '{id}' deleted."))
            : Task.FromResult(new ToolResult($"Scheduled task '{id}' not found."));
    }
}
