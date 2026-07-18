using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

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
}
