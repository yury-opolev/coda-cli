using System.Linq;
using Coda.Agent;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Agent;

/// <summary>
/// Asks the user to allow/deny a mutating tool call through the host-neutral prompt surface. Publishes
/// <see cref="PermissionRequestedEvent"/>/<see cref="PermissionResolvedEvent"/> so the UI can reflect
/// the decision. Non-interactive prompt surfaces deny by default for safety.
/// </summary>
public sealed class TuiPermissionPrompt(IUiPromptService prompts, IUiEventPublisher events) : IPermissionPrompt
{
    public async Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
    {
        events.Publish(new PermissionRequestedEvent(tool.Name, inputPreview));

        bool allowed;
        if (!prompts.IsInteractive)
        {
            allowed = false;
        }
        else
        {
            var response = await prompts.RequestAsync(
                UiPromptRequest.Confirm($"Allow {tool.Name} to run?", defaultValue: false),
                cancellationToken).ConfigureAwait(false);
            allowed = !response.Cancelled && response.SelectedIds.Contains("yes");
        }

        events.Publish(new PermissionResolvedEvent(tool.Name, allowed));
        return allowed;
    }
}