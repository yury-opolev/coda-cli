using System.Reflection;
using System.Text.Json.Nodes;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Coda.Sdk;

/// <summary>
/// Model metadata catalog backed by a models.dev snapshot (display names, context
/// limits, pricing). Source order: an explicit file (<c>CODA_MODELS_PATH</c>), the
/// on-disk cache (<c>~/.coda/cache/models.json</c>), then the bundled snapshot
/// resource. A live refresh from models.dev is opt-in and on-demand
/// (<see cref="RefreshAsync"/>), so the default path needs no network.
///
/// <para>Lookups are tolerant of version-suffix differences (e.g. the alias
/// <c>claude-haiku-4-5</c> resolves to <c>claude-haiku-4-5-20251001</c>).</para>
/// </summary>
public sealed class ModelCatalog
{
    // models.dev provider keys this catalog cares about.
    private static readonly string[] ProviderKeys = ["anthropic", "github-copilot"];

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, CatalogModel>> byProvider;

    public ModelCatalog(IReadOnlyDictionary<string, IReadOnlyDictionary<string, CatalogModel>> byProvider)
    {
        this.byProvider = byProvider;
    }

    // Lazy so the (file/resource) load runs once, off any lock, and is published
    // safely to concurrent readers. ResetDefault swaps in a fresh lazy.
    private static volatile Lazy<ModelCatalog> defaultLazy = NewLazy();

    private static Lazy<ModelCatalog> NewLazy() => new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The process-wide catalog, loaded once from cache/snapshot. Reset by <see cref="ResetDefault"/>.</summary>
    public static ModelCatalog Default => defaultLazy.Value;

    /// <summary>Drop the cached <see cref="Default"/> so the next access reloads (e.g. after a refresh).</summary>
    public static void ResetDefault() => defaultLazy = NewLazy();

    /// <summary>Map a Coda provider id to its models.dev provider key, or null if unmapped.</summary>
    public static string? ProviderKeyFor(string providerId) => providerId switch
    {
        ClaudeAiProvider.Id or ApiKeyProvider.Id => "anthropic",
        GitHubCopilotProvider.Id => "github-copilot",
        _ => null,
    };

    /// <summary>Default on-disk cache path for a refreshed catalog.</summary>
    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coda", "cache", "models.json");

    /// <summary>Look up a model's metadata for a provider, tolerant of version-suffix differences.</summary>
    public CatalogModel? Get(string providerId, string modelId)
    {
        var key = ProviderKeyFor(providerId);
        if (key is null || string.IsNullOrEmpty(modelId) || !this.byProvider.TryGetValue(key, out var models))
        {
            return null;
        }

        if (models.TryGetValue(modelId, out var exact))
        {
            return exact;
        }

        // Tolerant match for alias↔versioned ids:
        //  - query is an alias (catalog id starts with it, e.g. "claude-haiku-4-5"
        //    → "claude-haiku-4-5-20251001"): pick the SHORTEST such id (closest match).
        //  - query is versioned and the catalog has a shorter alias (query starts
        //    with the catalog id): pick the LONGEST such id (most specific alias).
        CatalogModel? aliasMatch = null;
        CatalogModel? versionMatch = null;
        foreach (var (id, model) in models)
        {
            if (id.StartsWith(modelId, StringComparison.OrdinalIgnoreCase)
                && (aliasMatch is null || id.Length < aliasMatch.Id.Length))
            {
                aliasMatch = model;
            }
            else if (modelId.StartsWith(id, StringComparison.OrdinalIgnoreCase)
                && (versionMatch is null || id.Length > versionMatch.Id.Length))
            {
                versionMatch = model;
            }
        }

        return aliasMatch ?? versionMatch;
    }

    /// <summary>All catalog models for a provider (empty when unmapped/unknown).</summary>
    public IReadOnlyList<CatalogModel> ForProvider(string providerId)
    {
        var key = ProviderKeyFor(providerId);
        if (key is null || !this.byProvider.TryGetValue(key, out var models))
        {
            return [];
        }

        return [.. models.Values];
    }

    /// <summary>
    /// Load the catalog from the first available source: <c>CODA_MODELS_PATH</c>,
    /// the on-disk cache, then the bundled snapshot. Never throws — returns an empty
    /// catalog if every source is missing/unparseable.
    /// </summary>
    public static ModelCatalog Load()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODA_MODELS_PATH");
        foreach (var source in new[] { explicitPath, CachePath })
        {
            if (!string.IsNullOrEmpty(source) && File.Exists(source))
            {
                var parsed = TryParseFile(source);
                if (parsed is not null)
                {
                    return new ModelCatalog(parsed);
                }
            }
        }

        var snapshot = ReadEmbeddedSnapshot();
        if (snapshot is not null)
        {
            var parsed = TryParse(snapshot);
            if (parsed is not null)
            {
                return new ModelCatalog(parsed);
            }
        }

