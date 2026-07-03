using Coda.Mcp;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Lists and inspects configured MCP servers. Read-only for now (<c>list</c> / <c>info</c>);
/// add/edit/remove and live start/stop arrive in later phases.
/// </summary>
public sealed class McpCommand : ISlashCommand
{
    public string Name => "mcp";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "List and inspect MCP servers";

    public CommandHelp Help => new(
        "/mcp [info <name>]",
        Description: "Show configured MCP servers and their connection status, or inspect one server's description and tools.",
        Options:
        [
            ("(no args) / list", "list configured servers (name, scope, transport, status)"),
            ("info <name>", "show a server's description, transport, status, and tools"),
        ],
        Examples: ["/mcp", "/mcp info github"]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        var statuses = BuildStatuses(context);
        var sub = args.Count > 0 ? args[0].ToLowerInvariant() : "list";

        switch (sub)
        {
            case "list":
                context.Console.MarkupLine(Markup.Escape(McpView.FormatList(statuses)));
                break;

            case "info":
                if (args.Count < 2)
                {
                    context.Console.MarkupLine("Usage: /mcp info <name>");
                    break;
                }

                var status = statuses.FirstOrDefault(s => string.Equals(s.Entry.Name, args[1], StringComparison.Ordinal));
                context.Console.MarkupLine(status is null
                    ? Markup.Escape($"Unknown MCP server '{args[1]}'. Run /mcp to list configured servers.")
                    : Markup.Escape(McpView.FormatInfo(status)));
                break;

            default:
                context.Console.MarkupLine(Markup.Escape($"Unknown /mcp subcommand '{sub}'. Try /mcp or /mcp info <name>."));
                break;
        }

        return Task.FromResult(CommandResult.Continue);
    }

    /// <summary>Gather the display snapshot from the configured entries + the live MCP manager.</summary>
    private static IReadOnlyList<McpServerStatus> BuildStatuses(CommandContext context)
    {
        var entries = McpConfig.LoadEntries(context.Session.WorkingDirectory);
        var manager = context.Mcp;
        var result = new List<McpServerStatus>(entries.Count);
        foreach (var entry in entries)
        {
            var tools = (manager?.ServerTools(entry.Name) ?? [])
                .Select(t => new McpToolLine(t.Name, t.Description))
                .ToList();
            result.Add(new McpServerStatus(
                entry,
                Connected: manager?.IsServerConnected(entry.Name) ?? false,
                Info: manager?.ServerInfoFor(entry.Name),
                Tools: tools));
        }

        return result;
    }
}
