namespace Coda.Tui.Plugins;

/// <summary>The per-plugin changes found during a marketplace refresh.</summary>
public sealed record MarketplaceRefreshDiff(
    /// <summary>Plugin names added in the updated manifest.</summary>
    IReadOnlyList<string> Added,

    /// <summary>Plugin names removed from the updated manifest.</summary>
    IReadOnlyList<string> Removed,

    /// <summary>Plugins whose declared version changed.</summary>
    IReadOnlyList<(string Name, string OldVersion, string NewVersion)> VersionChanged,

    /// <summary>
    /// Plugins that were renamed: the old name and the new name from the manifest's
    /// <c>renames</c> map. These are not also present in <see cref="Added"/> or
    /// <see cref="Removed"/>.
    /// </summary>
    IReadOnlyList<(string OldName, string NewName)>? Renamed = null);

/// <summary>The outcome of refreshing a single marketplace.</summary>
public sealed record MarketplaceRefreshResult(
    /// <summary>The marketplace name.</summary>
    string Name,

    /// <summary>Whether the refresh completed successfully.</summary>
    bool Ok,

    /// <summary>The diff between the old and new manifest; <see langword="null"/> on failure.</summary>
    MarketplaceRefreshDiff? Diff,

    /// <summary>Error message; <see langword="null"/> on success.</summary>
    string? Error,

    /// <summary>
    /// Plugin names whose <c>migratedTo</c> target was blocked (reserved or lookalike
    /// marketplace name). <see langword="null"/> when no migrations were blocked.
    /// </summary>
    IReadOnlyList<string>? BlockedMigrations = null);
