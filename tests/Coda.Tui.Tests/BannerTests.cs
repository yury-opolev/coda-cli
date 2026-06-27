using Coda.Tui;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class BannerTests
{
    private static TestConsole NewConsole()
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        return console;
    }

    [Fact]
    public void Render_shows_welcome_version_and_cwd()
    {
        var console = NewConsole();

        Banner.Render(console, new SessionState("claude-ai", "C:\\work"));

        var output = console.Output;
        Assert.Contains("Welcome to Coda", output);
        Assert.Contains(Branding.Version, output);
        Assert.Contains("C:\\work", output);
    }

    [Fact]
    public void Render_does_not_leak_claude_code_branding()
    {
        var console = NewConsole();

        Banner.Render(console, new SessionState("claude-ai", "C:\\work"));

        Assert.DoesNotContain("Claude Code", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}
