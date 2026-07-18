using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using LlmClient;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Clears the screen and re-renders the banner.</summary>
public sealed class ClearCommand : ISlashCommand
{
    public string Name => "clear";

    public IReadOnlyList<string> Aliases => ["cls"];

    public string Summary => "Clear the screen and start a fresh session";

    public CommandHelp Help => new(
        "/clear",
        Description: "Reset the conversation history and token usage and start a fresh session (the pre-clear session is preserved and stays resumable), then redraw the banner.");

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        context.Session.History.Clear();
        context.Session.SessionUsage = TokenUsage.Zero;
        var newId = SessionIds.NewId();
        context.Session.SessionId = newId;

        // Announce the new session boundary and refreshed metadata to the semantic UI.
        context.Events.Publish(new TranscriptClearedEvent(newId));
        SessionMetadataEvents.Publish(context);

        // In semantic mode the actor/renderer owns clearing via the event above; a raw console clear
        // + banner would duplicate that. The legacy REPL still clears its real console and redraws.
        if (!context.SemanticUiEnabled)
        {
            context.Console.Clear();
            var connectedProvider = await context.Credentials.GetConnectedProviderIdAsync(cancellationToken).ConfigureAwait(false);
            Banner.Render(context.Console, context.Session, connectedProvider, context.Session.Model);
        }

        return CommandResult.Continue;
    }
}
