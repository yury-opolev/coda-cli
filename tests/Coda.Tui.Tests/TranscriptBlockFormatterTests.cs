using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

using System.Collections.Immutable;
using Coda.Agent;

namespace Coda.Tui.Tests;

/// <summary>
/// Exercises the shared, host-neutral <see cref="TranscriptBlockFormatter"/>. The formatter parses
/// assistant markdown through Markdig's AST and projects every transcript block onto attributed,
/// wrapped lines that never contain ANSI/control sequences, so both the inline and full-screen shells
/// can render the same content with role-based color.
/// </summary>
public sealed class TranscriptBlockFormatterTests
{
    [Fact]
    public void Assistant_markdown_becomes_attributed_lines_without_ansi()
    {
        var block = new AssistantTranscriptBlock(Guid.NewGuid(), "# Heading\n\n**bold** text", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 40);

        Assert.Equal(["Heading", string.Empty, "bold text"], lines.Select(line => line.Text));
        Assert.Equal(TranscriptRole.Heading, lines[0].Role);
        Assert.Equal(TranscriptRole.Assistant, lines[2].Role);
        Assert.DoesNotContain(lines, line => line.Text.Contains("\u001b[", StringComparison.Ordinal));
    }

    [Fact]
    public void User_block_keeps_text_with_user_role()
    {
        var block = new UserTranscriptBlock(Guid.NewGuid(), "hello world");

        var lines = TranscriptBlockFormatter.Format(block, width: 40);

        var line = Assert.Single(lines);
        Assert.Equal("hello world", line.Text);
        Assert.Equal(TranscriptRole.User, line.Role);
        // A user message renders as a full-width background block.
        Assert.True(line.FillWidth);
        // Without a captured send time there is no right-aligned annotation.
        Assert.Null(line.RightText);
    }

