using Coda.Tui.Repl;

namespace Coda.Tui.Tests;

public sealed class SlashCommandCatalogTests
{
    [Fact]
    public void CreateAll_includes_core_commands_and_no_duplicates()
    {
        var commands = SlashCommandCatalog.CreateAll();
        var names = commands.Select(c => c.Name).ToList();

        Assert.Contains("help", names);
        Assert.Contains("log", names);
        Assert.Contains("mcp", names);
        Assert.Contains("tasks", names);
        Assert.Contains("exit", names);
        Assert.Equal(names.Count, names.Distinct().Count()); // no duplicate names
        Assert.Equal(37, names.Count);
        Assert.Equal("help", names[0]);   // first in display order
        Assert.Equal("exit", names[^1]);  // last in display order
    }

    [Fact]
    public void Completion_suggests_tasks_command_from_the_catalog()
    {
        var completion = new SlashCommandCompletion(new SlashCommandRegistry(SlashCommandCatalog.CreateAll()));

        completion.Update("/tas", 4);

        Assert.True(completion.IsVisible);
        Assert.Contains(completion.Suggestions, c => c.Name == "tasks");
    }
}
