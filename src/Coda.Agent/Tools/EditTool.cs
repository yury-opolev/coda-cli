using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>
/// In-place string replacement in a single file (the Edit tool). Replaces a unique
/// occurrence of <c>old_string</c> with <c>new_string</c>, or all occurrences when
/// <c>replace_all</c> is true. Mutating — requires permission. Contained to cwd.
/// </summary>
public sealed class EditTool : ITool
{
    public const string ToolName = "edit_file";

    public string Name => ToolName;

    public string Description => "Replace an exact string in a file. By default old_string must be unique; set replace_all to replace every occurrence.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string"},"old_string":{"type":"string"},"new_string":{"type":"string"},"replace_all":{"type":"boolean"}},"required":["path","old_string","new_string"]}
        """;

    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var path = ToolInput.GetString(input, "path");
        var oldString = ToolInput.GetString(input, "old_string");
        var newString = ToolInput.GetString(input, "new_string");
        if (string.IsNullOrEmpty(path) || oldString is null || newString is null)
        {
            return new ToolResult("Missing required 'path', 'old_string' and/or 'new_string'.", IsError: true);
        }

        var replaceAll = input.TryGetProperty("replace_all", out var ra) && ra.ValueKind == JsonValueKind.True;

        if (!ToolInput.TryResolveWithinRoot(context.WorkingDirectory, path, out var full, out var pathError, context.AllowOutsideWorkingDirectory))
        {
            return new ToolResult(pathError!, IsError: true);
        }

        if (!File.Exists(full))
        {
            return new ToolResult($"File not found: {full}", IsError: true);
        }

        if (oldString == newString)
        {
            return new ToolResult("old_string and new_string are identical.", IsError: true);
        }

        var content = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        var count = CountOccurrences(content, oldString);
        if (count == 0)
        {
            return new ToolResult("old_string was not found in the file.", IsError: true);
        }

        if (count > 1 && !replaceAll)
        {
            return new ToolResult($"old_string is not unique ({count} matches). Provide more context or set replace_all.", IsError: true);
        }

        var updated = replaceAll
            ? content.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(content, oldString, newString);

        await File.WriteAllTextAsync(full, updated, cancellationToken).ConfigureAwait(false);
        return new ToolResult($"Edited {full} ({(replaceAll ? count : 1)} replacement(s)).");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string content, string oldString, string newString)
    {
        var index = content.IndexOf(oldString, StringComparison.Ordinal);
        return index < 0 ? content : content[..index] + newString + content[(index + oldString.Length)..];
    }
}
