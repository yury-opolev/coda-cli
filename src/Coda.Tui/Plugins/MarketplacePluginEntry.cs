namespace Coda.Tui.Plugins;

public sealed record MarketplacePluginEntry(
    string Name,
    string Source,
    string? Description,
    string? Version,
    string? Category,
    IReadOnlyList<string> Tags);
