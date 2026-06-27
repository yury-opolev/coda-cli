using System.Text;

namespace Coda.Agent;

/// <summary>
/// Loads CLAUDE.md project-instruction files and concatenates them into one block
/// for the system prompt. Priority (lowest first, so highest-priority text comes
/// last): the user file (~/.claude/CLAUDE.md), then each ancestor directory from
/// the filesystem root down to the working directory. Lines that are a lone
/// <c>@relative/path</c> are replaced inline by that file's content (one level).
/// </summary>
public static class ProjectContext
{
    private const int MaxBytes = 32 * 1024;

    public static string? Load(string workingDirectory, string? userClaudeDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var userDir = userClaudeDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

        var files = new List<string>();

        // 1. User global (lowest priority).
        var userFile = Path.Combine(userDir, "CLAUDE.md");
        if (File.Exists(userFile))
        {
            files.Add(userFile);
        }

        // 2. Ancestor dirs from root down to cwd (cwd = highest priority, last).
        foreach (var dir in AncestorsRootToCwd(Path.GetFullPath(workingDirectory)))
        {
            var f = Path.Combine(dir, "CLAUDE.md");
            if (File.Exists(f))
            {
                files.Add(f);
            }
        }

        if (files.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var file in files)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(ReadWithImports(file));
            if (builder.Length >= MaxBytes)
            {
                break;
            }
        }

        var text = builder.ToString();
        if (text.Length > MaxBytes)
        {
            text = text[..MaxBytes] + "\n\n[project context truncated]";
        }

        return text;
    }

    private static IEnumerable<string> AncestorsRootToCwd(string workingDirectory)
    {
        var chain = new List<string>();
        var dir = new DirectoryInfo(workingDirectory);
        while (dir is not null)
        {
            chain.Add(dir.FullName);
            dir = dir.Parent;
        }

        chain.Reverse(); // root first, cwd last
        return chain;
    }

    private static string ReadWithImports(string file)
    {
        string content;
        try
        {
            content = File.ReadAllText(file);
        }
        catch
        {
            return string.Empty;
        }

        var dir = Path.GetDirectoryName(file)!;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 1 && trimmed[0] == '@' && !trimmed.Contains(' '))
            {
                var importPath = Path.GetFullPath(Path.Combine(dir, trimmed[1..]));
                var dirWithSep = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
                if (importPath.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase) && File.Exists(importPath))
                {
                    try
                    {
                        output.Append(File.ReadAllText(importPath));
                        output.Append('\n');
                        continue;
                    }
                    catch
                    {
                        // fall through to emit the line literally
                    }
                }
            }

            output.Append(line);
            output.Append('\n');
        }

        return output.ToString().TrimEnd();
    }
}
