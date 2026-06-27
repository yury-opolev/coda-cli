using LlmClient;

namespace Engine.Tests;

/// <summary>
/// GitHub Copilot's live model ids use a dotted version (claude-opus-4.8); the
/// claude.ai catalog uses a dashed one (claude-opus-4-8), which Copilot rejects with
/// HTTP 400 model_not_supported. Normalizing dash→dot stops a regressed settings.json
/// from silently 400-ing every call (the June 23 sessions 1–2 failure).
/// </summary>
public sealed class CopilotModelIdTests
{
    [Theory]
    [InlineData("claude-opus-4-8", "claude-opus-4.8")]
    [InlineData("claude-sonnet-4-6", "claude-sonnet-4.6")]
    [InlineData("claude-opus-4-1", "claude-opus-4.1")]
    [InlineData("claude-opus-4.8", "claude-opus-4.8")]    // already dotted
    [InlineData("gpt-4o", "gpt-4o")]                       // non-claude untouched
    [InlineData("claude-3-5-sonnet", "claude-3-5-sonnet")] // not a major-minor version tail
    [InlineData("", "")]
    public void Normalize_fixes_claude_version_dash(string input, string expected)
    {
        Assert.Equal(expected, CopilotModelId.Normalize(input));
    }
}
