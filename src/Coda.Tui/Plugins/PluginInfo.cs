namespace Coda.Tui.Plugins;

/// <summary>Metadata for a discovered Coda plugin.</summary>
public sealed record PluginInfo(string Name, string Version, string Description, string Directory)
{
    /// <summary>
    /// Whether the plugin is currently enabled. A disabled plugin contributes nothing to the
    /// session (no skills, no LSP servers, no hooks). Defaults to <see langword="true"/>.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// The fully-parsed Phase 3 manifest, or <see langword="null"/> for plugins loaded with only
    /// the three legacy fields (<c>name</c>, <c>version</c>, <c>description</c>).
    /// </summary>
    public PluginManifest? Manifest { get; init; }
}
