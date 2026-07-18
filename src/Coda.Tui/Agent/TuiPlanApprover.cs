using System.Linq;
using Coda.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Agent;

/// <summary>
/// Presents the agent''s proposed plan through the host-neutral prompt surface and asks the user to
/// approve it. Publishes <see cref="PlanApprovalRequestedEvent"/>/<see cref="PlanApprovalResolvedEvent"/>.
/// On approval, switches the session to <see cref="PermissionMode.AcceptEdits"/> so subsequent turns can
/// mutate files. Non-interactive prompt surfaces decline gracefully.
/// </summary>
public sealed class TuiPlanApprover(IUiPromptService prompts, IUiEventPublisher events, SessionState session) : IPlanApprover
{
    public async Task<bool> ApproveAsync(string plan, CancellationToken cancellationToken = default)
    {
        events.Publish(new PlanApprovalRequestedEvent(plan));

        bool approved;
        if (!prompts.IsInteractive)
        {
            approved = false;
        }
        else
        {
            var response = await prompts.RequestAsync(
                UiPromptRequest.Confirm("Approve this plan and start implementing?", defaultValue: false),
                cancellationToken).ConfigureAwait(false);
            approved = !response.Cancelled && response.SelectedIds.Contains("yes");
        }

        if (approved)
        {
            session.PermissionMode = PermissionMode.AcceptEdits;
        }

        events.Publish(new PlanApprovalResolvedEvent(approved));
        return approved;
    }
}