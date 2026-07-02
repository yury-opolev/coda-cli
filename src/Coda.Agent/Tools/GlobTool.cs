using System.Text.Json;
using System.Text.RegularExpressions;

namespace Coda.Agent.Tools;

/// <summary>Fast file pattern matching (the Glob tool), e.g. <c>**/*.cs</c>. Read-only.</summary>
public sealed class GlobTool : ITool
{
    private const int MaxResults = 500;

    public string Name => "glob";

    public string Description => "Find files by glob pattern (e.g. **/*.cs or src/*.json), relative to the working directory.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string","description":"Base directory (optional)"}},"required":["pattern"]}
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var pattern = ToolInput.GetString(input, "pattern");
        if (string.IsNullOrEmpty(pattern))
        {
            return Task.FromResult(new ToolResult("Missing required 'pattern'.", IsError: true));
        }

        var pathArg = ToolInput.GetString(input, "path");
        string baseDir;
        if (string.IsNullOrEmpty(pathArg))
        {
            baseDir = context.WorkingDirectory;
        }
        else if (!ToolInput.TryResolveWithinRoot(context.WorkingDirectory, pathArg, out baseDir!, out var pathError, context.AllowOutsideWorkingDirectory))
        {
            return Task.FromResult(new ToolResult(pathError!, IsError: true));
        }

        if (!Directory.Exists(baseDir))
        {
            return Task.FromResult(new ToolResult($"Directory not found: {baseDir}", IsError: true));
        }

        var regex = GlobToRegex(pattern);
        var matches = new List<string>();
        foreach (var file in EnumerateFilesSafe(baseDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            if (regex.IsMatch(rel))
            {
                matches.Add(rel);
                if (matches.Count >= MaxResults)
                {
                    break;
                }
            }
        }

        matches.Sort(StringComparer.OrdinalIgnoreCase);
        var listing = matches.Count == 0 ? "(no matches)" : string.Join('\n', matches);
        return Task.FromResult(new ToolResult(listing));
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(root, "*", options);
    }

    /// <summary>Translate a glob to a regex matching the forward-slash relative path.</summary>
    internal static Regex GlobToRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var builder = new System.Text.StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                    {
                        i++; // consume the second '*'
                        if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                        {
                            // **/ matches zero or more leading path segments
                            builder.Append("(?:.*/)?");
                            i++; // consume the slash
                        }
                        else
                        {
                            builder.Append(".*"); // trailing ** matches anything
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
