using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

/// <summary>Tests for the /output-style slash command.</summary>
public sealed class OutputStyleCommandTests
{
    private readonly TestConsole console;
    private readonly CommandContext context;

    public OutputStyleCommandTests()
    {
        this.console = new TestConsole();
        this.console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var credentials = new CredentialManager(store, new ICredentialProvider[]
        {
            new ClaudeAiProvider(),
            new GitHubCopilotProvider(),
            new ApiKeyProvider(),
        });

        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai");
        var registry = new SlashCommandRegistry(Array.Empty<ISlashCommand>());
        this.context = new CommandContext(this.console, credentials, session, providers, registry);
    }

    [Fact]
    public async Task Unknown_style_prints_warning_and_leaves_output_style_unchanged()
    {
        var cmd = new OutputStyleCommand();

        var result = await cmd.ExecuteAsync(this.context, new[] { "bogus-style-name" }, CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);

        // Should contain a warning mentioning the unknown style.
        var output = this.console.Output;
        Assert.True(
            output.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Unknown style", StringComparison.OrdinalIgnoreCase)
            || output.Contains("not a", StringComparison.OrdinalIgnoreCase),
            $"Expected a warning about unknown style but got: {output}");

        // OutputStyle must remain "default".
        Assert.Equal("default", this.context.Session.OutputStyle);
    }

    [Fact]
    public async Task Known_style_sets_output_style_on_session()
    {
        var cmd = new OutputStyleCommand();

        var result = await cmd.ExecuteAsync(this.context, new[] { "concise" }, CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.Equal("concise", this.context.Session.OutputStyle);
    }

    [Fact]
    public async Task No_args_lists_available_styles_and_current_style()
    {
        var cmd = new OutputStyleCommand();

        var result = await cmd.ExecuteAsync(this.context, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        var output = this.console.Output;
        Assert.Contains("concise", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explanatory", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default", output, StringComparison.OrdinalIgnoreCase);
    }
}
