using System.Globalization;
using Coda.Agent;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class PlainOutputRendererTests
{
    [Fact]
    public async Task Plain_output_contains_no_cursor_or_alternate_screen_sequences()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(new AssistantTextDeltaEvent("hello"), CancellationToken.None);
        await renderer.ApplyEventAsync(new AssistantTextCompletedEvent(), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolStartedEvent("grep", "{}"), CancellationToken.None);
        await renderer.ApplyEventAsync(new AgentErrorEvent("boom"), CancellationToken.None);

        Assert.Equal(
            "hello" + Environment.NewLine +
            "[tool] grep {}" + Environment.NewLine +
            "[error] boom" + Environment.NewLine,
            writer.ToString());
        Assert.DoesNotContain('\u001b', writer.ToString());
    }

    [Fact]
    public async Task Tool_progress_uses_invariant_one_decimal_seconds()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            await renderer.ApplyEventAsync(new ToolProgressEvent("build", 1500), CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal("[tool-progress] build 1.5s" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public async Task Tool_completed_renders_result_content()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(
            new ToolCompletedEvent("grep", new ToolResult("2 matches")),
            CancellationToken.None);

        Assert.Equal("[tool-result] grep: 2 matches" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public async Task Tool_completed_preserves_multiline_content()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(
            new ToolCompletedEvent("cat", new ToolResult("line1" + Environment.NewLine + "line2")),
            CancellationToken.None);

        Assert.Equal(
            "[tool-result] cat: line1" + Environment.NewLine + "line2" + Environment.NewLine,
            writer.ToString());
    }

    [Fact]
    public async Task Warning_limit_and_stop_render_stable_prefixes()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(new WarningEvent("careful"), CancellationToken.None);
        await renderer.ApplyEventAsync(new LimitReachedEvent("tokens", "too many"), CancellationToken.None);
        await renderer.ApplyEventAsync(new StopReasonEvent("end_turn"), CancellationToken.None);

        Assert.Equal(
            "[warning] careful" + Environment.NewLine +
            "[limit:tokens] too many" + Environment.NewLine +
            "[stop] end_turn" + Environment.NewLine,
            writer.ToString());
    }

    [Fact]
    public async Task Stop_reason_is_suppressed_when_empty()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(new StopReasonEvent(null), CancellationToken.None);
        await renderer.ApplyEventAsync(new StopReasonEvent(string.Empty), CancellationToken.None);
        await renderer.ApplyEventAsync(new StopReasonEvent("   "), CancellationToken.None);

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public async Task Diagnostic_and_notification_map_to_stable_prefixes()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(new DiagnosticEvent("lsp", "broke", UiNotificationLevel.Warning), CancellationToken.None);
        await renderer.ApplyEventAsync(new NotificationEvent("hi", UiNotificationLevel.Information), CancellationToken.None);
        await renderer.ApplyEventAsync(new NotificationEvent("watch out", UiNotificationLevel.Warning), CancellationToken.None);
        await renderer.ApplyEventAsync(new NotificationEvent("failed", UiNotificationLevel.Error), CancellationToken.None);

        Assert.Equal(
            "[diagnostic:lsp] broke" + Environment.NewLine +
            "[info] hi" + Environment.NewLine +
            "[warning] watch out" + Environment.NewLine +
            "[error] failed" + Environment.NewLine,
            writer.ToString());
    }

    [Fact]
    public async Task Diff_is_written_verbatim_with_exactly_one_trailing_newline()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(new DiffOutputEvent("--- a" + Environment.NewLine + "+++ b"), CancellationToken.None);
        await renderer.ApplyEventAsync(
            new DiffOutputEvent("@@ 1 @@" + Environment.NewLine + Environment.NewLine),
            CancellationToken.None);

        Assert.Equal(
            "--- a" + Environment.NewLine + "+++ b" + Environment.NewLine +
            "@@ 1 @@" + Environment.NewLine,
            writer.ToString());
    }

    [Fact]
    public async Task Command_output_has_exactly_one_trailing_newline()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(new CommandOutputEvent("done"), CancellationToken.None);
        await renderer.ApplyEventAsync(
            new CommandOutputEvent("again" + Environment.NewLine + Environment.NewLine),
            CancellationToken.None);

        Assert.Equal(
            "done" + Environment.NewLine +
            "again" + Environment.NewLine,
            writer.ToString());
    }

    [Fact]
    public async Task External_control_escapes_are_stripped_but_tabs_and_newlines_kept()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(new CommandOutputEvent("a\u001b[31mb\tc"), CancellationToken.None);

        Assert.Equal("a" + "b\tc" + Environment.NewLine, writer.ToString());
        Assert.DoesNotContain('\u001b', writer.ToString());
    }

    [Fact]
    public async Task Frame_only_events_produce_no_plain_output()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(new UserPromptSubmittedEvent("hi"), CancellationToken.None);
        await renderer.ApplyEventAsync(new ModeChangedEvent("plan"), CancellationToken.None);
        await renderer.ApplyEventAsync(new TurnStartedEvent("hi"), CancellationToken.None);
        await renderer.ApplyEventAsync(new TurnCompletedEvent(true), CancellationToken.None);
        await renderer.ApplyEventAsync(new TurnInterruptedEvent(), CancellationToken.None);
        await renderer.ApplyEventAsync(new ConsoleClearRequestedEvent(), CancellationToken.None);

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public async Task Cancellation_is_observed_before_writing()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await renderer.ApplyEventAsync(new AgentErrorEvent("boom"), new CancellationToken(canceled: true)));

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void Constructor_rejects_null_writer()
    {
        Assert.Throws<ArgumentNullException>(() => new PlainOutputRenderer(null!));
    }
}
