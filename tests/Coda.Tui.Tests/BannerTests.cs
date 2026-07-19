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

    [Fact]
    public void Render_SignedIn_ShowsProviderAndModel()
    {
        var console = NewConsole();
        var session = new SessionState("claude-ai", "/tmp") { Model = "claude-opus-4.8" };

        Banner.Render(console, session, connectedProvider: "github-copilot", model: "claude-opus-4.8");

        Assert.Contains("github-copilot", console.Output, StringComparison.Ordinal);
        Assert.Contains("claude-opus-4.8", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NotSignedIn_ShowsLoginHint()
    {
        var console = NewConsole();
        var session = new SessionState("claude-ai", "/tmp");

        Banner.Render(console, session, connectedProvider: null, model: null);

        Assert.Contains("not signed in", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_shows_embedded_wordmark_first_and_last_rows()
    {
        var console = NewConsole();

        Banner.Render(console, new SessionState("claude-ai", "C:\\work"));

        var output = console.Output;
        Assert.Contains(" ┌───┐      ┌┐", output);
        Assert.Contains(" └───┘└──┘└──┘└───┘", output);
    }
}
