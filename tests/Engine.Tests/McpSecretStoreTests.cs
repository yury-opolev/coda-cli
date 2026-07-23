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
    public async Task StageAsync_compensates_a_write_that_succeeds_before_throwing()
    {
        var store = new FakeStore();
        var canonicalKey = McpSecretStore.KeyFor("github", "env/TOKEN");
        await store.SetAsync(canonicalKey, "old-value");
        store.SetAfterWriteException = new InvalidOperationException("set failed after write");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => McpSecretStore.StageAsync(store, "github", "env/TOKEN", "new-value"));

        Assert.Equal("old-value", await store.GetAsync(canonicalKey));
        Assert.DoesNotContain(
            store.Keys,
            key => key.StartsWith(canonicalKey + "/", StringComparison.Ordinal));
        Assert.Contains(
            store.DeletedKeys,
            key => key.StartsWith(canonicalKey + "/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StageAsync_compensates_a_cancellation_after_write()
    {
        var store = new FakeStore
        {
            SetAfterWriteException = new OperationCanceledException(),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => McpSecretStore.StageAsync(store, "github", "env/TOKEN", "new-value"));

        Assert.Empty(store.Keys);
        Assert.Single(store.DeletedKeys);
    }

    [Fact]
    public async Task StageAsync_reports_safe_cleanup_incomplete_failure()
    {
        const string failureMarker = "never-show-stage-failure";
        var store = new FakeStore
        {
            SetAfterWriteException = new InvalidOperationException(failureMarker),
            DeleteException = new InvalidOperationException("delete failed"),
        };

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => McpSecretStore.StageAsync(store, "github", "env/TOKEN", "new-value"));

        Assert.Contains("cleanup incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(failureMarker, exception.ToString(), StringComparison.Ordinal);
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
            ["NON_WHOLE"] = "coda-secret:mcp:github/env/NON_WHOLE ",
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
    public async Task Stage_KeyFor_and_References_preserve_spaces_in_server_store_keys()
    {
        var store = new FakeStore();
        var key = McpSecretStore.KeyFor("my server", "env/TOKEN");
        var staged = await McpSecretStore.StageAsync(store, "my server", "env/TOKEN", "secret");
        var config = new McpStdioServerConfig(
            "npx",
            [],
            new Dictionary<string, string> { ["TOKEN"] = staged.Reference });

        Assert.Equal("mcp:my server/env/TOKEN", key);
        Assert.StartsWith(key + "/", staged.StoreKey, StringComparison.Ordinal);
        Assert.Equal(
            [new McpSecretBinding("env/TOKEN", staged.StoreKey)],
            McpSecretStore.References(config));
    }

    [Fact]
    public void IsOwnedKey_accepts_only_the_server_field_canonical_or_versioned_key()
    {
        const string server = "my server";
        const string field = "env/TOKEN";
        var canonical = McpSecretStore.KeyFor(server, field);

        Assert.True(McpSecretStore.IsOwnedKey(server, field, canonical));
        Assert.True(McpSecretStore.IsOwnedKey(
            server,
            field,
            canonical + "/0123456789abcdef0123456789ABCDEF"));
        Assert.False(McpSecretStore.IsOwnedKey(server, field, canonical + "/not-a-guid"));
        Assert.False(McpSecretStore.IsOwnedKey(server, field, canonical + "/0123456789abcdef0123456789abcdef/child"));
        Assert.False(McpSecretStore.IsOwnedKey(server, field, "mcp:my server/env/TOKEN-extra"));
        Assert.False(McpSecretStore.IsOwnedKey(server, field, "mcp:other/env/TOKEN"));
        Assert.False(McpSecretStore.IsOwnedKey(server, field, "llmauth:provider"));
    }

    [Fact]
    public async Task References_and_DeleteSecretsAsync_require_exact_safe_managed_references()
    {
        const string validKey = "mcp:my server/env/VALID";
        const string trailingKey = "mcp:my server/env/TRAILING ";
        const string formatKey = "mcp:my\u200Bserver/env/FORMAT";
        const string controlKey = "mcp:my\tserver/env/CONTROL";
        var store = new FakeStore();
        await store.SetAsync(validKey, "valid");
        await store.SetAsync(trailingKey, "trailing");
        await store.SetAsync(formatKey, "format");
        await store.SetAsync(controlKey, "control");
        var config = new McpStdioServerConfig(
            "npx",
            [],
            new Dictionary<string, string>
            {
                ["VALID"] = "coda-secret:mcp:my server/env/VALID",
                ["LEADING"] = " coda-secret:mcp:my server/env/LEADING",
                ["TRAILING"] = "coda-secret:mcp:my server/env/TRAILING ",
                ["FORMAT"] = "coda-secret:mcp:my\u200Bserver/env/FORMAT",
                ["CONTROL"] = "coda-secret:mcp:my\tserver/env/CONTROL",
            });

        Assert.Equal(
            [new McpSecretBinding("env/VALID", validKey)],
            McpSecretStore.References(config));

        await McpSecretStore.DeleteSecretsAsync(store, config);

        Assert.Null(await store.GetAsync(validKey));
        Assert.Equal("trailing", await store.GetAsync(trailingKey));
        Assert.Equal("format", await store.GetAsync(formatKey));
        Assert.Equal("control", await store.GetAsync(controlKey));
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

        public IReadOnlyCollection<string> Keys => this.map.Keys.ToArray();

        public Exception? SetException { get; init; }

        public Exception? SetAfterWriteException { get; set; }

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
            if (this.SetAfterWriteException is { } afterWriteException)
            {
                this.SetAfterWriteException = null;
                throw afterWriteException;
            }

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
