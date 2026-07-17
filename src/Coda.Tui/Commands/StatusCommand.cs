using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.State;
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
        // Semantic mode: render the immutable UI snapshot directly (no mutable-store enumeration).
        if (context.UiSnapshotProvider is { } snapshotProvider)
        {
            RenderSemantic(context, snapshotProvider());
            return CommandResult.Continue;
        }

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

    private static void RenderSemantic(CommandContext context, UiSessionSnapshot snapshot)
    {
        var console = context.Console;
        console.MarkupLine(Theme.BoldMarkup("Status"));

        var connection = snapshot.Connected ? Theme.SuccessMarkup("connected") : Theme.DimMarkup("not connected");
        console.MarkupLine($"{Theme.DimMarkup("provider:")} {Markup.Escape(snapshot.Provider)} ({connection})");
        console.MarkupLine($"{Theme.DimMarkup("model:")} {Markup.Escape(snapshot.Model)}");
        console.MarkupLine($"{Theme.DimMarkup("effort:")} {Markup.Escape(snapshot.EffectiveEffort)}");
        console.MarkupLine($"{Theme.DimMarkup("permissions:")} {Theme.AccentMarkup(snapshot.Permission.Mode.ToString())}");
        console.MarkupLine($"{Theme.DimMarkup("cwd:")} {Markup.Escape(snapshot.WorkingDirectory)}");

        if (snapshot.Git is { Branch: { } branch })
        {
            console.MarkupLine($"{Theme.DimMarkup("git:")} {Markup.Escape(branch)}{(snapshot.Git.Dirty ? "*" : string.Empty)}");
        }
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
