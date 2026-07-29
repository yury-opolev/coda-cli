namespace Coda.Tui.Repl;

/// <summary>Holds the registered slash commands and resolves them by name or alias.</summary>
public sealed class SlashCommandRegistry
{
    /// <summary>
    /// Immutable snapshot of the registry's command set. Both the lookup dictionary and the
    /// sorted list are built together and published as a single unit behind a
    /// <c>volatile</c> field, so a reader always observes either the complete old snapshot or
    /// the complete new one — never a half-rebuilt state. The lock-free read path is preserved.
    /// </summary>
    private sealed class RegistrySnapshot
    {
        public readonly Dictionary<string, ISlashCommand> ByKey;
        public readonly IReadOnlyList<ISlashCommand> Sorted;

        public RegistrySnapshot(IEnumerable<ISlashCommand> commands)
        {
            var byKey = new Dictionary<string, ISlashCommand>(StringComparer.OrdinalIgnoreCase);
            var list = new List<ISlashCommand>();
            foreach (var command in commands)
            {
                list.Add(command);
                byKey[command.Name] = command;
                foreach (var alias in command.Aliases)
                {
                    byKey[alias] = command;
                }
            }

            this.ByKey = byKey;
            this.Sorted = [.. list.OrderBy(c => c.Name, StringComparer.Ordinal)];
        }
    }

    // Single volatile field: publishing a new reference is the only mutation path.
    // A reader captures the field once and operates entirely on the captured snapshot,
    // so it can never observe a torn or half-rebuilt state.
    private volatile RegistrySnapshot snapshot;

    public SlashCommandRegistry(IEnumerable<ISlashCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        this.snapshot = new RegistrySnapshot(commands);
    }

    /// <summary>Resolve a command by name or alias (case-insensitive); null if unknown.</summary>
    public ISlashCommand? Resolve(string name) =>
        this.snapshot.ByKey.TryGetValue(name, out var command) ? command : null;

    /// <summary>All commands, sorted by name (for /help and the menu).</summary>
    public IReadOnlyList<ISlashCommand> ListSorted() => this.snapshot.Sorted;

    /// <summary>
    /// Replaces the entire command set with <paramref name="commands"/>, rebuilding all name
    /// and alias lookup indices. The swap is atomic: a fresh <see cref="RegistrySnapshot"/> is
    /// built entirely before being published through a single <c>volatile</c> write, so a
    /// concurrent reader always sees either the complete old set or the complete new one.
    /// Used by <c>/skills reload</c> to re-register skill-derived commands after skills
    /// change, without restarting the session.
    /// </summary>
    internal void ReplaceAll(IEnumerable<ISlashCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        this.snapshot = new RegistrySnapshot(commands); // single volatile write
    }
}
