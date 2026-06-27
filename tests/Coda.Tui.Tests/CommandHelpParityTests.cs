using Coda.Tui;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

/// <summary>
/// The TUI renderer and the headless runner are two presenters over the same
/// <see cref="CommandHelp"/>. This guards that they never diverge in the content they
/// show (usage, an option arg, an example) for the same command.
/// </summary>
public sealed class CommandHelpParityTests
{
    [Fact]
    public void Tui_and_headless_show_the_same_usage_options_and_examples()
    {
        var command = SlashCommandCatalog.CreateAll().Single(c => c.Name == "log");
        var help = command.Help;
        var sampleArg = help.Options![0].Arg;
        var sampleExample = help.Examples![0];

        // TUI render
        var console = new TestConsole();
        CommandHelpRenderer.Render(console, command);
        var tui = console.Output;

        // Headless render
        var sw = new StringWriter();
        HelpRunner.Run(["log"], sw, sw);
        var headless = sw.ToString();

        foreach (var fragment in new[] { help.Usage, sampleArg, sampleExample })
        {
            Assert.Contains(fragment, tui);
            Assert.Contains(fragment, headless);
        }
    }
}
