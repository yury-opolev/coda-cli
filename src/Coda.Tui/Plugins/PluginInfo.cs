namespace Coda.Tui.Plugins;

/// <summary>Metadata for a discovered Coda plugin.</summary>
public sealed record PluginInfo(string Name, string Version, string Description, string Directory);
