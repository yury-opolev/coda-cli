using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>Reads a UTF-8 text file (relative paths resolve against the working dir).</summary>
public sealed class ReadFileTool : ITool
{
    private const int MaxChars = 100_000;

    public string Name => "read_file";

    public string Description => "Read a UTF-8 text file and return its contents. The path may be relative to the working directory.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string","description":"File path to read"}},"required":["path"]}
        """;

    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var path = ToolInput.GetString(input, "path");
        if (string.IsNullOrEmpty(path))
        {
            return new ToolResult("Missing required 'path'.", IsError: true);
        }

        if (!ToolInput.TryResolveWithinRoot(context.WorkingDirectory, path, out var full, out var pathError))
        {
            return new ToolResult(pathError!, IsError: true);
        }

        if (!File.Exists(full))
        {
            return new ToolResult($"File not found: {full}", IsError: true);
        }

        var text = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        if (text.Length > MaxChars)
        {
            text = text[..MaxChars] + $"\n… [truncated, {text.Length} chars total]";
        }

        return new ToolResult(text);
    }
}
