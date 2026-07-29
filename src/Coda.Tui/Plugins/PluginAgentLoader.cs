using Coda.Agent.Subagents;
using Coda.Tui.Skills;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Plugins;

/// <summary>
/// Loads plugin-contributed subagent definitions from <c>agents/</c> (or the manifest-overridden
/// path). Agent files are <c>.md</c> files with YAML-subset frontmatter containing at minimum a
/// <c>type</c> field. Forbidden keys (<c>hooks</c>, <c>mcpServers</c>, <c>permissions</c>,
/// <c>permissionMode</c>) are rejected with a logged error; the rest of the definition still loads.
/// </summary>
public static class PluginAgentLoader
{
    // After SkillFrontmatterParser normalization (lower + underscores→hyphens), the set of
    // keys that are forbidden in plugin-contributed agent files. Comparison is done by stripping
    // hyphens so both "mcp-servers" and "mcpservers" match.
    private static readonly HashSet<string> ForbiddenStripped = new(StringComparer.Ordinal)
    {
        "hooks",
        "mcpservers",
        "permissions",
        "permissionmode",
    };

    /// <summary>
    /// Loads agent definitions contributed by a single plugin.
    /// Returns an empty list if the plugin is disabled, has no manifest, or the directory
    /// does not exist.
    /// </summary>
    public static IReadOnlyList<SubagentDefinition> Load(PluginInfo plugin, ILogger? logger = null)
    {
        if (!plugin.IsEnabled) return [];

        var agentsDir = ResolveAgentsDir(plugin);
        return LoadFromDirectory(agentsDir, plugin.Name, logger);
    }

    /// <summary>
    /// Scans <paramref name="agentsDir"/> for <c>.md</c> files and parses each as a subagent
    /// definition. Malformed files are skipped with a logged error; valid ones are returned.
    /// </summary>
    public static IReadOnlyList<SubagentDefinition> LoadFromDirectory(
        string agentsDir,
        string pluginName,
        ILogger? logger = null)
    {
        if (!Directory.Exists(agentsDir))
        {
            return [];
        }

        var definitions = new List<SubagentDefinition>();

        foreach (var file in Directory.EnumerateFiles(agentsDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            var definition = TryLoadFile(file, pluginName, logger);
            if (definition is not null)
            {
                definitions.Add(definition);
            }
        }

        return definitions;
    }

    private static SubagentDefinition? TryLoadFile(string file, string pluginName, ILogger? logger)
    {
        string content;
        try
        {
            content = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogError(
                "Plugin '{Plugin}': failed to read agent file '{File}': {Message}",
                pluginName, file, ex.Message);
            return null;
        }

        var frontmatter = SkillFrontmatterParser.Parse(content);
        if (!frontmatter.HasFrontmatter)
        {
            logger?.LogWarning(
                "Plugin '{Plugin}': agent file '{File}' has no frontmatter block — skipped.",
                pluginName, file);
            return null;
        }

        // Reject forbidden keys but keep loading the rest of the definition.
        // Real security guarantee: SubagentDefinition has only Type, Description,
        // SystemPromptBody, ReadOnlyToolsOnly — unrecognised keys land in UnknownFields
        // and are never read. The check below is a diagnostic aid so plugin authors
        // are notified early; it is not a security boundary.
        foreach (var (key, _) in frontmatter.UnknownFields)
        {
            var stripped = key
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal);
            if (ForbiddenStripped.Contains(stripped))
            {
                logger?.LogError(
                    "Plugin '{Plugin}' agent '{File}': key '{Key}' is forbidden in plugin-contributed " +
                    "agent definitions and will be ignored. Declare hooks, mcpServers, and permission " +
                    "settings at the plugin level in plugin.json instead.",
                    pluginName, Path.GetFileName(file), key);
            }
        }

        // "type" is not a SkillFrontmatterParser known key → lands in UnknownFields.
        frontmatter.UnknownFields.TryGetValue("type", out var agentType);

        if (string.IsNullOrWhiteSpace(agentType))
        {
            logger?.LogWarning(
                "Plugin '{Plugin}': agent file '{File}' has no 'type' field — skipped.",
                pluginName, file);
            return null;
        }

        var description = frontmatter.Description ?? string.Empty;

        // read-only-tools is not a known skill key → lands in UnknownFields.
        var readOnlyToolsOnly = false;
        if (frontmatter.UnknownFields.TryGetValue("read-only-tools", out var readOnlyValue))
        {
            readOnlyToolsOnly = string.Equals(readOnlyValue, "true", StringComparison.OrdinalIgnoreCase);
        }

        return new SubagentDefinition(
            Type: agentType.Trim(),
            Description: description,
            SystemPromptBody: frontmatter.Body,
            ReadOnlyToolsOnly: readOnlyToolsOnly);
    }

    private static string ResolveAgentsDir(PluginInfo plugin)
    {
        var relativePath = plugin.Manifest?.Agents ?? "agents";
        return Path.GetFullPath(Path.Combine(plugin.Directory, relativePath));
    }
}
