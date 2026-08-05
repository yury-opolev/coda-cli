using System.Collections.Immutable;
using Coda.Agent;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Exercises the gutter tagging and shaping pass in <see cref="TranscriptBlockFormatter"/>:
/// user/assistant/thinking/tool markers, continuation prefixes, child connectors, ASCII fallback,
/// link-span column shifting, callout prefix-cell inheritance, and width capping.
/// </summary>
public sealed class TranscriptGutterTests
{
    // ---------------------------------------------------------------------------
    // User block
    // ---------------------------------------------------------------------------

    [Fact]
    public void User_first_row_carries_user_marker()
    {
        var lines = TranscriptBlockFormatter.Format(
            new UserTranscriptBlock(Guid.NewGuid(), "hello", null), 40);

        Assert.Equal(" \u276f hello", lines[0].Text);
    }

    [Fact]
    public void User_wrapped_rows_start_with_three_spaces()
    {
        // Width 10 → contentWidth 7; "hello world" (11 chars) wraps into 2 rows.
        var lines = TranscriptBlockFormatter.Format(
            new UserTranscriptBlock(Guid.NewGuid(), "hello world", null), 10);

        Assert.True(lines.Count >= 2, "message should wrap at width 10");
        Assert.StartsWith(" \u276f ", lines[0].Text);
        Assert.StartsWith("   ", lines[1].Text);
    }

    // ---------------------------------------------------------------------------
    // Pending user block
    // ---------------------------------------------------------------------------

    [Fact]
    public void Pending_user_keeps_pending_tag_after_marker()
    {
        var lines = TranscriptBlockFormatter.Format(
            new PendingUserTranscriptBlock(Guid.NewGuid(), "queued", "entry", DateTimeOffset.UtcNow),
            80);

        var line = Assert.Single(lines);
        Assert.StartsWith(" \u276f [pending] ", line.Text);
        Assert.Contains("queued", line.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // Assistant block — active vs complete
    // ---------------------------------------------------------------------------

    [Fact]
    public void Incomplete_assistant_first_row_has_active_marker()
    {
        var lines = TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), "thinking...", Complete: false), 80);

