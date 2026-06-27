using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>Creates or overwrites a UTF-8 text file. Mutating — requires permission.</summary>
public sealed class WriteFileTool : ITool
{
    public const string ToolName = "write_file";

    public string Name => ToolName;

    public string Description => "Create or overwrite a UTF-8 text file with the given content.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string","description":"File path to write"},"content":{"type":"string","description":"Full file content"}},"required":["path","content"]}
        """;

    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var path = ToolInput.GetString(input, "path");
        var content = ToolInput.GetString(input, "content");
        if (string.IsNullOrEmpty(path) || content is null)
        {
            return new ToolResult("Missing required 'path' and/or 'content'.", IsError: true);
        }

        if (!ToolInput.TryResolveWithinRoot(context.WorkingDirectory, path, out var full, out var pathError))
        {
            return new ToolResult(pathError!, IsError: true);
        }

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(full, content, cancellationToken).ConfigureAwait(false);
        return new ToolResult($"Wrote {content.Length} chars to {full}");
    }
}
