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

        // Session-scoped plugin styles take precedence over the static registry for serve
        // isolation: each serve session carries only its own working-directory's plugin styles.
        // Built-in names still win because ResolveOutputStyle checks built-ins first.
        var outputStyle = ResolveOutputStyle(options);
        return AgentSystemPrompt.Build(
            options.WorkingDirectory,
            includeAnthropicSystemPrefix: options.ProviderId != GitHubCopilotProvider.Id,
            ProjectContext.Load(options.WorkingDirectory),
            outputStyle.SystemPromptSuffix);
    }

    private static OutputStyle ResolveOutputStyle(SessionOptions options)
    {
        var name = options.OutputStyle;

        // Built-ins always win — check them first so a plugin cannot shadow "concise" etc.
        var builtIn = BuiltInOutputStyles.Resolve(name);
        if (!string.IsNullOrWhiteSpace(name)
            && !string.Equals(name, "default", StringComparison.OrdinalIgnoreCase)
            && string.Equals(builtIn.Name, "default", StringComparison.OrdinalIgnoreCase))
        {
            // Built-in returned "default" as its fallback, meaning name is unknown to built-ins.
            // Check session-scoped plugin styles.
            foreach (var style in options.PluginOutputStyles)
            {
                if (string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return style;
                }
            }
        }

        return builtIn;
    }
}
