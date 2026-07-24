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
    public static (TuiApp App, CommandContext Context, TestConsole Console, CredentialManager Credentials) BuildApp(
        ITokenStore? store = null,
        IUiPromptService? prompts = null,
        IUiEventPublisher? events = null,
        string? workingDirectory = null)
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
        context.McpManagement = new CurrentContextMcpManagementService(context, store);
        return (new TuiApp(context), context, console, credentials);
    }

    private sealed class CurrentContextMcpManagementService : IMcpManagementService
    {
        private static readonly HttpClient http = new();
        private readonly CommandContext context;
        private readonly ITokenStore fallbackStore;

        public CurrentContextMcpManagementService(CommandContext context, ITokenStore fallbackStore)
        {
            this.context = context;
            this.fallbackStore = fallbackStore;
        }

        public event Action? Changed
        {
            add { }
            remove { }
        }

        private IMcpManagementService Create() => new McpManagementService(
            this.context.Session.WorkingDirectory,
            userMcpDir: null,
            this.context.Mcp,
            this.context.CredentialStore ?? this.fallbackStore,
            new DefaultMcpOAuthReauthenticator(http, this.context.CredentialStore ?? this.fallbackStore),
            this.context.Events);

        public Task<McpManagementSnapshot> RefreshAsync(CancellationToken ct) => this.Create().RefreshAsync(ct);

        public Task<McpServerDetail?> GetDetailAsync(McpServerKey key, CancellationToken ct) =>
            this.Create().GetDetailAsync(key, ct);

        public Task<McpServerDraft?> CreateEditDraftAsync(McpServerKey key, CancellationToken ct) =>
            this.Create().CreateEditDraftAsync(key, ct);

        public Task<McpEditPreview> PrepareAddAsync(McpServerDraft draft, CancellationToken ct) =>
            this.Create().PrepareAddAsync(draft, ct);

        public Task<McpEditPreview> PrepareEditAsync(McpServerKey original, McpServerDraft draft, CancellationToken ct) =>
            this.Create().PrepareEditAsync(original, draft, ct);

        public Task<McpMutationResult> CommitAddAsync(McpEditPreview preview, CancellationToken ct) =>
            this.Create().CommitAddAsync(preview, ct);

        public Task<McpMutationResult> CommitEditAsync(McpEditPreview preview, CancellationToken ct) =>
            this.Create().CommitEditAsync(preview, ct);

        public Task<McpMutationResult> SetEnabledAsync(McpServerKey key, bool enabled, CancellationToken ct) =>
            this.Create().SetEnabledAsync(key, enabled, ct);

        public Task<McpDeletePreview> PrepareDeleteAsync(McpServerKey key, CancellationToken ct) =>
            this.Create().PrepareDeleteAsync(key, ct);

        public Task<McpMutationResult> CommitDeleteAsync(McpDeletePreview preview, CancellationToken ct) =>
            this.Create().CommitDeleteAsync(preview, ct);

        public Task<McpReauthenticationPlan> PrepareReauthenticationAsync(McpServerKey key, CancellationToken ct) =>
            this.Create().PrepareReauthenticationAsync(key, ct);

        public Task<McpMutationResult> ReauthenticateAsync(
            McpReauthenticationPlan plan,
            IReadOnlyDictionary<string, McpSecretReplacement> replacements,
            CancellationToken ct) =>
            this.Create().ReauthenticateAsync(plan, replacements, ct);

        public Task<McpMutationResult> StartAsync(string name, CancellationToken ct) => this.Create().StartAsync(name, ct);

        public Task<McpMutationResult> StopAsync(string name, CancellationToken ct) => this.Create().StopAsync(name, ct);

        public Task<McpMutationResult> RestartAsync(string? name, CancellationToken ct) => this.Create().RestartAsync(name, ct);
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
