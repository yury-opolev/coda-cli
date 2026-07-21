using Coda.Tui.Repl;

namespace Coda.Tui.Ui;

/// <summary>
/// Classifies a submitted line as one of the small set of permission slash commands that are safe to
/// run out-of-band while a normal turn is still in flight: <c>/yolo</c>, <c>/permissions [mode]</c>, and
/// the <c>/mode [mode]</c> alias. Classification defers entirely to <see cref="CommandParser"/> /
/// <see cref="ParsedInput"/> (which already lowercases the name and trims surrounding whitespace), so
/// prompts, bash lines, and every other slash command are never treated as live permission commands.
/// </summary>
internal static class LivePermissionCommands
{
    /// <summary>Whether <paramref name="parsed"/> is a permission command safe to apply mid-turn.</summary>
    internal static bool IsLivePermissionCommand(ParsedInput parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.Kind != ParsedInputKind.Slash)
        {
            return false;
        }

        // Names are already lowercased by the parser; keep the alias set in one place next to the
        // PermissionsCommand aliases ("permissions" + "mode") and the /yolo shortcut.
        return parsed.Name is "yolo" or "permissions" or "mode";
    }
}
