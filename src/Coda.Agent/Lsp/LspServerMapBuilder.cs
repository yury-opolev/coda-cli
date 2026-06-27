namespace Coda.Agent.Lsp;

/// <summary>
/// Merges LSP server configurations from plugin directories and explicit settings,
/// producing a unified server map. Settings entries win on exact-key clashes;
/// plugin keys are namespaced (<c>plugin:&lt;name&gt;:&lt;server&gt;</c>) so real clashes are rare.
/// </summary>
public static class LspServerMapBuilder
{
    /// <summary>
    /// Builds the merged LSP server map by loading plugin servers first,
    /// then overlaying the settings servers (settings win).
    /// </summary>
    /// <param name="settingsServers">Servers from the settings file.</param>
    /// <param name="pluginBaseDirs">Directories to scan for plugin subdirectories.</param>
    /// <returns>A merged, read-only server map.</returns>
    public static IReadOnlyDictionary<string, LspServerConfig> Build(
        IReadOnlyDictionary<string, LspServerConfig> settingsServers,
        IReadOnlyList<string> pluginBaseDirs)
    {
        var merged = new Dictionary<string, LspServerConfig>(PluginLspServerLoader.Load(pluginBaseDirs));

        foreach (var (name, config) in settingsServers)
        {
            merged[name] = config; // settings overlay — settings win
        }

        return merged;
    }
}
