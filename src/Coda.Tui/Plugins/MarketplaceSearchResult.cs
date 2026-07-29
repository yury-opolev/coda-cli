namespace Coda.Tui.Plugins;

/// <summary>A single plugin entry returned by <c>/marketplace search</c>.</summary>
public sealed record MarketplaceSearchResult(
    /// <summary>The name of the marketplace the entry came from.</summary>
    string MarketplaceName,

    /// <summary>The plugin entry from the marketplace manifest.</summary>
    MarketplacePluginEntry Entry,

    /// <summary>
    /// Whether a plugin with this name is currently installed in the user plugins directory.
    /// </summary>
    bool IsInstalled,

    /// <summary>
    /// Rank for sorting: lower is better.
    /// <c>0</c> = name-prefix match; <c>1</c> = name-contains match;
    /// <c>2</c> = description / category / tag match.
    /// </summary>
    int Rank);
