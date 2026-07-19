using Coda.Tui.Repl;

namespace Coda.Tui.Commands;

/// <summary>Exits the REPL.</summary>
public sealed class ExitCommand : ISlashCommand
{
    public string Name => "exit";

    public IReadOnlyList<string> Aliases => ["quit"];

    public string Summary => "Exit Coda";

    public CommandHelp Help => new(
        "/exit",
        Description: "End the session and quit Coda.");

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        // The centralized post-exit session card is the only clean-exit output; no standalone line here.
        return Task.FromResult(CommandResult.Exit);
    }
}
