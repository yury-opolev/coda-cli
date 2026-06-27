using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Coda.Agent.Lsp;

/// <summary>
/// Loads LSP server configurations from plugin directories.
/// Each plugin directory may provide servers via a <c>.lsp.json</c> file and/or
/// the <c>lspServers</c> field in <c>plugin.json</c>.
/// </summary>
public static partial class PluginLspServerLoader
{
    /// <summary>
    /// Loads and scopes all LSP servers discovered across the given plugin base directories.
    /// Each server key is of the form <c>plugin:&lt;pluginName&gt;:&lt;serverName&gt;</c>.
    /// Malformed or missing files are silently skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, LspServerConfig> Load(IReadOnlyList<string> pluginBaseDirs)
    {
        var result = new Dictionary<string, LspServerConfig>();

        foreach (var baseDir in pluginBaseDirs)
        {
            if (!Directory.Exists(baseDir))
            {
                continue;
            }

            foreach (var pluginDir in Directory.EnumerateDirectories(baseDir))
            {
                var pluginJsonPath = Path.Combine(pluginDir, "plugin.json");
                if (!File.Exists(pluginJsonPath))
                {
                    continue;
                }

                try
                {
                    LoadPlugin(pluginDir, pluginJsonPath, result);
                }
                catch
                {
                    // tolerant: skip this plugin, continue with others
                }
            }
        }

        return result;
    }

    private static void LoadPlugin(string pluginDir, string pluginJsonPath, Dictionary<string, LspServerConfig> result)
    {
        var pluginJsonText = File.ReadAllText(pluginJsonPath);
        JsonNode? pluginRoot;
        try
        {
            pluginRoot = JsonNode.Parse(pluginJsonText);
        }
        catch
        {
            return;
        }

        if (pluginRoot is not JsonObject pluginObj)
        {
            return;
        }

        // Determine plugin name: use "name" field if present, otherwise directory name
        var dirName = Path.GetFileName(pluginDir);
        var pluginName = pluginObj["name"]?.GetValue<string>() ?? dirName ?? pluginDir;
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            pluginName = dirName ?? pluginDir;
        }

        // Collect raw server map; .lsp.json first, plugin.json lspServers second (later wins)
        var servers = new Dictionary<string, LspServerConfig>();

        // 1. .lsp.json file
        var lspJsonPath = Path.Combine(pluginDir, ".lsp.json");
        if (File.Exists(lspJsonPath))
        {
            LoadServerMapFromFile(lspJsonPath, servers);
        }

        // 2. plugin.json lspServers field
        var lspServersNode = pluginObj["lspServers"];
        if (lspServersNode is not null)
        {
            LoadFromDeclaration(lspServersNode, pluginDir, servers);
        }

        // Scope and resolve each collected server
        foreach (var (serverName, config) in servers)
        {
            var resolved = ResolvePluginEnvironment(config, pluginDir);
            var scopedKey = $"plugin:{pluginName}:{serverName}";
            result[scopedKey] = resolved;
        }
    }

    private static void LoadServerMapFromFile(string filePath, Dictionary<string, LspServerConfig> target)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var node = JsonNode.Parse(text);
            if (node is JsonObject obj)
            {
                MergeServerMap(obj, target);
            }
        }
        catch
        {
            // skip malformed file
        }
    }

    private static void LoadFromDeclaration(JsonNode declaration, string pluginDir, Dictionary<string, LspServerConfig> target)
    {
        // Normalise to array
        JsonArray declarations;
        if (declaration is JsonArray arr)
        {
            declarations = arr;
        }
        else
        {
            declarations = [declaration.DeepClone()];
        }

        foreach (var item in declarations)
        {
            if (item is null)
            {
                continue;
            }

            if (item is JsonValue strVal && strVal.TryGetValue<string>(out var relativePath))
            {
                // String path — validate and load from file
                var resolved = ValidatePathWithinPlugin(pluginDir, relativePath);
                if (resolved is null)
                {
                    continue;
                }

                LoadServerMapFromFile(resolved, target);
            }
            else if (item is JsonObject inlineObj)
            {
                // Inline record<serverName, config>
                MergeServerMap(inlineObj, target);
            }
        }
    }

    private static void MergeServerMap(JsonObject serversObject, Dictionary<string, LspServerConfig> target)
    {
        var parsed = LspServerConfigParser.ParseServerMap(serversObject);
        foreach (var (name, cfg) in parsed)
        {
            target[name] = cfg;
        }
    }

    private static LspServerConfig ResolvePluginEnvironment(LspServerConfig config, string pluginDir)
    {
        var resolvedCommand = ResolveVariables(config.Command, pluginDir);

        var resolvedArgs = config.Args
            .Select(a => ResolveVariables(a, pluginDir))
            .ToList();

        // Build env: start from existing env, then expand values, then inject CLAUDE_PLUGIN_ROOT
        var resolvedEnv = new Dictionary<string, string>();
        if (config.Env is not null)
        {
            foreach (var (key, value) in config.Env)
            {
                resolvedEnv[key] = ResolveVariables(value, pluginDir);
            }
        }

        // CLAUDE_PLUGIN_ROOT is injected verbatim (not variable-expanded)
        resolvedEnv["CLAUDE_PLUGIN_ROOT"] = pluginDir;

        return new LspServerConfig(
            resolvedCommand,
            resolvedArgs,
            config.ExtensionToLanguage,
            resolvedEnv,
            config.InitializationOptions,
            config.StartupTimeoutMs);
    }

    private static string ResolveVariables(string value, string pluginRoot)
    {
        // First replace ${CLAUDE_PLUGIN_ROOT} literally
        var result = value.Replace("${CLAUDE_PLUGIN_ROOT}", pluginRoot, StringComparison.Ordinal);

        // Then replace remaining ${VAR} patterns from the process environment
        result = VarPattern().Replace(result, match =>
        {
            var varName = match.Groups[1].Value;
            // CLAUDE_PLUGIN_ROOT was already handled above; if somehow still present, use pluginRoot
            if (string.Equals(varName, "CLAUDE_PLUGIN_ROOT", StringComparison.Ordinal))
            {
                return pluginRoot;
            }

            return Environment.GetEnvironmentVariable(varName) ?? string.Empty;
        });

        return result;
    }

    private static string? ValidatePathWithinPlugin(string pluginDir, string relativePath)
    {
        // Reject absolute paths
        if (Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var resolvedPlugin = Path.GetFullPath(pluginDir);
        var resolvedFile = Path.GetFullPath(Path.Combine(pluginDir, relativePath));

        // Ensure resolved file is inside the plugin directory (trailing separator for exact match)
        var pluginWithSep = resolvedPlugin.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!resolvedFile.StartsWith(pluginWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return resolvedFile;
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex VarPattern();
}
