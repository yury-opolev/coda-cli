using LlmAuth.Providers.ClaudeAi;

namespace LlmAuth.Tests;

public class ClaudeAiConfigTests
{
    [Fact]
    public void Prod_HasExactValues()
    {
        var c = ClaudeAiOAuthConfig.Prod;

        Assert.Equal(string.Empty, c.ClientId);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", c.TokenUrl);
        Assert.Equal("https://claude.com/cai/oauth/authorize", c.ClaudeAiAuthorizeUrl);
        Assert.Equal("https://platform.claude.com/oauth/authorize", c.ConsoleAuthorizeUrl);
        Assert.Equal("https://api.anthropic.com", c.BaseApiUrl);
        Assert.Equal("https://api.anthropic.com/api/oauth/claude_cli/create_api_key", c.ApiKeyUrl);
        Assert.Equal("https://api.anthropic.com/api/oauth/claude_cli/roles", c.RolesUrl);
        Assert.Equal("https://platform.claude.com/oauth/code/callback", c.ManualRedirectUrl);
    }

    [Fact]
    public void Prod_AllScopes_ExactSequence()
    {
        string[] expected =
        [
            "org:create_api_key",
            "user:profile",
            "user:inference",
            "user:sessions:claude_code",
            "user:mcp_servers",
            "user:file_upload",
        ];

        Assert.True(expected.SequenceEqual(ClaudeAiOAuthConfig.Prod.AllScopes));
    }

    [Fact]
    public void Constants_AreExact()
    {
        Assert.Equal("oauth-2025-04-20", ClaudeAiOAuthConfig.OAuthBetaHeader);
        Assert.Equal("user:inference", ClaudeAiOAuthConfig.InferenceScope);
        Assert.Equal("user:profile", ClaudeAiOAuthConfig.ProfileScope);
    }

    [Fact]
    public void ConsoleScopes_AndClaudeAiScopes_AreExact()
    {
        Assert.True(new[] { "org:create_api_key", "user:profile" }
            .SequenceEqual(ClaudeAiOAuthConfig.ConsoleScopes));

        Assert.True(new[]
        {
            "user:profile",
            "user:inference",
            "user:sessions:claude_code",
            "user:mcp_servers",
            "user:file_upload",
        }.SequenceEqual(ClaudeAiOAuthConfig.ClaudeAiScopes));
    }
}
