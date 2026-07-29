using Coda.Tui.Plugins;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Skills;

/// <summary>Discovers and loads skill definitions from <c>.coda/skills/*/SKILL.md</c> directories.</summary>
public static partial class SkillLoader
{
    private const string SkillFileName = "SKILL.md";
    private const string RelativeSkillsPath = ".coda/skills";

    [LoggerMessage(Level = LogLevel.Debug, Message = "skipping malformed/unreadable skill file (best-effort); it is omitted from the loaded set: file={file}")]
    private static partial void LogSkillSkipped(ILogger logger, string file, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "skill '{skillName}' has both 'disable-model-invocation: true' and 'user-invocable: false'; it would be unreachable — treating as user-invocable so it can still be run via /skill")]
    private static partial void LogBothExclusionsSet(ILogger logger, string skillName);

    /// <summary>
    /// Loads skills from foreign ecosystem paths (<c>.agents/skills/</c>, <c>~/.claude/agents/</c>,
    /// <c>~/.claude/commands/</c>, read-only), the Claude CLI (~/.claude/skills, read-only),
    /// user-level (~/.coda/skills), plugin skill directories, and project-level (.coda/skills in
    /// <paramref name="workingDirectory"/>). Precedence (lowest to highest):
    /// foreign &lt; Claude &lt; user &lt; plugins &lt; project — later entries override by name, so
    /// Coda's own skills always win. Missing directories are tolerated; malformed files are
    /// skipped or defaulted gracefully.
    /// </summary>
    /// <param name="foreignSkillsDirs">
    /// Overrides the foreign ecosystem directories scanned at lowest precedence (for testing).
    /// When <see langword="null"/> the standard set is used: <c>&lt;workingDirectory&gt;/.agents/skills</c>,
    /// <c>&lt;claudeBase&gt;/agents</c>, and <c>&lt;claudeBase&gt;/commands</c> (where <c>claudeBase</c> is the
    /// parent of the resolved Claude skills directory). Pass an empty list to opt out entirely.
    /// </param>
    public static IReadOnlyList<SkillDefinition> Load(
        string workingDirectory,
        string? userSkillsDir = null,
        string? claudeSkillsDir = null,
        ILogger? logger = null,
        Coda.Tui.Plugins.PluginStateStore? pluginStateStore = null,
        IReadOnlyList<string>? foreignSkillsDirs = null)
    {
        var userBase = userSkillsDir
            ?? Environment.GetEnvironmentVariable("CODA_USER_SKILLS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coda");

        // Reuse the Claude CLI's skills read-only, so users don't have to duplicate
        // them. The location is overridable via CODA_CLAUDE_SKILLS_DIR (point it at a
        // missing path to opt out).
        var claudeSkillsPath = claudeSkillsDir
            ?? Environment.GetEnvironmentVariable("CODA_CLAUDE_SKILLS_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                "skills");

        var userSkillsPath = Path.Combine(userBase, "skills");
        var projectSkillsPath = Path.Combine(workingDirectory, RelativeSkillsPath);

        // Foreign ecosystem directories (lowest precedence, read-only). The Claude agents/commands
        // dirs are siblings of the Claude skills dir, so honoring the CODA_CLAUDE_SKILLS_DIR override
        // (its parent) keeps them isolated in tests.
        var foreignDirs = foreignSkillsDirs ?? DefaultForeignSkillsDirs(workingDirectory, claudeSkillsPath);

        // Precedence: foreign < Claude < user < plugins < project (each level overrides the previous by name).
        var byName = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        // -1. Foreign ecosystem skills (lowest precedence, read-only).
        //     a. <cwd>/.agents/skills/ — project-level foreign (per-directory SKILL.md convention).
        //     b. ~/.claude/agents/     — subagent defs (read as skills).
        //     c. ~/.claude/commands/   — command defs (read as skills).
        foreach (var foreignDir in foreignDirs)
        {
            foreach (var skill in LoadForeignFromDirectory(foreignDir, logger))
            {
                byName[skill.Name] = skill;
            }
        }

        // 0. Claude CLI skills (read-only).
        foreach (var skill in LoadFromDirectory(claudeSkillsPath, SkillOrigin.Claude, logger))
        {
            byName[skill.Name] = skill;
        }

        // 1. User skills (override Claude CLI skills).
        foreach (var skill in LoadFromDirectory(userSkillsPath, SkillOrigin.User, logger))
        {
            byName[skill.Name] = skill;
        }

        // 2. Plugin skills (override user skills; project skills override plugins).
        var pluginSkillDirs = PluginLoader.SkillDirsFor(workingDirectory, userBase, pluginStateStore);
        foreach (var pluginSkillsDir in pluginSkillDirs)
        {
            foreach (var skill in LoadFromDirectory(pluginSkillsDir, SkillOrigin.Plugin, logger))
            {
                byName[skill.Name] = skill;
            }
        }

        // 3. Project skills (highest precedence).
        foreach (var skill in LoadFromDirectory(projectSkillsPath, SkillOrigin.Project, logger))
        {
            byName[skill.Name] = skill;
        }

        return [.. byName.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> DefaultForeignSkillsDirs(string workingDirectory, string claudeSkillsPath)
    {
        var dirs = new List<string>(3)
        {
            Path.Combine(workingDirectory, ".agents", "skills"),
        };

        // The Claude agents/commands directories are siblings of the Claude skills directory.
        var claudeBase = Path.GetDirectoryName(claudeSkillsPath);
        if (!string.IsNullOrEmpty(claudeBase))
        {
            dirs.Add(Path.Combine(claudeBase, "agents"));
            dirs.Add(Path.Combine(claudeBase, "commands"));
        }

        return dirs;
    }

    /// <summary>
    /// Loads skills from a foreign ecosystem directory, tolerating both conventions: a
    /// per-directory <c>&lt;name&gt;/SKILL.md</c> layout (as in <c>.agents/skills/</c>) and flat
    /// <c>&lt;name&gt;.md</c> files (as in <c>~/.claude/agents/</c> and <c>~/.claude/commands/</c>).
    /// Every returned skill carries <see cref="SkillOrigin.Foreign"/>.
    /// </summary>
    private static IEnumerable<SkillDefinition> LoadForeignFromDirectory(string root, ILogger? logger)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        // Per-directory SKILL.md layout.
        foreach (var subDir in Directory.EnumerateDirectories(root))
        {
            var skillFile = Path.Combine(subDir, SkillFileName);
            var skill = TryLoadForeignFile(skillFile, Path.GetFileName(subDir), logger);
            if (skill is not null)
            {
                yield return skill;
            }
        }

        // Flat *.md files (foreign agent/command definitions).
        foreach (var mdFile in Directory.EnumerateFiles(root, "*.md"))
        {
            if (string.Equals(Path.GetFileName(mdFile), SkillFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var skill = TryLoadForeignFile(mdFile, Path.GetFileNameWithoutExtension(mdFile), logger);
            if (skill is not null)
            {
                yield return skill;
            }
        }
    }

    private static SkillDefinition? TryLoadForeignFile(string file, string fallbackName, ILogger? logger)
    {
        try
        {
            var fullPath = Path.GetFullPath(file);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            var content = File.ReadAllText(fullPath);
            var skill = ParseSkillFile(content, fallbackName, fullPath, SkillOrigin.Foreign);

            // A foreign skill with both exclusions unreachable is restored to user-invocable.
            if (skill.DisableModelInvocation && !skill.UserInvocable)
            {
                skill = skill with { UserInvocable = true };
            }

            return skill;
        }
        catch (Exception ex)
        {
            if (logger is not null)
            {
                LogSkillSkipped(logger, file, ex);
            }

            return null;
        }
    }

    private static IEnumerable<SkillDefinition> LoadFromDirectory(
        string skillsRoot,
        SkillOrigin origin,
        ILogger? logger)
    {
        if (!Directory.Exists(skillsRoot))
        {
            yield break;
        }

        foreach (var subDir in Directory.EnumerateDirectories(skillsRoot))
        {
            var skillFile = Path.Combine(subDir, SkillFileName);
            SkillDefinition? skill = null;
            try
            {
                skillFile = Path.GetFullPath(skillFile);
                if (!File.Exists(skillFile))
                {
                    continue;
                }

                var content = File.ReadAllText(skillFile);
                skill = ParseSkillFile(content, Path.GetFileName(subDir), skillFile, origin);
            }
            catch (Exception ex)
            {
                // Skip malformed/unreadable files.
                if (logger is not null)
                {
                    LogSkillSkipped(logger, skillFile, ex);
                }
            }

            if (skill is not null)
            {
                // A skill with both exclusions set to their exclusionary values is unreachable.
                // Log a warning and restore user-invocability so /skill <name> still works.
                if (skill.DisableModelInvocation && !skill.UserInvocable)
                {
                    if (logger is not null)
                    {
                        LogBothExclusionsSet(logger, skill.Name);
                    }

                    skill = skill with { UserInvocable = true };
                }

                yield return skill;
            }
        }
    }

    /// <summary>
    /// Parses a SKILL.md file using the YAML-subset frontmatter parser. If no frontmatter is
    /// present, the directory name is used as the skill name and the whole file becomes the body.
    /// </summary>
    internal static SkillDefinition ParseSkillFile(
        string content,
        string directoryName,
        string? sourcePath = null,
        SkillOrigin origin = SkillOrigin.Project)
    {
        var fm = SkillFrontmatterParser.Parse(content);

        if (!fm.HasFrontmatter)
        {
            return new SkillDefinition(directoryName, string.Empty, content.Trim())
            {
                SourcePath = sourcePath,
                Origin = origin,
            };
        }

        return new SkillDefinition(
            string.IsNullOrWhiteSpace(fm.Name) ? directoryName : fm.Name,
            fm.Description ?? string.Empty,
            fm.Body)
        {
            WhenToUse = fm.WhenToUse,
            ArgumentHint = fm.ArgumentHint,
            Arguments = fm.Arguments,
            SourcePath = sourcePath,
            Origin = origin,
            UnknownFields = fm.UnknownFields,
            DisableModelInvocation = fm.DisableModelInvocation,
            UserInvocable = fm.UserInvocable,
            AllowedTools = fm.AllowedTools,
            DisallowedTools = fm.DisallowedTools,
            Model = fm.Model,
            Effort = fm.Effort,
            ContextMode = fm.ContextMode,
            AgentType = fm.Agent,
            Paths = fm.Paths,
        };
    }
}
