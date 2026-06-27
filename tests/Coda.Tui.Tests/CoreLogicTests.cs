using Coda.Tui;
using Coda.Tui.Repl;

namespace Coda.Tui.Tests;

public sealed class CommandParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_parses_as_empty(string input)
    {
        Assert.Equal(ParsedInputKind.Empty, CommandParser.Parse(input).Kind);
    }

    [Fact]
    public void Slash_command_without_args()
    {
        var parsed = CommandParser.Parse("/help");
        Assert.Equal(ParsedInputKind.Slash, parsed.Kind);
        Assert.Equal("help", parsed.Name);
        Assert.Empty(parsed.Args);
    }

    [Fact]
    public void Slash_command_with_args()
    {
        var parsed = CommandParser.Parse("/login copilot");
        Assert.Equal("login", parsed.Name);
        Assert.Equal(["copilot"], parsed.Args);
    }

    [Fact]
    public void Slash_command_respects_quotes()
    {
        var parsed = CommandParser.Parse("/login \"two words\" tail");
        Assert.Equal("login", parsed.Name);
        Assert.Equal(["two words", "tail"], parsed.Args);
    }

    [Fact]
    public void Bare_slash_is_menu_trigger()
    {
        var parsed = CommandParser.Parse("/");
        Assert.Equal(ParsedInputKind.Slash, parsed.Kind);
        Assert.Equal(string.Empty, parsed.Name);
    }

    [Fact]
    public void Plain_text_parses_as_prompt()
    {
        var parsed = CommandParser.Parse("hello world");
        Assert.Equal(ParsedInputKind.Prompt, parsed.Kind);
        Assert.Equal("hello world", parsed.Text);
    }

    [Fact]
    public void Command_name_is_case_insensitive_lowered()
    {
        Assert.Equal("help", CommandParser.Parse("/HELP").Name);
    }
}

public sealed class SlashCommandRegistryTests
{
    private sealed class FakeCommand(string name, string summary, params string[] aliases) : ISlashCommand
    {
        public string Name => name;
        public IReadOnlyList<string> Aliases => aliases;
        public string Summary => summary;
        public CommandHelp Help => new($"/{name}");
        public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
            => Task.FromResult(CommandResult.Continue);
    }

    private static SlashCommandRegistry Build() =>
        new([new FakeCommand("help", "Show help"), new FakeCommand("exit", "Leave", "quit")]);

    [Fact]
    public void Resolves_by_name_case_insensitive()
    {
        Assert.NotNull(Build().Resolve("HELP"));
        Assert.Equal("help", Build().Resolve("help")!.Name);
    }

    [Fact]
    public void Resolves_by_alias()
    {
        Assert.Equal("exit", Build().Resolve("quit")!.Name);
    }

    [Fact]
    public void Unknown_returns_null()
    {
        Assert.Null(Build().Resolve("nope"));
    }

    [Fact]
    public void List_is_sorted_by_name()
    {
        var names = Build().ListSorted().Select(c => c.Name).ToArray();
        Assert.Equal(["exit", "help"], names);
    }
}

public sealed class SessionStateTests
{
    [Fact]
    public void Defaults_to_first_provider_and_tracks_active()
    {
        var state = new SessionState("claude-ai");
        Assert.Equal("claude-ai", state.ActiveProviderId);
        state.ActiveProviderId = "github-copilot";
        Assert.Equal("github-copilot", state.ActiveProviderId);
    }
}

public sealed class BrandingTests
{
    [Fact]
    public void Product_name_is_coda_and_not_claude()
    {
        Assert.Equal("Coda", Branding.ProductName);
        Assert.Equal("coda", Branding.CliName);
        Assert.DoesNotContain("claude", Branding.ProductName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("claude", string.Join(" ", Branding.BannerLines), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Version_is_present()
    {
        Assert.False(string.IsNullOrWhiteSpace(Branding.Version));
    }
}

public sealed class StatusFormatterTests
{
    [Fact]
    public void Signed_out_line_says_not_signed_in()
    {
        var line = StatusFormatter.FormatProvider(new ProviderStatus("claude-ai", "Claude.ai", SignedIn: false, null, null, []));
        Assert.Contains("Claude.ai", line);
        Assert.Contains("not signed in", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Signed_in_line_shows_account()
    {
        var line = StatusFormatter.FormatProvider(new ProviderStatus(
            "claude-ai", "Claude.ai", SignedIn: true, "me@example.com", DateTimeOffset.UtcNow.AddHours(1), ["user:inference"]));
        Assert.Contains("me@example.com", line);
        Assert.Contains("Claude.ai", line);
    }
}
