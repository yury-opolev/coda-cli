namespace Coda.Tui.Plugins;

/// <summary>Describes how and when a plugin was installed.</summary>
public sealed record PluginInstallInfo(
    /// <summary>Version string read from <c>plugin.json</c> at install time.</summary>
    string Version,

    /// <summary>
    /// Install source: <c>git</c> for git-cloned plugins, <c>local</c> for directory installs.
    /// </summary>
    string Source,

    /// <summary>The git URL the plugin was cloned from; <see langword="null"/> for local installs.</summary>
    string? GitUrl,

    /// <summary>
    /// The resolved commit SHA at install time; <see langword="null"/> when not recorded.
    /// </summary>
    string? Commit,

    /// <summary>UTC timestamp of installation.</summary>
    DateTimeOffset InstalledAt);
