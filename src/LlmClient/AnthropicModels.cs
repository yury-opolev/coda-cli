namespace LlmClient;

/// <summary>Well-known Anthropic model ids and the wire system-prompt prefix.</summary>
public static class AnthropicModels
{
    public const string DefaultModel = "claude-sonnet-4-6";

    public static readonly IReadOnlyList<string> Known =
    [
        "claude-opus-4-8",
        "claude-sonnet-4-6",
        "claude-haiku-4-5",
    ];

    /// <summary>
    /// Required first line of the wire system prompt for Claude.ai OAuth-subscriber
    /// inference to be accepted. This is the provider fingerprint, NOT user-facing
    /// branding (the UI is branded "Coda").
    /// </summary>
    public const string AnthropicSystemPrefix = "You are Claude Code, Anthropic's official CLI for Claude.";
}
