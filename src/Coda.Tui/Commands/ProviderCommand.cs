using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Shows or switches the active provider; <c>--default</c> persists the choice.</summary>
public sealed class ProviderCommand : ISlashCommand
{
    public string Name => "provider";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show or switch the active provider (persisted as the default)";

    public CommandHelp Help => new(
        "/provider [<id>]",
        Description: "Show the active provider and available providers, or switch to a different one. Switching persists the choice as the startup default and resets the persisted model so the new provider's default is used on next launch.",
        Options:
        [
            ("(no args)", "show the active provider and list all available providers"),
            ("<id>", "switch to the named provider and save it as the startup default"),
            ("--default", "accepted for compatibility; switching always persists the choice"),
        ],
        Examples: ["/provider", "/provider copilot", "/provider claude"]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        // "--default" is accepted for back-compat but is now implied: choosing a
        // provider persists it as the startup default.
        var token = args.FirstOrDefault(a => !string.Equals(a, "--default", StringComparison.OrdinalIgnoreCase));

        if (token is null)
        {
            context.Console.MarkupLine($"Active provider: {Theme.AccentMarkup(context.ActiveProvider.DisplayName)} {Theme.DimMarkup($"({context.ActiveProvider.Id})")}");
            context.Console.MarkupLine(Theme.DimMarkup("Available:"));
            foreach (var provider in context.Providers)
            {
                context.Console.MarkupLine($"  {Theme.AccentMarkup(provider.Id)} {Theme.DimMarkup($"— {provider.DisplayName}")}");
            }

            return Task.FromResult(CommandResult.Continue);
        }

        var resolved = context.ResolveProvider(token);
        if (resolved is null)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Unknown provider '{token}'."));
            return Task.FromResult(CommandResult.Continue);
        }

        context.SetActiveProvider(resolved);

        // Persist the choice as the startup default, and reset the persisted model so
        // startup uses the new provider's default (avoids a stale cross-provider model);
        // a later /model pins a specific one. Best-effort: a failed write is reported,
        // not fatal — the in-session switch already applied.
        var note = ModelCommand.TryPersistDefaults(defaultProvider: resolved.Id, defaultModel: string.Empty);

        context.Console.MarkupLine(
            $"Active provider is now {Theme.AccentMarkup(resolved.DisplayName)} {Theme.DimMarkup($"(model: {context.Session.Model}) {note}")}");
        return Task.FromResult(CommandResult.Continue);
    }
}
