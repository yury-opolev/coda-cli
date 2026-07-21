using Coda.Agent.Tasks;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Spectre.Console;

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
    public async Task Ignores_arguments_and_still_renders_snapshot()
    {
        Directory.CreateDirectory(_dir);
        using var mgr = new TaskManager(sessionId: "sess-args", logRoot: _dir);
        mgr.Register(TaskKind.Shell, "build project", parentTaskId: null);

        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.TaskManagerProvider = () => mgr;

        var result = await new TasksCommand().ExecuteAsync(context, ["bogus", "args"], CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.Contains("build project", console.Output);
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
