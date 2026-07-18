using Coda.Tui;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using Spectre.Console;

namespace Coda.Tui.Tests;

/// <summary>
/// Builds a <see cref="TuiApp"/> wired exactly as the production plain-mode composition: the command
/// <see cref="IAnsiConsole"/> publishes into the shared mailbox (semantic mode enabled), and the
/// non-interactive <see cref="PlainUiPromptService"/> is the prompt surface. Used to prove the plain
/// loop dispatches through the actor pipeline without any direct <see cref="System.Console"/> writes.
/// </summary>
internal static class PlainCompositionFactory
{
    public static (TuiApp App, CommandContext Context) Build(IAnsiConsole console, IUiEventPublisher events)
    {
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
        var registry = new SlashCommandRegistry(SlashCommandCatalog.CreateAll());

        var context = new CommandContext(
            console,
            credentials,
            session,
            providers,
            registry,
            prompts: PlainUiPromptService.Instance,
            events: events,
            semanticUiEnabled: true);

        return (new TuiApp(context), context);
    }
}
