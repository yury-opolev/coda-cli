using Coda.Agent;
using Coda.Agent.OutputStyles;
using LlmAuth.Providers.GitHubCopilot;

namespace Coda.Sdk;

/// <summary>Resolves the complete root system prompt for a session.</summary>
public static class EffectiveSystemPrompt
{
    public static string Resolve(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SystemPromptOverride is not null)
        {
            return options.SystemPromptOverride;
        }

        var outputStyle = BuiltInOutputStyles.Resolve(options.OutputStyle);
        return AgentSystemPrompt.Build(
            options.WorkingDirectory,
            includeAnthropicSystemPrefix: options.ProviderId != GitHubCopilotProvider.Id,
            ProjectContext.Load(options.WorkingDirectory),
            outputStyle.SystemPromptSuffix);
    }
}
