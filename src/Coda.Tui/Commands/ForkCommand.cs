using Coda.Sdk;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Branches the live conversation into a new session: keeps the current history but mints a
/// fresh session id, so subsequent turns write to a new transcript and the original session is
/// frozen at its saved state (a branch point).
/// </summary>
public sealed class ForkCommand : ISlashCommand
{
    public string Name => "fork";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Branch the live conversation into a new session";

    public CommandHelp Help => new(
        "/fork",
        Description: "Keep the current conversation history but start writing to a new session id. The original session is frozen at its saved state and stays resumable.");

    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        context.Session.SessionId = await SessionForking.ForkAsync(
            context.Session.WorkingDirectory,
            context.Session.SessionId,
            context.Session.History,
            new SessionMetadata { SystemPromptOverride = context.Session.SystemPromptOverride },
            cancellationToken).ConfigureAwait(false);

        var escapedId = Markup.Escape(context.Session.SessionId);
        context.Console.MarkupLine($"[grey50]Forked into a new session {escapedId} (original frozen).[/]");
        SessionMetadataEvents.Publish(context);
        return CommandResult.Continue;
    }
}
