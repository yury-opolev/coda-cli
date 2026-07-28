using Coda.Tui.Repl;

namespace Coda.Tui.Tests;

public sealed class SlashCommandCompletionTests
{
    [Fact]
    public void Update_bare_slash_lists_commands()
    {
        var completion = CreateCompletion();

        completion.Update("/", 1);

        Assert.True(completion.IsVisible);
        Assert.Equal(["exit", "help", "model", "status"], completion.Suggestions.Select(command => command.Name));
    }

    [Fact]
    public void Update_ranks_name_prefix_before_summary_match()
    {
        var completion = CreateCompletion();

        completion.Update("/mo", 3);

        Assert.Equal("model", completion.Suggestions[0].Name);
    }

    [Fact]
    public void Update_matches_aliases()
    {
        var completion = CreateCompletion();

        completion.Update("/quit", 5);

        Assert.Single(completion.Suggestions);
        Assert.Equal("exit", completion.Suggestions[0].Name);
    }

    [Fact]
    public void Update_hides_after_command_token()
    {
        var completion = CreateCompletion();

        completion.Update("/model opus", 12);

        Assert.False(completion.IsVisible);
    }

    [Fact]
    public void MoveSelection_wraps_and_complete_adds_space()
    {
        var completion = CreateCompletion();
        completion.Update("/", 1);

        completion.MoveSelection(-1);

        Assert.Equal(new SlashCompletionAccept("/status ", 0), completion.Complete());
        Assert.False(completion.IsVisible);
    }

    [Theory]
    [InlineData("explain this /mo", 16, "mo", 13)]  // after a space, mid-sentence
    [InlineData("explain this /", 14, "", 13)]      // bare slash mid-sentence lists everything
    [InlineData("one\n/mo", 7, "mo", 4)]            // a newline counts as whitespace
    [InlineData("/mo", 3, "mo", 0)]                 // still works at the head of the draft
    public void Update_offers_the_menu_for_a_slash_token_after_whitespace(
        string input, int cursor, string expectedQuery, int expectedStart)
    {
        var completion = CreateCompletion();

        completion.Update(input, cursor);

        Assert.True(completion.IsVisible);
        Assert.Equal(expectedStart, completion.QueryStart);
        // The query drives the filtering, so assert it through the surviving suggestions.
        Assert.All(
            completion.Suggestions,
            command => Assert.Contains(expectedQuery, command.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("src/mo", 6)]        // a slash inside a word is a path separator, not a trigger
    [InlineData("https://ex", 10)]   // ... including in a URL
    [InlineData("/model opus", 11)]  // the caret has left the command token
    [InlineData("/model ", 7)]       // the caret sits on whitespace
    public void Update_ignores_a_slash_that_is_not_a_command_token(string input, int cursor)
    {
        var completion = CreateCompletion();

        completion.Update(input, cursor);

        Assert.False(completion.IsVisible);
    }

    [Fact]
    public void Complete_reports_the_slash_position_so_a_mid_draft_token_can_be_spliced()
    {
        var completion = CreateCompletion();
        completion.Update("explain this /mo then", "explain this /mo".Length);

        // Already followed by a space, so the command is spliced in without adding another.
        Assert.Equal(new SlashCompletionAccept("/model", 13), completion.Complete());
    }

    [Fact]
    public void Complete_keeps_the_separating_space_when_the_token_ends_the_draft()
    {
        var completion = CreateCompletion();
        completion.Update("explain this /mo", "explain this /mo".Length);

        Assert.Equal(new SlashCompletionAccept("/model ", 13), completion.Complete());
    }

    [Fact]
    public void Dismiss_hides_until_reactivated()
    {
        var completion = CreateCompletion();
        completion.Update("/m", 2);

        completion.Dismiss();
        Assert.False(completion.IsVisible);

        completion.Reactivate();
        completion.Update("/mo", 3);
        Assert.True(completion.IsVisible);
    }

    [Fact]
    public void Update_bare_slash_lists_every_command()
    {
        // The popup bounds how many rows are on screen and scrolls through the rest, so the
        // suggestion list itself must never be truncated.
        var registry = new SlashCommandRegistry(
            Enumerable.Range(0, 40).Select(i => (ISlashCommand)new TestCommand($"cmd{i:D2}", $"Command {i}")).ToArray());
        var completion = new SlashCommandCompletion(registry);

        completion.Update("/", 1);

        Assert.Equal(40, completion.Suggestions.Count);
    }

    private static SlashCommandCompletion CreateCompletion() =>
        new(new SlashCommandRegistry(
        [
            new TestCommand("help", "Show command help"),
            new TestCommand("model", "Select the chat model"),
            new TestCommand("status", "Show connection status"),
            new TestCommand("exit", "Exit Coda", ["quit"]),
        ]));

    private sealed class TestCommand : ISlashCommand
    {
        public TestCommand(string name, string summary, IReadOnlyList<string>? aliases = null)
        {
            this.Name = name;
            this.Summary = summary;
            this.Aliases = aliases ?? [];
        }

        public string Name { get; }

        public IReadOnlyList<string> Aliases { get; }

        public string Summary { get; }

        public CommandHelp Help => new($"/{this.Name}");

        public Task<CommandResult> ExecuteAsync(
            CommandContext context,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CommandResult.Continue);
    }
}
