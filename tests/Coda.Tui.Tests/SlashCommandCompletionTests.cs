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

        Assert.Equal("/status ", completion.Complete());
        Assert.False(completion.IsVisible);
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