    [Fact]
    public void User_block_with_sent_time_annotates_the_first_row_with_local_hhmm()
    {
        var sentAt = new DateTimeOffset(2026, 7, 21, 9, 5, 0, TimeSpan.Zero);
        var block = new UserTranscriptBlock(Guid.NewGuid(), "hello", sentAt);

        var lines = TranscriptBlockFormatter.Format(block, width: 40);

        var line = Assert.Single(lines);
        Assert.Equal("hello", line.Text);
        Assert.True(line.FillWidth);
        // The sent time is attached to the block (HH:mm), drawn as a right annotation — never mixed into the
        // copyable text.
        Assert.Equal("09:05", line.RightText);
        Assert.Equal(1, line.RightTextTrailingCells);
        Assert.DoesNotContain(":", line.Text);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public void User_timestamp_reserves_one_trailing_cell(int width)
    {
        var sentAt = new DateTimeOffset(2026, 7, 21, 9, 5, 0, TimeSpan.Zero);
        var lines = TranscriptBlockFormatter.Format(
            new UserTranscriptBlock(Guid.NewGuid(), "timestamped user message", sentAt),
            width);

        var first = lines[0];
        if (first.RightText is { } timestamp)
        {
            Assert.Equal("09:05", timestamp);
            Assert.Equal(1, first.RightTextTrailingCells);
            Assert.True(
                TerminalCellText.Width(first.Text) + 1 + TerminalCellText.Width(timestamp) + first.RightTextTrailingCells <= width);
        }
    }

    [Fact]
    public void User_block_reserves_first_row_cells_so_text_never_overlaps_the_time()
    {
        var sentAt = new DateTimeOffset(2026, 7, 21, 9, 5, 0, TimeSpan.Zero);
        // Twenty single-cell characters at width 20 would fill the row; with a reserved time zone the first
        // row must wrap earlier so the "09:05" annotation cannot overlap the text.
        var block = new UserTranscriptBlock(Guid.NewGuid(), new string('x', 20), sentAt);

        var lines = TranscriptBlockFormatter.Format(block, width: 20);

        Assert.True(lines.Count >= 2, "the reserved time zone must force the first row to wrap earlier");
        Assert.Equal("09:05", lines[0].RightText);
        // "09:05" is five cells plus one cell on each side, so the first row keeps at most 13 cells of text.
        Assert.True(TerminalCellText.Width(lines[0].Text) <= 13);
        Assert.Equal(1, lines[0].RightTextTrailingCells);
        Assert.All(lines, line => Assert.True(line.FillWidth));
        Assert.Null(lines[1].RightText);
    }

    [Fact]
    public void Tool_block_shows_name_and_result_with_tool_role()
    {
        var block = new ToolTranscriptBlock(
            Guid.NewGuid(), "grep", "{\"pattern\":\"x\"}", 12, "match one\nmatch two", IsError: false, Complete: true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.Equal(TranscriptRole.Tool, lines[0].Role);
        Assert.Contains(lines, line => line.Text.Contains("grep", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Text == "match one");
        Assert.Contains(lines, line => line.Text == "match two");
        Assert.DoesNotContain(lines, line => line.Text.Contains("\u001b[", StringComparison.Ordinal));
    }

    [Fact]
    public void Compact_tool_block_shows_sanitized_capped_preview_and_status_without_result()
    {
        var input = "\u001b[31mline one\nline two\u001b[0m " + new string('x', 140);
        var block = new ToolTranscriptBlock(
            Guid.NewGuid(), "grep", input, 12, "secret result", IsError: false, Complete: true);

        var lines = TranscriptBlockFormatter.Format(block, width: 200, ToolDisplayMode.Compact);
        var text = string.Join('\n', lines.Select(line => line.Text));

        Assert.Single(lines);
        Assert.Contains("grep", text, StringComparison.Ordinal);
        Assert.Contains("[success]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret result", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\n', text);
        Assert.True(text.Length <= 128 + "grep  [success]".Length);
        Assert.Equal(input, block.InputJson);
    }

    [Fact]
    public void Tiny_tool_block_is_hidden_without_mutating_the_block()
    {
        var block = new ToolTranscriptBlock(
            Guid.NewGuid(), "grep", "{\"pattern\":\"x\"}", 12, "result", IsError: false, Complete: true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80, ToolDisplayMode.Tiny);

        Assert.Empty(lines);
        Assert.Equal("{\"pattern\":\"x\"}", block.InputJson);
        Assert.Equal("result", block.Result);
    }

    [Fact]
    public void Compact_failed_tool_block_reports_error_once()
    {
        var block = new ToolTranscriptBlock(
            Guid.NewGuid(), "write_file", "{}", null, "denied", IsError: true, Complete: true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80, ToolDisplayMode.Compact);

        Assert.Equal("write_file {} [error]", Assert.Single(lines).Text);
    }

    [Fact]
    public void Failed_tool_block_uses_error_role()
    {
        var block = new ToolTranscriptBlock(
            Guid.NewGuid(), "write_file", "{}", 4, "permission denied", IsError: true, Complete: true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.Contains(lines, line => line.Role == TranscriptRole.Error);
    }

    [Fact]
    public void Diff_block_maps_each_line_to_diff_role()
    {
        var block = new DiffTranscriptBlock(Guid.NewGuid(), "--- a\n+++ b\n-old\n+new");

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.Equal(["--- a", "+++ b", "-old", "+new"], lines.Select(line => line.Text));
        Assert.All(lines, line => Assert.Equal(TranscriptRole.Diff, line.Role));
    }

    [Fact]
    public void Permission_block_shows_decision_with_permission_role()
    {
        var block = new PermissionTranscriptBlock(Guid.NewGuid(), "write_file", "path/x", Allowed: true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.All(lines, line => Assert.Equal(TranscriptRole.Permission, line.Role));
        Assert.Contains(lines, line => line.Text.Contains("write_file", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Text.Contains("allowed", StringComparison.Ordinal));
    }

    [Fact]
    public void Question_block_shows_answer_with_question_role()
    {
        var block = new UserQuestionTranscriptBlock(Guid.NewGuid(), "Proceed?", Answer: "yes");

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.All(lines, line => Assert.Equal(TranscriptRole.Question, line.Role));
        Assert.Contains(lines, line => line.Text.Contains("Proceed?", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Text.Contains("yes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(UiNotificationLevel.Error, TranscriptRole.Error)]
    [InlineData(UiNotificationLevel.Warning, TranscriptRole.Warning)]
    [InlineData(UiNotificationLevel.Information, TranscriptRole.Notification)]
    public void Notice_block_maps_level_to_role(UiNotificationLevel level, TranscriptRole expected)
    {
        var block = new NoticeTranscriptBlock(Guid.NewGuid(), "boom", level);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.Contains(lines, line => line.Role == expected && line.Text.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public void Fenced_code_block_uses_code_role()
    {
        var block = new AssistantTranscriptBlock(Guid.NewGuid(), "```\ncode line1\ncode line2\n```", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.Contains(lines, line => line.Role == TranscriptRole.Code && line.Text == "code line1");
        Assert.Contains(lines, line => line.Role == TranscriptRole.Code && line.Text == "code line2");
    }

    [Fact]
    public void Long_paragraph_wraps_within_the_display_width()
    {
        var block = new AssistantTranscriptBlock(
            Guid.NewGuid(), "aaaa bbbb cccc dddd eeee ffff gggg hhhh", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 10);

        Assert.True(lines.Count > 1);
        Assert.All(lines, line => Assert.True(line.Text.Length <= 10));
    }

    [Fact]
    public void Wide_unicode_wraps_by_display_cells_not_char_count()
    {
        var block = new AssistantTranscriptBlock(Guid.NewGuid(), "\u4f60\u597d\u4e16\u754c", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 4);

        Assert.Equal(["\u4f60\u597d", "\u4e16\u754c"], lines.Select(line => line.Text));
    }

    [Fact]
    public void User_block_wraps_wide_runs_by_display_cells()
    {
        // "界界é": 界 and 界 fill four cells, é (one cell) overflows to the next row.
        var block = new UserTranscriptBlock(Guid.NewGuid(), "\u754c\u754c\u00e9");

        var lines = TranscriptBlockFormatter.Format(block, width: 4);

        Assert.Equal(["\u754c\u754c", "\u00e9"], lines.Select(line => line.Text));
        Assert.All(lines, line => Assert.Equal(TranscriptRole.User, line.Role));
    }

    [Fact]
    public void Grapheme_clusters_are_never_split_across_lines()
    {
        // "a" + combining acute, then "b" + combining acute: two graphemes, one display cell each.
        var block = new AssistantTranscriptBlock(Guid.NewGuid(), "a\u0301b\u0301", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 1);

        Assert.Equal(["a\u0301", "b\u0301"], lines.Select(line => line.Text));
    }

    [Fact]
    public void Formatter_uses_shared_cell_width_for_wide_and_combining_graphemes()
    {
        // 界界 fills four cells; the combining acute stays attached to e, which overflows to the next row.
        var block = new UserTranscriptBlock(Guid.NewGuid(), "\u754c\u754ce\u0301");

        var lines = TranscriptBlockFormatter.Format(block, width: 4);

        Assert.Equal(["\u754c\u754c", "e\u0301"], lines.Select(line => line.Text));
    }

    [Fact]
    public void Plain_text_projection_joins_lines_with_newlines()
    {
        var block = new DiffTranscriptBlock(Guid.NewGuid(), "-old\n+new");

        var plain = TranscriptBlockFormatter.FormatPlainText(block, width: 80);

        Assert.Equal("-old\n+new", plain);
    }

    [Fact]
    public void Preformatted_content_preserves_leading_and_internal_whitespace()
    {
        var block = new CommandOutputTranscriptBlock(Guid.NewGuid(), "    indented   spaced\nplain");

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.Equal(["    indented   spaced", "plain"], lines.Select(line => line.Text));
        Assert.All(lines, line => Assert.Equal(TranscriptRole.Code, line.Role));
    }

    [Fact]
    public void Fenced_code_preserves_indentation()
    {
        var block = new AssistantTranscriptBlock(Guid.NewGuid(), "```\nif x:\n    return 1\n```", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.Contains(lines, line => line.Role == TranscriptRole.Code && line.Text == "    return 1");
    }

    [Fact]
    public void Nested_unordered_list_retains_all_items_in_order()
    {
        var block = new AssistantTranscriptBlock(
            Guid.NewGuid(), "- top one\n  - nested a\n  - nested b\n- top two", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        var order = new[] { "top one", "nested a", "nested b", "top two" };
        AssertContentInOrder(lines, order);
    }

    [Fact]
    public void Ordered_list_item_with_nested_bullets_retains_numbering_and_details()
    {
        var block = new AssistantTranscriptBlock(
            Guid.NewGuid(), "1. first step\n   - detail a\n   - detail b\n2. second step", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        AssertContentInOrder(lines, new[] { "first step", "detail a", "detail b", "second step" });
        Assert.Contains(lines, line => line.Text.Contains("1. first step", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Text.Contains("2. second step", StringComparison.Ordinal));
    }

    [Fact]
    public void Fenced_code_block_under_list_item_retains_code_text()
    {
        var block = new AssistantTranscriptBlock(
            Guid.NewGuid(), "- run this\n\n  ```\n  echo hello\n  ```", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.Contains(lines, line => line.Text.Contains("run this", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Role == TranscriptRole.Code && line.Text.Contains("echo hello", StringComparison.Ordinal));

        var plain = TranscriptBlockFormatter.FormatPlainText(block, width: 80);
        Assert.Contains("echo hello", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_continuation_under_list_item_retains_content()
    {
        var block = new AssistantTranscriptBlock(
            Guid.NewGuid(), "- item head\n\n  > quoted note\n\n  trailing paragraph", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        AssertContentInOrder(lines, new[] { "item head", "quoted note", "trailing paragraph" });
    }

    [Fact]
    public void Deeply_nested_list_recurses_without_corrupting_indentation()
    {
        var block = new AssistantTranscriptBlock(
            Guid.NewGuid(), "- a\n  - b\n    - c\n      - d", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        AssertContentInOrder(lines, new[] { "a", "b", "c", "d" });

        int IndentOf(string needle)
        {
            var text = lines.First(line => line.Text.Contains(needle, StringComparison.Ordinal)).Text;
            return text.Length - text.TrimStart().Length;
        }

        // Each deeper level must be indented strictly more than its parent.
        Assert.True(IndentOf("a") < IndentOf("b"));
        Assert.True(IndentOf("b") < IndentOf("c"));
        Assert.True(IndentOf("c") < IndentOf("d"));
        Assert.DoesNotContain(lines, line => line.Text.Contains("\u001b[", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Summary_renders_every_running_child_through_five(int count)
    {
        var lines = TranscriptBlockFormatter.Format(
            ActivityWithRunningCalls(count, "read_file"),
            width: 120,
            ToolDisplayMode.Summary);

        Assert.Equal(count + 1, lines.Count);
        Assert.Equal($"Running {count} tool{(count == 1 ? "" : "s")}...", lines[0].Text);
        Assert.Equal(count == 1 ? "`- Reading file-0" : "|- Reading file-0", lines[1].Text);
        Assert.Equal($"`- Reading file-{count - 1}", lines[^1].Text);
    }

    [Fact]
    public void Six_running_calls_reserve_the_fifth_child_row_for_overflow()
    {
        var lines = TranscriptBlockFormatter.Format(
            ActivityWithRunningCalls(6, "read_file"),
            width: 120,
            ToolDisplayMode.Summary);

        Assert.Equal(6, lines.Count);
        Assert.Equal("Running 6 tools...", lines[0].Text);
        Assert.Equal("|- Reading file-0", lines[1].Text);
        Assert.Equal("|- Reading file-3", lines[4].Text);
        Assert.Equal("`- ...and 2 more", lines[^1].Text);
    }

    [Fact]
    public void Summary_counts_every_call_but_renders_only_running_children()
    {
        var activity = Activity(
            ToolActivityCompletionState.Active,
            Call("read_file", """{"path":"done"}""", ToolCallStatus.Succeeded),
            Call("read_file", """{"path":"queued"}""", ToolCallStatus.Pending),
            Call("read_file", """{"path":"first"}""", ToolCallStatus.Running),
            Call("grep", """{"pattern":"second"}""", ToolCallStatus.Running));

        var lines = TranscriptBlockFormatter.Format(activity, width: 120, ToolDisplayMode.Summary);

        Assert.Equal(
            ["Running 4 tools...", "|- Reading first", "`- Searching for second"],
            lines.Select(line => line.Text));
    }

    [Fact]
    public void Summary_uses_shell_command_wording_only_for_homogeneous_run_commands()
    {
        var oneRunning = TranscriptBlockFormatter.Format(
            ActivityWithRunningCalls(1, "run_command"),
            width: 120,
            ToolDisplayMode.Summary);
        var manyRunning = TranscriptBlockFormatter.Format(
            ActivityWithRunningCalls(2, "run_command"),
            width: 120,
            ToolDisplayMode.Summary);

        Assert.Equal("Running 1 shell command...", oneRunning[0].Text);
        Assert.Equal("Running 2 shell commands...", manyRunning[0].Text);

        var oneCompleted = Activity(
            ToolActivityCompletionState.Completed,
            Call("run_command", """{"command":"echo one"}""", ToolCallStatus.Succeeded));
        var manyCompleted = Activity(
            ToolActivityCompletionState.Completed,
            Call("run_command", """{"command":"echo one"}""", ToolCallStatus.Succeeded),
            Call("run_command", """{"command":"echo two"}""", ToolCallStatus.Succeeded));
        var homogeneousOther = Activity(
            ToolActivityCompletionState.Completed,
            Call("read_file", """{"path":"one"}""", ToolCallStatus.Succeeded));
        var mixed = Activity(
            ToolActivityCompletionState.Completed,
            Call("read_file", """{"path":"one"}""", ToolCallStatus.Succeeded),
            Call("grep", """{"pattern":"two"}""", ToolCallStatus.Succeeded));

        Assert.Equal("Ran 1 shell command", Assert.Single(TranscriptBlockFormatter.Format(oneCompleted, 120, ToolDisplayMode.Summary)).Text);
        Assert.Equal("Ran 2 shell commands", Assert.Single(TranscriptBlockFormatter.Format(manyCompleted, 120, ToolDisplayMode.Summary)).Text);
        Assert.Equal("Ran 1 tool", Assert.Single(TranscriptBlockFormatter.Format(homogeneousOther, 120, ToolDisplayMode.Summary)).Text);
        Assert.Equal("Ran 2 tools", Assert.Single(TranscriptBlockFormatter.Format(mixed, 120, ToolDisplayMode.Summary)).Text);
    }

    [Fact]
    public void Summary_completed_and_cancelled_activity_uses_one_shared_suffix_line()
    {
        var activity = Activity(
            ToolActivityCompletionState.Cancelled,
            Call("read_file", """{"path":"failed"}""", ToolCallStatus.Failed, error: "denied"),
            Call("grep", """{"pattern":"cancelled"}""", ToolCallStatus.Cancelled),
            Call("grep", """{"pattern":"skipped"}""", ToolCallStatus.Skipped));

        var lines = TranscriptBlockFormatter.Format(activity, width: 120, ToolDisplayMode.Summary);

        Assert.Equal("Ran 3 tools - 1 failed, cancelled", Assert.Single(lines).Text);
        Assert.Equal(
            "Ran 1 tool",
            Assert.Single(TranscriptBlockFormatter.Format(
                Activity(
                    ToolActivityCompletionState.Completed,
                    Call("grep", """{"pattern":"skipped"}""", ToolCallStatus.Skipped)),
                120,
                ToolDisplayMode.Summary)).Text);
        Assert.Equal(
            "Ran 1 tool - cancelled",
            Assert.Single(TranscriptBlockFormatter.Format(
                Activity(
                    ToolActivityCompletionState.Cancelled,
                    Call("grep", """{"pattern":"skipped"}""", ToolCallStatus.Skipped)),
                120,
                ToolDisplayMode.Summary)).Text);
    }

    [Theory]
    [InlineData("run_command", """{"command":"echo hello"}""", "$ echo hello")]
    [InlineData("read_file", """{"path":"src/file.cs"}""", "Reading src/file.cs")]
    [InlineData("write_file", """{"path":"src/file.cs"}""", "Writing src/file.cs")]
    [InlineData("edit_file", """{"path":"src/file.cs"}""", "Editing src/file.cs")]
    [InlineData("notebook_edit", """{"notebook_path":"work.ipynb"}""", "Editing work.ipynb")]
    [InlineData("grep", """{"pattern":"needle"}""", "Searching for needle")]
    [InlineData("glob", """{"pattern":"**/*.cs"}""", "Searching for **/*.cs")]
    [InlineData("web_search", """{"query":"coda"}""", "Searching for coda")]
    [InlineData("tool_search", """{"query":"files"}""", "Searching for files")]
    public void Activity_preview_maps_known_tools(string toolName, string inputJson, string expected)
    {
        Assert.Equal(expected, ToolActivityPreview.Create(toolName, inputJson));
    }

    [Fact]
    public void Activity_preview_redacts_before_extracting_sanitizes_and_bounds()
    {
        var preview = ToolActivityPreview.Create(
            "run_command",
            """{"command":"echo \u001b[31mhello\nworld","password":"not-visible"}""");
        var fallback = ToolActivityPreview.Create(
            "custom\u001b[2J",
            """{"password":"not-visible","value":"ok"}""");
        var headerFallback = ToolActivityPreview.Create(
            "custom",
            """{"x-api-key":"not-visible","set-cookie":"not-visible","proxy-authorization":"not-visible"}""");
        var malformed = ToolActivityPreview.Create("read_file", "{not valid");
        var missing = ToolActivityPreview.Create("read_file", "{}");
        var bounded = ToolActivityPreview.Create(
            "custom",
            "{\"value\":\"" + new string('界', 200) + "\"}");

        Assert.Equal("$ echo hello world", preview);
        Assert.DoesNotContain("not-visible", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("not-visible", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("not-visible", headerFallback, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', fallback);
        Assert.DoesNotContain('\n', fallback);
        Assert.DoesNotContain('\r', fallback);
        Assert.NotEmpty(malformed);
        Assert.Equal("Reading read_file", missing);
        Assert.True(TerminalCellText.Width(bounded) <= ToolDisplayModeResolver.CompactInputPreviewMax);
    }

    [Fact]
    public void Activity_modes_project_verbose_compact_and_tiny_without_changing_legacy_tool_modes()
    {
        var activity = Activity(
            ToolActivityCompletionState.Active,
            Call("run_command", """{"command":"echo \u001b[31mhello"}""", ToolCallStatus.Running, result: "line one\nline two"),
            Call("write_file", """{"path":"x"}""", ToolCallStatus.Failed, error: "\u001b[2Jdenied"));

        var verbose = TranscriptBlockFormatter.Format(activity, width: 120, ToolDisplayMode.Verbose);
        var compact = TranscriptBlockFormatter.Format(activity, width: 120, ToolDisplayMode.Compact);
        var tiny = TranscriptBlockFormatter.Format(activity, width: 120, ToolDisplayMode.Tiny);
        var legacy = new ToolTranscriptBlock(
            Guid.NewGuid(), "grep", "{}", 1, "legacy result", IsError: false, Complete: true);

        Assert.Contains(verbose, line => line.Text.Contains("line one", StringComparison.Ordinal));
        Assert.Contains(verbose, line => line.Text.Contains("denied", StringComparison.Ordinal));
        Assert.DoesNotContain(verbose, line => line.Text.Contains('\u001b'));
        Assert.Equal(2, compact.Count);
        Assert.All(compact, line => Assert.DoesNotContain('\u001b', line.Text));
        Assert.DoesNotContain(compact, line => line.Text.Contains("line one", StringComparison.Ordinal));
        Assert.Empty(tiny);
        Assert.Contains(
            TranscriptBlockFormatter.Format(legacy, 120, ToolDisplayMode.Verbose),
            line => line.Text == "legacy result");
        Assert.Equal(
            "grep {} [success]",
            Assert.Single(TranscriptBlockFormatter.Format(legacy, 120, ToolDisplayMode.Compact)).Text);
        Assert.Empty(TranscriptBlockFormatter.Format(legacy, 120, ToolDisplayMode.Tiny));
    }

    [Fact]
    public void Summary_mode_is_resolved_without_changing_tiny_fallback()
    {
        var summary = ToolDisplayModeResolver.Resolve("summary");
        var fallback = ToolDisplayModeResolver.Resolve(null);

        Assert.Equal(ToolDisplayMode.Summary, summary.Mode);
        Assert.True(summary.IsValid);
        Assert.Equal(ToolDisplayMode.Tiny, fallback.Mode);
    }

    private static void AssertContentInOrder(
        IReadOnlyList<TranscriptRenderLine> lines, IReadOnlyList<string> needles)
    {
        var lastIndex = -1;
        foreach (var needle in needles)
        {
            var index = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Text.Contains(needle, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            Assert.True(index >= 0, $"Expected content '{needle}' to be present.");
            Assert.True(index > lastIndex, $"Expected content '{needle}' to appear after previous content.");
            lastIndex = index;
        }
    }

    private static ToolActivityTranscriptBlock Activity(
        ToolActivityCompletionState completionState,
        params ToolActivityCall[] calls) =>
        new(Guid.NewGuid(), "root", "activity", calls.ToImmutableArray(), completionState);

    private static ToolActivityTranscriptBlock ActivityWithRunningCalls(int count, string toolName) =>
        Activity(
            ToolActivityCompletionState.Active,
            Enumerable.Range(0, count)
                .Select(index => Call(
                    toolName,
                    toolName == "run_command"
                        ? $$"""{"command":"echo {{index}}"}"""
                        : $$"""{"path":"file-{{index}}"}""",
                    ToolCallStatus.Running))
                .ToArray());

    private static ToolActivityCall Call(
        string toolName,
        string inputJson,
        ToolCallStatus status,
        string? result = null,
        string? error = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            "root:root",
            toolName,
            inputJson,
            "unsafe preview",
            status,
            ElapsedMs: 10,
            Result: result,
            Error: error);
}
