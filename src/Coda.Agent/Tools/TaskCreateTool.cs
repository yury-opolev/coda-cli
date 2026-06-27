using System.Text.Json;
using Coda.Agent.Teams;

namespace Coda.Agent.Tools;

/// <summary>Creates a new task on the team task board.</summary>
public sealed class TaskCreateTool : ITool
{
    public string Name => "task_create";

    public string Description =>
        "Create a new task on the team task board. Provide a subject (required), optional description, and optional list of task IDs that must be completed before this task can start (blocked_by).";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "subject": { "type": "string" },
            "description": { "type": "string" },
            "blocked_by": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["subject"]
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

        if (!input.TryGetProperty("subject", out var subjectEl) || subjectEl.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("task_create requires a 'subject' string.", IsError: true);
        }

        var subject = subjectEl.GetString()!;

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

        try
        {
            var task = await context.TeamTasks
                .CreateAsync(context.TeamName, subject, description, blockedBy, cancellationToken)
                .ConfigureAwait(false);

            return new ToolResult($"Created task {task.Id}: {task.Subject}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error creating task: {ex.Message}", IsError: true);
        }
    }
}
