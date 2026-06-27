using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class CommandHelpRendererTests
{
    private sealed class FakeCommand : ISlashCommand
    {
        public string Name => "demo";
        public IReadOnlyList<string> Aliases => ["d"];
        public string Summary => "A demo command";
        public CommandHelp Help => new(
            "/demo <thing>",
            Description: "Does the demo thing.",
            Options: [("<thing>", "the thing to demo")],
            Examples: ["/demo widget"]);
        public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken ct = default)
            => Task.FromResult(CommandResult.Continue);
    }

    [Fact]
    public void Render_emits_all_sections()
    {
        var console = new TestConsole();

        CommandHelpRenderer.Render(console, new FakeCommand());

        var output = console.Output;
        Assert.Contains("/demo", output);
        Assert.Contains("alias:", output);             // alias label rendered
        Assert.Contains("/d", output);                 // the alias itself
        Assert.Contains("/demo <thing>", output);      // usage
        Assert.Contains("Does the demo thing.", output);
        Assert.Contains("<thing>", output);            // option arg
        Assert.Contains("the thing to demo", output);  // option meaning
        Assert.Contains("/demo widget", output);       // example
    }

    [Fact]
    public void Render_minimal_help_shows_usage()
    {
        var console = new TestConsole();
        CommandHelpRenderer.Render(console, new MinimalCommand());
        Assert.Contains("/min", console.Output);
    }

    private sealed class MinimalCommand : ISlashCommand
    {
        public string Name => "min";
        public IReadOnlyList<string> Aliases => [];
        public string Summary => "Minimal";
        public CommandHelp Help => new("/min");
        public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken ct = default)
            => Task.FromResult(CommandResult.Continue);
    }
}
