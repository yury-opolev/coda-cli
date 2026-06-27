using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Removes a stored credential (the active provider, or one named in args).</summary>
public sealed class LogoutCommand : ISlashCommand
{
    public string Name => "logout";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Sign out of a provider";

    public CommandHelp Help => new(
        "/logout [<provider>]",
        Description: "Remove stored credentials for a provider. With no argument, signs out of the currently active provider.",
        Options:
        [
            ("(no args)", "sign out of the active provider"),
            ("<provider>", "sign out of a specific provider (e.g. claude, copilot)"),
        ],
        Examples: ["/logout", "/logout copilot"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var provider = args.Count > 0 ? context.ResolveProvider(args[0]) : context.ActiveProvider;
        if (provider is null)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Unknown provider '{args[0]}'."));
            return CommandResult.Continue;
        }

        await context.Credentials.LogoutAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        context.Console.MarkupLine($"Signed out of {Theme.AccentMarkup(provider.DisplayName)}.");
        return CommandResult.Continue;
    }
}
