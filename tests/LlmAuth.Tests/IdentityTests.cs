namespace LlmAuth.Tests;

public class IdentityTests
{
    [Fact]
    public void GetUserAgent_ExactFormat()
    {
        var identity = new AnthropicClientIdentity
        {
            Version = "2.1.156",
            UserType = "external",
            Entrypoint = "cli",
        };

        Assert.Equal("claude-cli/2.1.156 (external, cli)", identity.GetUserAgent());
    }

    [Fact]
    public void GetDefaultHeaders_ContainsExpectedKeys()
    {
        var identity = new AnthropicClientIdentity
        {
            Version = "2.1.156",
            UserType = "external",
            Entrypoint = "cli",
        };

        var headers = identity.GetDefaultHeaders();
        Assert.Equal("cli", headers["x-app"]);
        Assert.Equal("2023-06-01", headers["anthropic-version"]);
        Assert.Equal("claude-cli/2.1.156 (external, cli)", headers["User-Agent"]);
        Assert.False(string.IsNullOrEmpty(headers["X-Claude-Code-Session-Id"]));
    }

    [Fact]
    public void Constants_AreExact()
    {
        Assert.Equal("2023-06-01", AnthropicClientIdentity.AnthropicApiVersion);
        Assert.Equal("2.1.156", AnthropicClientIdentity.DefaultVersion);
    }
}
