using System.Collections.Immutable;
using Coda.Sdk;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Covers the semantic <c>/context</c> render path: the immutable, UI-owned <see cref="ContextUsageData"/>
/// (mapped from the mutable SDK <see cref="ContextReport"/>), the reducer append of a
/// <see cref="ContextUsageTranscriptBlock"/>, and the compact, no-blank-line
/// <see cref="TranscriptBlockFormatter"/> projection where every context category owns a distinct,
/// glyph-legible marker and a distinct semantic role.
/// </summary>
public sealed class ContextUsageTests
{
    private static readonly string[] CategoryNames =
    [
        "System prompt", "System tools", "MCP tools", "Messages", "Autocompact buffer", "Free space",
    ];

    private static ContextReport SampleReport(bool isExact = true) => new()
    {
        Model = "usage-model-zzz",
        MaxTokens = 200_000,
        Categories =
        [
            new ContextCategory("System prompt", 20_000),
            new ContextCategory("System tools", 16_000),
            new ContextCategory("MCP tools", 12_000),
            new ContextCategory("Messages", 24_000),
            new ContextCategory("Autocompact buffer", 8_000),
            new ContextCategory("Free space", 120_000),
        ],
        UsedTokens = 72_000,
        IsExact = isExact,
        MessageCount = 5,
    };

    [Fact]
    public void FromReport_maps_every_field_and_category_immutably()
    {
        var report = SampleReport();

        var data = ContextUsageData.FromReport(report);

        Assert.Equal(report.Model, data.Model);
        Assert.Equal(report.UsedTokens, data.UsedTokens);
        Assert.Equal(report.MaxTokens, data.MaxTokens);
        Assert.Equal(report.Percentage, data.Percentage);
        Assert.Equal(report.MessageCount, data.MessageCount);
        Assert.Equal(report.IsExact, data.IsExact);
        Assert.Equal(
            report.Categories.Select(c => (c.Name, c.Tokens)),
            data.Categories.Select(c => (c.Name, c.Tokens)));
    }

    [Fact]
    public void Reducer_appends_a_context_usage_block()
    {
        var data = ContextUsageData.FromReport(SampleReport());

        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new ContextUsageEvent(data));

        var block = Assert.IsType<ContextUsageTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Same(data, block.Usage);
    }

    [Fact]
    public void Formatter_emits_no_blank_rows_and_a_heading_with_the_model()
    {
        var data = ContextUsageData.FromReport(SampleReport());
        var block = new ContextUsageTranscriptBlock(Guid.NewGuid(), data);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, line => string.IsNullOrWhiteSpace(line.Text));
        Assert.Equal(TranscriptRole.Heading, lines[0].Role);
        Assert.Contains("Context Usage", lines[0].Text, StringComparison.Ordinal);
        Assert.Contains("usage-model-zzz", lines[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_uses_all_six_distinct_glyphs_and_context_roles()
    {
        var data = ContextUsageData.FromReport(SampleReport());
        var block = new ContextUsageTranscriptBlock(Guid.NewGuid(), data);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        foreach (var glyph in new[] { '\u25c6', '\u25b2', '\u25cf', '\u25a0', '\u2592', '\u2591' })
        {
            Assert.Contains(lines, line => line.Text.Contains(glyph, StringComparison.Ordinal));
        }

        foreach (var role in new[]
        {
            TranscriptRole.ContextSystemPrompt,
            TranscriptRole.ContextSystemTools,
            TranscriptRole.ContextMcpTools,
            TranscriptRole.ContextMessages,
            TranscriptRole.ContextAutocompactBuffer,
            TranscriptRole.ContextFreeSpace,
        })
        {
            Assert.Contains(lines, line => line.Role == role);
        }
    }

    [Fact]
    public void Formatter_shows_one_category_line_per_category_with_label_and_tokens()
    {
        var data = ContextUsageData.FromReport(SampleReport());
        var block = new ContextUsageTranscriptBlock(Guid.NewGuid(), data);

        var lines = TranscriptBlockFormatter.Format(block, width: 120);

        foreach (var name in CategoryNames)
        {
            Assert.Contains(lines, line => line.Text.Contains(name, StringComparison.Ordinal)
                && line.Text.Contains("tokens", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Formatter_adds_estimated_note_only_when_not_exact()
    {
        var exact = new ContextUsageTranscriptBlock(
            Guid.NewGuid(), ContextUsageData.FromReport(SampleReport(isExact: true)));
        var estimated = new ContextUsageTranscriptBlock(
            Guid.NewGuid(), ContextUsageData.FromReport(SampleReport(isExact: false)));

        var exactLines = TranscriptBlockFormatter.Format(exact, width: 80);
        var estimatedLines = TranscriptBlockFormatter.Format(estimated, width: 80);

        Assert.DoesNotContain(exactLines, line => line.Text.Contains("estimated", StringComparison.Ordinal));
        Assert.Contains(estimatedLines, line => line.Text.Contains("estimated", StringComparison.Ordinal));
    }

    [Fact]
    public void Formatter_bounds_each_category_bar_to_ten_symbols()
    {
        var report = new ContextReport
        {
            Model = "full-window",
            MaxTokens = 100,
            Categories = [new ContextCategory("Free space", 100)],
            UsedTokens = 0,
            IsExact = true,
            MessageCount = 0,
        };
        var block = new ContextUsageTranscriptBlock(Guid.NewGuid(), ContextUsageData.FromReport(report));

        var lines = TranscriptBlockFormatter.Format(block, width: 200);

        var categoryLine = Assert.Single(lines, line => line.Role == TranscriptRole.ContextFreeSpace);
        var barGlyphs = categoryLine.Text.Count(ch => ch == '\u2591');
        Assert.InRange(barGlyphs, 1, 11);
    }
}
