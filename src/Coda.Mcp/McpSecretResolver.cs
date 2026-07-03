using LlmAuth;

namespace Coda.Mcp;

/// <summary>
/// Dereferences secret-looking values in a loaded MCP config so no plaintext secret need live in
/// <c>.mcp.json</c>. Resolution is a separate step from <see cref="McpConfig.Load"/> (which stays a
/// pure parser); the three entry points (TUI, <c>coda run</c>, <c>serve</c>) call it after loading.
/// <para>Three value forms are resolved (in <c>env</c> values, HTTP headers, and the bearer token):</para>
/// <list type="bullet">
///   <item><c>coda-secret:&lt;key&gt;</c> → decrypt from the credential <paramref name="store"/>.</item>
///   <item><c>${ENV_VAR}</c> → the environment variable's value.</item>
///   <item>anything else → literal (unchanged) — today's behavior, fully back-compatible.</item>
/// </list>
/// A missing secret / env var resolves to an empty string rather than leaking the reference.
/// </summary>
public static class McpSecretResolver
{
    /// <summary>Prefix marking a value stored (encrypted) in the credential store.</summary>
    public const string SecretRefPrefix = "coda-secret:";

    public static async Task<IReadOnlyDictionary<string, McpServerConfig>> ResolveAsync(
        IReadOnlyDictionary<string, McpServerConfig> servers, ITokenStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(store);

        var result = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var (name, config) in servers)
        {
            result[name] = await ResolveConfigAsync(config, store, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Resolve secret references in a single server config (used by the live <c>/mcp start</c> path).</summary>
    public static Task<McpServerConfig> ResolveAsync(McpServerConfig config, ITokenStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        return ResolveConfigAsync(config, store, cancellationToken);
    }

    private static async Task<McpServerConfig> ResolveConfigAsync(McpServerConfig config, ITokenStore store, CancellationToken ct)
    {
        switch (config)
        {
            case McpStdioServerConfig stdio:
                return stdio with { Env = await ResolveMapAsync(stdio.Env, store, ct).ConfigureAwait(false) };

            case McpHttpServerConfig http:
                var headers = await ResolveMapAsync(http.Headers, store, ct).ConfigureAwait(false);
                var token = http.Auth.BearerToken is { } bearer
                    ? await ResolveValueAsync(bearer, store, ct).ConfigureAwait(false)
                    : null;
                return http with { Headers = headers, Auth = http.Auth with { BearerToken = token } };

            default:
                return config;
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> ResolveMapAsync(
        IReadOnlyDictionary<string, string> map, ITokenStore store, CancellationToken ct)
    {
        if (map.Count == 0)
        {
            return map;
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            resolved[key] = await ResolveValueAsync(value, store, ct).ConfigureAwait(false);
        }

        return resolved;
    }

    private static async Task<string> ResolveValueAsync(string value, ITokenStore store, CancellationToken ct)
    {
        if (value.StartsWith(SecretRefPrefix, StringComparison.Ordinal))
        {
            var key = value[SecretRefPrefix.Length..];
            return await store.GetAsync(key, ct).ConfigureAwait(false) ?? string.Empty;
        }

        if (value.Length > 3 && value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'))
        {
            return Environment.GetEnvironmentVariable(value[2..^1]) ?? string.Empty;
        }

        return value;
    }
}
