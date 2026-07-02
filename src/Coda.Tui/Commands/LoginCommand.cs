using Coda.Agent.Settings;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
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
        var provider = args.Count > 0 ? context.ResolveProvider(args[0]) : ChooseProvider(context);
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
            PersistDefaultProvider(provider);
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
                PersistDefaultProvider(provider);
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

    /// <summary>
    /// Pick the provider to sign in to when <c>/login</c> is called with no argument:
    /// an interactive picker when a terminal is attached and more than one provider
    /// exists (so the user isn't silently sent to whatever happens to be active),
    /// otherwise the active provider.
    /// </summary>
    private static ProviderDescriptor ChooseProvider(CommandContext context)
    {
        if (!context.Console.Profile.Capabilities.Interactive || context.Providers.Count <= 1)
        {
            return context.ActiveProvider;
        }

        return context.Console.Prompt(
            new SelectionPrompt<ProviderDescriptor>()
                .Title(Theme.DimMarkup("Sign in to which provider?"))
                .UseConverter(p => p.DisplayName)
                .AddChoices(context.Providers));
    }

    /// <summary>
    /// Persist the signed-in provider as the startup default so it survives restarts.
    /// The default model is cleared so the new provider's own default is used rather than
    /// a stale cross-provider model id. Best-effort — a settings write failure must not
    /// fail the sign-in.
    /// </summary>
    private static void PersistDefaultProvider(ProviderDescriptor provider)
    {
        try
        {
            SettingsWriter.SetUserDefaults(defaultProvider: provider.Id, defaultModel: string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave the default unchanged; the session still uses the just-selected provider.
        }
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
        // GitHub Copilot is deployment-aware: ask whether to sign in to public github.com
        // or a GitHub Enterprise Cloud data-residency tenant, persist the choice, and run
        // the device flow against that instance — so enterprise users authenticate against
        // their own host and are not re-prompted on later sessions.
        if (provider.Id == GitHubCopilotProvider.Id)
        {
            return await LoginCopilotDeviceAsync(context, provider, cancellationToken).ConfigureAwait(false);
        }

        return await context.Credentials.LoginWithDeviceCodeAsync(
            provider.Id,
            (prompt, ct) =>
            {
                ShowDevicePrompt(context, provider, prompt);
                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Credential> LoginCopilotDeviceAsync(CommandContext context, ProviderDescriptor provider, CancellationToken cancellationToken)
    {
        var enterpriseDomain = SettingsLoader.Load(context.Session.WorkingDirectory).GitHubEnterpriseDomain;

        if (context.Console.Profile.Capabilities.Interactive)
        {
            const string publicChoice = "Public github.com";
            const string enterpriseChoice = "GitHub Enterprise (data residency, *.ghe.com)";
            var deployment = context.Console.Prompt(
                new SelectionPrompt<string>()
                    .Title(Theme.DimMarkup("Which GitHub Copilot deployment?"))
                    .AddChoices(publicChoice, enterpriseChoice));

            if (deployment == enterpriseChoice)
            {
                var entered = context.Console.Prompt(
                    new TextPrompt<string>(Theme.DimMarkup("GitHub Enterprise domain (e.g. octocorp.ghe.com):"))
                        .DefaultValue(string.IsNullOrWhiteSpace(enterpriseDomain) ? string.Empty : enterpriseDomain)
                        .Validate(v => string.IsNullOrWhiteSpace(v)
                            ? ValidationResult.Error("Enter your GitHub Enterprise domain")
                            : ValidationResult.Success()));
                enterpriseDomain = entered.Trim();
            }
            else
            {
                enterpriseDomain = null;
            }

            // Persist for future sessions (empty string clears back to public github.com).
            SettingsWriter.SetGitHubEnterpriseDomain(enterpriseDomain ?? string.Empty);
        }

        // Make this process agree immediately: the auth provider below AND the connection
        // test (which builds a chat client via FromEnvironment) resolve the same host.
        Environment.SetEnvironmentVariable(
            "GH_COPILOT_ENTERPRISE_DOMAIN",
            string.IsNullOrWhiteSpace(enterpriseDomain) ? null : enterpriseDomain);

        var config = GitHubCopilotConfig.FromEnvironment();
        using var hostProvider = new GitHubCopilotProvider(config);
        var credential = await hostProvider.LoginWithDeviceCodeAsync(
            new LoginOptions(),
            (prompt, ct) =>
            {
                ShowDevicePrompt(context, provider, prompt);
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        await context.Credentials.StoreAsync(provider.Id, credential, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(enterpriseDomain))
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                $"Using GitHub Enterprise {Markup.Escape(enterpriseDomain)}. Saved for future sessions."));
        }

        return credential;
    }

    private static void ShowDevicePrompt(CommandContext context, ProviderDescriptor provider, DeviceCodePrompt prompt)
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
    }
}
