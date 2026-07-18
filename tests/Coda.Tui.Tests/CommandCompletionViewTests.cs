using Coda.Tui.Repl;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

/// <summary>
/// Host-neutral tests for <see cref="CommandCompletionView"/>: it renders slash-command rows with a
/// selection marker, bounds its height, scrolls the selection into view, and truncates to the view width.
/// These need no running application because rendering is exposed through
/// <see cref="CommandCompletionView.RenderVisibleRows"/>.
/// </summary>
public sealed class CommandCompletionViewTests
{
    private static ISlashCommand Cmd(string name, string summary) => new TestCommand(name, summary);

    [Fact]
    public void Hidden_and_non_focusable_by_default()
    {
        using var view = new CommandCompletionView();

        Assert.False(view.CanFocus);
        Assert.False(view.Visible);
        Assert.Equal(0, view.DesiredHeight);
        Assert.Empty(view.Suggestions);
        Assert.Equal(-1, view.SelectedIndex);
    }

    [Fact]
    public void Renders_command_names_summaries_and_selected_marker()
    {
        using var view = new CommandCompletionView();
        view.SetSuggestions([Cmd("help", "Show help"), Cmd("model", "Pick model")], selectedIndex: 1);

        var rows = view.RenderVisibleRows(80);

        Assert.Equal(2, rows.Count);
        Assert.Contains("help", rows[0]);
        Assert.Contains("Show help", rows[0]);
        Assert.Contains("model", rows[1]);
        Assert.Contains("Pick model", rows[1]);
        Assert.DoesNotContain(">", rows[0]);
        Assert.Contains(">", rows[1]);
        Assert.Equal(2, view.DesiredHeight);
    }

    [Fact]
    public void Height_is_bounded_and_selection_scrolls_into_the_window()
    {
        using var view = new CommandCompletionView();
        var many = Enumerable.Range(0, 20).Select(i => Cmd($"cmd{i:D2}", $"summary {i}")).ToArray();

        view.SetSuggestions(many, selectedIndex: 0);
        Assert.Equal(CommandCompletionView.MaxVisibleOptions, view.DesiredHeight);
        Assert.Equal(0, view.FirstVisibleIndex);

        view.SetSuggestions(many, selectedIndex: 15);
        Assert.True(view.FirstVisibleIndex <= 15);
        Assert.True(15 < view.FirstVisibleIndex + CommandCompletionView.MaxVisibleOptions);

        var rows = view.RenderVisibleRows(80);
        Assert.Equal(CommandCompletionView.MaxVisibleOptions, rows.Count);
        Assert.Contains(rows, row => row.Contains("cmd15"));
    }

    [Fact]
    public void Narrow_width_truncates_without_overflow()
    {
        using var view = new CommandCompletionView();
        view.SetSuggestions([Cmd("configure", "A very long summary that must be truncated")], selectedIndex: 0);

        var rows = view.RenderVisibleRows(10);

        Assert.Single(rows);
        Assert.All(rows, row => Assert.True(row.Length <= 10, $"row '{row}' exceeds width 10"));
    }

    [Fact]
    public void Empty_suggestions_reset_selection_and_render_nothing()
    {
        using var view = new CommandCompletionView();
        view.SetSuggestions([Cmd("help", "Show help")], selectedIndex: 0);
        Assert.True(view.HasSuggestions);

        view.SetSuggestions([], selectedIndex: 0);

        Assert.False(view.HasSuggestions);
        Assert.Equal(-1, view.SelectedIndex);
        Assert.Empty(view.RenderVisibleRows(80));
    }

    private sealed class TestCommand(string name, string summary) : ISlashCommand
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> Aliases => [];

        public string Summary { get; } = summary;

        public CommandHelp Help => new($"/{this.Name}");

        public Task<CommandResult> ExecuteAsync(
            CommandContext context,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CommandResult.Continue);
    }
}
