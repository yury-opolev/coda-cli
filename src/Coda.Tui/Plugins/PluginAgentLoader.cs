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
    /// <param name="plugin">The plugin to load agents from.</param>
    /// <param name="workingDirectory">
    /// The session working directory used to determine whether the plugin is project-scoped.
    /// When null, the plugin is treated as user-scoped (model key is accepted).
    /// </param>
    /// <param name="logger">Optional logger for warnings and errors.</param>
    public static IReadOnlyList<SubagentDefinition> Load(
        PluginInfo plugin,
        string? workingDirectory = null,
        ILogger? logger = null)
    {
        if (!plugin.IsEnabled) return [];

        var isProjectPlugin = workingDirectory is not null
            && PluginComponentComposer.IsProjectPlugin(plugin, workingDirectory);
        var agentsDir = PluginResourceLoader.ResolvePath(plugin, plugin.Manifest?.Agents ?? "agents");
        return LoadFromDirectory(agentsDir, plugin.Name, isProjectPlugin: isProjectPlugin, logger: logger);
    }

    /// <summary>
    /// Scans <paramref name="agentsDir"/> for <c>.md</c> files and parses each as a subagent
    /// definition. Malformed files are skipped with a logged error; valid ones are returned.
    /// </summary>
    public static IReadOnlyList<SubagentDefinition> LoadFromDirectory(
        string agentsDir,
        string pluginName,
        ILogger? logger = null,
        bool isProjectPlugin = false) =>
        PluginResourceLoader.LoadDirectory<SubagentDefinition>(
            agentsDir, "*.md", pluginName, "agent",
            file => TryLoadFile(file, pluginName, isProjectPlugin, logger), logger);

    private static SubagentDefinition? TryLoadFile(
        string file,
        string pluginName,
        bool isProjectPlugin,
        ILogger? logger)
    {
        if (PluginResourceLoader.TryReadText(file, pluginName, "agent", logger) is not { } content)
        {
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

        // SECURITY INVARIANT: SubagentDefinition carries Type, Description, SystemPromptBody,
        // ReadOnlyToolsOnly, and Model (user-scoped plugins only). Every other frontmatter key
        // lands in UnknownFields and is never read. The forbidden-key check below is a diagnostic
        // aid so plugin authors are notified early; the real restriction is that only recognised
        // fields are mapped into the definition.
        // Model is additionally gated on plugin scope: a project-scoped plugin's `model:` key is
        // ignored (with a warning) because the project directory is attacker-controlled and model
        // choice is a cost lever.
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

        // model key: accepted for user-scoped plugins; ignored with a warning for project-scoped
        // plugins because the project directory is attacker-controlled after a hostile clone, and
        // model choice is a cost lever.
        //
        // NOTE: `model` is a first-class field on the shared frontmatter parser
        // (SkillFrontmatter.Model), not an unknown key — reading it from UnknownFields silently
        // yields null for every agent file.
        string? model = null;
        if (!string.IsNullOrWhiteSpace(frontmatter.Model))
        {
            if (isProjectPlugin)
            {
                logger?.LogWarning(
                    "Plugin '{Plugin}' agent '{File}': 'model' key is ignored in project-scoped plugin " +
                    "agent definitions. Declare model overrides in your user settings file instead.",
                    pluginName, Path.GetFileName(file));
            }
            else
            {
                model = frontmatter.Model.Trim();
            }
        }

        return new SubagentDefinition(
            Type: agentType.Trim(),
            Description: description,
            SystemPromptBody: frontmatter.Body,
            ReadOnlyToolsOnly: readOnlyToolsOnly,
            Model: model);
    }
}
