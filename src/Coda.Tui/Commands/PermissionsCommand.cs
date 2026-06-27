using Coda.Agent;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Shows or sets the tool-permission mode.</summary>
public sealed class PermissionsCommand : ISlashCommand
{
    public string Name => "permissions";

    public IReadOnlyList<string> Aliases => ["mode"];

    public string Summary => "Show or set the tool-permission mode";

    public CommandHelp Help => new(
        Usage: "/permissions [<mode>]",
        Description: "Shows the current tool-permission mode or switches to a different one. " +
            "The mode controls whether the agent asks before running each tool. " +
            "With no argument, prints the active mode and a description of all modes.",
        Options:
        [
            ("default", "Ask for confirmation before each tool call (the safe default)."),
            ("acceptEdits", "Auto-approve file edits without prompting; still asks for other tools."),
            ("plan", "Read-only mode — prevents any tool that writes or executes."),
            ("bypass", "Allow every tool without asking (same as /yolo). Aliases: bypassPermissions, yolo."),
        ],
        Examples:
        [
            "/permissions",
            "/permissions acceptEdits",
            "/permissions default",
            "/permissions bypass",
        ]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Count == 0)
        {
            context.Console.MarkupLine($"Permission mode: {Theme.AccentMarkup(context.Session.PermissionMode.ToString())}");
            context.Console.MarkupLine(Theme.DimMarkup("Modes: default (ask), acceptEdits (auto-edit), plan (read-only), bypass (yolo: allow all)"));
            return Task.FromResult(CommandResult.Continue);
        }

        if (!TryParseMode(args[0], out var mode))
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Unknown mode '{args[0]}'. Use: default | acceptEdits | plan | bypass"));
            return Task.FromResult(CommandResult.Continue);
        }

        context.Session.PermissionMode = mode;
        var note = mode == PermissionMode.BypassPermissions
            ? Theme.WarnMarkup(" (allows every tool without asking)")
            : string.Empty;
        context.Console.MarkupLine($"Permission mode is now {Theme.AccentMarkup(mode.ToString())}.{note}");
        return Task.FromResult(CommandResult.Continue);
    }

    internal static bool TryParseMode(string value, out PermissionMode mode)
    {
        switch (value.ToLowerInvariant())
        {
            case "default": mode = PermissionMode.Default; return true;
            case "acceptedits" or "accept-edits" or "edits": mode = PermissionMode.AcceptEdits; return true;
            case "plan": mode = PermissionMode.Plan; return true;
            case "bypass" or "bypasspermissions" or "yolo": mode = PermissionMode.BypassPermissions; return true;
            default: mode = PermissionMode.Default; return false;
        }
    }
}
