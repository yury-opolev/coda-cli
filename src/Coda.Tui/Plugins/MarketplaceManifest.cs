namespace Coda.Tui.Plugins;

public sealed record MarketplaceManifest(
    string Name,
    string? OwnerName,
    string? PluginRoot,
    IReadOnlyList<MarketplacePluginEntry> Plugins);
