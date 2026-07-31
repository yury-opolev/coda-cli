namespace Coda.Tui.Plugins;

/// <summary>
/// Counts the components a plugin provides, for use in the install-time inventory prompt.
/// </summary>
public sealed record PluginInventory
{
    /// <summary>Number of <c>SKILL.md</c> files contributed by the plugin.</summary>
    public int SkillCount { get; init; }

    /// <summary>Number of hook configuration files declared in the plugin manifest.</summary>
    public int HookCount { get; init; }

    /// <summary>Number of MCP server entries declared in the plugin manifest.</summary>
    public int McpServerCount { get; init; }

    /// <summary>
    /// Number of LSP servers the plugin declares, counted by resolving them exactly as the loader
    /// does — so an inline <c>lspServers</c> object, a path to a file, and a <c>.lsp.json</c> all count.
    /// </summary>
    public int LspServerCount { get; init; }

    /// <summary>Number of subagent definition files found in the plugin's agents directory.</summary>
    public int SubagentCount { get; init; }

    /// <summary>Number of command files (<c>.md</c>) found in the plugin's commands directory.</summary>
    public int CommandCount { get; init; }

    /// <summary><see langword="true"/> when the plugin provides no components at all.</summary>
    public bool IsEmpty =>
        this.SkillCount == 0 && this.HookCount == 0 && this.McpServerCount == 0 &&
        this.SubagentCount == 0 && this.CommandCount == 0 && this.LspServerCount == 0;

    /// <summary>
    /// The set of <see cref="PluginComponentClass"/> values present in this inventory
    /// (only classes with at least one component are included).
    /// </summary>
    public IReadOnlySet<PluginComponentClass> PresentClasses
    {
        get
        {
            var set = new HashSet<PluginComponentClass>();
            if (this.SkillCount > 0) set.Add(PluginComponentClass.Skill);
            if (this.HookCount > 0) set.Add(PluginComponentClass.Hook);
            if (this.McpServerCount > 0) set.Add(PluginComponentClass.McpServer);
            if (this.SubagentCount > 0) set.Add(PluginComponentClass.Subagent);
            if (this.CommandCount > 0) set.Add(PluginComponentClass.SlashCommand);
            if (this.LspServerCount > 0) set.Add(PluginComponentClass.Lsp);
            return set;
        }
    }

    /// <summary>
    /// Returns a human-readable summary such as <c>2 skills, 1 hook, 1 MCP server</c>.
    /// Returns <c>(no components)</c> when the inventory is empty.
    /// </summary>
    public string ToDisplayString()
    {
        var parts = new List<string>();
        if (this.SkillCount > 0) parts.Add($"{this.SkillCount} skill{(this.SkillCount == 1 ? string.Empty : "s")}");
        if (this.CommandCount > 0) parts.Add($"{this.CommandCount} command{(this.CommandCount == 1 ? string.Empty : "s")}");
        if (this.HookCount > 0) parts.Add($"{this.HookCount} hook{(this.HookCount == 1 ? string.Empty : "s")}");
        if (this.McpServerCount > 0) parts.Add($"{this.McpServerCount} MCP server{(this.McpServerCount == 1 ? string.Empty : "s")}");
        if (this.SubagentCount > 0) parts.Add($"{this.SubagentCount} subagent{(this.SubagentCount == 1 ? string.Empty : "s")}");
        if (this.LspServerCount > 0) parts.Add($"{this.LspServerCount} LSP server{(this.LspServerCount == 1 ? string.Empty : "s")}");
        return parts.Count == 0 ? "(no components)" : string.Join(", ", parts);
    }

    /// <summary>
    /// Counts the components contributed by <paramref name="manifest"/> from <paramref name="pluginDirectory"/>.
    /// Returns an empty inventory when <paramref name="manifest"/> is <see langword="null"/>.
    /// </summary>
    public static PluginInventory FromManifest(PluginManifest? manifest, string pluginDirectory)
    {
        if (manifest is null)
        {
            return new PluginInventory();
        }

        return new PluginInventory
        {
            SkillCount = CountSkills(manifest, pluginDirectory),
            HookCount = manifest.Hooks.Count,
            McpServerCount = manifest.McpServers.Count,
            SubagentCount = CountSubagents(manifest, pluginDirectory),
            CommandCount = CountCommands(manifest, pluginDirectory),
            LspServerCount = CountLspServers(pluginDirectory),
        };
    }

    /// <summary>
    /// Counts LSP servers by resolving them through the loader that actually starts them, so the
    /// inventory cannot disagree with what would run. The manifest model only captures the path form;
    /// a plugin may equally declare them inline or in a <c>.lsp.json</c>.
    /// </summary>
    private static int CountLspServers(string pluginDirectory)
    {
        try
        {
            return Coda.Agent.Lsp.PluginLspServerLoader.LoadForPluginDirectories([pluginDirectory]).Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private static int CountSkills(PluginManifest manifest, string pluginDirectory)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var conventionDir = Path.GetFullPath(Path.Combine(pluginDirectory, "skills"));
        if (Directory.Exists(conventionDir))
        {
            dirs.Add(conventionDir);
        }

        foreach (var extra in manifest.Skills)
        {
            try
            {
                var full = Path.GetFullPath(Path.Combine(pluginDirectory, extra));
                if (Directory.Exists(full))
                {
                    dirs.Add(full);
                }
            }
            catch (ArgumentException)
            {
                // Invalid path — skip.
            }
        }

        return dirs.Sum(d =>
        {
            try
            {
                return Directory.EnumerateFiles(d, "SKILL.md", SearchOption.AllDirectories).Count();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        });
    }

    private static int CountSubagents(PluginManifest manifest, string pluginDirectory)
    {
        var agentsRelative = manifest.Agents ?? "agents";
        try
        {
            var agentsDir = Path.GetFullPath(Path.Combine(pluginDirectory, agentsRelative));
            if (!Directory.Exists(agentsDir))
            {
                return 0;
            }

            return Directory.EnumerateFiles(agentsDir, "*.md", SearchOption.TopDirectoryOnly).Count()
                   + Directory.EnumerateFiles(agentsDir, "*.json", SearchOption.TopDirectoryOnly).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private static int CountCommands(PluginManifest manifest, string pluginDirectory)
    {
        var commandsRelative = manifest.Commands ?? "commands";
        try
        {
            var commandsDir = Path.GetFullPath(Path.Combine(pluginDirectory, commandsRelative));
            if (!Directory.Exists(commandsDir)) return 0;
            return Directory.EnumerateFiles(commandsDir, "*.md", SearchOption.TopDirectoryOnly).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }
}
