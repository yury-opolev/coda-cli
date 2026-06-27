using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>Deletes the current team, terminating all running teammates.</summary>
public sealed class TeamDeleteTool : ITool
{
    public string Name => "team_delete";

    public string Description =>
        "Delete the current team and stop all running teammates. This is irreversible.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {}
        }
        """;

    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Teams is null)
        {
            return new ToolResult("Teams are not available.", IsError: true);
        }

        try
        {
            var (ok, message) = await context.Teams
                .DeleteTeamAsync(cancellationToken)
                .ConfigureAwait(false);

            return ok
                ? new ToolResult(message)
                : new ToolResult(message, IsError: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error deleting team: {ex.Message}", IsError: true);
        }
    }
}
