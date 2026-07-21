using Coda.Agent.Tasks;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Spectre.Console;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Coda.Tui.Tests;

public sealed class TasksCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coda-taskscmd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Renders_empty_notice_when_no_task_manager()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var result = await new TasksCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.Contains("No background tasks", console.Output);
    }

    [Fact]
    public async Task Renders_empty_notice_when_provider_returns_null_before_first_turn()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.TaskManagerProvider = () => null; // set but no live session yet

        await new TasksCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("No background tasks", console.Output);
    }

    [Fact]
    public async Task Lists_running_task_with_status_and_description()
    {
        Directory.CreateDirectory(_dir);
        using var mgr = new TaskManager(sessionId: "sess-cmd", logRoot: _dir);
        mgr.Register(TaskKind.Subagent, "deploy api", parentTaskId: null);

        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.TaskManagerProvider = () => mgr;

        await new TasksCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("deploy api", console.Output);
        Assert.Contains("Running", console.Output);
    }

    [Fact]
    public async Task Reads_the_live_manager_snapshot_not_a_throwaway()
    {
        Directory.CreateDirectory(_dir);
        using var mgr = new TaskManager(sessionId: "sess-live", logRoot: _dir);

        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.TaskManagerProvider = () => mgr;

        // Registered AFTER the provider is wired: proves the command reads the live manager at call time.
        mgr.Register(TaskKind.Shell, "late registered task", parentTaskId: null);

        await new TasksCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("late registered task", console.Output);
    }

    [Fact]
    public async Task Rejects_arguments_with_usage_message_and_prints_no_snapshot()
    {
        Directory.CreateDirectory(_dir);
        using var mgr = new TaskManager(sessionId: "sess-args", logRoot: _dir);
        mgr.Register(TaskKind.Shell, "build project", parentTaskId: null);

        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.TaskManagerProvider = () => mgr;

        var result = await new TasksCommand().ExecuteAsync(context, ["bogus", "args"], CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        // Args must not silently print a snapshot; a clear usage message is shown instead.
        Assert.DoesNotContain("build project", console.Output);
        Assert.Contains("/tasks", console.Output);
        Assert.Contains("does not take arguments", console.Output);
    }

    [Fact]
    public async Task Escapes_markup_in_description_without_throwing()
    {
        Directory.CreateDirectory(_dir);
        using var mgr = new TaskManager(sessionId: "sess-cmd2", logRoot: _dir);
        mgr.Register(TaskKind.Shell, "danger [x] tag", parentTaskId: null);

        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.TaskManagerProvider = () => mgr;

        // Would throw a Spectre markup parse error if the description were not escaped.
        await new TasksCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("danger [x] tag", console.Output);
    }

    [Fact]
    public void RenderLines_returns_dim_notice_for_empty_list()
    {
        var lines = TasksCommand.RenderLines([], TimeProvider.System);

        var single = Assert.Single(lines);
        Assert.Contains("No background tasks", single);
    }

    [Fact]
    public void RenderLines_projects_active_hierarchy_with_indentation_and_recent_section()
    {
        var started = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var parent = Snapshot("parent", parentId: null, depth: 0, TaskKind.Subagent, "parent work", TaskRunStatus.Running, started);
        var child = Snapshot("child", parentId: "parent", depth: 1, TaskKind.Subagent, "child work", TaskRunStatus.Running, started);
        var done = Snapshot("done", parentId: null, depth: 0, TaskKind.Shell, "finished work", TaskRunStatus.Completed, started, started.AddSeconds(5));

        var lines = TasksCommand.RenderLines([parent, child, done], TimeProvider.System);

        var text = string.Join("\n", lines);
        var parentLine = lines.First(l => l.Contains("parent work"));
        var childLine = lines.First(l => l.Contains("child work"));

        // Parent appears before its child, and the child is indented further than the parent.
        Assert.True(text.IndexOf("parent work", StringComparison.Ordinal) < text.IndexOf("child work", StringComparison.Ordinal));
        Assert.True(LeadingSpaces(childLine) > LeadingSpaces(parentLine));

        // A dedicated recent/terminal section lists the completed task.
        Assert.Contains(lines, l => l.Contains("Recent"));
        Assert.Contains(lines, l => l.Contains("finished work"));
    }

    [Fact]
    public void RenderLines_shows_status_kind_mode_and_duration_labels()
    {
        var started = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var running = Snapshot("run", null, 0, TaskKind.Subagent, "running task", TaskRunStatus.Running, started, mode: TaskExecutionMode.Background);
        var time = new FixedTimeProvider(started.AddSeconds(90));

        var lines = TasksCommand.RenderLines([running], time);
        var text = string.Join("\n", lines);

        Assert.Contains("Running", text);
        Assert.Contains("subagent", text);
        Assert.Contains("background", text);
        Assert.Contains("1m", text); // 90s -> "1m30s"
    }

    [Fact]
    public void RenderLines_uses_ended_at_for_terminal_task_duration()
    {
        var started = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var done = Snapshot("done", null, 0, TaskKind.Shell, "quick task", TaskRunStatus.Completed, started, started.AddSeconds(5));
        // TimeProvider is far ahead; a terminal task must use EndedAt, not "now".
        var time = new FixedTimeProvider(started.AddHours(3));

        var lines = TasksCommand.RenderLines([done], time);
        var text = string.Join("\n", lines);

        Assert.Contains("5.0s", text);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void RenderLines_formats_subminute_duration_with_invariant_decimal_point(string culture)
    {
        var started = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var done = Snapshot("done", null, 0, TaskKind.Shell, "quick task", TaskRunStatus.Completed, started, started.AddSeconds(5));

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try
        {
            var text = string.Join("\n", TasksCommand.RenderLines([done], TimeProvider.System));

            // Comma-decimal cultures must still render a period so the row reads "5.0s" everywhere.
            Assert.Contains("5.0s", text);
            Assert.DoesNotContain("5,0s", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void RenderLines_collapses_multiline_and_tab_description_to_single_line()
    {
        var started = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var task = Snapshot("t", null, 0, TaskKind.Shell, "first line\nsecond\tthird", TaskRunStatus.Running, started);

        var line = TasksCommand.RenderLines([task], TimeProvider.System).First(l => l.Contains("first line"));

        // A multi-line/tabbed description collapses to one row so hierarchy rows cannot be split/spoofed.
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\t', line);
        Assert.Contains("first line second third", line);
    }

    [Fact]
    public void RenderLines_truncates_wide_cjk_description_by_display_cells_preserving_graphemes()
    {
        var started = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        // 界 is two display cells; 60 of them = 120 cells, well over the 80-cell budget.
        var wide = new string('界', 60);
        var task = Snapshot("t", null, 0, TaskKind.Shell, wide, TaskRunStatus.Running, started);

        var line = TasksCommand.RenderLines([task], TimeProvider.System).First(l => l.Contains('界'));

        Assert.Contains('…', line);
        // Truncation counts display cells, so at most ~40 two-cell graphemes fit in an 80-cell budget.
        var kept = line.Count(c => c == '界');
        Assert.True(kept is > 0 and <= 40, $"kept {kept} wide graphemes");
    }

    [Fact]
    public void RenderLines_truncates_emoji_description_without_splitting_surrogate_pairs()
    {
        var started = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var emoji = string.Concat(Enumerable.Repeat("🚀", 60));
        var task = Snapshot("t", null, 0, TaskKind.Shell, emoji, TaskRunStatus.Running, started);

        var line = TasksCommand.RenderLines([task], TimeProvider.System).First(l => l.Contains("🚀"));

        Assert.Contains('…', line);
        // Counting the two-unit "🚀" substring only succeeds when whole surrogate pairs survive.
        var kept = Regex.Matches(line, "🚀").Count;
        Assert.True(kept is > 0 and < 60, $"kept {kept} emoji graphemes");
        Assert.DoesNotContain('\uFFFD', line); // no replacement char from a split surrogate
    }

    [Fact]
    public void RenderLines_sanitizes_then_truncates_ansi_and_markup_description()
    {
        var started = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var desc = "\x1B[31m[danger]\x1B[0m " + new string('x', 200);
        var task = Snapshot("t", null, 0, TaskKind.Shell, desc, TaskRunStatus.Running, started);

        var line = TasksCommand.RenderLines([task], TimeProvider.System).First(l => l.Contains("danger"));

        Assert.DoesNotContain('\x1B', line);
        Assert.Contains("[[danger]]", line); // escaped markup, never interpreted
        Assert.Contains('…', line);
    }

    [Fact]
    public async Task Sanitizes_ansi_osc_and_control_sequences_without_throwing()
    {
        // ESC[2J clear screen, an OSC hyperlink, bare markup, bell and backspace controls.
        var malicious = "\x1B[2J\x1B]8;;http://evil\x07link[bad]desc\x07\b done";
        var started = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var task = Snapshot("evil", null, 0, TaskKind.Shell, malicious, TaskRunStatus.Running, started);

        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var lines = TasksCommand.RenderLines([task], TimeProvider.System);
        var joined = string.Join("\n", lines);
        var exception = Record.Exception(() =>
        {
            foreach (var line in lines)
            {
                console.MarkupLine(line);
            }
        });

        Assert.Null(exception);
        // The produced markup carries no raw ANSI/OSC escape or bell; the malicious [markup] is escaped.
        // Char overloads use ordinal equality (a culture-sensitive string search treats ESC as ignorable).
        Assert.DoesNotContain('\x1B', joined);
        Assert.DoesNotContain('\x07', joined);
        Assert.Contains("[[bad]]", joined); // brackets doubled = escaped, never interpreted as markup
        Assert.Contains("desc", joined);
        Assert.Contains("desc", console.Output);
    }

    [Fact]
    public void Help_does_not_claim_slash_tasks_prints_a_snapshot_in_serve()
    {
        var help = new TasksCommand().Help;
        var desc = help.Description!;

        // The print contexts are named explicitly and do NOT include serve.
        Assert.Contains("plain, Spectre, and legacy", desc);
        Assert.Contains("snapshot", desc);

        // serve is described as having equivalent non-UI capabilities via task_* tools, not a printed snapshot.
        Assert.Contains("coda serve does not run /tasks", desc);
        Assert.Contains("task_* tools", desc);

        // No overclaim that /tasks prints/renders its snapshot inside coda serve.
        Assert.DoesNotContain("serve contexts this", desc);
        Assert.DoesNotContain("and serve", desc);
    }

    private static int LeadingSpaces(string line) => line.Length - line.TrimStart(' ').Length;

    private static TaskSnapshot Snapshot(
        string id,
        string? parentId,
        int depth,
        TaskKind kind,
        string description,
        TaskRunStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt = null,
        TaskExecutionMode mode = TaskExecutionMode.Foreground) =>
        new(id, parentId, depth, kind, description, status, mode, Version: 1, startedAt, endedAt, LogPath: "log", Result: null, Error: null);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
