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
    /// <summary>The store key for a server's secret field, e.g. <c>mcp:github/env/TOKEN</c>.
    /// Assumes server names contain no <c>/</c> (they are JSON object keys in practice).</summary>
    public static string KeyFor(string server, string field) => $"mcp:{server}/{field}";

    /// <summary>Encrypt <paramref name="value"/> under <c>mcp:&lt;server&gt;/&lt;field&gt;</c>; returns the reference to store in config.</summary>
    public static async Task<string> StoreAsync(ITokenStore store, string server, string field, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var key = KeyFor(server, field);
        await store.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        return McpSecretResolver.SecretRefPrefix + key;
    }

    /// <summary>
    /// Delete every credential-store secret referenced by <paramref name="config"/> (its
    /// <c>coda-secret:&lt;key&gt;</c> env / header / token values) — called when a server is removed so
    /// its encrypted secrets are not orphaned. The store's lossy key sanitization prevents
    /// enumeration, so we derive the exact keys from the config's own references. Keys are assumed
    /// server-private (the <c>mcp:&lt;server&gt;/…</c> convention); a hand-shared ref could delete a
    /// key another server still uses.
    /// </summary>
    public static async Task DeleteSecretsAsync(ITokenStore store, McpServerConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        foreach (var key in SecretKeys(config))
        {
            await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> SecretKeys(McpServerConfig config)
    {
        IEnumerable<string> values = config switch
        {
            McpStdioServerConfig stdio => stdio.Env.Values,
            McpHttpServerConfig http => http.Auth.BearerToken is { } token
                ? http.Headers.Values.Append(token)
                : http.Headers.Values,
            _ => [],
        };

        foreach (var value in values)
        {
            if (value.StartsWith(McpSecretResolver.SecretRefPrefix, StringComparison.Ordinal))
            {
                yield return value[McpSecretResolver.SecretRefPrefix.Length..];
            }
        }
    }
}
