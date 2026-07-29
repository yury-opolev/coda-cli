namespace Coda.Agent.Hooks;

/// <summary>
/// Canonical list of environment variables that carry provider credentials, and the policy
/// for removing them from a hook subprocess environment.
/// </summary>
/// <remarks>
/// Command hooks inherit coda's process environment. For user-scoped hooks that is intended —
/// the user authored the command and already has the secrets on disk. For project-scoped hooks
/// (a cloned repository's <c>.coda/settings.json</c>) and plugin-contributed hooks the author is
/// not necessarily the operator, so the credentials are stripped before the child starts.
/// </remarks>
public static class HookCredentialScrubber
{
    private static readonly string[] Names =
    [
        // Anthropic / Claude
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "CLAUDE_CODE_OAUTH_TOKEN",

        // OpenAI and Azure OpenAI
        "OPENAI_API_KEY",
        "OPENAI_ORG_ID",
        "AZURE_OPENAI_API_KEY",

        // GitHub / Copilot
        "GITHUB_TOKEN",
        "GH_TOKEN",
        "GITHUB_COPILOT_TOKEN",

        // Google
        "GOOGLE_API_KEY",
        "GEMINI_API_KEY",
        "GOOGLE_APPLICATION_CREDENTIALS",

        // AWS (Bedrock)
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SESSION_TOKEN",

        // Other model providers
        "AZURE_API_KEY",
        "COHERE_API_KEY",
        "DEEPSEEK_API_KEY",
        "FIREWORKS_API_KEY",
        "GROQ_API_KEY",
        "HF_TOKEN",
        "HUGGING_FACE_HUB_TOKEN",
        "MISTRAL_API_KEY",
        "OPENROUTER_API_KEY",
        "PERPLEXITY_API_KEY",
        "TOGETHER_API_KEY",
        "XAI_API_KEY",

        // coda's own server credential
        "CODA_SERVE_API_KEY",
    ];

    /// <summary>The environment variable names removed from a scrubbed hook environment.</summary>
    public static IReadOnlyList<string> VariableNames => Names;

    /// <summary>
    /// Returns <see langword="true"/> when a hook with the given provenance must run without
    /// coda's provider credentials.
    /// </summary>
    /// <param name="scope">The settings file the hook was loaded from.</param>
    /// <param name="fromPlugin">Whether the hook was contributed by an installed plugin.</param>
    public static bool ShouldScrub(HookScope scope, bool fromPlugin)
        => fromPlugin || scope == HookScope.Project;

    /// <summary>
    /// Removes every name in <see cref="VariableNames"/> from <paramref name="environment"/>.
    /// </summary>
    /// <param name="environment">
    /// The child process environment dictionary (for example <c>ProcessStartInfo.Environment</c>).
    /// </param>
    public static void Scrub(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach (var name in Names)
        {
            environment.Remove(name);
        }
    }
}
