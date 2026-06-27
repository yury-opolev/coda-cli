namespace Coda.Tui.Plugins;

/// <summary>An entry in the known marketplaces registry.</summary>
public sealed record KnownMarketplaceEntry(MarketplaceSource Source, string InstallLocation, string LastUpdated);
