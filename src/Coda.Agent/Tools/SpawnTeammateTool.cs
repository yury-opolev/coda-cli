using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>Spawns a new in-process teammate on the active team.</summary>
public sealed class SpawnTeammateTool : ITool
{
    public string Name => "spawn_teammate";

    public string Description =>
        "Spawn a new in-process AI teammate on the current team. The teammate starts working on the given prompt immediately.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "name":       { "type": "string", "description": "Short name for the teammate (e.g. 'researcher')." },
            "prompt":     { "type": "string", "description": "Initial task prompt for the teammate." },
            "agent_type": { "type": "string", "description": "Optional agent type identifier." },
            "model":      { "type": "string", "description": "Optional LLM model override for the teammate." }
          },
          "required": ["name", "prompt"]
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

        if (!input.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("spawn_teammate requires a 'name' string.", IsError: true);
        }

        if (!input.TryGetProperty("prompt", out var promptEl) || promptEl.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("spawn_teammate requires a 'prompt' string.", IsError: true);
        }

        var name = nameEl.GetString()!;
        var prompt = promptEl.GetString()!;

        string? agentType = null;
        if (input.TryGetProperty("agent_type", out var agentTypeEl) && agentTypeEl.ValueKind == JsonValueKind.String)
        {
            agentType = agentTypeEl.GetString();
        }

        string? model = null;
        if (input.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
        {
            model = modelEl.GetString();
        }

        try
        {
            var (ok, message) = await context.Teams
                .SpawnTeammateAsync(name, prompt, agentType, model, cancellationToken)
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
            return new ToolResult($"Error spawning teammate: {ex.Message}", IsError: true);
        }
    }
}
