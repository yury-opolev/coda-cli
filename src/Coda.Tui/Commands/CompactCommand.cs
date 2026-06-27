using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Summarizes the conversation so far to free up context.</summary>
public sealed class CompactCommand : ISlashCommand
{
    public string Name => "compact";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Summarize the conversation to free up context";

    public CommandHelp Help => new(
        "/compact",
        Description: "Asks the model to summarize the current conversation and replaces the history with that summary, freeing up context-window space. Has no effect if the conversation is empty.",
        Examples: ["/compact"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (context.Session.History.Count == 0)
        {
            context.Console.MarkupLine($"{Theme.DimMarkup("Nothing to compact yet.")}");
            return CommandResult.Continue;
        }

        var options = new SessionOptions
        {
            ProviderId = context.Session.ActiveProviderId,
            Model = context.Session.Model,
            WorkingDirectory = context.Session.WorkingDirectory,
        };

        context.Console.MarkupLine($"{Theme.DimMarkup("Compacting…")}");
        using var session = new CodaSession(context.Credentials, options, history: context.Session.History);
        await session.CompactAsync(cancellationToken).ConfigureAwait(false);

        context.Console.MarkupLine($"{Theme.DimMarkup($"Conversation compacted ({context.Session.History.Count} messages kept).")}");
        return CommandResult.Continue;
    }
}
