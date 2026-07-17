namespace Coda.Tui.Repl;

internal sealed class SlashCommandCompletion
{
    private const int MaxSuggestions = 10;
    private readonly SlashCommandRegistry commands;
    private IReadOnlyList<ISlashCommand> suggestions = [];
    private bool isDismissed;

    public SlashCommandCompletion(SlashCommandRegistry commands)
    {
        this.commands = commands;
    }

    public IReadOnlyList<ISlashCommand> Suggestions => this.suggestions;

    public int SelectedIndex { get; private set; }

    public bool IsVisible => this.suggestions.Count > 0 && !this.isDismissed;

    public void Update(string input, int cursorIndex)
    {
        var query = GetQuery(input, cursorIndex);
        if (query is null)
        {
            this.suggestions = [];
            this.SelectedIndex = 0;
            this.isDismissed = false;
            return;
        }

        var previousName = this.IsVisible ? this.suggestions[this.SelectedIndex].Name : null;
        this.suggestions = this.commands.ListSorted()
            .Select(command => new { Command = command, Rank = GetRank(command, query) })
            .Where(match => match.Rank >= 0)
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Command.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(match => match.Command)
            .ToArray();

        var previousIndex = previousName is null
            ? -1
            : this.suggestions.ToList().FindIndex(command =>
                string.Equals(command.Name, previousName, StringComparison.OrdinalIgnoreCase));
        this.SelectedIndex = previousIndex >= 0 ? previousIndex : 0;
    }

    public void MoveSelection(int offset)
    {
        if (!this.IsVisible)
        {
            return;
        }

        this.SelectedIndex = (this.SelectedIndex + offset + this.suggestions.Count) % this.suggestions.Count;
    }

    public string? Complete()
    {
        if (!this.IsVisible)
        {
            return null;
        }

        this.isDismissed = true;
        return $"/{this.suggestions[this.SelectedIndex].Name} ";
    }

    public void Dismiss()
    {
        this.isDismissed = true;
    }

    public void Reactivate()
    {
        this.isDismissed = false;
    }

    private static string? GetQuery(string input, int cursorIndex)
    {
        if (cursorIndex <= 0 || cursorIndex > input.Length || input[0] != '/')
        {
            return null;
        }

        var commandToken = input[..cursorIndex];
        return commandToken.Any(char.IsWhiteSpace) ? null : commandToken[1..];
    }

    private static int GetRank(ISlashCommand command, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (command.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (command.Aliases.Any(alias => alias.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        if (command.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (command.Aliases.Any(alias => alias.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            return 3;
        }

        return command.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ? 4 : -1;
    }
}
