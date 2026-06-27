using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Prints a read-only diagnostic panel: version, cwd, provider status, and registry info.</summary>
public sealed class DoctorCommand : ISlashCommand
{
    public string Name => "doctor";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Print diagnostic information about this Coda session";

    public CommandHelp Help => new(
        "/doctor",
        Description: "Print a diagnostic panel showing the version, working directory, provider sign-in status, and command registry.");

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        context.Console.MarkupLine(Theme.BoldMarkup("Coda Doctor"));
        context.Console.WriteLine();

        // Version and session info.
        context.Console.MarkupLine($"  {Theme.DimMarkup("version:")}          {Markup.Escape(Branding.Version)}");
        context.Console.MarkupLine($"  {Theme.DimMarkup("working directory:")} {Markup.Escape(context.Session.WorkingDirectory)}");
        context.Console.MarkupLine($"  {Theme.DimMarkup("active provider:")}   {Theme.AccentMarkup(context.Session.ActiveProviderId)}");
        context.Console.MarkupLine($"  {Theme.DimMarkup("model:")}             {Markup.Escape(context.Session.Model)}");
        context.Console.WriteLine();

        // Per-provider sign-in status (mirrors StatusCommand logic).
        context.Console.MarkupLine(Theme.BoldMarkup("Providers"));
        foreach (var provider in context.Providers)
        {
            var status = await BuildProviderStatusAsync(context, provider, cancellationToken).ConfigureAwait(false);
            var marker = status.SignedIn ? Theme.SuccessMarkup("●") : Theme.DimMarkup("○");
            var signedInLabel = status.SignedIn ? Theme.SuccessMarkup("signed in") : Theme.DimMarkup("not signed in");
            var active = provider.Id == context.Session.ActiveProviderId ? Theme.AccentMarkup(" (active)") : string.Empty;
            context.Console.MarkupLine($"  {marker} {Markup.Escape(provider.DisplayName)}: {signedInLabel}{active}");
        }

        context.Console.WriteLine();

        // Slash command registry.
        var commandCount = context.Commands.ListSorted().Count;
        context.Console.MarkupLine($"  {Theme.DimMarkup("slash commands:")} {commandCount} commands registered");

        // .coda directory presence.
        var codaDir = Path.Combine(context.Session.WorkingDirectory, ".coda");
        var codaDirExists = Directory.Exists(codaDir);
        context.Console.MarkupLine($"  {Theme.DimMarkup(".coda directory:")} {(codaDirExists ? Theme.SuccessMarkup("present") : Theme.DimMarkup("absent"))}");

        return CommandResult.Continue;
    }

    private static async Task<(string Id, bool SignedIn)> BuildProviderStatusAsync(
        CommandContext context,
        ProviderDescriptor provider,
        CancellationToken cancellationToken)
    {
        if (provider.LoginKind == LoginKind.ApiKey)
        {
            var hasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ApiKeyProvider.EnvVarName));
            return (provider.Id, hasKey);
        }

        try
        {
            var credential = await context.Credentials.GetStoredCredentialAsync(provider.Id, cancellationToken).ConfigureAwait(false);
            return (provider.Id, credential is not null);
        }
        catch (LlmAuthException)
        {
            return (provider.Id, false);
        }
    }
}
