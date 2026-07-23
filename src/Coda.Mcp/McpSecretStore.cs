using System.Globalization;
using System.Text;
using LlmAuth;

namespace Coda.Mcp;

/// <summary>A managed secret reference bound to a typed field in an MCP server config.</summary>
public sealed record McpSecretBinding(string Field, string StoreKey);

/// <summary>A newly written, versioned secret reference that can be committed with a config edit.</summary>
public sealed record McpStagedSecret(string Field, string StoreKey, string Reference);

/// <summary>
/// Indicates that a staged secret write failed and its compensation could not be confirmed.
/// The original failures are retained for programmatic diagnostics without becoming part of
/// exception rendering, which could otherwise expose a credential-store error value.
/// </summary>
internal sealed class McpSecretStagingCleanupException : Exception
{
    internal McpSecretStagingCleanupException(Exception stagingFailure, Exception cleanupFailure)
        : base("MCP secret staging failed and cleanup incomplete.")
    {
        this.StagingFailure = stagingFailure;
        this.CleanupFailure = cleanupFailure;
    }

    internal Exception StagingFailure { get; }

    internal Exception CleanupFailure { get; }
}

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
    public static string KeyFor(string server, string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        return $"mcp:{server}/{field}";
    }

    /// <summary>
    /// Returns whether <paramref name="storeKey"/> belongs to precisely this server field. A managed
    /// key is either the canonical key or a versioned child written by <see cref="StageAsync"/>.
    /// Similar prefixes and credentials in other namespaces are not owned by this manager.
    /// </summary>
    public static bool IsOwnedKey(string server, string field, string storeKey)
    {
        if (storeKey is null)
        {
            return false;
        }

        var canonical = KeyFor(server, field);
        if (string.Equals(storeKey, canonical, StringComparison.Ordinal))
        {
            return true;
        }

        if (!storeKey.StartsWith(canonical + "/", StringComparison.Ordinal))
        {
            return false;
        }

        var version = storeKey[(canonical.Length + 1)..];
        return version.Length == 32 && version.All(Uri.IsHexDigit);
    }

    /// <summary>
    /// Gets the credential-store key from an exact managed secret reference. Ordinary Unicode space
    /// separators are retained inside a key, while surrounding whitespace and unsafe control or
    /// format characters are rejected.
    /// </summary>
    public static bool TryGetStoreKey(string? value, out string storeKey)
    {
        storeKey = string.Empty;
        if (string.IsNullOrEmpty(value)
            || !value.StartsWith(McpSecretResolver.SecretRefPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = value[McpSecretResolver.SecretRefPrefix.Length..];
        if (candidate.Length == 0
            || char.IsWhiteSpace(candidate[0])
            || char.IsWhiteSpace(candidate[^1])
            || !IsWellFormedUtf16(candidate))
        {
            return false;
        }

        foreach (var rune in candidate.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                || (Rune.IsWhiteSpace(rune) && category != UnicodeCategory.SpaceSeparator))
            {
                return false;
            }
        }

        storeKey = candidate;
        return true;
    }

    /// <summary>Encrypt <paramref name="value"/> under <c>mcp:&lt;server&gt;/&lt;field&gt;</c>; returns the reference to store in config.</summary>
    public static async Task<string> StoreAsync(ITokenStore store, string server, string field, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(value);
        var key = KeyFor(server, field);
        await store.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        return McpSecretResolver.SecretRefPrefix + key;
    }

    /// <summary>
    /// Encrypt a replacement value under a new versioned key without changing the current canonical
    /// key. The returned reference is safe to place in a subsequently committed config edit. The
    /// optional callback registers the generated key before the write so a caller can compensate
    /// a failed multi-secret operation even if the store writes and then throws.
    /// </summary>
    public static Task<McpStagedSecret> StageAsync(
        ITokenStore store,
        string server,
        string field,
        string value,
        CancellationToken ct = default) =>
        StageAsync(store, server, field, value, ct, onStaging: null);

    /// <inheritdoc cref="StageAsync(ITokenStore, string, string, string, CancellationToken)"/>
    public static async Task<McpStagedSecret> StageAsync(
        ITokenStore store,
        string server,
        string field,
        string value,
        CancellationToken ct,
        Action<McpStagedSecret>? onStaging)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(value);

        var storeKey = $"{KeyFor(server, field)}/{Guid.NewGuid():N}";
        var staged = new McpStagedSecret(field, storeKey, McpSecretResolver.SecretRefPrefix + storeKey);
        onStaging?.Invoke(staged);
        try
        {
            await store.SetAsync(storeKey, value, ct).ConfigureAwait(false);
            return staged;
        }
        catch (Exception stagingFailure)
        {
            try
            {
                await store.DeleteAsync(storeKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new McpSecretStagingCleanupException(stagingFailure, cleanupFailure);
            }

            throw;
        }
    }

    /// <summary>
    /// Enumerate exact, managed secret references in a config. Results are ordered by typed field
    /// using ordinal comparison, with duplicate fields and store keys removed.
    /// </summary>
    public static IReadOnlyList<McpSecretBinding> References(McpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var bindings = new List<McpSecretBinding>();
        switch (config)
        {
            case McpStdioServerConfig stdio:
                AddBindings(bindings, stdio.Env, "env");
                break;

            case McpHttpServerConfig http:
                AddBindings(bindings, http.Headers, "header");
                AddBinding(bindings, "auth/token", http.Auth.BearerToken);
                break;
        }

        return bindings
            .OrderBy(static binding => binding.Field, StringComparer.Ordinal)
            .ThenBy(static binding => binding.StoreKey, StringComparer.Ordinal)
            .DistinctBy(static binding => binding.Field, StringComparer.Ordinal)
            .DistinctBy(static binding => binding.StoreKey, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Delete only the explicitly supplied credential-store keys. Blank keys are ignored, duplicate
    /// keys are removed with ordinal comparison, and store failures are intentionally propagated.
    /// </summary>
    public static async Task DeleteKeysAsync(
        ITokenStore store, IEnumerable<string> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keys);
        ct.ThrowIfCancellationRequested();

        foreach (var key in keys.Where(static key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            await store.DeleteAsync(key, ct).ConfigureAwait(false);
        }
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
        foreach (var binding in References(config))
        {
            await store.DeleteAsync(binding.StoreKey, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddBindings(
        ICollection<McpSecretBinding> bindings, IReadOnlyDictionary<string, string> values, string fieldPrefix)
    {
        foreach (var (name, value) in values)
        {
            AddBinding(bindings, $"{fieldPrefix}/{name}", value);
        }
    }

    private static void AddBinding(ICollection<McpSecretBinding> bindings, string field, string? value)
    {
        if (TryGetStoreKey(value, out var storeKey))
        {
            bindings.Add(new McpSecretBinding(field, storeKey));
        }
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }
}
