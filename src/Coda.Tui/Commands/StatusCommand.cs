using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Shows sign-in status for every provider, plus cwd and the active provider.</summary>
public sealed class StatusCommand : ISlashCommand
{
    public string Name => "status";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show sign-in status and session info";

    public CommandHelp Help => new(
        "/status",
        Description: "Show sign-in status for each provider and display the current working directory and permission mode.");

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        context.Console.MarkupLine(Theme.BoldMarkup("Status"));

        foreach (var provider in context.Providers)
        {
            var status = await BuildStatusAsync(context, provider, cancellationToken).ConfigureAwait(false);
            var line = StatusFormatter.FormatProvider(status);
            var marker = status.SignedIn ? Theme.SuccessMarkup("●") : Theme.DimMarkup("○");
            var active = provider.Id == context.Session.ActiveProviderId ? Theme.AccentMarkup(" (active)") : string.Empty;
            context.Console.MarkupLine($"  {marker} {Markup.Escape(line)}{active}");
        }

        context.Console.WriteLine();
        context.Console.MarkupLine($"{Theme.DimMarkup("cwd:")} {Markup.Escape(context.Session.WorkingDirectory)}");
        context.Console.MarkupLine($"{Theme.DimMarkup("permissions:")} {Theme.AccentMarkup(context.Session.PermissionMode.ToString())}");
        return CommandResult.Continue;
    }

    private static async Task<ProviderStatus> BuildStatusAsync(CommandContext context, ProviderDescriptor provider, CancellationToken cancellationToken)
    {
        if (provider.LoginKind == LoginKind.ApiKey)
        {
            var hasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ApiKeyProvider.EnvVarName));
            return new ProviderStatus(provider.Id, provider.DisplayName, hasKey, hasKey ? ApiKeyProvider.EnvVarName : null, null, []);
        }

        Credential? credential = null;
        try
        {
            credential = await context.Credentials.GetStoredCredentialAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmAuthException)
        {
            // treat as signed out
        }

        return credential is null
            ? new ProviderStatus(provider.Id, provider.DisplayName, false, null, null, [])
            : new ProviderStatus(provider.Id, provider.DisplayName, true, credential.Account?.EmailAddress, credential.ExpiresAt, credential.Scopes);
    }
}
