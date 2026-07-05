using Coda.Tui.Repl;
using LlmClient;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Removes the last <em>n</em> user exchanges from the conversation history.
/// Each "exchange" is defined as a user turn and everything that follows it
/// up to (but not including) the previous user turn.
/// </summary>
/// <remarks>
/// <c>/rewind</c>   — remove the last exchange (default n=1).<br/>
/// <c>/rewind n</c> — remove the last n exchanges.
/// </remarks>
public sealed class RewindCommand : ISlashCommand
{
    public string Name => "rewind";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Remove the last exchange(s) from the conversation";

    public CommandHelp Help => new(
        "/rewind [<n>]",
        Description: "Removes the last n user exchanges (each exchange = one user turn and all following messages up to the prior user turn) from the conversation history. Defaults to 1 if n is omitted. Has no effect on an empty conversation.",
        Options:
        [
            ("[<n>]", "number of exchanges to remove (positive integer, default 1)"),
        ],
        Examples: ["/rewind", "/rewind 3"]);

    public Task<CommandResult> ExecuteAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var history = context.Session.History;

        if (history.Count == 0)
        {
            context.Console.MarkupLine("[grey50]Nothing to rewind.[/]");
            return Task.FromResult(CommandResult.Continue);
        }

        var n = 1;
        if (args.Count > 0)
        {
            if (!int.TryParse(args[0], out n) || n < 1)
            {
                context.Console.MarkupLine("[red]Usage: /rewind [[n]] where n is a positive integer.[/]");
                return Task.FromResult(CommandResult.Continue);
            }
        }

        var removed = 0;
        for (var i = 0; i < n; i++)
        {
            var userIndex = this.FindLastUserIndex(history);
            if (userIndex < 0)
            {
                break;
            }

            var countToRemove = history.Count - userIndex;
            history.RemoveRange(userIndex, countToRemove);
            removed++;
        }

        if (removed == 0)
        {
            context.Console.MarkupLine("[grey50]Nothing to rewind.[/]");
        }
        else
        {
            context.Console.MarkupLine($"[grey50]Rewound {removed} exchange(s). {history.Count} message(s) remain.[/]");
        }

        return Task.FromResult(CommandResult.Continue);
    }

    private int FindLastUserIndex(List<ChatMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User)
            {
                return i;
            }
        }

        return -1;
    }
}