        Assert.StartsWith(" \u25cb ", lines[0].Text); // " ○ "
    }

    [Fact]
    public void Complete_assistant_first_row_has_complete_marker()
    {
        var lines = TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), "done", Complete: true), 80);

        Assert.StartsWith(" \u25cf ", lines[0].Text); // " ● "
    }

    [Fact]
    public void Multi_paragraph_blank_separator_stays_empty()
    {
        // Markdig inserts a blank line between paragraphs; the shaper must NOT add whitespace.
        var lines = TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), "para one\n\npara two", Complete: true),
            80);

        // There must be a blank separator row — it should be empty string, not whitespace.
        Assert.Contains(lines, l => l.Text == string.Empty);
    }

    // ---------------------------------------------------------------------------
    // Summary-mode tool activity — active
    // ---------------------------------------------------------------------------

    [Fact]
    public void Active_summary_with_three_running_calls_has_header_then_child_connectors()
    {
        var calls = Enumerable.Range(0, 3)
            .Select(i => new ToolActivityCall(
                $"call-{i}", "root", "read_file", $$"""{"path":"file-{{i}}"}""",
                "unsafe", ToolCallStatus.Running, 10, null, null))
            .ToImmutableArray();
        var activity = new ToolActivityTranscriptBlock(
            Guid.NewGuid(), "root", "activity", calls, ToolActivityCompletionState.Active);

        var lines = TranscriptBlockFormatter.Format(activity, 120, ToolDisplayMode.Summary);

        Assert.Equal(4, lines.Count); // header + 3 child rows
        Assert.Equal(" \u25cb Running 3 tools...", lines[0].Text);
        Assert.StartsWith("   \u2502 ", lines[1].Text); // "   │ "
        Assert.StartsWith("   \u2502 ", lines[2].Text); // "   │ "
        Assert.StartsWith("   \u2514 ", lines[3].Text); // "   └ "
    }

    // ---------------------------------------------------------------------------
    // Summary-mode tool activity — completed
    // ---------------------------------------------------------------------------

    [Fact]
    public void Completed_summary_activity_renders_complete_marker_on_single_row()
    {
        var calls = ImmutableArray.Create(
            new ToolActivityCall(
                "call-0", "root", "read_file", """{"path":"f"}""",
                "unsafe", ToolCallStatus.Succeeded, 10, "ok", null));
        var activity = new ToolActivityTranscriptBlock(
            Guid.NewGuid(), "root", "activity", calls, ToolActivityCompletionState.Completed);

        var line = Assert.Single(
            TranscriptBlockFormatter.Format(activity, 120, ToolDisplayMode.Summary));

        Assert.StartsWith(" \u25cf ", line.Text); // " ● "
    }

    // ---------------------------------------------------------------------------
    // ASCII glyph set
    // ---------------------------------------------------------------------------

    [Fact]
    public void Ascii_glyphs_produce_correct_marker_forms()
    {
        var ascii = TranscriptGlyphs.Ascii;

        var user = TranscriptBlockFormatter.Format(
            new UserTranscriptBlock(Guid.NewGuid(), "hi", null), 40, ToolDisplayMode.Full, null, ascii);
        Assert.Equal(" > hi", user[0].Text);

        var active = TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), "active", Complete: false), 40, ToolDisplayMode.Full, null, ascii);
        Assert.StartsWith(" o ", active[0].Text);

        var complete = TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), "complete", Complete: true), 40, ToolDisplayMode.Full, null, ascii);
        Assert.StartsWith(" * ", complete[0].Text);
    }

    [Fact]
    public void Ascii_child_connector_and_last_child_connector_are_correct()
    {
        var ascii = TranscriptGlyphs.Ascii;
        var calls = ImmutableArray.Create(
            new ToolActivityCall("c0", "root", "read_file", """{"path":"a"}""", "unsafe", ToolCallStatus.Running, 10, null, null),
            new ToolActivityCall("c1", "root", "read_file", """{"path":"b"}""", "unsafe", ToolCallStatus.Running, 10, null, null));
        var activity = new ToolActivityTranscriptBlock(
            Guid.NewGuid(), "root", "act", calls, ToolActivityCompletionState.Active);

        var lines = TranscriptBlockFormatter.Format(activity, 120, ToolDisplayMode.Summary, null, ascii);

        Assert.Equal(3, lines.Count);
        Assert.StartsWith("   | ", lines[1].Text);  // "   | " (ASCII ChildConnector)
        Assert.StartsWith("   ` ", lines[2].Text);  // "   ` " (ASCII LastChildConnector)
    }

    // ---------------------------------------------------------------------------
    // Link span shifting
    // ---------------------------------------------------------------------------

    [Fact]
    public void Link_StartColumn_and_EndColumn_are_shifted_by_gutter_width()
    {
        // " ● https://example.com" — URL starts at column 3 (gutter " ● " = 3 cells).
        var lines = TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), "https://example.com", Complete: true),
            80);

        var line = Assert.Single(lines);
        Assert.NotNull(line.Links);
        var link = Assert.Single(line.Links!);
        Assert.Equal(TranscriptGlyphs.MarkerCells, link.StartColumn);
        Assert.Equal(TranscriptGlyphs.MarkerCells + TerminalCellText.Width("https://example.com"), link.EndColumn);
    }

    // ---------------------------------------------------------------------------
    // Callout bar PrefixCells inheritance
    // ---------------------------------------------------------------------------

    [Fact]
    public void Callout_body_PrefixCells_grows_by_gutter_width()
    {
        // Body rows have "│ " bar (2 cells) set as PrefixCells before gutter.
        // After gutter ("   " = 3 cells), PrefixCells = 2 + 3 = 5 = ChildCells.
        var lines = TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), "> [!NOTE]\n> body text", Complete: true),
            80);

        Assert.True(lines.Count >= 2);
        var bodyLine = lines[1];
        // The bar "│ " (2) plus gutter "   " (3) = 5 cells are coloured.
        Assert.Equal(TerminalCellText.Width("   \u2502 "), bodyLine.PrefixCells);
    }

    // ---------------------------------------------------------------------------
    // Width capping — content fits inside requested width
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("user")]
    [InlineData("assistant")]
    [InlineData("tool-activity")]
    public void No_rendered_row_exceeds_requested_width(string blockType)
    {
        const int width = 20;
        const string longText = "The quick brown fox jumps over the lazy dog repeatedly.";

        IReadOnlyList<TranscriptRenderLine> lines = blockType switch
        {
            "user" => TranscriptBlockFormatter.Format(
                new UserTranscriptBlock(Guid.NewGuid(), longText, null), width),
            "assistant" => TranscriptBlockFormatter.Format(
                new AssistantTranscriptBlock(Guid.NewGuid(), longText, Complete: true), width),
            "tool-activity" => TranscriptBlockFormatter.Format(
                new ToolActivityTranscriptBlock(
                    Guid.NewGuid(), "root", "act",
                    ImmutableArray.Create(
                        new ToolActivityCall("c0", "root", "read_file",
                            $$"""{"path":"{{longText}}"}""", "unsafe",
                            ToolCallStatus.Running, 10, null, null)),
                    ToolActivityCompletionState.Active),
                width, ToolDisplayMode.Summary),
            _ => throw new ArgumentOutOfRangeException(nameof(blockType)),
        };

        foreach (var line in lines.Where(l => !string.IsNullOrEmpty(l.Text)))
        {
            var cellWidth = TerminalCellText.Width(line.Text);
            Assert.True(
                cellWidth <= width,
                $"Row \"{line.Text}\" has {cellWidth} cells which exceeds requested width {width}");
        }
    }

    // ---------------------------------------------------------------------------
    // Child rows terminate on content, not on a trailing blank row
    // ---------------------------------------------------------------------------

    [Fact]
    public void Tool_result_ending_in_newline_terminates_the_tree_on_the_last_content_row()
    {
        // Command output almost always ends with a newline, which yields a trailing empty row. The
        // closing connector must land on the last row that carries text, and the blank row must stay blank.
        var block = new ToolTranscriptBlock(
            Guid.NewGuid(), "run", "{}", 5, "output\n", IsError: false, Complete: true);

        var lines = TranscriptBlockFormatter.Format(block, 60);

        Assert.Equal("   \u2514 output", lines[^2].Text); // "   └ output"
        Assert.Equal(string.Empty, lines[^1].Text);
    }

    [Fact]
    public void Full_activity_result_ending_in_newline_terminates_the_tree_on_the_last_content_row()
    {
        var calls = ImmutableArray.Create(
            new ToolActivityCall(
                "c0", "root", "read_file", """{"path":"a"}""",
                "unsafe", ToolCallStatus.Succeeded, 5, "line1\nline2\n", null));
        var activity = new ToolActivityTranscriptBlock(
            Guid.NewGuid(), "root", "act", calls, ToolActivityCompletionState.Completed);

        var lines = TranscriptBlockFormatter.Format(activity, 60, ToolDisplayMode.Full);

        Assert.Equal("   \u2502 line1", lines[^3].Text); // "   │ line1"
        Assert.Equal("   \u2514 line2", lines[^2].Text); // "   └ line2"
        Assert.Equal(string.Empty, lines[^1].Text);
    }

    [Fact]
    public void Blank_row_inside_tool_output_never_becomes_whitespace_only()
    {
        var block = new ToolTranscriptBlock(
            Guid.NewGuid(), "run", "{}", 5, "a\n\nb", IsError: false, Complete: true);

        var lines = TranscriptBlockFormatter.Format(block, 60);

        Assert.DoesNotContain(
            lines,
            line => line.Text.Length > 0 && line.Text.Trim().Length == 0);
    }

    // ---------------------------------------------------------------------------
    // Hostile input never reaches the terminal
    // ---------------------------------------------------------------------------

    [Fact]
    public void Permission_row_cannot_forge_extra_rows_or_reorder_its_command()
    {
        // A permission entry is the only place the UI shows WHAT the agent asked to run, so a newline
        // could forge a second "allowed" row and a bidi override could make a dangerous command read as
        // a benign one.
        var block = new PermissionTranscriptBlock(
            Guid.NewGuid(),
            "run_command",
            "rm -rf /\n\u001b[31mwrite_file safe.txt \u2192 allowed\u202e",
            Allowed: false);

        var lines = TranscriptBlockFormatter.Format(block, 200);

        var row = Assert.Single(lines);
        Assert.DoesNotContain('\u001b', row.Text);
        Assert.DoesNotContain('\u202e', row.Text);
        Assert.EndsWith("\u2192 denied", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_mode_tool_result_is_stripped_of_escapes()
    {
        var block = new ToolTranscriptBlock(
            Guid.NewGuid(), "run", "{}", 5, "ok\u001b[2J\u202edone", IsError: false, Complete: true);

        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Full);

        Assert.DoesNotContain(lines, line => line.Text.Contains('\u001b'));
        Assert.DoesNotContain(lines, line => line.Text.Contains('\u202e'));
    }

    // ---------------------------------------------------------------------------
    // The gutter is chrome: it draws with the row but is never copied
    // ---------------------------------------------------------------------------

    [Fact]
    public void Gutter_cells_are_recorded_so_copy_can_skip_them()
    {
        var user = TranscriptBlockFormatter.Format(
            new UserTranscriptBlock(Guid.NewGuid(), "hello"), 40);
        Assert.Equal(TranscriptGlyphs.MarkerCells, user[0].GutterCells);

        var activity = TranscriptBlockFormatter.Format(
            new ToolActivityTranscriptBlock(
                Guid.NewGuid(), "root", "act",
                ImmutableArray.Create(
                    new ToolActivityCall("c0", "root", "read_file", """{"path":"a"}""", "p",
                        ToolCallStatus.Running, 10, null, null)),
                ToolActivityCompletionState.Active),
            120, ToolDisplayMode.Summary);

        Assert.Equal(TranscriptGlyphs.MarkerCells, activity[0].GutterCells);
        Assert.Equal(TranscriptGlyphs.ChildCells, activity[1].GutterCells);
    }

    [Fact]
    public void Rows_without_a_gutter_record_no_gutter_cells()
    {
        var lines = TranscriptBlockFormatter.Format(
            new CommandOutputTranscriptBlock(Guid.NewGuid(), "plain output"), 40);

        Assert.All(lines, line => Assert.Equal(0, line.GutterCells));
    }}
