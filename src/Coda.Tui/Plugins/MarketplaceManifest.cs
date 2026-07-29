namespace Coda.Tui.Plugins;

/// <summary>The parsed contents of a <c>marketplace.json</c> manifest.</summary>
public sealed record MarketplaceManifest(
    string Name,
    string? OwnerName,
    string? PluginRoot,
    IReadOnlyList<MarketplacePluginEntry> Plugins,
    /// <summary>
    /// Plugin rename map: old plugin name → new plugin name, or <see langword="null"/>
    /// to retire a plugin. Populated from the manifest's <c>renames</c> field.
    /// </summary>
    IReadOnlyDictionary<string, string?>? Renames = null)
{
    /// <summary>Returns the renames map, or an empty map when none was declared.</summary>
    public IReadOnlyDictionary<string, string?> GetRenames()
        => this.Renames ?? (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>();
}
