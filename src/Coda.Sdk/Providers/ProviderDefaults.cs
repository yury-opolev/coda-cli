using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Coda.Sdk.Providers;

/// <summary>
/// Single source of truth for the default model of a given provider, used when
/// no explicit <c>--model</c>/<c>CODA_MODEL</c>/persisted default is supplied.
/// Previously copy-pasted as <c>DefaultModelFor</c> across the serve/run/models
/// runners. Delegates to each provider's canonical default-model constant.
/// </summary>
public static class ProviderDefaults
{
    /// <summary>
    /// Return the default model id for the given provider id.
    /// </summary>
    /// <param name="providerId">A canonical provider id (see <see cref="ProviderAliases"/>).</param>
    /// <returns>
    /// <see cref="CopilotModels.DefaultModel"/> for GitHub Copilot; otherwise
    /// <see cref="AnthropicModels.DefaultModel"/> (Claude.ai, Anthropic API key,
    /// and any unrecognized provider).
    /// </returns>
    public static string ModelFor(string providerId)
    {
        return providerId == GitHubCopilotProvider.Id
            ? CopilotModels.DefaultModel
            : AnthropicModels.DefaultModel;
    }
}
