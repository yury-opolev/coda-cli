using Coda.Agent.Goals;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Shows, sets, or clears the autonomous goal. The goal persists in <see cref="SessionState"/>
/// and is mapped into each run's <see cref="Coda.Sdk.SessionOptions"/> by <see cref="Coda.Tui.Agent.AgentRunner"/>,
/// so it takes effect on the next user turn.
/// </summary>
public sealed class GoalCommand : ISlashCommand
{
    public string Name => "goal";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show, set, or clear the autonomous goal";

    public CommandHelp Help => new(
        Usage: "/goal [--timeout <duration>] [--max-turns <n>] [<text...>]",
        Description: "Sets an autonomous goal that persists in the session and is passed to the agent on every " +
            "subsequent turn until cleared. The agent will keep running autonomously until the goal is met, the " +
            "timeout expires, or the max-turns limit is reached. Run /goal with no arguments to show the active goal.",
        Options:
        [
            ("<text>", "Free-form goal text to set, e.g. \"all tests pass\". Multi-word input is always treated as goal text " +
                "unless the sole word is a keyword (off/clear/stop/none/current/status)."),
            ("off | clear | stop | none", "Clear the active goal and reset budget overrides. Only recognized as sole argument."),
            ("current | status", "Show the active goal (same as no arguments). Only recognized as sole argument."),
            ("--timeout <duration>", "Max wall-clock time for the goal run. Accepts 30s, 10m, 2h, 1d, or hh:mm:ss. " +
                "Defaults to the value in settings. Alias: -t."),
            ("--max-turns <n>", "Max agent continuations (positive integer). Defaults to the value in settings. " +
                "Alias: --max-continuations."),
        ],
        Examples:
        [
            "/goal all tests pass",
            "/goal --timeout 30m --max-turns 50 ship the feature",
            "/goal off",
            "/goal",
        ]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        // Sub-command keywords are only recognized when they are the SOLE argument, so a
        // legitimate goal like "/goal turn off the feature" or "/goal status report" is set
        // as text rather than swallowed by the show/clear branches.
        var sole = args.Count == 1 ? args[0].ToLowerInvariant() : null;

        if (args.Count == 0 || sole is "current" or "status")
        {
            this.ShowCurrent(context);
            return Task.FromResult(CommandResult.Continue);
        }

        if (sole is "off" or "clear" or "stop" or "none")
        {
            context.Session.Goal = null;
            context.Session.GoalMaxDuration = null;
            context.Session.GoalMaxContinuations = null;
            context.Console.MarkupLine(Theme.AccentMarkup("Goal cleared."));
            return Task.FromResult(CommandResult.Continue);
        }

        return Task.FromResult(this.SetGoal(context, args));
    }

    private CommandResult SetGoal(CommandContext context, IReadOnlyList<string> args)
    {
        TimeSpan? timeout = null;
        int? maxTurns = null;
        var textTokens = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (token is "--timeout" or "-t")
            {
                if (i + 1 >= args.Count)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup("--timeout requires a value, e.g. /goal --timeout 30m do something."));
                    return CommandResult.Continue;
                }

                i++;
                if (!DurationParser.TryParse(args[i], out var parsed))
                {
                    context.Console.MarkupLine(Theme.WarnMarkup(
                        $"Invalid duration '{args[i]}'. Use a suffix: 30s, 10m, 2h, 1d, or hh:mm:ss."));
                    return CommandResult.Continue;
                }

                timeout = parsed;
                continue;
            }

            if (token is "--max-turns" or "--max-continuations")
            {
                if (i + 1 >= args.Count)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup("--max-turns requires a value, e.g. /goal --max-turns 50 do something."));
                    return CommandResult.Continue;
                }

                i++;
                if (!int.TryParse(args[i], out var turns) || turns <= 0)
                {
                    context.Console.MarkupLine(Theme.WarnMarkup(
                        $"Invalid turn count '{args[i]}'. Provide a positive integer."));
                    return CommandResult.Continue;
                }

                maxTurns = turns;
                continue;
            }

            textTokens.Add(token);
        }

        var goalText = string.Join(" ", textTokens).Trim();
        if (string.IsNullOrEmpty(goalText))
        {
            context.Console.MarkupLine(Theme.WarnMarkup("Provide a goal, e.g. /goal all tests pass"));
            return CommandResult.Continue;
        }

        context.Session.Goal = goalText;
        context.Session.GoalMaxDuration = timeout;
        context.Session.GoalMaxContinuations = maxTurns;

        context.Console.MarkupLine(
            $"Goal set {Theme.DimMarkup("(persists until /goal off)")}: {Theme.AccentMarkup(goalText)}");
        context.Console.MarkupLine(
            Theme.DimMarkup("Budget: " + this.FormatBudget(timeout, maxTurns)));
        context.Console.MarkupLine(
            Theme.DimMarkup("Takes effect on the next message."));

        return CommandResult.Continue;
    }

    private void ShowCurrent(CommandContext context)
    {
        var goal = context.Session.Goal;
        if (string.IsNullOrEmpty(goal))
        {
            context.Console.MarkupLine($"No goal set. {Theme.DimMarkup("Use /goal <text> to set one.")}");
            return;
        }

        context.Console.MarkupLine($"Active goal: {Theme.AccentMarkup(goal)}");
        context.Console.MarkupLine(
            Theme.DimMarkup("Budget: " + this.FormatBudget(context.Session.GoalMaxDuration, context.Session.GoalMaxContinuations)));
    }

    private string FormatBudget(TimeSpan? timeout, int? maxTurns)
    {
        if (timeout is null && maxTurns is null)
        {
            return "(defaults)";
        }

        var parts = new List<string>();
        if (timeout is not null)
        {
            parts.Add($"timeout {timeout.Value:c}");
        }

        if (maxTurns is not null)
        {
            parts.Add($"max-turns {maxTurns.Value}");
        }

        return string.Join(", ", parts);
    }
}
