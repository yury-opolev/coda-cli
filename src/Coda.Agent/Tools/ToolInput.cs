using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>Helpers for reading tool input + resolving paths against the working dir.</summary>
internal static class ToolInput
{
    public static string? GetString(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? GetInt(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var n)
            ? n
            : null;

    /// <summary>Resolve a (possibly relative) path against the working directory.</summary>
    public static string ResolvePath(string workingDirectory, string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(workingDirectory, path));

    /// <summary>
    /// True if <paramref name="fullPath"/> is the root or inside it. Resolves
    /// symlinks/junctions on the deepest existing path component so a reparse point
    /// inside cwd that points elsewhere can't be used to escape.
    /// </summary>
    public static bool IsWithinRoot(string root, string fullPath)
    {
        var rootFull = ResolveFinalTarget(Path.GetFullPath(root))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = ResolveFinalTarget(Path.GetFullPath(fullPath));
        return target.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolve the final link target of the deepest existing path component.</summary>
    private static string ResolveFinalTarget(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
            }

            if (File.Exists(path))
            {
                return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
            }

            // Not created yet (e.g. a new file): resolve through the parent directory.
            var parent = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(parent)
                ? path
                : Path.Combine(ResolveFinalTarget(parent), Path.GetFileName(path));
        }
        catch (IOException)
        {
            return path;
        }
        catch (UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>
    /// Resolve a path and confirm it stays within the working directory. Blocks the
    /// model from reading/writing arbitrary files (e.g. credential stores) outside cwd.
    /// </summary>
    /// <param name="allowOutsideRoot">
    /// When true (bypass/"yolo" mode), the containment check is skipped so the model
    /// may read/write anywhere the process can — the path is still resolved to a full
    /// path. Defaults to false so every non-bypass caller keeps the cwd sandbox.
    /// </param>
    public static bool TryResolveWithinRoot(string root, string path, out string fullPath, out string? error, bool allowOutsideRoot = false)
    {
        fullPath = ResolvePath(root, path);
        if (!allowOutsideRoot && !IsWithinRoot(root, fullPath))
        {
            error = $"Path '{path}' is outside the working directory and is not allowed. "
                + "Switch to bypass permissions (/yolo) to allow paths outside the working directory.";
            return false;
        }

        error = null;
        return true;
    }
}
