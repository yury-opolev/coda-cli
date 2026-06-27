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

    /// <summary>
    /// Loads skills from the Claude CLI (~/.claude/skills, read-only), user-level
    /// (~/.coda/skills), plugin skill directories, and project-level (.coda/skills in
    /// <paramref name="workingDirectory"/>). Precedence (lowest to highest):
    /// Claude &lt; user &lt; plugins &lt; project — later entries override by name, so Coda's
    /// own skills always win. Missing directories are tolerated; malformed files are
    /// skipped or defaulted gracefully.
    /// </summary>
    public static IReadOnlyList<SkillDefinition> Load(
        string workingDirectory,
        string? userSkillsDir = null,
        string? claudeSkillsDir = null,
        ILogger? logger = null)
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

        // Precedence: Claude < user < plugins < project (each level overrides the previous by name).
        var byName = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        // 0. Claude CLI skills (lowest precedence, read-only).
        foreach (var skill in LoadFromDirectory(claudeSkillsPath, logger))
        {
            byName[skill.Name] = skill;
        }

        // 1. User skills (override Claude CLI skills).
        foreach (var skill in LoadFromDirectory(userSkillsPath, logger))
        {
            byName[skill.Name] = skill;
        }

        // 2. Plugin skills (override user skills; project skills override plugins).
        var pluginSkillDirs = PluginLoader.SkillDirsFor(workingDirectory, userBase);
        foreach (var pluginSkillsDir in pluginSkillDirs)
        {
            foreach (var skill in LoadFromDirectory(pluginSkillsDir, logger))
            {
                byName[skill.Name] = skill;
            }
        }

        // 3. Project skills (highest precedence).
        foreach (var skill in LoadFromDirectory(projectSkillsPath, logger))
        {
            byName[skill.Name] = skill;
        }

        return [.. byName.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<SkillDefinition> LoadFromDirectory(string skillsRoot, ILogger? logger)
    {
        if (!Directory.Exists(skillsRoot))
        {
            yield break;
        }

        foreach (var subDir in Directory.EnumerateDirectories(skillsRoot))
        {
            var skillFile = Path.Combine(subDir, SkillFileName);
            if (!File.Exists(skillFile))
            {
                continue;
            }

            SkillDefinition? skill = null;
            try
            {
                var content = File.ReadAllText(skillFile);
                skill = ParseSkillFile(content, Path.GetFileName(subDir));
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
                yield return skill;
            }
        }
    }

    /// <summary>
    /// Parses a SKILL.md file. Optional YAML-ish frontmatter is delimited by lines of <c>---</c>
    /// at the top and may contain <c>name:</c> and <c>description:</c> keys. If no frontmatter,
    /// the directory name is used as the skill name.
    /// </summary>
    internal static SkillDefinition ParseSkillFile(string content, string directoryName)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');

        // Check for frontmatter: first non-empty line must be "---"
        var firstNonEmpty = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (firstNonEmpty >= 0 && lines[firstNonEmpty].Trim() == "---")
        {
            // Find closing "---"
            var closingIndex = -1;
            for (var i = firstNonEmpty + 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    closingIndex = i;
                    break;
                }
            }

            if (closingIndex > firstNonEmpty)
            {
                var name = string.Empty;
                var description = string.Empty;

                for (var i = firstNonEmpty + 1; i < closingIndex; i++)
                {
                    var line = lines[i];
                    if (TryParseYamlValue(line, "name", out var nameVal))
                    {
                        name = nameVal;
                    }
                    else if (TryParseYamlValue(line, "description", out var descVal))
                    {
                        description = descVal;
                    }
                }

                // Body is everything after the closing ---
                var bodyLines = lines[(closingIndex + 1)..];
                var body = string.Join("\n", bodyLines).Trim();

                return new SkillDefinition(
                    string.IsNullOrWhiteSpace(name) ? directoryName : name,
                    description,
                    body);
            }
        }

        // No valid frontmatter — name = directory name, description = "", body = whole file.
        return new SkillDefinition(directoryName, string.Empty, content.Trim());
    }

    private static bool TryParseYamlValue(string line, string key, out string value)
    {
        value = string.Empty;
        var prefix = key + ":";
        if (!line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex < 0 || colonIndex >= line.Length - 1)
        {
            return false;
        }

        value = line[(colonIndex + 1)..].Trim().Trim('"').Trim('\'');
        return true;
    }
}
