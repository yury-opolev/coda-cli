using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Coda.Agent.Tools;

/// <summary>Regex content search across files (the Grep tool). Read-only.</summary>
public sealed class GrepTool : ITool
{
    private const int MaxMatches = 200;

    public string Name => "grep";

    public string Description => "Search file contents by regular expression. Optionally filter files by a glob. Returns path:line: match.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"},"glob":{"type":"string","description":"Optional file glob filter, e.g. **/*.cs"}},"required":["pattern"]}
        """;

    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var pattern = ToolInput.GetString(input, "pattern");
        if (string.IsNullOrEmpty(pattern))
        {
            return new ToolResult("Missing required 'pattern'.", IsError: true);
        }

        Regex regex;
        try
        {
            // Match timeout guards against catastrophic backtracking from a
            // model-supplied pattern (e.g. (a+)+$).
            regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return new ToolResult($"Invalid regex: {ex.Message}", IsError: true);
        }

        var pathArg = ToolInput.GetString(input, "path");
        string baseDir;
        if (string.IsNullOrEmpty(pathArg))
        {
            baseDir = context.WorkingDirectory;
        }
        else if (!ToolInput.TryResolveWithinRoot(context.WorkingDirectory, pathArg, out baseDir!, out var pathError, context.AllowOutsideWorkingDirectory))
        {
            return new ToolResult(pathError!, IsError: true);
        }

        if (!Directory.Exists(baseDir))
        {
            return new ToolResult($"Directory not found: {baseDir}", IsError: true);
        }

        var globArg = ToolInput.GetString(input, "glob");
        var globRegex = string.IsNullOrEmpty(globArg) ? null : GlobTool.GlobToRegex(globArg);

        var results = new StringBuilder();
        var matchCount = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (var file in Directory.EnumerateFiles(baseDir, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (globRegex is not null)
            {
                var rel = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                if (!globRegex.IsMatch(rel))
                {
                    continue;
                }
            }

            // Skip oversized and binary files (NUL byte in the first block).
            try
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.Length > 8_000_000 || await IsBinaryAsync(file, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                bool matched;
                try
                {
                    matched = regex.IsMatch(lines[i]);
                }
                catch (RegexMatchTimeoutException)
                {
                    return new ToolResult("Search pattern timed out (possible catastrophic backtracking).", IsError: true);
                }

                if (!matched)
                {
                    continue;
                }

                var rel = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                var text = lines[i].Trim();
                if (text.Length > 200)
                {
                    text = text[..200] + "…";
                }

                results.Append(rel).Append(':').Append(i + 1).Append(": ").Append(text).Append('\n');
                if (++matchCount >= MaxMatches)
                {
                    results.Append("… [more matches truncated]\n");
                    return new ToolResult(results.ToString().TrimEnd('\n'));
                }
            }
        }

        return new ToolResult(matchCount == 0 ? "No matches found." : results.ToString().TrimEnd('\n'));
    }

    private static async Task<bool> IsBinaryAsync(string file, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        await using var stream = File.OpenRead(file);
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0)
            {
                return true; // NUL byte → treat as binary
            }
        }

        return false;
    }
}
