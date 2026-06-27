using System.Text;
using System.Text.Json;
using Coda.Agent.Teams;

namespace Coda.Agent.Tools;

/// <summary>Lists all tasks on the team task board.</summary>
public sealed class TaskListTool : ITool
{
    public string Name => "task_list";

    public string Description =>
        "List all tasks on the team task board. Returns each task's id, status, subject, and owner (if any).";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {}
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

        try
        {
            var tasks = await context.TeamTasks
                .ListAsync(context.TeamName, cancellationToken)
                .ConfigureAwait(false);

            if (tasks.Count == 0)
            {
                return new ToolResult("No tasks.");
            }

            var builder = new StringBuilder();
            foreach (var task in tasks)
            {
                builder.Append(task.Id)
                    .Append(" [")
                    .Append(task.Status)
                    .Append("] ")
                    .Append(task.Subject);

                if (task.Owner is not null)
                {
                    builder.Append(" (owner: ").Append(task.Owner).Append(')');
                }

                builder.Append('\n');
            }

            return new ToolResult(builder.ToString().TrimEnd());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error listing tasks: {ex.Message}", IsError: true);
        }
    }
}
