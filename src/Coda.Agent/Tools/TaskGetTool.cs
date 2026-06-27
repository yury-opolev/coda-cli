using System.Text;
using System.Text.Json;
using Coda.Agent.Teams;

namespace Coda.Agent.Tools;

/// <summary>Gets the full detail of a single task from the team task board.</summary>
public sealed class TaskGetTool : ITool
{
    public string Name => "task_get";

    public string Description =>
        "Get the full details of a task by its id. Returns id, subject, status, owner, blocked_by, and description.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" }
          },
          "required": ["id"]
        }
        """;

    public bool IsReadOnly => true;

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
            return new ToolResult("task_get requires an 'id' string.", IsError: true);
        }

        var id = idEl.GetString()!;

        try
        {
            var task = await context.TeamTasks
                .GetAsync(context.TeamName, id, cancellationToken)
                .ConfigureAwait(false);

            if (task is null)
            {
                return new ToolResult($"No such task: {id}");
            }

            var builder = new StringBuilder();
            builder.Append("id: ").Append(task.Id).Append('\n');
            builder.Append("subject: ").Append(task.Subject).Append('\n');
            builder.Append("status: ").Append(task.Status).Append('\n');
            builder.Append("owner: ").Append(task.Owner ?? "(none)").Append('\n');

            if (task.BlockedBy.Count > 0)
            {
                builder.Append("blocked_by: ").Append(string.Join(", ", task.BlockedBy)).Append('\n');
            }

            if (task.Description is not null)
            {
                builder.Append("description: ").Append(task.Description).Append('\n');
            }

            return new ToolResult(builder.ToString().TrimEnd());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error getting task: {ex.Message}", IsError: true);
        }
    }
}
