namespace Coda.Tui.Repl;

/// <summary>A slash command (e.g. <c>/login</c>). One implementation per command file.</summary>
public interface ISlashCommand
{
    /// <summary>Canonical name without the leading slash (lowercase).</summary>
    string Name { get; }

    /// <summary>Alternative names (e.g. "quit" for /exit).</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>One-line description shown in /help.</summary>
    string Summary { get; }

    /// <summary>Structured help for this command (usage, arguments, examples).</summary>
    CommandHelp Help { get; }

    Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default);
}
