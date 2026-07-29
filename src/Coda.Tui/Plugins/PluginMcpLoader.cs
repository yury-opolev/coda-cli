using System.Text.Json;
using Coda.Mcp;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Plugins;

/// <summary>
/// Loads MCP server definitions contributed by plugins from the files listed in each plugin
/// manifest's <c>mcpServers</c> array. Each file has the same format as <c>.mcp.json</c>.
/// <para>
/// Plugin servers are the lowest-precedence source: a user or project entry with the same server
/// name wins. A disabled plugin contributes no servers.
/// </para>
/// </summary>
public static class PluginMcpLoader
{
    /// <summary>
    /// Loads all MCP servers contributed by a collection of plugins, tagged with their
    /// originating plugin name. Later plugins in the list can shadow earlier ones if they
    /// declare the same server name.
    /// </summary>
    /// <returns>
    /// A dictionary from server name to <c>(config, pluginName)</c>. Does not include
    /// servers contributed by disabled plugins.
    /// </returns>
    public static IReadOnlyDictionary<string, (McpServerConfig Config, string PluginName)> Load(
        IReadOnlyList<PluginInfo> plugins,
        ILogger? logger = null)
    {
        var result = new Dictionary<string, (McpServerConfig, string)>(StringComparer.Ordinal);

        foreach (var plugin in plugins)
        {
            if (!plugin.IsEnabled) continue;
            if (plugin.Manifest?.McpServers is not { Count: > 0 } mcpPaths) continue;

            foreach (var relativePath in mcpPaths)
            {
                var resolved = PluginResourceLoader.ResolvePath(plugin, relativePath);

                if (!PluginResourceLoader.IsContained(resolved, plugin.Directory))
                {
                    logger?.LogError(
                        "Plugin '{Plugin}': MCP server path '{Path}' escapes the plugin directory — skipped.",
                        plugin.Name, relativePath);
                    continue;
                }

                if (!File.Exists(resolved))
                {
                    logger?.LogWarning(
                        "Plugin '{Plugin}': MCP server file '{Path}' not found — skipped.",
                        plugin.Name, resolved);
                    continue;
                }

                // Validate the JSON explicitly: McpConfig.Parse silently returns empty on
                // malformed JSON, so pre-validating is what produces the diagnostic log entry.
                using var doc = PluginResourceLoader.TryReadJsonObject(
                    resolved, plugin.Name, "MCP server", logger);
                if (doc is null)
                {
                    continue;
                }

                var servers = McpConfig.Parse(doc.RootElement.GetRawText());
                foreach (var (name, config) in servers)
                {
                    result[name] = (config, plugin.Name);
                }
            }
        }

        return result;
    }
}
