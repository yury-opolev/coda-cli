using System.Text.Json;
using Coda.Tui;

namespace Coda.Tui.Tests;

public sealed class HelpRunnerTests
{
    [Fact]
    public void List_text_includes_commands()
    {
        var sw = new StringWriter();
        var exit = HelpRunner.Run([], sw, sw);

        Assert.Equal(0, exit);
        Assert.Contains("log", sw.ToString());
        Assert.Contains("help", sw.ToString());
    }

    [Fact]
    public void List_json_has_commands_array()
    {
        var sw = new StringWriter();
        var exit = HelpRunner.Run(["--json"], sw, sw);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(sw.ToString());
        Assert.True(doc.RootElement.GetProperty("commands").GetArrayLength() >= 30);
    }

    [Fact]
    public void Single_command_text_shows_usage()
    {
        var sw = new StringWriter();
        var exit = HelpRunner.Run(["log"], sw, sw);

        Assert.Equal(0, exit);
        Assert.Contains("/log", sw.ToString());
        Assert.Contains("Usage", sw.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Single_command_json_has_usage_field()
    {
        var sw = new StringWriter();
        var exit = HelpRunner.Run(["log", "--json"], sw, sw);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(sw.ToString());
        Assert.Equal("log", doc.RootElement.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("usage").GetString()));
    }

    [Fact]
    public void Unknown_command_errors_to_stderr_and_nonzero()
    {
        var outw = new StringWriter();
        var errw = new StringWriter();
        var exit = HelpRunner.Run(["nope"], outw, errw);

        Assert.NotEqual(0, exit);
        Assert.Contains("nope", errw.ToString());
    }

    [Fact]
    public void Unexpected_second_positional_errors_to_stderr_and_nonzero()
    {
        var outw = new StringWriter();
        var errw = new StringWriter();
        var exit = HelpRunner.Run(["log", "extra"], outw, errw);

        Assert.NotEqual(0, exit);
        Assert.Contains("extra", errw.ToString());
    }
}
