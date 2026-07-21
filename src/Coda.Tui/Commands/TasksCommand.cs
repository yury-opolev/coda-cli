using System.Globalization;
using Coda.Agent.Tasks;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Tasks;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Prints a read-only, sanitized textual snapshot of the session's background tasks. Shares the same
/// live <see cref="TaskManager"/> as the interactive TUI (via <see cref="CommandContext.TaskManagerProvider"/>),
/// so the plain, Spectre, and legacy console contexts all print the exact same tasks the browser shows.
/// In the interactive Terminal.Gui shell the bare <c>/tasks</c> submission is intercepted before dispatch and
/// opens the live browser instead; this command prints the snapshot in the other console contexts.
/// <c>coda serve</c> does not run this slash command and prints no snapshot; it instead exposes equivalent
/// non-UI task inspection and control through the <c>task_*</c> model tools. This command never mutates task
/// state — steering, backgrounding, and stopping are done through the <c>task_*</c> model tools.
/// </summary>
public sealed class TasksCommand : ISlashCommand
{
    // Keep description columns readable and the row width stable in narrow terminals. Measured in terminal
    // display cells (not grapheme clusters), so wide CJK and emoji are truncated by the space they occupy.
    private const int MaxDescriptionCells = 80;

    private readonly TimeProvider _time;

    public TasksCommand()
        : this(TimeProvider.System)
    {
    }

    internal TasksCommand(TimeProvider time) => _time = time;

    public string Name => "tasks";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "List the session's background tasks (opens the live browser in the interactive TUI)";

    public CommandHelp Help => new(
        "/tasks",
        Description: "List the session's background tasks and their status as a read-only textual snapshot. " +
            "In the interactive TUI, /tasks opens the live task browser to attach to, steer, and stop tasks, and " +
            "Ctrl+B sends a running foreground shell to the background. The plain, Spectre, and legacy contexts " +
            "print the same snapshot. coda serve does not run /tasks and prints no snapshot; it offers equivalent " +
            "non-UI task inspection and control through the task_* tools. Manage tasks through the task_* tools.");

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        // /tasks takes no arguments. Rather than silently printing a snapshot (which would hide a typo or a
        // misused flag), show a clear usage/unsupported-args message and stop. The bare command still prints
        // the snapshot below.
        if (args.Count > 0)
        {
            context.Console.MarkupLine(Theme.WarnMarkup("/tasks does not take arguments."));
            context.Console.MarkupLine(Theme.DimMarkup("Usage: /tasks — list the session's background tasks."));
            return Task.FromResult(CommandResult.Continue);
        }

        // Live provider (never a throwaway CodaSession). Null/before-first-turn renders the empty notice.
        var tasks = context.TaskManagerProvider?.Invoke()?.List() ?? [];
        foreach (var line in RenderLines(tasks, _time))
        {
            context.Console.MarkupLine(line);
        }

        return Task.FromResult(CommandResult.Continue);
    }

    /// <summary>
    /// Pure and separately testable. Projects the flat snapshot into the browser's active parent/child
    /// hierarchy and recent-terminal history, then renders one line per task. Every dynamic field is first
    /// stripped of ANSI/OSC/control sequences (<see cref="TerminalTextSanitizer.Sanitize"/>); the compact
    /// description additionally collapses to a single line (<see cref="TerminalTextSanitizer.SanitizeSingleLine"/>)
    /// and is truncated by terminal display cells so it can never split or spoof a hierarchy row. Fields are
    /// then colored through <see cref="Theme"/>, whose helpers run <c>Markup.Escape</c>, so a task can never
    /// inject raw ANSI or Spectre markup. <paramref name="time"/> is the clock seam for a running task's live
    /// duration; sub-minute durations format with <see cref="CultureInfo.InvariantCulture"/> (a period decimal).
    /// </summary>
    internal static IReadOnlyList<string> RenderLines(IReadOnlyList<TaskSnapshot> tasks, TimeProvider time)
    {
        if (tasks.Count == 0)
        {
            return [Theme.DimMarkup("No background tasks.")];
        }

        var projection = TaskListProjector.Project(tasks);
        var lines = new List<string>(tasks.Count + 3);

        if (projection.Active.Count > 0)
        {
            lines.Add(Theme.BoldMarkup("Tasks"));
            foreach (var row in projection.Active)
            {
                lines.Add(RenderRow(row, time));
            }
        }

        if (projection.Recent.Count > 0)
        {
            lines.Add(Theme.BoldMarkup("Recent"));
            foreach (var row in projection.Recent)
            {
                lines.Add(RenderRow(row, time));
            }
        }

        lines.Add(Theme.DimMarkup("Read-only snapshot. Manage tasks with the task_* tools."));
        return lines;
    }

    private static string RenderRow(TaskListRow row, TimeProvider time)
    {
        var t = row.Task;
        var marker = t.Status switch
        {
            TaskRunStatus.Running => Theme.AccentMarkup("●"),
            TaskRunStatus.Completed => Theme.SuccessMarkup("✓"),
            TaskRunStatus.Failed => Theme.ErrorMarkup("✗"),
            _ => Theme.DimMarkup("■"),
        };

        var indent = new string(' ', 2 + (row.IndentDepth * 2));
        var id = TerminalTextSanitizer.Sanitize(t.Id);
        var kind = t.Kind.ToString().ToLowerInvariant();
        var mode = t.Mode.ToString().ToLowerInvariant();
        var desc = TruncateToCells(TerminalTextSanitizer.SanitizeSingleLine(t.Description), MaxDescriptionCells);
        var duration = FormatDuration(t, time);

        return $"{indent}{marker} {Theme.DimMarkup(id)} {Theme.AccentMarkup(kind)} " +
               $"({Theme.DimMarkup(mode)}) {Theme.BoldMarkup(desc)}  " +
               $"{Theme.DimMarkup(t.Status.ToString())} {Theme.DimMarkup(duration)}";
    }

    private static string FormatDuration(TaskSnapshot t, TimeProvider time)
    {
        var end = t.EndedAt ?? time.GetUtcNow();
        var span = end - t.StartedAt;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m{span.Seconds:00}s")
            : string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.0}s");
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="maxCells"/> terminal display cells
    /// (measured with <see cref="TerminalCellText.Width"/>), appending an ellipsis when it overflows. The
    /// kept prefix is sliced with <see cref="TerminalCellText.SliceByCells"/>, which keeps whole grapheme
    /// clusters, so a wide CJK glyph or an astral/ZWJ emoji is never split into an invalid surrogate and a
    /// double-width character counts as the two cells it occupies rather than one grapheme.
    /// </summary>
    private static string TruncateToCells(string text, int maxCells)
    {
        if (TerminalCellText.Width(text) <= maxCells)
        {
            return text;
        }

        // Reserve one cell for the ellipsis; SliceByCells keeps every whole grapheme that intersects.
        var head = TerminalCellText.SliceByCells(text, 0, Math.Max(0, maxCells - 1));
        return head + "…";
    }
}
