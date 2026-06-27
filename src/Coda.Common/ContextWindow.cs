namespace Coda.Common;

/// <summary>Default model context-window budget used when a live token count is unavailable.</summary>
public static class ContextWindow
{
    /// <summary>The default Claude context window (200K tokens).</summary>
    public const int DefaultTokens = 200_000;
}
