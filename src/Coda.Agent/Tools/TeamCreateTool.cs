using System.Text.Json;
using Coda.Agent.Teams;

namespace Coda.Agent.Tools;

/// <summary>Creates a new team and sets the manager's team context.</summary>
public sealed class TeamCreateTool : ITool
{
    public string Name => "team_create";

    public string Description =>
        "Create a new team. Sets the active team context so that spawn_teammate and other team tools become available.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "team_name": { "type": "string", "description": "Unique name for the team." },
            "description": { "type": "string", "description": "Optional description of the team's purpose." }
          },
          "required": ["team_name"]
        }
        """;

    public bool IsReadOnly => false;

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Teams is null)
        {
            return Task.FromResult(new ToolResult("Teams are not available.", IsError: true));
        }

        if (!input.TryGetProperty("team_name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(new ToolResult("team_create requires a 'team_name' string.", IsError: true));
        }

        var teamName = nameEl.GetString()!;

        string? description = null;
        if (input.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
        {
            description = descEl.GetString();
        }

        var (ok, message) = context.Teams.CreateTeam(teamName, description);
        return Task.FromResult(ok
            ? new ToolResult(message)
            : new ToolResult(message, IsError: true));
    }
}
