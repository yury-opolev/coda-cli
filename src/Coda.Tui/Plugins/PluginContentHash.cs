using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Coda.Tui.Plugins;

/// <summary>
/// Computes a stable SHA-256 content hash of a plugin's identity and behavior-affecting fields.
/// The hash is used as the trust key: updating a plugin's version or changing its hook bodies,
/// MCP server configs, skill file set, or agent file set changes the hash and causes a re-prompt
/// rather than inheriting the previous approval decision.
/// </summary>
public static class PluginContentHash
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Returns the hex-encoded SHA-256 of the plugin's name and version only.
    /// Used as a stable key when no manifest is available (legacy / backward-compat callers)
    /// and for version-change detection in unit tests.
    /// </summary>
    public static string Compute(string name, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var canonical = new
        {
            name = name.ToLowerInvariant(),
            version = (version ?? "0.0.0").ToLowerInvariant(),
        };
        var json = JsonSerializer.Serialize(canonical, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Returns the hex-encoded SHA-256 over the plugin's full behavior-affecting surface:
    /// name, version, hook file contents, MCP server file contents, skill file names, and
    /// agent file names. Any in-place change (even at the same version) produces a different
    /// hash and triggers a re-approval prompt.
    /// Falls back to <see cref="Compute(string, string)"/> when <paramref name="plugin"/> has
    /// no manifest — preserving backward compatibility for callers that do not parse manifests.
    /// </summary>
    public static string Compute(PluginInfo plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        if (plugin.Manifest is null)
        {
            return Compute(plugin.Name, plugin.Version);
        }

        var sb = new StringBuilder();
        sb.Append("name=").AppendLine(plugin.Name.ToLowerInvariant());
        sb.Append("version=").AppendLine((plugin.Manifest.Version ?? "0.0.0").ToLowerInvariant());

        // Hook file contents — sorted by relative path for determinism.
        foreach (var hookPath in plugin.Manifest.Hooks.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(plugin.Directory, hookPath));
                if (File.Exists(fullPath))
                {
                    sb.Append("hook:").Append(hookPath.Replace('\\', '/')).Append('=');
                    sb.AppendLine(File.ReadAllText(fullPath));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Skip unreadable hook files — fail-safe.
            }
        }

        // MCP server file contents — sorted by relative path for determinism.
        foreach (var mcpPath in plugin.Manifest.McpServers.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(plugin.Directory, mcpPath));
                if (File.Exists(fullPath))
                {
                    sb.Append("mcp:").Append(mcpPath.Replace('\\', '/')).Append('=');
                    sb.AppendLine(File.ReadAllText(fullPath));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Skip unreadable MCP server files — fail-safe.
            }
        }

        // LSP servers — hashed as the loader resolves them, so an inline declaration, a referenced
        // file and a .lsp.json all move the hash. These start processes, so a changed command must
        // force the approval prompt again.
        try
        {
            var lspServers = Coda.Agent.Lsp.PluginLspServerLoader
                .LoadForPluginDirectories([plugin.Directory])
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal);

            foreach (var (name, config) in lspServers)
            {
                sb.Append("lsp:").Append(name).Append('=')
                  .Append(config.Command).Append(' ')
                  .Append(string.Join(' ', config.Args ?? []));

                if (config.Env is { Count: > 0 } env)
                {
                    foreach (var (key, value) in env.OrderBy(e => e.Key, StringComparer.Ordinal))
                    {
                        sb.Append(' ').Append(key).Append('=').Append(value);
                    }
                }

                sb.AppendLine();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Skip unreadable LSP declarations — fail-safe.
        }

        // Skill file names — adding or removing skills changes the hash.
        var skillDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(plugin.Directory, "skills")),
        };
        foreach (var extra in plugin.Manifest.Skills)
        {
            try { skillDirs.Add(Path.GetFullPath(Path.Combine(plugin.Directory, extra))); }
            catch (ArgumentException) { }
        }
        foreach (var dir in skillDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var skillFile in Directory.EnumerateFiles(dir, "SKILL.md", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append("skill:").AppendLine(
                        Path.GetRelativePath(plugin.Directory, skillFile).Replace('\\', '/'));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable skill directories — fail-safe.
            }
        }

        // Agent file names — sorted for determinism.
        try
        {
            var agentsDir = Path.GetFullPath(
                Path.Combine(plugin.Directory, plugin.Manifest.Agents ?? "agents"));
            if (Directory.Exists(agentsDir))
            {
                var agentFiles = Directory.EnumerateFiles(agentsDir, "*.md", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateFiles(agentsDir, "*.json", SearchOption.TopDirectoryOnly))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                foreach (var agentFile in agentFiles)
                {
                    sb.Append("agent:").AppendLine(
                        Path.GetRelativePath(plugin.Directory, agentFile).Replace('\\', '/'));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Skip unreadable agent directories — fail-safe.
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
