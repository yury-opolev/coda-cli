using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using LlmClient;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Clears the screen and re-renders the banner.</summary>
public sealed class ClearCommand : ISlashCommand
{
    public string Name => "clear";

    public IReadOnlyList<string> Aliases => ["cls"];

    public string Summary => "Clear the screen";

    public CommandHelp Help => new(
        "/clear",
        Description: "Reset the conversation history and token usage, then redraw the banner.");

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        context.Session.History.Clear();
        context.Session.SessionUsage = TokenUsage.Zero;
        context.Console.Clear();
        var connectedProvider = await context.Credentials.GetConnectedProviderIdAsync(cancellationToken).ConfigureAwait(false);
        Banner.Render(context.Console, context.Session, connectedProvider, context.Session.Model);
        return CommandResult.Continue;
    }
}
