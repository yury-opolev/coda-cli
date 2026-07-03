using System.Text.RegularExpressions;
using LlmAuth;

namespace Coda.Mcp;

/// <summary>
/// Dereferences secret-looking values in a loaded MCP config so no plaintext secret need live in
/// <c>.mcp.json</c>. Resolution is a separate step from <see cref="McpConfig.Load"/> (which stays a
/// pure parser); the three entry points (TUI, <c>coda run</c>, <c>serve</c>) call it after loading.
/// <para>Applied to <c>env</c> values, HTTP headers, and the bearer token:</para>
/// <list type="bullet">
///   <item><c>coda-secret:&lt;key&gt;</c> (whole value) → decrypt from the credential <paramref name="store"/>.</item>
///   <item><c>${ENV_VAR}</c> anywhere in the value → substitute the environment variable (embedded ok, e.g. <c>Bearer ${TOKEN}</c>).</item>
///   <item>anything else → literal (unchanged).</item>
/// </list>
/// A missing secret / env var resolves to an empty string (never leaks the reference) and, when a
/// <paramref name="log"/> callback is supplied, emits a diagnostic so the misconfiguration is visible.
/// </summary>
public static partial class McpSecretResolver
{
    /// <summary>Prefix marking a value stored (encrypted) in the credential store.</summary>
    public const string SecretRefPrefix = "coda-secret:";

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex EnvVarPattern();

    public static async Task<IReadOnlyDictionary<string, McpServerConfig>> ResolveAsync(
        IReadOnlyDictionary<string, McpServerConfig> servers, ITokenStore store,
        CancellationToken cancellationToken = default, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(store);

        var result = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var (name, config) in servers)
        {
            result[name] = await ResolveConfigAsync(config, store, log, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Resolve secret references in a single server config (used by the live <c>/mcp start</c> path).</summary>
    public static Task<McpServerConfig> ResolveAsync(
        McpServerConfig config, ITokenStore store, CancellationToken cancellationToken = default, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        return ResolveConfigAsync(config, store, log, cancellationToken);
    }

    private static async Task<McpServerConfig> ResolveConfigAsync(McpServerConfig config, ITokenStore store, Action<string>? log, CancellationToken ct)
    {
        switch (config)
        {
            case McpStdioServerConfig stdio:
                return stdio with { Env = await ResolveMapAsync(stdio.Env, store, log, ct).ConfigureAwait(false) };

            case McpHttpServerConfig http:
                var headers = await ResolveMapAsync(http.Headers, store, log, ct).ConfigureAwait(false);
                var token = http.Auth.BearerToken is { } bearer
                    ? await ResolveValueAsync(bearer, store, log, ct).ConfigureAwait(false)
                    : null;
                return http with { Headers = headers, Auth = http.Auth with { BearerToken = token } };

            default:
                return config;
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> ResolveMapAsync(
        IReadOnlyDictionary<string, string> map, ITokenStore store, Action<string>? log, CancellationToken ct)
    {
        if (map.Count == 0)
        {
            return map;
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            resolved[key] = await ResolveValueAsync(value, store, log, ct).ConfigureAwait(false);
        }

        return resolved;
    }

    private static async Task<string> ResolveValueAsync(string value, ITokenStore store, Action<string>? log, CancellationToken ct)
    {
        if (value.StartsWith(SecretRefPrefix, StringComparison.Ordinal))
        {
            var key = value[SecretRefPrefix.Length..];
            var secret = await store.GetAsync(key, ct).ConfigureAwait(false);
            if (secret is null)
            {
                log?.Invoke($"MCP secret '{key}' was not found in the credential store; using an empty value.");
            }

            return secret ?? string.Empty;
        }

        if (value.Contains("${", StringComparison.Ordinal))
        {
            return EnvVarPattern().Replace(value, match =>
            {
                var name = match.Groups[1].Value;
                var resolved = Environment.GetEnvironmentVariable(name);
                if (resolved is null)
                {
                    log?.Invoke($"Environment variable '{name}' referenced in MCP config is not set; using an empty value.");
                }

                return resolved ?? string.Empty;
            });
        }

        return value;
    }
}
