using Coda.Tui;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

/// <summary>Tests for /doctor slash command.</summary>
public sealed class DoctorCommandTests
{
    [Fact]
    public async Task Doctor_output_contains_version()
    {
        var (app, _, console) = BuildDoctorApp();

        var result = await app.DispatchAsync(ParsedInput.Slash("doctor", Array.Empty<string>()), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains(Branding.Version, console.Output);
        Assert.Contains(".coda", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Doctor_output_contains_active_provider_id()
    {
        var (app, context, console) = BuildDoctorApp();

        await app.DispatchAsync(ParsedInput.Slash("doctor", Array.Empty<string>()), CancellationToken.None);

        Assert.Contains(context.Session.ActiveProviderId, console.Output);
    }

    [Fact]
    public async Task Doctor_output_mentions_commands()
    {
        var (app, _, console) = BuildDoctorApp();

        await app.DispatchAsync(ParsedInput.Slash("doctor", Array.Empty<string>()), CancellationToken.None);

        // Must mention "commands" (the word) or contain a digit so the user knows the command count.
        var output = console.Output;
        Assert.True(
            output.Contains("commands", StringComparison.OrdinalIgnoreCase) || output.Any(char.IsDigit),
            "expected 'commands' word or a digit in doctor output");
    }

    [Fact]
    public async Task Doctor_returns_Continue()
    {
        var (app, _, _) = BuildDoctorApp();

        var result = await app.DispatchAsync(ParsedInput.Slash("doctor", Array.Empty<string>()), CancellationToken.None);

        Assert.False(result.ShouldExit);
    }

    private static (TuiApp App, CommandContext Context, TestConsole Console) BuildDoctorApp()
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var copilot = new GitHubCopilotProvider();
        var apiKey = new ApiKeyProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude, copilot, apiKey });

        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
            new("github-copilot", "GitHub Copilot", LoginKind.DeviceCode, "gpt-4o"),
            new("anthropic-api-key", "Anthropic API key", LoginKind.ApiKey, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai");
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new LoginCommand(), new LogoutCommand(), new StatusCommand(),
            new ProviderCommand(), new ModelCommand(), new HeadersCommand(), new ClearCommand(),
            new VersionCommand(), new ExitCommand(),
            new DiffCommand(), new DoctorCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (new TuiApp(context), context, console);
    }
}

/// <summary>Tests for /diff slash command.</summary>
public sealed class DiffCommandTests
{
    [Fact]
    public async Task Diff_returns_Continue_in_non_git_directory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var (app, _, _) = BuildDiffApp(tempDir);

            CommandResult result = CommandResult.Continue;
            var ex = await Record.ExceptionAsync(async () =>
            {
                result = await app.DispatchAsync(ParsedInput.Slash("diff", Array.Empty<string>()), CancellationToken.None);
            });

            Assert.Null(ex);
            Assert.False(result.ShouldExit);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Diff_in_non_git_dir_prints_something()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var (app, _, console) = BuildDiffApp(tempDir);

            await app.DispatchAsync(ParsedInput.Slash("diff", Array.Empty<string>()), CancellationToken.None);

            // Whatever the outcome (no git, not a repo, etc.), something must be printed.
            Assert.NotEmpty(console.Output);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static (TuiApp App, CommandContext Context, TestConsole Console) BuildDiffApp(string workingDirectory)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var copilot = new GitHubCopilotProvider();
        var apiKey = new ApiKeyProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude, copilot, apiKey });

        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
            new("github-copilot", "GitHub Copilot", LoginKind.DeviceCode, "gpt-4o"),
            new("anthropic-api-key", "Anthropic API key", LoginKind.ApiKey, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai") { WorkingDirectory = workingDirectory };
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new DiffCommand(), new DoctorCommand(),
            new HelpCommand(), new LoginCommand(), new LogoutCommand(), new StatusCommand(),
            new ProviderCommand(), new ModelCommand(), new HeadersCommand(), new ClearCommand(),
            new VersionCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (new TuiApp(context), context, console);
    }
}
