using Coda.Tui.Repl;

namespace Coda.Tui.Tests;

public sealed class CommandHelpTests
{
    [Fact]
    public void Minimal_help_has_usage_only()
    {
        var help = new CommandHelp("/clear");

        Assert.Equal("/clear", help.Usage);
        Assert.Null(help.Description);
        Assert.Null(help.Options);
        Assert.Null(help.Examples);
    }

    [Fact]
    public void Full_help_carries_all_sections()
    {
        var help = new CommandHelp(
            "/log [<level> | stderr on|off]",
            Description: "Show or set telemetry logging.",
            Options: [("<level>", "trace|debug|info|warn|error|off"), ("stderr on|off", "echo to stderr")],
            Examples: ["/log debug", "/log stderr on"]);

        Assert.Equal(2, help.Options!.Count);
        Assert.Equal("<level>", help.Options![0].Arg);
        Assert.Contains("/log debug", help.Examples!);
    }
}
