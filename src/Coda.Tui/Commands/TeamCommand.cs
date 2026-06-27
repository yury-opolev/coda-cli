using Coda.Agent.Teams;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// /team command: inspect and manage the running team.
///
/// Subcommands:
///   /team              — list current team + members + task summary
///   /team list         — same as no args
///   /team stop <name>  — kill the named teammate
///   /team delete       — delete the whole team
///
/// Reads team state from a TeamStore over the user teams dir.
/// When a live TeamManager is available (via SessionState extension), stop/delete
/// operate through it; otherwise they display a hint.
///
/// Test seam: pass <paramref name="userTeamsDirOverride"/> to use a temp dir instead
/// of the real ~/.coda/teams.
/// </summary>
public sealed class TeamCommand : ISlashCommand
{
    private readonly string? userTeamsDirOverride;

    /// <summary>Production constructor: resolves the user teams dir from ~/.coda/teams.</summary>
    public TeamCommand()
    {
    }

    /// <summary>Test-seam constructor: uses the supplied dir instead of ~/.coda/teams.</summary>
    public TeamCommand(string userTeamsDirOverride)
    {
        this.userTeamsDirOverride = userTeamsDirOverride;
    }

    public string Name => "team";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Inspect and manage the running agent team";

    public CommandHelp Help => new(
        "/team [list | stop <name> | delete]",
        Description: "Inspect and manage agent teams stored under ~/.coda/teams. With no argument (or 'list'), displays each team's name, description, and members with active/idle status. 'stop' signals a named member to stop; 'delete' removes the whole team.",
        Options:
        [
            ("(no args) | list", "list teams and their members with active/idle status"),
            ("stop <name>", "stop the named team member"),
            ("delete", "delete the current team"),
        ],
        Examples: ["/team", "/team list", "/team stop worker-1", "/team delete"]);

    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var teamsDir = this.userTeamsDirOverride
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".coda",
                "teams");

        var subcommand = args.Count > 0 ? args[0].ToLowerInvariant() : "list";

        switch (subcommand)
        {
            case "list":
                this.ListTeams(context, teamsDir);
                break;

            case "stop":
                await this.StopMemberAsync(context, args, teamsDir, cancellationToken);
                break;

            case "delete":
                await this.DeleteTeamAsync(context, teamsDir, cancellationToken);
                break;

            default:
                this.ShowUsage(context);
                break;
        }

        return CommandResult.Continue;
    }

    // ── Subcommand handlers ───────────────────────────────────────────────────

    private void ListTeams(CommandContext context, string teamsDir)
    {
        if (!Directory.Exists(teamsDir))
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                "No active team. Use [bold]team_create[/] to start one."));
            return;
        }

        var store = new TeamStore(teamsDir);
        var teams = store.ListTeams();

        if (teams.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                "No active team. Use [bold]team_create[/] to start one."));
            return;
        }

        foreach (var teamName in teams)
        {
            var teamFile = store.Read(teamName);
            if (teamFile is null)
            {
                continue;
            }

            var escapedTeamName = Markup.Escape(teamFile.Name);
            context.Console.MarkupLine($"[bold]{escapedTeamName}[/]");

            if (teamFile.Description is not null)
            {
                context.Console.MarkupLine(Theme.DimMarkup(Markup.Escape(teamFile.Description)));
            }

            context.Console.MarkupLine("[grey50]Members:[/]");

            foreach (var member in teamFile.Members)
            {
                var status = member.IsActive ? "[green]active[/]" : "[grey50]idle[/]";
                var escapedName = Markup.Escape(member.Name);
                context.Console.MarkupLine($"  {escapedName} {status}");
            }
        }
    }

    private async Task StopMemberAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        string teamsDir,
        CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            context.Console.MarkupLine(Theme.WarnMarkup("Usage: /team stop <member-name>"));
            return;
        }

        var memberName = args[1];

        if (!Directory.Exists(teamsDir))
        {
            context.Console.MarkupLine(Theme.WarnMarkup("No active team."));
            return;
        }

        var store = new TeamStore(teamsDir);
        var teams = store.ListTeams();

        if (teams.Count == 0)
        {
            context.Console.MarkupLine(Theme.WarnMarkup("No active team."));
            return;
        }

        // Find the member in the first (active) team.
        var teamName = teams[0];
        var teamFile = store.Read(teamName);
        if (teamFile is null)
        {
            context.Console.MarkupLine(Theme.WarnMarkup("Team not found."));
            return;
        }

        var member = teamFile.Members.FirstOrDefault(m =>
            string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));

        if (member is null)
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"No member named '{Markup.Escape(memberName)}' in team '{Markup.Escape(teamName)}'."));
            return;
        }

        // Attempt to kill via a live TeamManager constructed over the same dir.
        var manager = new TeamManager(teamsDir, (_, _) => throw new InvalidOperationException());
        await using (manager)
        {
            manager.CreateTeam(teamName, null);
            var killed = manager.Kill(member.AgentId);

            var escapedMemberName = Markup.Escape(member.Name);
            if (killed)
            {
                context.Console.MarkupLine($"Stopped teammate [bold]{escapedMemberName}[/].");
            }
            else
            {
                context.Console.MarkupLine(Theme.DimMarkup(
                    $"Member {escapedMemberName} is not running (may already be idle)."));
            }
        }

        await Task.CompletedTask;
    }

    private async Task DeleteTeamAsync(
        CommandContext context,
        string teamsDir,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(teamsDir))
        {
            context.Console.MarkupLine(Theme.WarnMarkup("No active team."));
            return;
        }

        var store = new TeamStore(teamsDir);
        var teams = store.ListTeams();

        if (teams.Count == 0)
        {
            context.Console.MarkupLine(Theme.WarnMarkup("No active team."));
            return;
        }

        var teamName = teams[0];
        var manager = new TeamManager(teamsDir, (_, _) => throw new InvalidOperationException());
        await using (manager)
        {
            manager.CreateTeam(teamName, null);
            var (ok, msg) = await manager.DeleteTeamAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);

            if (ok)
            {
                context.Console.MarkupLine($"Team [bold]{Markup.Escape(teamName)}[/] deleted.");
            }
            else
            {
                context.Console.MarkupLine(Theme.ErrorMarkup(Markup.Escape(msg)));
            }
        }
    }

    private void ShowUsage(CommandContext context)
    {
        context.Console.MarkupLine("[grey50]Usage:[/]");
        context.Console.MarkupLine("  [bold]/team[/]             — list team + members");
        context.Console.MarkupLine("  [bold]/team list[/]        — same as above");
        context.Console.MarkupLine("  [bold]/team stop[/] [grey50]<name>[/] — kill a teammate");
        context.Console.MarkupLine("  [bold]/team delete[/]      — delete the team");
    }
}
