using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>
/// Presents the agent's proposed plan to the user for approval, then signals
/// whether the session should proceed with implementation.
/// The host approver (e.g. <see cref="IPlanApprover"/>) is responsible for
/// switching the session out of plan mode on approval.
/// </summary>
public sealed class ExitPlanModeTool : ITool
{
    public string Name => "exit_plan_mode";

    public string Description =>
        "Present the proposed plan to the user for approval. " +
        "Call this when you have finished researching and have a concrete plan ready. " +
        "Provide the full plan in markdown format. " +
        "If approved, you may proceed with implementation.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "plan": {
              "type": "string",
              "description": "The proposed plan in markdown format."
            }
          },
          "required": ["plan"]
        }
        """;

    // Read-only: presenting a plan for approval causes no file or network mutations;
    // the host approver owns any side-effects (mode change) on approval.
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var plan = string.Empty;
        if (input.TryGetProperty("plan", out var planEl) && planEl.ValueKind == JsonValueKind.String)
        {
            plan = planEl.GetString() ?? string.Empty;
        }

        if (context.PlanApprover is null)
        {
            return new ToolResult("No interactive user is available to approve the plan; remaining in plan mode.");
        }

        // The host approver is responsible for switching the session out of plan mode on approval.
        var approved = await context.PlanApprover.ApproveAsync(plan, cancellationToken).ConfigureAwait(false);

        return approved
            ? new ToolResult("Plan approved. You may now proceed with implementation.")
            : new ToolResult("Plan was not approved. Continue refining the plan or ask the user what to change.");
    }
}
