namespace Coda.Tui.Repl;

/// <summary>
/// An accepted suggestion: the text to splice into the draft and the draft index the splice
/// starts at. <see cref="Start"/> is 0 when the command leads the draft — the only position at
/// which <see cref="CommandParser"/> dispatches it as a command rather than leaving it as prose.
/// </summary>
internal readonly record struct SlashCompletionAccept(string Text, int Start);

/// <summary>
/// Tracks the slash-command suggestions for the token under the caret.
/// </summary>
/// <remarks>
/// The menu opens for a token that begins with <c>/</c> at the start of the draft or immediately
/// after whitespace, so it is offered mid-sentence as well as at the start — a path such as
/// <c>src/foo</c> never triggers it. Only a leading command is executed; one accepted mid-draft
/// stays literal text and is sent to the model along with the prose around it.
/// </remarks>
internal sealed class SlashCommandCompletion
{
    private readonly SlashCommandRegistry commands;
    private IReadOnlyList<ISlashCommand> suggestions = [];
    private bool isDismissed;
    private string draft = string.Empty;
    private int queryStart;
    private int queryEnd;

    public SlashCommandCompletion(SlashCommandRegistry commands)
    {
        this.commands = commands;
    }

    public IReadOnlyList<ISlashCommand> Suggestions => this.suggestions;

    public int SelectedIndex { get; private set; }

    public bool IsVisible => this.suggestions.Count > 0 && !this.isDismissed;

    /// <summary>The draft index of the <c>/</c> that opened the current menu; 0 when none is open.</summary>
    public int QueryStart => this.queryStart;

    public void Update(string input, int cursorIndex)
    {
        this.draft = input ?? string.Empty;
        this.queryEnd = Math.Clamp(cursorIndex, 0, this.draft.Length);

        if (GetQuery(input, cursorIndex) is not var (query, start))
        {
            this.suggestions = [];
            this.SelectedIndex = 0;
            this.queryStart = 0;
            this.isDismissed = false;
            return;
        }

        this.queryStart = start;
        var previousName = this.IsVisible ? this.suggestions[this.SelectedIndex].Name : null;

        // Every match is offered; the popup bounds what is on screen at once and scrolls through
        // the rest, so truncating here would silently hide commands the query legitimately matched.
        this.suggestions = this.commands.ListSorted()
            .Select(command => new { Command = command, Rank = GetRank(command, query) })
            .Where(match => match.Rank >= 0)
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Command.Name, StringComparer.OrdinalIgnoreCase)
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

    public SlashCompletionAccept? Complete()
    {
        if (!this.IsVisible)
        {
            return null;
        }

        this.isDismissed = true;

        // A trailing space separates the command from whatever is typed next, but a token accepted
        // mid-draft is often already followed by one — a second would leave a double space behind.
        var alreadySeparated = this.queryEnd < this.draft.Length && char.IsWhiteSpace(this.draft[this.queryEnd]);
        var separator = alreadySeparated ? string.Empty : " ";
        return new SlashCompletionAccept(
            $"/{this.suggestions[this.SelectedIndex].Name}{separator}", this.queryStart);
    }

    public void Dismiss()
    {
        this.isDismissed = true;
    }

    public void Reactivate()
    {
        this.isDismissed = false;
    }

    /// <summary>
    /// Resolves the token the caret sits in to a menu query, or <see langword="null"/> when the
    /// caret is not inside a slash-command token.
    /// </summary>
    /// <returns>The text typed after the <c>/</c>, and the index of the <c>/</c> itself.</returns>
    private static (string Query, int Start)? GetQuery(string? input, int cursorIndex)
    {
        if (input is null || cursorIndex <= 0 || cursorIndex > input.Length)
        {
            return null;
        }

        // Walk back to the start of the token under the caret. Stopping at whitespace is what makes
        // the menu available mid-sentence while keeping it away from a slash inside a word: `src/foo`
        // resolves to a token starting at `s`, not at the slash.
        var start = cursorIndex;
        while (start > 0 && !char.IsWhiteSpace(input[start - 1]))
        {
            start--;
        }

        if (start == cursorIndex || input[start] != '/')
        {
            return null;
        }

        return (input[(start + 1)..cursorIndex], start);
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
