using LlmAuth;

namespace Coda.Mcp;

/// <summary>
/// Stores an MCP secret (encrypted) in the credential store and returns the
/// <c>coda-secret:</c> reference to write into <c>.mcp.json</c> in its place. The key convention is
/// <c>mcp:&lt;server&gt;/&lt;field&gt;</c> (distinct from the <c>llmauth:&lt;provider&gt;</c> namespace);
/// values round-trip through <see cref="McpSecretResolver"/>.
/// </summary>
public static class McpSecretStore
{
    /// <summary>The store key for a server's secret field, e.g. <c>mcp:github/env/TOKEN</c>.</summary>
    public static string KeyFor(string server, string field) => $"mcp:{server}/{field}";

    /// <summary>Encrypt <paramref name="value"/> under <c>mcp:&lt;server&gt;/&lt;field&gt;</c>; returns the reference to store in config.</summary>
    public static async Task<string> StoreAsync(ITokenStore store, string server, string field, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var key = KeyFor(server, field);
        await store.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        return McpSecretResolver.SecretRefPrefix + key;
    }
}
