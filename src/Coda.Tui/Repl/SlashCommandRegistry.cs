namespace Coda.Tui.Repl;

/// <summary>Holds the registered slash commands and resolves them by name or alias.</summary>
public sealed class SlashCommandRegistry
{
    private readonly Dictionary<string, ISlashCommand> byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ISlashCommand> commands = [];

    public SlashCommandRegistry(IEnumerable<ISlashCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        foreach (var command in commands)
        {
            this.commands.Add(command);
            this.byKey[command.Name] = command;
            foreach (var alias in command.Aliases)
            {
                this.byKey[alias] = command;
            }
        }
    }

    /// <summary>Resolve a command by name or alias (case-insensitive); null if unknown.</summary>
    public ISlashCommand? Resolve(string name) =>
        this.byKey.TryGetValue(name, out var command) ? command : null;

    /// <summary>All commands, sorted by name (for /help and the menu).</summary>
    public IReadOnlyList<ISlashCommand> ListSorted() =>
        [.. this.commands.OrderBy(c => c.Name, StringComparer.Ordinal)];
}
