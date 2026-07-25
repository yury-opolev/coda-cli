using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for ThinkingTranscriptBlock rendering in <see cref="TranscriptBlockFormatter"/> across all
/// display modes: Summary (default), Compact, Full, and Hidden.
/// </summary>
public sealed class ThinkingTranscriptBlockFormatterTests
{
    // A fixed StartedAt in the past so frozen-ElapsedMs tests are deterministic.
    private static readonly DateTimeOffset StartedAt = new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);

    // A complete block with a known ElapsedMs so the "Thought for Xs" line is deterministic.
    private static ThinkingTranscriptBlock CompletedBlock(string text, long elapsedMs = 3000L) =>
        new(Guid.NewGuid(), text, Complete: true, StartedAt, ElapsedMs: elapsedMs, ThinkingTokens: null);

    private static ThinkingTranscriptBlock ActiveBlock(string text) =>
        new(Guid.NewGuid(), text, Complete: false, StartedAt, ElapsedMs: null, ThinkingTokens: null);

    // ─── Hidden ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Hidden_mode_produces_no_lines()
    {
        var block = CompletedBlock("lots of reasoning");
        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Hidden);

        Assert.Empty(lines);
    }

    [Fact]
    public void Active_block_in_hidden_mode_produces_no_lines()
    {
        var block = ActiveBlock("streaming");
        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Hidden);

        Assert.Empty(lines);
    }

    // ─── Summary ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_mode_on_completed_block_shows_one_status_line()
    {
        var block = CompletedBlock("some reasoning", elapsedMs: 5000L);
        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Summary);

        Assert.Single(lines);
        var text = lines[0].Text;
        Assert.Contains("Thought for 5s", text);
        Assert.Equal(TranscriptRole.Notification, lines[0].Role);
    }

    [Fact]
    public void Summary_mode_on_completed_block_does_not_show_reasoning_text()
    {
        const string reasoning = "This is the full reasoning that should not appear in summary.";
        var block = CompletedBlock(reasoning);
        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Summary);

        Assert.DoesNotContain(lines, l => l.Text.Contains("full reasoning"));
    }

    [Fact]
    public void Summary_mode_with_tokens_shows_token_count()
    {
        var block = new ThinkingTranscriptBlock(
            Guid.NewGuid(),
            "reasoning",
            Complete: false,
            StartedAt,
            ElapsedMs: null,
            ThinkingTokens: 320);

        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Summary);

        Assert.Single(lines);
        Assert.Contains("320 tok", lines[0].Text);
    }

    // ─── Compact ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Compact_mode_on_completed_block_shows_status_line()
    {
        var block = CompletedBlock("some reasoning", elapsedMs: 2000L);
        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Compact);

        Assert.True(lines.Count >= 1);
        Assert.Contains("Thought for 2s", lines[0].Text);
    }

    [Fact]
    public void Compact_mode_includes_reasoning_text_lines()
    {
        var longReasoning = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"Line {i}"));
        var block = CompletedBlock(longReasoning, elapsedMs: 1000L);
        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Compact);

        // Should have status + up to 5 tail lines (compact tail)
        Assert.True(lines.Count > 1, "Compact should include reasoning text lines");
        // The tail lines should contain the end of the reasoning
        var allText = string.Join(" ", lines.Select(l => l.Text));
        Assert.Contains("Line 10", allText);
    }

    [Fact]
    public void Compact_mode_shows_at_most_5_tail_lines_for_long_reasoning()
    {
        var veryLong = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"Line {i}"));
        var block = CompletedBlock(veryLong, elapsedMs: 1000L);
        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Compact);

        // status line + up to 5 tail lines (may vary due to wrapping, but not all 20)
        // The lines from early in the reasoning should NOT appear
        var allText = string.Join(" ", lines.Select(l => l.Text));
        Assert.DoesNotContain("Line 1 ", allText + " ");  // "Line 1" early - should not appear
        Assert.Contains("Line 20", allText);              // last line should appear
    }

    // ─── Full ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Full_mode_on_completed_block_shows_status_and_full_text()
    {
        var block = CompletedBlock("## Heading\n\nParagraph text.", elapsedMs: 4000L);
        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Full);

        Assert.True(lines.Count >= 2, "Full mode should show status + text");
        Assert.Contains("Thought for 4s", lines[0].Text);
        var allText = string.Join(" ", lines.Select(l => l.Text));
        Assert.Contains("Paragraph text.", allText);
    }

    [Fact]
    public void Full_mode_renders_markdown_heading()
    {
        var block = CompletedBlock("# My Heading\n\nSome text.", elapsedMs: 1000L);
        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Full);

        var headingLine = lines.FirstOrDefault(l => l.Role == TranscriptRole.Heading);
        Assert.Equal(TranscriptRole.Heading, headingLine.Role);
        Assert.Contains("My Heading", headingLine.Text);
    }

    // ─── Default (no mode arg) ────────────────────────────────────────────────────

    [Fact]
    public void Default_format_overload_uses_full_mode_for_thinking_block()
    {
        var block = CompletedBlock("reasoning text", elapsedMs: 1000L);

        // Format(block, width) defaults to Full mode
        var linesDefault = TranscriptBlockFormatter.Format(block, 80);
        var linesFull = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Full);

        Assert.Equal(linesDefault.Count, linesFull.Count);
        for (var i = 0; i < linesDefault.Count; i++)
        {
            Assert.Equal(linesDefault[i].Text, linesFull[i].Text);
        }
    }

    // ─── Status line format ───────────────────────────────────────────────────────

    [Fact]
    public void Status_line_uses_notification_role()
    {
        var block = CompletedBlock("reasoning");
        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Summary);

        Assert.Equal(TranscriptRole.Notification, lines[0].Role);
    }

    [Fact]
    public void Active_block_status_contains_Thinking_ellipsis()
    {
        // We need a block whose ElapsedMs=null so it reports "Thinking…" 
        // Use a block with a recent StartedAt so elapsed is small.
        var recent = new ThinkingTranscriptBlock(
            Guid.NewGuid(),
            "streaming",
            Complete: false,
            DateTimeOffset.UtcNow.AddMilliseconds(-500),
            ElapsedMs: null,
            ThinkingTokens: null);

        var lines = TranscriptBlockFormatter.Format(recent, 80, ToolDisplayMode.Summary);
        Assert.Single(lines);
        // Text should contain "Thinking" (either the word or the emoji prefix)
        Assert.Contains("Thinking", lines[0].Text);
    }

    // ─── Fix 3: injectable TimeProvider for live elapsed ─────────────────────────

    [Fact]
    public void Active_block_live_elapsed_uses_injected_TimeProvider()
    {
        var tp = new ManualTimeProvider();
        // StartedAt = tp.GetUtcNow() at t=0 (Origin). Advance to t=5s so "now - startedAt = 5s".
        var startedAt = tp.GetUtcNow();
        tp.Advance(TimeSpan.FromSeconds(5));

        var block = new ThinkingTranscriptBlock(
            Guid.NewGuid(),
            "reasoning",
            Complete: false,
            startedAt,
            ElapsedMs: null,
            ThinkingTokens: null);

        var lines = TranscriptBlockFormatter.Format(block, 80, ToolDisplayMode.Summary, tp);

        Assert.Single(lines);
        Assert.Contains("Thinking", lines[0].Text);
        Assert.Contains("5s", lines[0].Text); // deterministic elapsed from injected clock
    }
}
