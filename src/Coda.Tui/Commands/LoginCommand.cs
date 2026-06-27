using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Signs in to a provider. Claude.ai opens the browser + a loopback listener;
/// GitHub Copilot uses the device-code flow (the library calls back here to show
/// the code). Both are driven through the LlmAuth host-callback model.
/// </summary>
public sealed class LoginCommand : ISlashCommand
{
    public string Name => "login";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Sign in to a provider";

    public CommandHelp Help => new(
        "/login [<provider>]",
        Description: "Sign in to a provider. Claude.ai uses a browser OAuth loopback flow; GitHub Copilot uses the device-code flow (prints a URL + code to paste). With no argument, signs in to the currently active provider.",
        Options:
        [
            ("(no args)", "sign in to the active provider"),
            ("<provider>", "sign in to a specific provider (e.g. claude, copilot)"),
        ],
        Examples: ["/login", "/login copilot", "/login claude"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var provider = args.Count > 0 ? context.ResolveProvider(args[0]) : context.ActiveProvider;
        if (provider is null)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Unknown provider '{args[0]}'."));
            return CommandResult.Continue;
        }

        // API-key auth has no interactive step — handle it up front.
        if (provider.LoginKind == LoginKind.ApiKey)
        {
            context.Console.MarkupLine(Theme.DimMarkup($"{provider.DisplayName} uses {ApiKeyProvider.EnvVarName} — no interactive login needed."));
            context.SetActiveProvider(provider);
            return CommandResult.Continue;
        }

        try
        {
            var credential = provider.LoginKind switch
            {
                LoginKind.OAuthLoopback => await LoginLoopbackAsync(context, provider, cancellationToken).ConfigureAwait(false),
                LoginKind.DeviceCode => await LoginDeviceAsync(context, provider, cancellationToken).ConfigureAwait(false),
                _ => null,
            };

            if (credential is not null)
            {
                context.SetActiveProvider(provider);
                context.Console.MarkupLine(Theme.SuccessMarkup($"Signed in to {provider.DisplayName}."));
                if (!string.IsNullOrEmpty(credential.Account?.EmailAddress))
                {
                    context.Console.MarkupLine($"  {Theme.DimMarkup("account:")} {Markup.Escape(credential.Account!.EmailAddress!)}");
                }

                if (credential.ExpiresAt is { } expires)
                {
                    context.Console.MarkupLine($"  {Theme.DimMarkup("expires:")} {expires.ToLocalTime():g}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            context.Console.MarkupLine(Theme.DimMarkup("Sign-in canceled."));
        }
        catch (LlmAuthException ex)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Sign-in failed: {ex.Message}"));
        }

        return CommandResult.Continue;
    }

    private static async Task<Credential> LoginLoopbackAsync(CommandContext context, ProviderDescriptor provider, CancellationToken cancellationToken)
    {
        context.Console.MarkupLine($"Opening your browser to sign in to {Theme.AccentMarkup(provider.DisplayName)}…");
        return await context.Credentials.LoginAsync(provider.Id, new LoginOptions
        {
            OpenBrowser = async (url, ct) =>
            {
                context.Console.MarkupLine(Theme.DimMarkup("If your browser didn't open, visit:"));
                context.Console.WriteLine(url.ToString());
                await SystemBrowser.OpenAsync(url, ct).ConfigureAwait(false);
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Credential> LoginDeviceAsync(CommandContext context, ProviderDescriptor provider, CancellationToken cancellationToken)
    {
        return await context.Credentials.LoginWithDeviceCodeAsync(
            provider.Id,
            (prompt, ct) =>
            {
                var panel = new Panel(new Markup(
                    $"{Theme.DimMarkup("1. Open")} {Theme.AccentMarkup(prompt.VerificationUri.ToString())}\n" +
                    $"{Theme.DimMarkup("2. Enter code")} {Theme.BoldMarkup(prompt.UserCode)}"))
                {
                    Header = new PanelHeader($" Sign in to {provider.DisplayName} ", Justify.Left),
                    Border = BoxBorder.Rounded,
                };
                context.Console.Write(panel);
                context.Console.MarkupLine(Theme.DimMarkup("Waiting for you to authorize…"));
                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
