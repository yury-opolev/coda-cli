namespace Coda.Agent.Lsp;

/// <summary>
/// Merges LSP server configurations from plugin directories and explicit settings,
/// producing a unified server map. Settings entries win on exact-key clashes;
/// plugin keys are namespaced (<c>plugin:&lt;name&gt;:&lt;server&gt;</c>) so real clashes are rare.
/// </summary>
public static class LspServerMapBuilder
{
    /// <summary>
    /// Builds the merged LSP server map by overlaying the settings servers on the plugin servers
    /// (settings win).
    /// </summary>
    /// <param name="settingsServers">Servers from the settings file.</param>
    /// <param name="pluginServers">
    /// Plugin-contributed servers, already filtered to the plugins the user enabled and approved.
    /// Discovering them here is deliberately not offered: an LSP server runs a process, and this layer
    /// cannot see the plugin trust decisions that should gate it.
    /// </param>
    /// <returns>A merged, read-only server map.</returns>
    public static IReadOnlyDictionary<string, LspServerConfig> Build(
        IReadOnlyDictionary<string, LspServerConfig> settingsServers,
        IReadOnlyDictionary<string, LspServerConfig> pluginServers)
    {
        var merged = new Dictionary<string, LspServerConfig>(pluginServers);

        foreach (var (name, config) in settingsServers)
        {
            merged[name] = config; // settings overlay — settings win
        }

        return merged;
    }
}
