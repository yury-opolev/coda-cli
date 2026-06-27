using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Shows the outgoing auth + identity headers for the active provider (Authorization redacted).</summary>
public sealed class HeadersCommand : ISlashCommand
{
    public string Name => "headers";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show the outgoing request headers (secrets redacted)";

    public CommandHelp Help => new(
        "/headers",
        Description: "Show the outgoing authentication and identity headers that Coda sends for the active provider. The Authorization header value is redacted. Useful for debugging connectivity or verifying which credentials are in use.");

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var provider = context.ActiveProvider;

        if (provider.Id == ClaudeAiProvider.Id)
        {
            context.Console.MarkupLine(Theme.DimMarkup("# identity headers (sent on every first-party request):"));
            foreach (var (name, value) in new AnthropicClientIdentity().GetDefaultHeaders())
            {
                context.Console.MarkupLine($"{Theme.AccentMarkup(name)}: {Markup.Escape(value)}");
            }
        }

        AuthHeaders headers;
        try
        {
            headers = await context.Credentials.GetAuthHeadersAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialNotFoundException)
        {
            context.Console.MarkupLine(Theme.WarnMarkup($"Not signed in to {provider.DisplayName}. Run /login first."));
            return CommandResult.Continue;
        }

        context.Console.MarkupLine(Theme.DimMarkup("# credential headers:"));
        foreach (var (name, value) in headers.Headers)
        {
            var shown = name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer <redacted>" : value;
            context.Console.MarkupLine($"{Theme.AccentMarkup(name)}: {Markup.Escape(shown)}");
        }

        return CommandResult.Continue;
    }
}
