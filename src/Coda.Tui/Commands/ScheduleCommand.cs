using Coda.Agent.Scheduling;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Schedule;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Prints a read-only, sanitized textual snapshot of the session's scheduled tasks. Shares the same
/// live <see cref="IScheduleControl"/> as the interactive TUI (via
/// <see cref="CommandContext.ScheduleControlProvider"/>), so the plain, Spectre, and legacy console
/// contexts all print the exact same definitions the browser shows. In the interactive Terminal.Gui
/// shell the bare <c>/schedule</c> submission is intercepted before dispatch and opens the live
/// browser instead (<see cref="ScheduleBrowserController.IsOpenRequest"/>); this command prints the
/// snapshot in the other console contexts. This command never mutates schedule state — create and
/// delete are done through the interactive browser or the <c>schedule_*</c> model tools.
/// </summary>
public sealed class ScheduleCommand : ISlashCommand
{
    public string Name => "schedule";

    public IReadOnlyList<string> Aliases => [];

    public string Summary =>
        "List the session's scheduled tasks (opens the live browser in the interactive TUI)";

    public CommandHelp Help => new(
        "/schedule",
        Description: "List the session's scheduled tasks as a read-only textual snapshot. " +
            "In the interactive TUI, /schedule opens the live schedule browser where you can " +
            "create and delete definitions. The plain, Spectre, and legacy contexts print a " +
            "snapshot. Manage schedules through the schedule_* tools or the interactive browser.");

    public Task<CommandResult> ExecuteAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        // /schedule takes no arguments. Show a clear usage message rather than silently ignoring.
        if (args.Count > 0)
        {
            context.Console.MarkupLine(Theme.WarnMarkup("/schedule does not take arguments."));
            context.Console.MarkupLine(Theme.DimMarkup("Usage: /schedule — list the session's scheduled tasks."));
            return Task.FromResult(CommandResult.Continue);
        }

        var schedules = context.ScheduleControlProvider?.Invoke()?.List() ?? [];
        foreach (var line in RenderLines(schedules))
        {
            context.Console.MarkupLine(line);
        }

        return Task.FromResult(CommandResult.Continue);
    }

    /// <summary>
    /// Pure and separately testable. Renders one line per <see cref="ScheduledTaskReadModel"/>. All
    /// dynamic fields are sanitized via <see cref="TerminalTextSanitizer.SanitizeSingleLine"/> and
    /// then escaped via <see cref="Markup.Escape"/> (through <see cref="Theme"/> helpers) so a
    /// definition can never inject raw ANSI or Spectre markup into the output.
    /// </summary>
    internal static IReadOnlyList<string> RenderLines(IReadOnlyList<ScheduledTaskReadModel> schedules)
    {
        if (schedules.Count == 0)
        {
            return [Theme.DimMarkup("No scheduled tasks.")];
        }

        var lines = new List<string>(schedules.Count + 2);
        lines.Add(Theme.BoldMarkup("Scheduled Tasks"));

        foreach (var s in schedules)
        {
            lines.Add(RenderRow(s));
        }

        lines.Add(Theme.DimMarkup("Read-only snapshot. Manage schedules with the schedule_* tools or /schedule browser."));
        return lines;
    }

    private static string RenderRow(ScheduledTaskReadModel s)
    {
        var statusMarker = s.State switch
        {
            ScheduleRuntimeStatus.Running => Theme.AccentMarkup("●"),
            ScheduleRuntimeStatus.Pending => Theme.DimMarkup("○"),
            _ => Theme.DimMarkup("■"),
        };

        var id = TerminalTextSanitizer.SanitizeSingleLine(s.Id);
        var name = s.Name is { Length: > 0 } n
            ? $" \"{TerminalTextSanitizer.SanitizeSingleLine(n)}\""
            : string.Empty;
        var rule = TerminalTextSanitizer.SanitizeSingleLine(s.Rule);
        var tz = TerminalTextSanitizer.SanitizeSingleLine(s.TimeZone);
        var nextUtc = s.NextRunUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
        var nextLocal = TerminalTextSanitizer.SanitizeSingleLine(s.NextRunLocal);
        var localLabel = TerminalTextSanitizer.SanitizeSingleLine(s.NextRunLocalLabel);
        var state = s.State.ToString();

        var outcomeStr = s.LastOutcome is { } lo
            ? $"  last: {TerminalTextSanitizer.SanitizeSingleLine(lo.Outcome.ToString())}"
            : string.Empty;

        return $"  {statusMarker} {Theme.DimMarkup(id)}{Theme.DimMarkup(name)}  " +
               $"{Theme.AccentMarkup(rule)}  {Theme.DimMarkup(tz)}  " +
               $"next {Theme.BoldMarkup(nextUtc)} UTC ({Theme.DimMarkup(nextLocal)} {Theme.DimMarkup(localLabel)})  " +
               $"{Theme.DimMarkup(state)}{Theme.DimMarkup(outcomeStr)}";
    }
}
