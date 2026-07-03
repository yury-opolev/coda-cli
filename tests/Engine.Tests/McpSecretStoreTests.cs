using Coda.Mcp;
using LlmAuth;

namespace Engine.Tests;

public sealed class McpSecretStoreTests
{
    [Fact]
    public async Task StoreAsync_encrypts_and_returns_ref_that_resolves_back()
    {
        var store = new FakeStore();

        var reference = await McpSecretStore.StoreAsync(store, "github", "env/TOKEN", "ghp_x");

        Assert.Equal("coda-secret:mcp:github/env/TOKEN", reference);
        Assert.Equal("ghp_x", await store.GetAsync("mcp:github/env/TOKEN"));

        // The reference resolves back to the stored plaintext via the resolver.
        var servers = new Dictionary<string, McpServerConfig>
        {
            ["github"] = new McpStdioServerConfig("npx", [], new Dictionary<string, string> { ["TOKEN"] = reference }),
        };
        var resolved = await McpSecretResolver.ResolveAsync(servers, store);
        Assert.Equal("ghp_x", ((McpStdioServerConfig)resolved["github"]).Env["TOKEN"]);
    }

    [Fact]
    public async Task DeleteSecretsAsync_removes_only_referenced_keys()
    {
        var store = new FakeStore();
        await store.SetAsync("mcp:github/env/TOKEN", "x");
        await store.SetAsync("mcp:other/env/K", "y"); // unrelated server
        var config = new McpStdioServerConfig("npx", [], new Dictionary<string, string>
        {
            ["TOKEN"] = "coda-secret:mcp:github/env/TOKEN",
            ["PLAIN"] = "literal", // not a ref → not touched
        });

        await McpSecretStore.DeleteSecretsAsync(store, config);

        Assert.Null(await store.GetAsync("mcp:github/env/TOKEN")); // deleted
        Assert.Equal("y", await store.GetAsync("mcp:other/env/K")); // untouched
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
