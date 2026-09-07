using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Setup;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Shows the active provider, or connects to a different one (replacing the current connection).</summary>
public sealed class ProviderCommand : ISlashCommand
{
    public string Name => "provider";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show the active provider, or connect to a different one";

    public CommandHelp Help => new(
        "/provider [<id>]",
        Description: "Show the active provider and available providers, or connect to a provider (replaces the current connection). Provider identity is derived from the connected credential — no startup default is written.",
        Options:
        [
            ("(no args)", "show the active provider and list all available providers"),
            ("<id>", "connect to the named provider, replacing the current connection"),
        ],
        Examples: ["/provider", "/provider copilot", "/provider claude"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        // "--default" is accepted for back-compat but is now a no-op: connecting no
        // longer persists a startup default (provider is derived from the credential).
        var token = args.FirstOrDefault(a => !string.Equals(a, "--default", StringComparison.OrdinalIgnoreCase));

        if (token is null)
        {
            // With a prompt surface that can answer, offer the shared picker and connect to the
            // selection; otherwise keep the plain listing (and never await a prompt).
            if (context.Prompts.IsInteractive)
            {
                var chosen = await SetupWizard.ChooseProviderAsync(context, cancellationToken).ConfigureAwait(false);
                if (chosen is null)
                {
                    return CommandResult.Continue;
                }

                await ConnectAndPublishAsync(context, chosen, cancellationToken).ConfigureAwait(false);
                return CommandResult.Continue;
            }

            // Non-interactive: query the actual connected credential state now,
            // not the session-start snapshot in ActiveProviderId.  Showing
            // "active" without checking authentication status is misleading —
            // the provider might be a no-credential fallback or have an expired
            // token, yet still appear as "active" to the user.
            await RenderProviderStatusAsync(context, cancellationToken).ConfigureAwait(false);
            return CommandResult.Continue;
        }

        var resolved = context.ResolveProvider(token);
        if (resolved is null)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Unknown provider '{token}'."));
            return CommandResult.Continue;
        }

        // Connect to the resolved provider — the same login/connect flow /login uses.
        // The credential store enforces a single credential, so this replaces whatever
        // was previously connected; no defaultProvider settings pointer is written.
        await ConnectAndPublishAsync(context, resolved, cancellationToken).ConfigureAwait(false);
        return CommandResult.Continue;
    }

    /// <summary>
    /// Show the actual sign-in status of every provider, using the credential
    /// store at call time rather than the stale session snapshot.  This avoids
    /// the misleading "Active provider: X" label when X was merely the first
    /// registered provider used as a no-credential fallback.
    /// </summary>
    private static async Task RenderProviderStatusAsync(CommandContext context, CancellationToken cancellationToken)
    {
        // Which provider currently has a stored credential (raw check, no refresh).
        var connectedId = await context.Credentials.GetConnectedProviderIdAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var provider in context.Providers)
        {
            bool signedIn;
            if (provider.LoginKind == LoginKind.ApiKey)
            {
                signedIn = !string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable(ApiKeyProvider.EnvVarName));
            }
            else
            {
                signedIn = string.Equals(provider.Id, connectedId, StringComparison.OrdinalIgnoreCase);
            }

            var marker = signedIn ? Theme.SuccessMarkup("●") : Theme.DimMarkup("○");
            var statusLabel = signedIn ? Theme.SuccessMarkup("signed in") : Theme.DimMarkup("not signed in");
            // "in use" reflects the running session's choice; "signed in" reflects
            // what the credential store says, so both can be true independently.
            var inUse = string.Equals(provider.Id, context.Session.ActiveProviderId, StringComparison.OrdinalIgnoreCase)
                ? Theme.AccentMarkup(" (in use)") : string.Empty;

            context.Console.MarkupLine(
                $"  {marker} {Theme.AccentMarkup(provider.DisplayName)} {Theme.DimMarkup($"({provider.Id})")}: {statusLabel}{inUse}");
        }

        if (connectedId is null)
        {
            context.Console.MarkupLine(Theme.WarnMarkup("No provider is signed in. Run /login to authenticate."));
        }
    }

    /// <summary>
    /// Connect to <paramref name="provider"/> and publish a <see cref="Ui.Events.SessionMetadataChangedEvent"/>
    /// only when the connection was accepted (the session's active provider is now this one). A
    /// cancelled or failed sign-in leaves the active provider unchanged and publishes nothing.
    /// </summary>
    private static async Task ConnectAndPublishAsync(CommandContext context, ProviderDescriptor provider, CancellationToken cancellationToken)
    {
        await LoginCommand.ConnectAsync(context, provider, cancellationToken).ConfigureAwait(false);
        if (string.Equals(context.Session.ActiveProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
        {
            SessionMetadataEvents.Publish(context);
        }
    }
}
