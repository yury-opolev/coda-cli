using System.Text.Json;
using Coda.Agent.Teams;

namespace Coda.Agent.Tools;

/// <summary>Updates one or more fields of a task on the team task board.</summary>
public sealed class TaskUpdateTool : ITool
{
    public string Name => "task_update";

    public string Description =>
        "Update a task on the team task board. Provide the task id and any fields to change: status (pending|in_progress|completed|blocked|cancelled), description, blocked_by (array of task ids), or owner.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "status": {
              "type": "string",
              "enum": ["pending", "in_progress", "completed", "blocked", "cancelled"]
            },
            "description": { "type": "string" },
            "blocked_by": { "type": "array", "items": { "type": "string" } },
            "owner": { "type": "string" }
          },
          "required": ["id"]
        }
        """;

    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.TeamTasks is null || context.TeamName is null)
        {
            return new ToolResult("Not in a team context.", IsError: true);
        }

        if (!input.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("task_update requires an 'id' string.", IsError: true);
        }

        var id = idEl.GetString()!;

        TeamTaskStatus? status = null;
        if (input.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
        {
            status = ParseStatus(statusEl.GetString());
        }

        string? description = null;
        if (input.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
        {
            description = descEl.GetString();
        }

        List<string>? blockedBy = null;
        if (input.TryGetProperty("blocked_by", out var blockedEl) && blockedEl.ValueKind == JsonValueKind.Array)
        {
            blockedBy = [];
            foreach (var item in blockedEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    blockedBy.Add(item.GetString()!);
                }
            }
        }

        string? owner = null;
        if (input.TryGetProperty("owner", out var ownerEl) && ownerEl.ValueKind == JsonValueKind.String)
        {
            owner = ownerEl.GetString();
        }

        var patch = new TeamTaskPatch
        {
            Status = status,
            Description = description,
            BlockedBy = blockedBy,
            Owner = owner,
        };

        try
        {
            var updated = await context.TeamTasks
                .UpdateAsync(context.TeamName, id, patch, cancellationToken)
                .ConfigureAwait(false);

            if (!updated)
            {
                return new ToolResult($"No such task: {id}");
            }

            return new ToolResult($"Updated task {id}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error updating task: {ex.Message}", IsError: true);
        }
    }

    private static TeamTaskStatus? ParseStatus(string? value) => value switch
    {
        "pending" => TeamTaskStatus.Pending,
        "in_progress" => TeamTaskStatus.InProgress,
        "completed" => TeamTaskStatus.Completed,
        "blocked" => TeamTaskStatus.Blocked,
        "cancelled" => TeamTaskStatus.Cancelled,
        _ => null,
    };
}
