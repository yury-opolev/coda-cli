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
    public async Task Summary_emits_only_final_activity_completion()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer, ToolDisplayMode.Summary);
        var identity = new ToolCallIdentity("turn", "activity", "call", "root:turn");

        await renderer.ApplyEventAsync(new ToolQueuedEvent(identity, "grep", "{}"), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolStartedEvent("grep", "{}", identity), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolStateChangedEvent(identity, "grep", ToolCallStatus.Running), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolProgressEvent("grep", 1200, identity), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolCompletedEvent("grep", new ToolResult("secret"), identity), CancellationToken.None);

        Assert.Equal(string.Empty, writer.ToString());

        await renderer.ApplyEventAsync(
            new ToolActivityCompletedEvent(new ToolActivitySummary("turn", "activity", 12, 1, 0, 0, null)),
            CancellationToken.None);

        Assert.Equal("Ran 12 tools - 1 failed" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public async Task Summary_suppresses_legacy_tool_events_and_preserves_shell_cancelled_wording()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer, ToolDisplayMode.Summary);

        await renderer.ApplyEventAsync(new ToolStartedEvent("run_command", """{"command":"echo one"}"""), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolProgressEvent("run_command", 1000), CancellationToken.None);
        await renderer.ApplyEventAsync(
            new ToolCompletedEvent("run_command", new ToolResult("result")),
            CancellationToken.None);

        Assert.Equal(string.Empty, writer.ToString());

        await renderer.ApplyEventAsync(
            new ToolActivityCompletedEvent(new ToolActivitySummary("turn", "activity", 2, 0, 2, 0, "run_command")),
            CancellationToken.None);

        Assert.Equal("Ran 2 shell commands - cancelled" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public async Task Summary_suppresses_multiple_correlated_batches_until_each_completion()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer, ToolDisplayMode.Summary);
        var first = new ToolCallIdentity("turn", "activity-1", "call-1", "root:turn");
        var second = new ToolCallIdentity("turn", "activity-2", "call-2", "root:turn");

        await renderer.ApplyEventAsync(new ToolQueuedEvent(first, "grep", "{}"), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolQueuedEvent(second, "read_file", "{}"), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolStateChangedEvent(first, "grep", ToolCallStatus.Running), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolStateChangedEvent(second, "read_file", ToolCallStatus.Succeeded), CancellationToken.None);
        await renderer.ApplyEventAsync(
            new ToolActivityCompletedEvent(new ToolActivitySummary("turn", "activity-1", 1, 0, 0, 0, "grep")),
            CancellationToken.None);

        Assert.Equal("Ran 1 tool" + Environment.NewLine, writer.ToString());

        await renderer.ApplyEventAsync(
            new ToolActivityCompletedEvent(new ToolActivitySummary("turn", "activity-2", 1, 0, 0, 0, "read_file")),
            CancellationToken.None);

        Assert.Equal(
            "Ran 1 tool" + Environment.NewLine + "Ran 1 tool" + Environment.NewLine,
            writer.ToString());
    }

    [Fact]
    public async Task Default_constructor_remains_verbose()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(new ToolStartedEvent("grep", "{}"), CancellationToken.None);

        Assert.Equal("[tool] grep {}" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public async Task Compact_tool_output_contains_preview_and_status_but_not_result()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer, ToolDisplayMode.Compact);

        await renderer.ApplyEventAsync(
            new ToolStartedEvent("grep", "\u001b[31mline one\nline two\u001b[0m"),
            CancellationToken.None);
        await renderer.ApplyEventAsync(
            new ToolCompletedEvent("grep", new ToolResult("secret result")),
            CancellationToken.None);

        var output = writer.ToString();
        Assert.Contains("[tool] grep line one line two [running]", output, StringComparison.Ordinal);
        Assert.Contains("[tool-result] grep [success]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("secret result", output, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', output);
    }

    [Fact]
    public async Task Tiny_tool_output_is_suppressed_without_affecting_non_tool_events()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer, ToolDisplayMode.Tiny);

        await renderer.ApplyEventAsync(new ToolStartedEvent("grep", "{}"), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolProgressEvent("grep", 1500), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolCompletedEvent("grep", new ToolResult("result")), CancellationToken.None);
        await renderer.ApplyEventAsync(new WarningEvent("careful"), CancellationToken.None);

        Assert.Equal("[warning] careful" + Environment.NewLine, writer.ToString());
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
    public async Task External_osc_hyperlink_escapes_are_stripped_after_shared_sanitizer_extraction()
    {
        // Characterization: PlainOutputRenderer now delegates escape stripping to the shared
        // TerminalTextSanitizer regex, which also removes OSC hyperlink sequences (previously untouched).
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(
            new CommandOutputEvent("\x1B]8;;https://example.com\x07link\x1B]8;;\x07"),
            CancellationToken.None);

        Assert.Equal("link" + Environment.NewLine, writer.ToString());
        Assert.DoesNotContain('\u001b', writer.ToString());
    }

    [Fact]
    public async Task Double_escaped_clear_screen_cannot_reform_a_live_sequence()
    {
        // Regression: stripping the inner ESC[2J must not leave the leading ESC glued to a literal
        // "[2J" that reforms a live clear-screen once written to the terminal.
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(new CommandOutputEvent("\u001B\u001B[2J[2J"), CancellationToken.None);
        await renderer.ApplyEventAsync(new CommandOutputEvent("\u001B\u001BA[2J"), CancellationToken.None);

        var output = writer.ToString();
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain("\u001b[", output, StringComparison.Ordinal);
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
