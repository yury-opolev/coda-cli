using System.Text.Json.Nodes;

namespace Coda.Agent.Lsp;

/// <summary>
/// Parses <see cref="LspServerConfig"/> entries from raw <see cref="JsonObject"/> nodes.
/// Extracted from <c>SettingsLoader</c> so that plugin loaders and settings loaders
/// share identical parse logic.
/// </summary>
public static class LspServerConfigParser
{
    /// <summary>
    /// Parses a single LSP server entry from a JSON object.
    /// Returns <see langword="null"/> if any required field is missing or invalid.
    /// </summary>
    public static LspServerConfig? ParseEntry(JsonObject obj)
    {
        // command: must be non-empty
        var command = obj["command"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        // command with space must be an absolute path
        if (command.Contains(' ') && !IsAbsolutePath(command))
        {
            return null;
        }

        // transport: only stdio (or absent) is supported
        var transport = obj["transport"]?.GetValue<string>();
        if (transport is not null && !string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // extensionToLanguage: must have >= 1 entry
        var extMapNode = obj["extensionToLanguage"];
        if (extMapNode is not JsonObject extMapObj || extMapObj.Count == 0)
        {
            return null;
        }

        var extensionToLanguage = new Dictionary<string, string>(extMapObj.Count);
        foreach (var (rawExt, langNode) in extMapObj)
        {
            var lang = langNode?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(lang))
            {
                continue;
            }

            var normalized = NormalizeExtension(rawExt);
            extensionToLanguage[normalized] = lang;
        }

        if (extensionToLanguage.Count == 0)
        {
            return null;
        }

        // args: optional, default to empty
        var args = new List<string>();
        if (obj["args"] is JsonArray argsArray)
        {
            foreach (var item in argsArray)
            {
                var arg = item?.GetValue<string>();
                if (arg is not null)
                {
                    args.Add(arg);
                }
            }
        }

        // env: optional
        Dictionary<string, string>? env = null;
        if (obj["env"] is JsonObject envObj)
        {
            env = new Dictionary<string, string>(envObj.Count);
            foreach (var (key, valNode) in envObj)
            {
                var val = valNode?.GetValue<string>();
                if (val is not null)
                {
                    env[key] = val;
                }
            }
        }

        // initializationOptions: optional raw JsonNode
        var initializationOptions = obj["initializationOptions"]?.DeepClone();

        // startupTimeoutMs: optional int
        int? startupTimeoutMs = null;
        if (obj["startupTimeoutMs"] is JsonValue timeoutVal
            && timeoutVal.TryGetValue<int>(out var timeout))
        {
            startupTimeoutMs = timeout;
        }

        return new LspServerConfig(
            command,
            args,
            extensionToLanguage,
            env,
            initializationOptions,
            startupTimeoutMs);
    }

    /// <summary>
    /// Iterates a JSON object whose keys are server names and values are server config objects.
    /// Invalid entries are skipped; valid entries are keyed by name.
    /// </summary>
    public static Dictionary<string, LspServerConfig> ParseServerMap(JsonObject serversObject)
    {
        var result = new Dictionary<string, LspServerConfig>();

        foreach (var (serverName, serverNode) in serversObject)
        {
            if (serverNode is not JsonObject serverObj)
            {
                continue;
            }

            var config = ParseEntry(serverObj);
            if (config is not null)
            {
                result[serverName] = config;
            }
        }

        return result;
    }

    private static bool IsAbsolutePath(string command)
    {
        // Unix absolute path starts with /
        if (command.StartsWith('/'))
        {
            return true;
        }

        // Windows absolute path: drive letter followed by :\ or :/
        if (command.Length >= 3
            && char.IsLetter(command[0])
            && command[1] == ':'
            && (command[2] == '\\' || command[2] == '/'))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeExtension(string ext)
    {
        var lower = ext.ToLowerInvariant();
        if (!lower.StartsWith('.'))
        {
            return "." + lower;
        }

        return lower;
    }
}
