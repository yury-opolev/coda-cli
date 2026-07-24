using Coda.Tui;
using Coda.Tui.Commands;
using Coda.Tui.Mcp;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using Coda.Mcp.Auth;
using Coda.Mcp;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

/// <summary>
/// Shared fixture: builds a <see cref="TuiApp"/> wired to a <see cref="TestConsole"/>
/// and a real <see cref="CredentialManager"/> over an in-memory token store, so command
/// dispatch and rendering can be asserted against plain-text output.
/// </summary>
internal static class TestAppBuilder
{
    private static readonly HttpClient mcpHttp = new();

    public static (TuiApp App, CommandContext Context, TestConsole Console, CredentialManager Credentials) BuildApp(
        ITokenStore? store = null,
        IUiPromptService? prompts = null,
        IUiEventPublisher? events = null,
        string? workingDirectory = null,
        McpClientManager? mcp = null,
        string? userMcpDir = null)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        store ??= new InMemoryTokenStore();
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

        var session = new SessionState("claude-ai", workingDirectory);
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new LoginCommand(), new LogoutCommand(), new StatusCommand(),
            new ProviderCommand(), new ModelCommand(), new HeadersCommand(), new ClearCommand(),
            new VersionCommand(), new ExitCommand(), new ImageCommand(), new LogCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry, prompts, events);
        context.CredentialStore = store;
        context.Mcp = mcp;
        context.McpManagement = new McpManagementService(
            context.Session.WorkingDirectory,
            userMcpDir,
            mcp,
            store,
            new DefaultMcpOAuthReauthenticator(mcpHttp, store),
            context.Events);
        return (new TuiApp(context), context, console, credentials);
    }

    /// <summary>A fake OAuth credential for the given provider, valid for one hour.</summary>
    public static Credential OAuthCredential(string providerId, string? emailAddress = "me@example.com") =>
        new()
        {
            ProviderId = providerId,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Account = emailAddress is null ? null : new AccountInfo { EmailAddress = emailAddress },
            Scopes = Array.Empty<string>(),
        };
}
