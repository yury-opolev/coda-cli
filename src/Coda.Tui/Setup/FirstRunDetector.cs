using Coda.Tui.Repl;
using LlmAuth.Providers.ClaudeAi;

namespace Coda.Tui.Setup;

/// <summary>
/// First run = no provider has a stored credential and no ANTHROPIC_API_KEY is set.
/// Drives whether the setup wizard auto-launches.
/// </summary>
public static class FirstRunDetector
{
    public static async Task<bool> IsFirstRunAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        foreach (var provider in context.Providers)
        {
            if (provider.LoginKind == LoginKind.ApiKey)
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ApiKeyProvider.EnvVarName)))
                {
                    return false;
                }

                continue;
            }

            var stored = await context.Credentials.GetStoredCredentialAsync(provider.Id, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                return false;
            }
        }

        return true;
    }
}
