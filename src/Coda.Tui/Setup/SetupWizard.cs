using System.Text;
using Coda.Agent;
using Coda.Agent.OutputStyles;
using Coda.Sdk;
using Coda.Tui.Agent;
using Coda.Tui.Commands;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using LlmClient;
using LlmAuth.Providers.GitHubCopilot;
using Spectre.Console;

namespace Coda.Tui.Setup;

/// <summary>
/// Guided first-run setup: welcome → pick a provider → sign in → (optionally)
/// verify the connection with a tiny real completion → ready. Re-runnable via
/// the /setup command.
/// </summary>
public sealed class SetupWizard
{
    public async Task RunAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var console = context.Console;

        var intro = new Panel(new Markup(
            $"{Theme.DimMarkup("Let's connect")} {Theme.AccentMarkup(Branding.ProductName)} {Theme.DimMarkup("to an LLM so you can start chatting.")}"))
        {
            Header = new PanelHeader(" Setup ", Justify.Left),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0, 1, 0),
        };
        console.Write(intro);

        if (!context.Prompts.IsInteractive)
        {
            console.MarkupLine(Theme.DimMarkup("Non-interactive: run /login <claude|copilot> to connect, then type a message."));
            return;
        }

        var provider = await ChooseProviderAsync(context, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            // Cancelled (or no interactive prompt surface) — nothing mutated.
            return;
        }

        // Reuse the real login flow (browser-loopback / device-code / api-key).
        await new LoginCommand().ExecuteAsync(context, [provider.Id], cancellationToken).ConfigureAwait(false);
        SystemPromptCompatibilityWarning.Publish(context);

        await this.VerifyAsync(context, provider, cancellationToken).ConfigureAwait(false);

        console.MarkupLine(Theme.SuccessMarkup("Setup complete.") + " " +
            Theme.DimMarkup("Type a message to start, or /help for commands."));
    }

    /// <summary>
    /// Present the provider picker (title <c>Choose a provider</c>, option id = provider id) through
    /// the host-neutral prompt surface. Returns the chosen provider, or <c>null</c> when the surface is
    /// non-interactive or the user dismisses the prompt — the caller mutates nothing until a provider
    /// is returned.
    /// </summary>
    internal static async Task<ProviderDescriptor?> ChooseProviderAsync(
        CommandContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Prompts.IsInteractive)
        {
            return null;
        }

        var options = context.Providers.Select(p => new UiPromptOption(p.Id, p.DisplayName));
        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Select("Choose a provider", options),
            cancellationToken).ConfigureAwait(false);

        if (response.Cancelled || response.SelectedIds.Length == 0)
        {
            return null;
        }

        return context.FindProvider(response.SelectedIds[0]);
    }

    private async Task VerifyAsync(CommandContext context, ProviderDescriptor provider, CancellationToken cancellationToken)
    {
        var console = context.Console;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var client = LlmClientFactory.Create(provider.Id, context.Credentials, new ClientFingerprint(), http);
        if (client is null)
        {
            console.MarkupLine(Theme.DimMarkup($"Skipping connection test for {provider.DisplayName} (chat not wired yet)."));
            return;
        }

        console.MarkupLine(Theme.DimMarkup("Testing the connection…"));
        try
        {
            var request = new ChatRequest
            {
                Model = context.Session.Model,
                MaxTokens = 16,
                System = ResolveVerificationSystemPrompt(context.Session),
                Messages = [ChatMessage.UserText("Reply with the single word: OK")],
            };

            var reply = new StringBuilder();
            await foreach (var streamEvent in client.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (streamEvent.Kind == AssistantEventKind.TextDelta)
                {
                    reply.Append(streamEvent.Text);
                }
            }

            console.MarkupLine(Theme.SuccessMarkup($"✓ Connected — model replied: {reply.ToString().Trim()}"));
        }
        catch (LlmClientException ex)
        {
            console.MarkupLine(Theme.WarnMarkup($"Couldn't verify (HTTP {ex.StatusCode}). You can still try chatting; check /model."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.MarkupLine(Theme.WarnMarkup($"Couldn't verify the connection: {ex.Message}"));
        }
    }

    internal static string ResolveVerificationSystemPrompt(SessionState session) =>
        session.SystemPromptOverride
        ?? AgentSystemPrompt.Build(
            session.WorkingDirectory,
            includeAnthropicSystemPrefix: session.ActiveProviderId != GitHubCopilotProvider.Id,
            ProjectContext.Load(session.WorkingDirectory),
            BuiltInOutputStyles.Resolve(session.OutputStyle).SystemPromptSuffix);
}
