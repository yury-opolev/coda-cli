using System.Collections.Specialized;
using System.Web;
using LlmAuth.Providers.ClaudeAi;

namespace LlmAuth.Tests;

public class AuthorizeUrlTests
{
    private static ClaudeAiProvider NewProvider() =>
        new(ClaudeAiOAuthConfig.Prod, new HttpClient(new StubHandler(_ => (System.Net.HttpStatusCode.OK, "{}"))));

    [Fact]
    public void Manual_QueryOrderAndEncoding_AreExact()
    {
        using var provider = NewProvider();
        var flow = provider.BeginLogin(new LoginOptions { RedirectMode = RedirectMode.Manual });

        // Base URL must be the claude.ai authorize endpoint.
        Assert.StartsWith("https://claude.com/cai/oauth/authorize?", flow.AuthorizeUrl.AbsoluteUri);

        var raw = flow.AuthorizeUrl.AbsoluteUri;
        var queryRaw = raw[(raw.IndexOf('?') + 1)..];
        var keysInOrder = queryRaw.Split('&').Select(p => p.Split('=')[0]).ToArray();

        Assert.Equal(
            new[]
            {
                "code", "client_id", "response_type", "redirect_uri",
                "scope", "code_challenge", "code_challenge_method", "state",
            },
            keysInOrder);

        // Decoded values.
        NameValueCollection q = HttpUtility.ParseQueryString(flow.AuthorizeUrl.Query);
        Assert.Equal("true", q["code"]);
        Assert.Equal(string.Empty, q["client_id"]); // no official client id bundled; supply via CLAUDE_CODE_OAUTH_CLIENT_ID
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("https://platform.claude.com/oauth/code/callback", q["redirect_uri"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(43, q["code_challenge"]!.Length);
        Assert.Equal(43, q["state"]!.Length);
        Assert.Equal(flow.State, q["state"]);

        // Scope decodes to the 6 scopes space-separated.
        Assert.Equal(
            "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload",
            q["scope"]);

        // Raw encoding: separators are '+' (URLSearchParams form-encoding), not %20; ':' is %3A.
        var rawScope = queryRaw.Split('&').First(p => p.StartsWith("scope=", StringComparison.Ordinal))["scope=".Length..];
        Assert.Equal(
            "org%3Acreate_api_key+user%3Aprofile+user%3Ainference+user%3Asessions%3Aclaude_code+user%3Amcp_servers+user%3Afile_upload",
            rawScope);
        Assert.DoesNotContain("%20", rawScope);
    }

    [Fact]
    public void Loopback_RedirectUri_EncodesLocalhostCallback()
    {
        using var provider = NewProvider();
        var flow = provider.BeginLogin(new LoginOptions
        {
            RedirectMode = RedirectMode.Loopback,
            LoopbackPort = 12345,
        });

        NameValueCollection q = HttpUtility.ParseQueryString(flow.AuthorizeUrl.Query);
        Assert.Equal("http://localhost:12345/callback", q["redirect_uri"]);
    }

    [Fact]
    public void InferenceOnly_ScopeIsJustInference()
    {
        using var provider = NewProvider();
        var flow = provider.BeginLogin(new LoginOptions
        {
            RedirectMode = RedirectMode.Manual,
            InferenceOnly = true,
        });

        NameValueCollection q = HttpUtility.ParseQueryString(flow.AuthorizeUrl.Query);
        Assert.Equal("user:inference", q["scope"]);
    }

    [Fact]
    public void UseClaudeAiFalse_UsesConsoleAuthorizeUrl()
    {
        using var provider = NewProvider();
        var flow = provider.BeginLogin(new LoginOptions
        {
            RedirectMode = RedirectMode.Manual,
            UseClaudeAi = false,
        });

        Assert.StartsWith("https://platform.claude.com/oauth/authorize?", flow.AuthorizeUrl.AbsoluteUri);
    }
}
