using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coda.Tui.Plugins;

/// <summary>Persists known marketplace entries to <c>known_marketplaces.json</c>.</summary>
public sealed class KnownMarketplacesStore
{
    private static readonly JsonSerializerOptions serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string filePath;
    private readonly string userPluginsDir;

    public KnownMarketplacesStore(string userPluginsDir)
    {
        this.userPluginsDir = userPluginsDir;
        this.filePath = Path.Combine(userPluginsDir, "known_marketplaces.json");
    }

    /// <summary>Returns all known marketplace entries. Missing or corrupt file yields empty.</summary>
    public IReadOnlyDictionary<string, KnownMarketplaceEntry> List()
    {
        return this.LoadEntries();
    }

    /// <summary>Tries to get a marketplace entry by name.</summary>
    public bool TryGet(string name, out KnownMarketplaceEntry? entry)
    {
        var entries = this.LoadEntries();
        return entries.TryGetValue(name, out entry);
    }

    /// <summary>Adds or replaces a marketplace entry. Validates name and persists.</summary>
    public void Add(string name, KnownMarketplaceEntry entry)
    {
        if (!IsValidMarketplaceName(name))
        {
            throw new ArgumentException($"Invalid marketplace name: '{name}'", nameof(name));
        }

        var entries = this.LoadEntries();
        var mutable = new Dictionary<string, KnownMarketplaceEntry>(entries)
        {
            [name] = entry,
        };
        this.Save(mutable);
    }

    /// <summary>Removes a marketplace entry by name. Validates name; no-op if absent.</summary>
    public void Remove(string name)
    {
        if (!IsValidMarketplaceName(name))
        {
            throw new ArgumentException($"Invalid marketplace name: '{name}'", nameof(name));
        }

        var entries = this.LoadEntries();
        if (!entries.ContainsKey(name))
        {
            return;
        }

        var mutable = new Dictionary<string, KnownMarketplaceEntry>(entries);
        mutable.Remove(name);
        this.Save(mutable);
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> is a safe single-segment marketplace name
    /// (no path separators, no <c>..</c>, no invalid filename characters).
    /// </summary>
    public static bool IsValidMarketplaceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name == ".." || name == ".")
        {
            return false;
        }

        if (name.Contains('/') || name.Contains('\\'))
        {
            return false;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in name)
        {
            if (Array.IndexOf(invalidChars, c) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private IReadOnlyDictionary<string, KnownMarketplaceEntry> LoadEntries()
    {
        if (!File.Exists(this.filePath))
        {
            return new Dictionary<string, KnownMarketplaceEntry>();
        }

        try
        {
            var json = File.ReadAllText(this.filePath);
            var dto = JsonSerializer.Deserialize<Dictionary<string, EntryDto>>(json, serializerOptions);
            if (dto is null)
            {
                return new Dictionary<string, KnownMarketplaceEntry>();
            }

            var result = new Dictionary<string, KnownMarketplaceEntry>();
            foreach (var (key, value) in dto)
            {
                var source = MapSourceFromDto(value.Source);
                if (source is null)
                {
                    continue;
                }

                result[key] = new KnownMarketplaceEntry(source, value.InstallLocation ?? string.Empty, value.LastUpdated ?? string.Empty);
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, KnownMarketplaceEntry>();
        }
    }

    private void Save(Dictionary<string, KnownMarketplaceEntry> entries)
    {
        Directory.CreateDirectory(this.userPluginsDir);

        var dto = new Dictionary<string, EntryDto>();
        foreach (var (key, value) in entries)
        {
            dto[key] = new EntryDto
            {
                Source = MapSourceToDto(value.Source),
                InstallLocation = value.InstallLocation,
                LastUpdated = value.LastUpdated,
            };
        }

        var json = JsonSerializer.Serialize(dto, serializerOptions);
        File.WriteAllText(this.filePath, json);
    }

    private static SourceDto MapSourceToDto(MarketplaceSource source)
    {
        return source switch
        {
            GithubSource g => new SourceDto { Kind = "github", Repo = g.Repo, Ref = g.Ref, ManifestPath = g.Path },
            GitSource g => new SourceDto { Kind = "git", Url = g.Url, Ref = g.Ref, ManifestPath = g.Path },
            LocalFileSource f => new SourceDto { Kind = "file", Path = f.Path },
            LocalDirectorySource d => new SourceDto { Kind = "directory", Path = d.Path },
            _ => new SourceDto { Kind = "unknown" },
        };
    }

    private static MarketplaceSource? MapSourceFromDto(SourceDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        return dto.Kind switch
        {
            "github" when dto.Repo is not null => new GithubSource(dto.Repo, dto.Ref, dto.ManifestPath),
            "git" when dto.Url is not null => new GitSource(dto.Url, dto.Ref, dto.ManifestPath),
            "file" when dto.Path is not null => new LocalFileSource(dto.Path),
            "directory" when dto.Path is not null => new LocalDirectorySource(dto.Path),
            _ => null,
        };
    }

    private sealed class EntryDto
    {
        [JsonPropertyName("source")]
        public SourceDto? Source { get; set; }

        [JsonPropertyName("installLocation")]
        public string? InstallLocation { get; set; }

        [JsonPropertyName("lastUpdated")]
        public string? LastUpdated { get; set; }
    }

    private sealed class SourceDto
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("repo")]
        public string? Repo { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("ref")]
        public string? Ref { get; set; }

        [JsonPropertyName("manifestPath")]
        public string? ManifestPath { get; set; }
    }
}
