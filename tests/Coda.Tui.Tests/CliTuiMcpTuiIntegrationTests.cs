using System.Collections.Immutable;
using System.Drawing;
using Coda.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class CliTuiMcpTuiIntegrationTests
{
    [Fact]
    public async Task Detached_interior_activity_replacement_reflows_without_losing_its_typed_anchor_or_unseen_state()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            transcriptFormatter: (block, width) =>
                TranscriptBlockFormatter.Format(block, width, ToolDisplayMode.Verbose));
        var before = Enumerable.Range(0, 6)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"before {index}"))
            .ToImmutableArray();
        var first = Id("call-alpha", "root");
        var second = Id("call-beta", "subagent:review");
        var firstInput = """{"command":"dotnet test this-project-with-a-long-name --filter alpha"}""";
        var secondInput = """{"pattern":"a long distinct search pattern which wraps at the constrained width"}""";
        var state = UiSessionSnapshot.Empty with { Transcript = before };
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);

        state = UiReducer.Reduce(state, new ToolQueuedEvent(first, "run_command", firstInput));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);
        var activity = Assert.Single(state.Transcript.OfType<ToolActivityTranscriptBlock>());
        var activityId = activity.Id;

        var later = Enumerable.Range(0, 48)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"later {index}"))
            .ToImmutableArray();
        state = state with { Transcript = state.Transcript.AddRange(later) };
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);

        fixture.Shell.Transcript.ScrollToRowForTest(fixture.Shell.Transcript.ContentRowsForTest / 2);
        var anchor = Assert.IsType<TranscriptViewportAnchor>(fixture.Shell.Transcript.TopAnchorForTest);
        Assert.Contains(later, block => block.Id == anchor.BlockId);

        state = UiReducer.Reduce(state, new CommandOutputEvent("one unseen detached append"));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);
        Assert.Equal(1, fixture.Shell.Transcript.UnseenBlocks);

        var replaceAtCount = fixture.Shell.Transcript.ReplaceAtCount;
        state = UiReducer.Reduce(state, new ToolStateChangedEvent(first, "run_command", ToolCallStatus.Running));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);
        state = UiReducer.Reduce(state, new ToolQueuedEvent(second, "grep", secondInput));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);

        activity = Assert.Single(state.Transcript.OfType<ToolActivityTranscriptBlock>());
        Assert.Equal(activityId, activity.Id);
        Assert.True(
            TranscriptBlockFormatter.Format(activity, 80, ToolDisplayMode.Verbose).Count
            != TranscriptBlockFormatter.Format(activity, 40, ToolDisplayMode.Verbose).Count);

        var contentRowsBeforeReflow = fixture.Shell.Transcript.ContentRowsForTest;
        fixture.HostApplication.Driver!.SetScreenSize(40, 24);
        fixture.HostApplication.LayoutAndDraw();

        Assert.True(fixture.Shell.Transcript.ReplaceAtCount >= replaceAtCount + 2);
        Assert.Equal(0, fixture.Shell.Transcript.ReplaceLastCount);
        Assert.NotEqual(contentRowsBeforeReflow, fixture.Shell.Transcript.ContentRowsForTest);
        var reflowedAnchor = Assert.IsType<TranscriptViewportAnchor>(fixture.Shell.Transcript.TopAnchorForTest);
        Assert.Equal(anchor.BlockId, reflowedAnchor.BlockId);
        Assert.Equal(anchor.WrappedRowOffset, reflowedAnchor.WrappedRowOffset);
        Assert.Equal(1, fixture.Shell.Transcript.UnseenBlocks);
        Assert.Equal(TranscriptFollowMode.Detached, fixture.Shell.Transcript.FollowModeForTest);
    }

    [Fact]
    public async Task Parallel_completions_keep_distinct_terminal_calls_and_render_one_final_summary()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            transcriptFormatter: (block, width) =>
                TranscriptBlockFormatter.Format(block, width, ToolDisplayMode.Summary));
        var first = Id("call-alpha", "root");
        var second = Id("call-beta", "subagent:review");
        var firstInput = """{"path":"alpha.cs"}""";
        var secondInput = """{"query":"beta symbol"}""";
        var state = UiReducer.Reduce(
            UiSessionSnapshot.Empty,
            new ToolQueuedEvent(first, "read_file", firstInput));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);
        var activityId = Assert.Single(state.Transcript.OfType<ToolActivityTranscriptBlock>()).Id;

        state = UiReducer.Reduce(state, new ToolQueuedEvent(second, "grep", secondInput));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);
        state = UiReducer.Reduce(state, new ToolStateChangedEvent(first, "read_file", ToolCallStatus.Running));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);
        state = UiReducer.Reduce(state, new ToolStateChangedEvent(second, "grep", ToolCallStatus.AwaitingApproval));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);
        state = UiReducer.Reduce(
            state,
            new ToolCompletedEvent("read_file", new ToolResult("alpha result"), first, ToolCallStatus.Succeeded));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);
        state = UiReducer.Reduce(
            state,
            new ToolCompletedEvent("grep", new ToolResult("beta failure", IsError: true), second, ToolCallStatus.Failed));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);
        state = UiReducer.Reduce(
            state,
            new ToolActivityCompletedEvent(new ToolActivitySummary(
                "root",
                "activity",
                TotalCalls: 2,
                FailedCalls: 1,
                CancelledCalls: 0,
                SkippedCalls: 0,
                HomogeneousToolName: null)));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);

        var activity = Assert.Single(state.Transcript.OfType<ToolActivityTranscriptBlock>());
        Assert.Equal(activityId, activity.Id);
        Assert.Equal(ToolActivityCompletionState.Completed, activity.CompletionState);
        Assert.Collection(
            activity.Calls,
            call =>
            {
                Assert.Equal(("call-alpha", "root", "read_file", firstInput, ToolCallStatus.Succeeded, "alpha result", null),
                    (call.CallId, call.SourceId, call.ToolName, call.InputJson, call.Status, call.Result, call.Error));
            },
            call =>
            {
                Assert.Equal(("call-beta", "subagent:review", "grep", secondInput, ToolCallStatus.Failed, null, "beta failure"),
                    (call.CallId, call.SourceId, call.ToolName, call.InputJson, call.Status, call.Result, call.Error));
            });

        var summaryRows = fixture.Shell.Transcript.CollectVisibleRows()
            .Where(row => row.BlockId == activityId && !row.IsSeparator)
            .Select(row => row.Text)
            .ToArray();
        Assert.Equal(["Ran 2 tools - 1 failed"], summaryRows);
    }

    [Theory]
    [InlineData(TuiRunMode.Fullscreen)]
    [InlineData(TuiRunMode.Inline)]
    public async Task Drawn_timestamp_keeps_a_blank_cell_before_the_scrollbar_in_both_shell_modes(TuiRunMode mode)
    {
        using IApplication app = Application.Create();
        app.AppModel = mode == TuiRunMode.Inline ? AppModel.Inline : AppModel.FullScreen;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        var driver = app.Driver ?? throw new InvalidOperationException("The ANSI driver must be initialized.");
        driver.SetScreenSize(40, 12);
        using FullscreenTuiShell shell = mode == TuiRunMode.Inline
            ? new InlineTuiShell(
                app,
                new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([]))),
                new RecordingUiEvents(),
                UiSessionSnapshot.Empty,
                transcriptFormatter: SummaryFormatter)
            : new FullscreenTuiShell(
                app,
                new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([]))),
                new RecordingUiEvents(),
                UiSessionSnapshot.Empty,
                transcriptFormatter: SummaryFormatter);
        var token = app.Begin(shell);
        app.LayoutAndDraw();
        try
        {
            var timestamped = new UserTranscriptBlock(
                Guid.NewGuid(),
                "timestamped message",
                new DateTimeOffset(2026, 7, 24, 9, 5, 0, TimeSpan.Zero));
            var seed = Enumerable.Range(0, 48)
                .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {index}"))
                .Append(timestamped)
                .ToImmutableArray();
            await shell.ApplyAsync(
                UiSessionSnapshot.Empty with { Transcript = seed },
                CancellationToken.None);
            app.LayoutAndDraw();

            Assert.True(shell.Transcript.ScrollbarVisibleForTest);
            var timestamp = FindRenderedTextEnd(app, "09:05");
            var scrollbarColumn = shell.Transcript.Frame.X + shell.Transcript.Frame.Width - 1;
            Assert.Equal(scrollbarColumn - 2, timestamp.EndColumn);
            Assert.Equal(" ", GraphemeAt(driver, timestamp.Row, scrollbarColumn - 1));
            Assert.True(GraphemeAt(driver, timestamp.Row, scrollbarColumn) is "│" or "█");
        }
        finally
        {
            if (token is not null)
            {
                app.End(token);
            }
        }
    }

    private static IReadOnlyList<TranscriptRenderLine> SummaryFormatter(TranscriptBlock block, int width) =>
        TranscriptBlockFormatter.Format(block, width, ToolDisplayMode.Summary);

    private static ToolCallIdentity Id(string callId, string sourceId) =>
        new("root", "activity", callId, sourceId);

    private static (int EndColumn, int Row) FindRenderedTextEnd(IApplication app, string text)
    {
        var driver = app.Driver
            ?? throw new InvalidOperationException("The ANSI driver must be initialized.");
        for (var row = 0; row < driver.Rows; row++)
        {
            var rendered = string.Concat(
                Enumerable.Range(0, driver.Cols).Select(column => GraphemeAt(driver, row, column)));
            var start = rendered.IndexOf(text, StringComparison.Ordinal);
            if (start >= 0)
            {
                return (start + text.Length - 1, row);
            }
        }

        throw new Xunit.Sdk.XunitException($"Did not find {text} in the ANSI driver buffer.");
    }

    private static string GraphemeAt(Terminal.Gui.Drivers.IDriver driver, int row, int column)
    {
        var contents = driver.Contents
            ?? throw new InvalidOperationException("The ANSI driver output buffer must be initialized.");
        return contents[row, column].Grapheme ?? " ";
    }
}
