namespace Coda.Tui.Plugins;

/// <summary>Discriminated union for all supported marketplace source kinds.</summary>
public abstract record MarketplaceSource;

/// <summary>A plugin marketplace hosted on GitHub, referenced by owner/repo shorthand.</summary>
/// <param name="Sha">
/// Optional full 40-character commit SHA. When present the install is pinned to that exact
/// commit rather than the branch head, making it immune to force-pushes or tag moves.
/// When absent the branch head is resolved at install time and recorded.
/// </param>
public sealed record GithubSource(
    string Repo,
    string? Ref = null,
    string? Path = null,
    string? Sha = null) : MarketplaceSource;

/// <summary>A plugin marketplace hosted in any git repository (SSH or HTTPS URL).</summary>
/// <param name="Sha">
/// Optional full 40-character commit SHA. See <see cref="GithubSource.Sha"/> for details.
/// </param>
public sealed record GitSource(
    string Url,
    string? Ref = null,
    string? Path = null,
    string? Sha = null) : MarketplaceSource;

/// <summary>A plugin marketplace defined by a single local marketplace.json file.</summary>
public sealed record LocalFileSource(string Path) : MarketplaceSource;

/// <summary>A plugin marketplace defined by a local directory containing a marketplace.json.</summary>
public sealed record LocalDirectorySource(string Path) : MarketplaceSource;
