using System.Text.Json;
using Coda.Agent.Teams;

namespace Coda.Agent.Tools;

/// <summary>Cancels a task on the team task board.</summary>
public sealed class TaskStopTool : ITool
{
    public string Name => "task_stop";

    public string Description =>
        "Cancel a task on the team task board by its id. Sets the task status to Cancelled.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" }
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
            return new ToolResult("task_stop requires an 'id' string.", IsError: true);
        }

        var id = idEl.GetString()!;

        try
        {
            var stopped = await context.TeamTasks
                .StopAsync(context.TeamName, id, cancellationToken)
                .ConfigureAwait(false);

            if (!stopped)
            {
                return new ToolResult($"No such task: {id}");
            }

            return new ToolResult($"Cancelled task {id}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error stopping task: {ex.Message}", IsError: true);
        }
    }
}
