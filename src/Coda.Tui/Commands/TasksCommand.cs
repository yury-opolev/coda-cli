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
    // Keep description columns readable and the row width stable in narrow terminals.
    private const int MaxDescriptionColumns = 80;

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
    /// stripped of ANSI/OSC/control sequences (<see cref="TerminalTextSanitizer.Sanitize"/>) and then colored
    /// through <see cref="Theme"/>, whose helpers run <c>Markup.Escape</c>, so a task can never inject raw ANSI
    /// or Spectre markup. <paramref name="time"/> is the clock seam used for a running task's live duration.
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
        var desc = TruncateGraphemes(TerminalTextSanitizer.Sanitize(t.Description), MaxDescriptionColumns);
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
            ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s"
            : $"{span.TotalSeconds:0.0}s";
    }

    /// <summary>
    /// Truncates to at most <paramref name="maxColumns"/> grapheme clusters, appending an ellipsis, so an
    /// astral emoji or a combining sequence is never split across its boundary into an invalid surrogate.
    /// </summary>
    private static string TruncateGraphemes(string text, int maxColumns)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var count = 0;
        var cutIndex = -1;
        while (enumerator.MoveNext())
        {
            count++;
            if (count == maxColumns + 1)
            {
                cutIndex = enumerator.ElementIndex;
                break;
            }
        }

        return cutIndex < 0 ? text : text[..cutIndex] + "…";
    }
}
