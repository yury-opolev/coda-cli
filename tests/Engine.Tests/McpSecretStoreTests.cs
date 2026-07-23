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
    public async Task StageAsync_writes_versioned_value_without_replacing_canonical_secret()
    {
        var store = new FakeStore();
        var canonicalKey = McpSecretStore.KeyFor("github", "env/TOKEN");
        await store.SetAsync(canonicalKey, "old-value");

        var staged = await McpSecretStore.StageAsync(store, "github", "env/TOKEN", "new-value");

        Assert.Equal("env/TOKEN", staged.Field);
        Assert.StartsWith(canonicalKey + "/", staged.StoreKey, StringComparison.Ordinal);
        Assert.Equal(McpSecretResolver.SecretRefPrefix + staged.StoreKey, staged.Reference);
        Assert.Equal("new-value", await store.GetAsync(staged.StoreKey));
        Assert.Equal("old-value", await store.GetAsync(canonicalKey));
    }

    [Fact]
    public async Task StageAsync_uses_a_unique_key_for_each_staged_value()
    {
        var store = new FakeStore();

        var first = await McpSecretStore.StageAsync(store, "github", "env/TOKEN", "first");
        var second = await McpSecretStore.StageAsync(store, "github", "env/TOKEN", "second");

        Assert.NotEqual(first.StoreKey, second.StoreKey);
        Assert.Equal("first", await store.GetAsync(first.StoreKey));
        Assert.Equal("second", await store.GetAsync(second.StoreKey));
    }

    [Theory]
    [InlineData("", "env/TOKEN")]
    [InlineData(" ", "env/TOKEN")]
    [InlineData("github", "")]
    [InlineData("github", " ")]
    public async Task StageAsync_rejects_blank_server_or_field(string server, string field)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => McpSecretStore.StageAsync(new FakeStore(), server, field, string.Empty));
    }

    [Fact]
    public async Task StageAsync_does_not_return_a_staged_reference_when_store_write_fails()
    {
        var store = new FakeStore { SetException = new InvalidOperationException("set failed") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => McpSecretStore.StageAsync(store, "github", "env/TOKEN", "new-value"));
    }

    [Fact]
    public void References_returns_only_exact_managed_bindings_in_deterministic_order()
    {
        var stdio = new McpStdioServerConfig("npx", [], new Dictionary<string, string>
        {
            ["Z"] = "coda-secret:mcp:github/env/Z",
            ["A"] = "coda-secret:mcp:github/env/A",
            ["VARIABLE"] = "${TOKEN}",
            ["LITERAL"] = "literal",
            ["EMBEDDED"] = "prefix coda-secret:mcp:github/env/EMBEDDED",
            ["NON_WHOLE"] = "coda-secret:mcp:github/env/NON_WHOLE suffix",
            ["STAGED"] = "coda-secret:mcp:github/env/STAGED/0123456789abcdef0123456789abcdef",
        });
        var http = new McpHttpServerConfig(
            new Uri("https://mcp.example.com/mcp"),
            new Dictionary<string, string>
            {
                ["Z-Duplicate"] = "coda-secret:mcp:github/header/shared",
                ["A-Duplicate"] = "coda-secret:mcp:github/header/shared",
                ["Live"] = "coda-secret:mcp:github/header/live",
                ["Shared"] = "coda-secret:shared-key",
                ["Ignored"] = "${HEADER_TOKEN}",
            },
            new McpAuthConfig(McpAuthMode.Bearer, BearerToken: "coda-secret:mcp:github/auth/token"));

        Assert.Equal(
        [
            new McpSecretBinding("env/A", "mcp:github/env/A"),
            new McpSecretBinding("env/STAGED", "mcp:github/env/STAGED/0123456789abcdef0123456789abcdef"),
            new McpSecretBinding("env/Z", "mcp:github/env/Z"),
        ],
        McpSecretStore.References(stdio));
        Assert.Equal(
        [
            new McpSecretBinding("auth/token", "mcp:github/auth/token"),
            new McpSecretBinding("header/A-Duplicate", "mcp:github/header/shared"),
            new McpSecretBinding("header/Live", "mcp:github/header/live"),
            new McpSecretBinding("header/Shared", "shared-key"),
        ],
        McpSecretStore.References(http));
    }

    [Fact]
    public async Task DeleteKeysAsync_deletes_distinct_nonblank_keys_in_first_seen_ordinal_order()
    {
        var store = new FakeStore();

        await McpSecretStore.DeleteKeysAsync(store,
        [
            "mcp:github/env/TOKEN",
            "mcp:github/env/TOKEN",
            string.Empty,
            " ",
            "mcp:github/env/token",
            "mcp:github/env/OTHER",
            "mcp:github/env/OTHER",
        ]);

        Assert.Equal(
        [
            "mcp:github/env/TOKEN",
            "mcp:github/env/token",
            "mcp:github/env/OTHER",
        ],
        store.DeletedKeys);
    }

    [Fact]
    public async Task DeleteKeysAsync_propagates_cancellation_without_deleting()
    {
        var store = new FakeStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => McpSecretStore.DeleteKeysAsync(store, ["mcp:github/env/TOKEN"], cts.Token));

        Assert.Empty(store.DeletedKeys);
    }

    [Fact]
    public async Task DeleteKeysAsync_propagates_store_failures()
    {
        var store = new FakeStore { DeleteException = new InvalidOperationException("delete failed") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => McpSecretStore.DeleteKeysAsync(store, ["mcp:github/env/TOKEN"]));
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

        public List<string> DeletedKeys { get; } = [];

        public Exception? SetException { get; init; }

        public Exception? DeleteException { get; init; }

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(this.map.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (this.SetException is { } exception)
            {
                throw exception;
            }

            this.map[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (this.DeleteException is { } exception)
            {
                throw exception;
            }

            this.DeletedKeys.Add(key);
            this.map.Remove(key);
            return Task.CompletedTask;
        }
    }
}
