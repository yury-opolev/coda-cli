using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Setup;
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

            context.Console.MarkupLine($"Active provider: {Theme.AccentMarkup(context.ActiveProvider.DisplayName)} {Theme.DimMarkup($"({context.ActiveProvider.Id})")}");
            context.Console.MarkupLine(Theme.DimMarkup("Available:"));
            foreach (var provider in context.Providers)
            {
                context.Console.MarkupLine($"  {Theme.AccentMarkup(provider.Id)} {Theme.DimMarkup($"— {provider.DisplayName}")}");
            }

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
