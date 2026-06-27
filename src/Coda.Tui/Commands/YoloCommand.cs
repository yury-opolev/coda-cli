using Coda.Agent;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Shortcut to switch to bypass ("yolo") permission mode — allow every tool without asking.</summary>
public sealed class YoloCommand : ISlashCommand
{
    public string Name => "yolo";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Allow all tools without asking (bypass permissions)";

    public CommandHelp Help => new(
        Usage: "/yolo",
        Description: "Switches the session to bypass-permissions mode so every tool runs without a confirmation " +
            "prompt. Takes effect immediately for the rest of the session. Use /permissions default to revert.",
        Examples:
        [
            "/yolo",
        ]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        context.Session.PermissionMode = PermissionMode.BypassPermissions;
        context.Console.MarkupLine(Theme.WarnMarkup("YOLO mode: tools now run without asking for permission.") + " " +
            Theme.DimMarkup("Switch back with /permissions default."));
        return Task.FromResult(CommandResult.Continue);
    }
}
