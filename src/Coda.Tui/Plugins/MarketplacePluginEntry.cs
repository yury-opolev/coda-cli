namespace Coda.Tui.Plugins;

/// <summary>A single plugin entry in a marketplace manifest.</summary>
public sealed record MarketplacePluginEntry(
    string Name,
    string Source,
    string? Description,
    string? Version,
    string? Category,
    IReadOnlyList<string> Tags,
    /// <summary>
    /// Optional new source URL declared by the plugin author. When set and the target
    /// passes reserved-name validation, the refresh diff surfaces the plugin as blocked
    /// from migrating. Automatically following this redirect is not yet implemented.
    /// </summary>
    string? MigratedTo = null,
    /// <summary>
    /// Full 40-character commit SHA from the source object, when the marketplace manifest
    /// specifies one. Recorded as <see cref="PluginInstallInfo.Commit"/> at install time.
    /// </summary>
    string? SourceSha = null);
