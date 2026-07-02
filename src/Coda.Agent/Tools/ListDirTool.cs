using System.Text;
using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>Lists the entries of a directory (defaults to the working directory).</summary>
public sealed class ListDirTool : ITool
{
    private const int MaxEntries = 200;

    public string Name => "list_dir";

    public string Description => "List files and subdirectories of a directory (defaults to the working directory).";

    public string InputSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string","description":"Directory path (optional, defaults to cwd)"}}}
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var path = ToolInput.GetString(input, "path");
        string dir;
        if (string.IsNullOrEmpty(path))
        {
            dir = context.WorkingDirectory;
        }
        else if (!ToolInput.TryResolveWithinRoot(context.WorkingDirectory, path, out dir!, out var pathError, context.AllowOutsideWorkingDirectory))
        {
            return Task.FromResult(new ToolResult(pathError!, IsError: true));
        }

        if (!Directory.Exists(dir))
        {
            return Task.FromResult(new ToolResult($"Directory not found: {dir}", IsError: true));
        }

        var builder = new StringBuilder();
        var count = 0;
        foreach (var entry in Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (count++ >= MaxEntries)
            {
                break;
            }

            builder.Append(Path.GetFileName(entry)).Append('/').Append('\n');
        }

        foreach (var entry in Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (count++ >= MaxEntries)
            {
                break;
            }

            builder.Append(Path.GetFileName(entry)).Append('\n');
        }

        var listing = builder.Length == 0 ? "(empty directory)" : builder.ToString().TrimEnd('\n');
        return Task.FromResult(new ToolResult(listing));
    }
}