        return new ModelCatalog(new Dictionary<string, IReadOnlyDictionary<string, CatalogModel>>());
    }

    /// <summary>
    /// Fetch a fresh catalog from models.dev and write it to the cache, unless
    /// disabled by <c>CODA_DISABLE_MODELS_FETCH</c>. Returns true on success.
    /// Best-effort: returns false on any failure without throwing.
    /// </summary>
    public static Task<bool> RefreshAsync(HttpClient httpClient, CancellationToken cancellationToken = default) =>
        RefreshAsync(httpClient, CachePath, cancellationToken);

    /// <summary>Default staleness window for the startup background refresh.</summary>
    public static readonly TimeSpan DefaultMaxCacheAge = TimeSpan.FromHours(12);

    /// <summary>
    /// Refresh the catalog only if the cache is missing or older than
    /// <paramref name="maxAge"/>. Intended as a fire-and-forget background refresh at
    /// startup (opencode-style) so the cache stays current without a request on every
    /// launch. Best-effort: returns false (and never throws) when up-to-date,
    /// disabled, or on failure.
    /// </summary>
    public static Task<bool> RefreshIfStaleAsync(HttpClient httpClient, TimeSpan? maxAge = null, CancellationToken cancellationToken = default)
    {
        if (IsTruthy(Environment.GetEnvironmentVariable("CODA_DISABLE_MODELS_FETCH")))
        {
            return Task.FromResult(false);
        }

        if (!IsStale(CachePath, maxAge ?? DefaultMaxCacheAge))
        {
            return Task.FromResult(false); // still fresh
        }

        return RefreshAsync(httpClient, cancellationToken);
    }

    /// <summary>True when the cache file is missing or older than <paramref name="maxAge"/>.</summary>
    public static bool IsStale(string cachePath, TimeSpan maxAge)
    {
        try
        {
            return !File.Exists(cachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) >= maxAge;
        }
        catch (IOException)
        {
            return true; // unreadable timestamp → treat as stale
        }
    }

    /// <summary>
    /// <see cref="RefreshAsync(HttpClient, CancellationToken)"/> writing to an explicit
    /// destination (exposed for tests so they don't touch the real cache directory).
    /// </summary>
    public static async Task<bool> RefreshAsync(HttpClient httpClient, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (IsTruthy(Environment.GetEnvironmentVariable("CODA_DISABLE_MODELS_FETCH")))
        {
            return false;
        }

        var baseUrl = (Environment.GetEnvironmentVariable("CODA_MODELS_URL") ?? "https://models.dev").TrimEnd('/');
        try
        {
            var json = await httpClient.GetStringAsync($"{baseUrl}/api.json", cancellationToken).ConfigureAwait(false);
            // Validate before persisting so we never cache garbage.
            if (TryParse(json) is null)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllTextAsync(destinationPath, json, cancellationToken).ConfigureAwait(false);
            ResetDefault();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool IsTruthy(string? value) => EnvFlags.IsTruthy(value);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, CatalogModel>>? TryParseFile(string path)
    {
        try
        {
            return TryParse(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a models.dev-shaped JSON document (full <c>api.json</c> or the trimmed
    /// snapshot), extracting only the providers we map. Returns null on malformed input.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, CatalogModel>>? TryParse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (root is not JsonObject obj)
        {
            return null;
        }

        var result = new Dictionary<string, IReadOnlyDictionary<string, CatalogModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var providerKey in ProviderKeys)
        {
            if (obj[providerKey]?["models"] is not JsonObject models)
            {
                continue;
            }

            var parsedModels = new Dictionary<string, CatalogModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, node) in models)
            {
                if (node is null)
                {
                    continue;
                }

                parsedModels[id] = new CatalogModel(
                    id,
                    (string?)node["name"],
                    (int?)node["limit"]?["context"],
                    (int?)node["limit"]?["output"],
                    ReadDecimal(node["cost"]?["input"]),
                    ReadDecimal(node["cost"]?["output"]),
                    ReadDecimal(node["cost"]?["cache_read"]),
                    ReadDecimal(node["cost"]?["cache_write"]));
            }

            result[providerKey] = parsedModels;
        }

        return result;
    }

    private static decimal? ReadDecimal(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<decimal>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string? ReadEmbeddedSnapshot()
    {
        var assembly = typeof(ModelCatalog).Assembly;
        const string exact = "Coda.Sdk.Resources.models-snapshot.json";
        var names = assembly.GetManifestResourceNames();
        var name = Array.IndexOf(names, exact) >= 0
            ? exact
            : Array.Find(names, n => n.EndsWith("models-snapshot.json", StringComparison.Ordinal));
        if (name is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
