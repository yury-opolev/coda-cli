using Coda.Agent.Scheduling;
using Coda.Tui.Commands;
using Coda.Tui.Repl;

namespace Coda.Tui.Tests;

/// <summary>
/// Coverage for <see cref="ScheduleCommand"/>: snapshot rendering, sanitization, argument rejection,
/// and the empty-state notice — mirroring <see cref="TasksCommandTests"/> in structure and rigor.
/// </summary>
public sealed class ScheduleCommandTests
{
    // ── ExecuteAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Renders_empty_notice_when_no_schedule_control_provider()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var result = await new ScheduleCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.Contains("No scheduled tasks", console.Output);
    }

    [Fact]
    public async Task Renders_empty_notice_when_provider_returns_null()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.ScheduleControlProvider = () => null;

        await new ScheduleCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("No scheduled tasks", console.Output);
    }

    [Fact]
    public async Task Renders_empty_notice_when_list_is_empty()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.ScheduleControlProvider = () => new FakeScheduleControl([]);

        await new ScheduleCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("No scheduled tasks", console.Output);
    }

    [Fact]
    public async Task Renders_row_for_each_definition_in_list()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        var models = new[]
        {
            ReadModel("def-1", "Nightly backup", "every 1d", "UTC",
                new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero)),
            ReadModel("def-2", null, "0 9 * * *", "Europe/Berlin",
                new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero)),
        };
        context.ScheduleControlProvider = () => new FakeScheduleControl(models);

        await new ScheduleCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("def-1", console.Output);
        Assert.Contains("Nightly backup", console.Output);
        Assert.Contains("def-2", console.Output);
        Assert.Contains("every 1d", console.Output);
    }

    [Fact]
    public async Task Reads_the_live_service_snapshot_not_a_throwaway()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        var service = new FakeScheduleControl([]);
        context.ScheduleControlProvider = () => service;

        // Add a definition AFTER the provider is wired — proves the command reads at call time.
        service.Items.Add(ReadModel("late", "late definition", "every 5m", "UTC",
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero)));

        await new ScheduleCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("late definition", console.Output);
    }

    [Fact]
    public async Task Rejects_arguments_with_usage_message_and_prints_no_snapshot()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        var service = new FakeScheduleControl([
            ReadModel("def-1", "should not appear", "every 1h", "UTC",
                new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero)),
        ]);
        context.ScheduleControlProvider = () => service;

        var result = await new ScheduleCommand().ExecuteAsync(context, ["bogus"], CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.DoesNotContain("should not appear", console.Output);
        Assert.Contains("/schedule", console.Output);
        Assert.Contains("does not take arguments", console.Output);
    }

    [Fact]
    public async Task Sanitizes_markup_in_all_dynamic_fields_without_throwing()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        var dangerous = "[danger] \x1B[31mred\x1B[0m";
        var service = new FakeScheduleControl([
            ReadModel("id-[x]", dangerous, "every 1h", "UTC",
                new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero)),
        ]);
        context.ScheduleControlProvider = () => service;

        var exception = await Record.ExceptionAsync(async () =>
            await new ScheduleCommand().ExecuteAsync(context, [], CancellationToken.None));

        Assert.Null(exception);
        Assert.DoesNotContain('\x1B', console.Output);
    }

    // ── RenderLines (pure, separately testable) ──────────────────────────────

    [Fact]
    public void RenderLines_returns_dim_notice_for_empty_list()
    {
        var lines = ScheduleCommand.RenderLines([]);

        var single = Assert.Single(lines);
        Assert.Contains("No scheduled tasks", single);
    }

    [Fact]
    public void RenderLines_shows_id_name_rule_timezone_and_next_run_utc()
    {
        var model = ReadModel("def-1", "Hourly probe", "every 1h", "America/New_York",
            new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

        var lines = ScheduleCommand.RenderLines([model]);
        var text = string.Join("\n", lines);

        Assert.Contains("def-1", text);
        Assert.Contains("Hourly probe", text);
        Assert.Contains("every 1h", text);
        Assert.Contains("America/New_York", text);
        Assert.Contains("2026-07-25", text);
    }

    [Fact]
    public void RenderLines_shows_state_and_last_outcome()
    {
        var outcome = new ScheduleTerminalMetadata(
            ScheduleTerminalOutcome.Succeeded,
            new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero),
            "completed ok");
        var model = ReadModel("def-x", null, "every 1h", "UTC",
            new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            state: ScheduleRuntimeStatus.Running,
            lastOutcome: outcome);

        var text = string.Join("\n", ScheduleCommand.RenderLines([model]));

        Assert.Contains("Running", text);
        Assert.Contains("Succeeded", text);
    }

    [Fact]
    public void RenderLines_sanitizes_escape_sequences_from_name_and_prompt()
    {
        var evil = "\x1B[2J\x1B]8;;evil\x07 [bad]name";
        var model = ReadModel("def-e", evil, "every 1h", "UTC",
            new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

        var lines = ScheduleCommand.RenderLines([model]);
        var text = string.Join("\n", lines);

        Assert.DoesNotContain('\x1B', text);
        Assert.DoesNotContain('\x07', text);
        // Brackets must be escaped so they aren't interpreted as Spectre markup.
        Assert.Contains("[[bad]]", text);
    }

    [Fact]
    public void RenderLines_shows_read_only_notice_at_end()
    {
        var model = ReadModel("def-1", null, "every 1h", "UTC",
            new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

        var lines = ScheduleCommand.RenderLines([model]);

        Assert.Contains(lines, l => l.Contains("Read-only snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderLines_includes_multiple_rows_for_multiple_definitions()
    {
        var models = Enumerable.Range(1, 5)
            .Select(i => ReadModel($"def-{i}", $"task {i}", "every 1h", "UTC",
                new DateTimeOffset(2026, 7, 25, i, 0, 0, TimeSpan.Zero)))
            .ToList();

        var lines = ScheduleCommand.RenderLines(models);

        for (var i = 1; i <= 5; i++)
        {
            Assert.Contains(lines, l => l.Contains($"def-{i}", StringComparison.Ordinal));
        }
    }

    // ── Help ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Help_mentions_interactive_browser_and_snapshot_contexts()
    {
        var help = new ScheduleCommand().Help;
        var desc = help.Description!;

        Assert.Contains("schedule", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("snapshot", desc, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static ScheduledTaskReadModel ReadModel(
        string id,
        string? name,
        string rule,
        string timeZone,
        DateTimeOffset nextRunUtc,
        ScheduleRuntimeStatus state = ScheduleRuntimeStatus.Idle,
        ScheduleTerminalMetadata? lastOutcome = null) =>
        new(
            id,
            name,
            ScheduleKind.Interval,
            $"Run {name ?? id}",
            rule,
            timeZone,
            nextRunUtc,
            nextRunUtc.ToString("yyyy-MM-dd HH:mm"),
            timeZone,
            state,
            ActiveTaskId: null,
            lastOutcome);

    /// <summary>
    /// Fake <see cref="IScheduleControl"/> that returns whatever is in <see cref="Items"/>.
    /// Supports late-addition tests by exposing a mutable list.
    /// </summary>
    internal sealed class FakeScheduleControl(IEnumerable<ScheduledTaskReadModel> initial)
        : IScheduleControl
    {
        public List<ScheduledTaskReadModel> Items { get; } = [.. initial];

        public IReadOnlyList<ScheduledTaskReadModel> List() => Items;

        public ScheduleCreateResult Create(ScheduleCreateRequest request) =>
            ScheduleCreateResult.Fail("not implemented in fake");

        public bool Delete(string id) => Items.RemoveAll(m => m.Id == id) > 0;
    }
}
