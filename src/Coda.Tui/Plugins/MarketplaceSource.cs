namespace Coda.Tui.Plugins;

/// <summary>Discriminated union for all supported marketplace source kinds.</summary>
public abstract record MarketplaceSource;

/// <summary>A plugin marketplace hosted on GitHub, referenced by owner/repo shorthand.</summary>
public sealed record GithubSource(string Repo, string? Ref = null, string? Path = null) : MarketplaceSource;

/// <summary>A plugin marketplace hosted in any git repository (SSH or HTTPS URL).</summary>
public sealed record GitSource(string Url, string? Ref = null, string? Path = null) : MarketplaceSource;

/// <summary>A plugin marketplace defined by a single local marketplace.json file.</summary>
public sealed record LocalFileSource(string Path) : MarketplaceSource;

/// <summary>A plugin marketplace defined by a local directory containing a marketplace.json.</summary>
public sealed record LocalDirectorySource(string Path) : MarketplaceSource;
