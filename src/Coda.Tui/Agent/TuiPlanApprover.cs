using Coda.Agent;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Agent;

/// <summary>
/// Renders the agent's proposed plan in the TUI and asks the user interactively
/// whether to approve it. When approving out of <see cref="PermissionMode.Plan"/>,
/// switches the session to <see cref="PermissionMode.AcceptEdits"/> so subsequent
/// turns can mutate files. More permissive modes (e.g.
/// <see cref="PermissionMode.BypassPermissions"/>) are left untouched.
/// </summary>
public sealed class TuiPlanApprover(IAnsiConsole console, SessionState session) : IPlanApprover
{
    public Task<bool> ApproveAsync(string plan, CancellationToken cancellationToken = default)
    {
        if (!console.Profile.Capabilities.Interactive)
        {
            // Non-interactive console: cannot prompt, so decline gracefully.
            // In practice this branch is unreachable because AgentRunner only wires this
            // approver when the console is interactive; kept as defence in depth.
            return Task.FromResult(false);
        }

        var escapedPlan = plan.Length > 0 ? Markup.Escape(plan) : "(no plan text provided)";
        console.MarkupLine($"[bold]Proposed plan:[/]\n{escapedPlan}");

        var confirmed = console.Confirm("Approve this plan and start implementing?", defaultValue: false);
        if (confirmed && session.PermissionMode == PermissionMode.Plan)
        {
            // Promote out of Plan so edits are allowed. Never downgrade a more
            // permissive pre-existing mode (e.g. BypassPermissions from --yolo).
            session.PermissionMode = PermissionMode.AcceptEdits;
        }

        return Task.FromResult(confirmed);
    }
}
