using Coda.Mcp;
using LlmAuth;

namespace Engine.Tests;

public sealed class McpSecretResolverTests
{
    [Fact]
    public async Task Resolves_coda_secret_ref_from_store_in_stdio_env()
    {
        var store = new FakeStore();
        await store.SetAsync("mcp:github/env/TOKEN", "ghp_secret");
        var servers = new Dictionary<string, McpServerConfig>
        {
            ["github"] = new McpStdioServerConfig("npx", [],
                new Dictionary<string, string> { ["TOKEN"] = "coda-secret:mcp:github/env/TOKEN" }),
        };

        var resolved = await McpSecretResolver.ResolveAsync(servers, store);

        Assert.Equal("ghp_secret", ((McpStdioServerConfig)resolved["github"]).Env["TOKEN"]);
    }

    [Fact]
    public async Task Resolves_env_var_form()
    {
        Environment.SetEnvironmentVariable("CODA_TEST_MCP_SECRET", "from-env");
        try
        {
            var servers = new Dictionary<string, McpServerConfig>
            {
                ["s"] = new McpStdioServerConfig("x", [],
                    new Dictionary<string, string> { ["K"] = "${CODA_TEST_MCP_SECRET}" }),
            };

            var resolved = await McpSecretResolver.ResolveAsync(servers, new FakeStore());

            Assert.Equal("from-env", ((McpStdioServerConfig)resolved["s"]).Env["K"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODA_TEST_MCP_SECRET", null);
        }
    }

    [Fact]
    public async Task Leaves_literals_unchanged()
    {
        var servers = new Dictionary<string, McpServerConfig>
        {
            ["s"] = new McpStdioServerConfig("x", [], new Dictionary<string, string> { ["K"] = "plain-value" }),
        };

        var resolved = await McpSecretResolver.ResolveAsync(servers, new FakeStore());

        Assert.Equal("plain-value", ((McpStdioServerConfig)resolved["s"]).Env["K"]);
    }

    [Fact]
    public async Task Resolves_http_header_and_bearer_token()
    {
        var store = new FakeStore();
        await store.SetAsync("mcp:remote/auth/token", "bearer-secret");
        await store.SetAsync("mcp:remote/header/X-Key", "hdr-secret");
        var servers = new Dictionary<string, McpServerConfig>
        {
            ["remote"] = new McpHttpServerConfig(
                new Uri("https://x/mcp"),
                new Dictionary<string, string> { ["X-Key"] = "coda-secret:mcp:remote/header/X-Key" },
                new McpAuthConfig(McpAuthMode.Bearer, BearerToken: "coda-secret:mcp:remote/auth/token")),
        };

        var http = (McpHttpServerConfig)(await McpSecretResolver.ResolveAsync(servers, store))["remote"];

        Assert.Equal("hdr-secret", http.Headers["X-Key"]);
        Assert.Equal("bearer-secret", http.Auth.BearerToken);
    }

    [Fact]
    public async Task Missing_secret_resolves_to_empty()
    {
        var servers = new Dictionary<string, McpServerConfig>
        {
            ["s"] = new McpStdioServerConfig("x", [],
                new Dictionary<string, string> { ["K"] = "coda-secret:mcp:s/env/absent" }),
        };

        var resolved = await McpSecretResolver.ResolveAsync(servers, new FakeStore());

        Assert.Equal(string.Empty, ((McpStdioServerConfig)resolved["s"]).Env["K"]);
    }

    private sealed class FakeStore : ITokenStore
    {
        private readonly Dictionary<string, string> map = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(this.map.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            this.map[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            this.map.Remove(key);
            return Task.CompletedTask;
        }
    }
}
