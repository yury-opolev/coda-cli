using System;
using System.Linq;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace Coda.Tui.Tests;

/// <summary>
/// Covers the projection of tokenizer output onto rendered transcript rows: fenced code blocks and
/// diff bodies must carry <see cref="SyntaxSpan"/>s whose columns are measured in terminal cells,
/// re-based per render line after wrapping, and offset past any gutter the row already carries.
/// </summary>
public sealed class SyntaxHighlightTests
{
    private static TranscriptRenderLine[] FormatMarkdown(string markdown, int width = 80) =>
        TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), markdown, true), width).ToArray();

    private static TranscriptRenderLine[] FormatDiff(string patch, int width = 80) =>
        TranscriptBlockFormatter.Format(
            new DiffTranscriptBlock(Guid.NewGuid(), patch), width).ToArray();

    private static SyntaxSpan[] SpansOn(TranscriptRenderLine line) =>
        line.Syntax?.ToArray() ?? [];

    // -----------------------------------------------------------------------
    // Fenced code blocks
    // -----------------------------------------------------------------------

    [Fact]
    public void A_fenced_code_block_with_a_known_language_highlights_its_keywords()
    {
        var lines = FormatMarkdown("```csharp\npublic int Count = 42;\n```");

        var row = Assert.Single(lines, l => l.Text.Contains("public int Count", StringComparison.Ordinal));
        var spans = SpansOn(row);

        Assert.Contains(spans, s => s.Kind == SyntaxTokenKind.Keyword);
        Assert.Contains(spans, s => s.Kind == SyntaxTokenKind.Type);
        Assert.Contains(spans, s => s.Kind == SyntaxTokenKind.Number);
    }

    [Fact]
    public void A_keyword_span_covers_exactly_the_keyword_cells()
    {
        var lines = FormatMarkdown("```csharp\npublic class Foo\n```");
        var row = Assert.Single(lines, l => l.Text.Contains("public class Foo", StringComparison.Ordinal));

        var start = row.Text.IndexOf("public", StringComparison.Ordinal);
        var keyword = SpansOn(row).First(s => s.Kind == SyntaxTokenKind.Keyword);

        Assert.Equal(start, keyword.StartColumn);
        Assert.Equal(start + "public".Length, keyword.EndColumn);
    }

    [Fact]
    public void A_fenced_code_block_with_no_language_carries_no_spans()
    {
        var lines = FormatMarkdown("```\npublic class Foo\n```");

        var row = Assert.Single(lines, l => l.Text.Contains("public class Foo", StringComparison.Ordinal));

        Assert.Empty(SpansOn(row));
    }

    [Fact]
    public void A_fenced_code_block_with_an_unknown_language_carries_no_spans()
    {
        var lines = FormatMarkdown("```brainfuck\npublic class Foo\n```");

        var row = Assert.Single(lines, l => l.Text.Contains("public class Foo", StringComparison.Ordinal));

        Assert.Empty(SpansOn(row));
    }

    [Fact]
    public void An_indented_code_block_carries_no_spans_because_it_declares_no_language()
    {
        var lines = FormatMarkdown("    public class Foo\n");

        var row = Assert.Single(lines, l => l.Text.Contains("public class Foo", StringComparison.Ordinal));

        Assert.Empty(SpansOn(row));
    }

    [Fact]
    public void A_block_comment_spanning_lines_highlights_the_line_between_its_delimiters()
    {
        var lines = FormatMarkdown("```csharp\n/* one\ntwo\nthree */\n```");

        var middle = Assert.Single(lines, l => l.Text.TrimEnd().EndsWith("two", StringComparison.Ordinal));
        var span = Assert.Single(SpansOn(middle));

        Assert.Equal(SyntaxTokenKind.Comment, span.Kind);
    }

    // -----------------------------------------------------------------------
    // Wrapping
    // -----------------------------------------------------------------------

    [Fact]
    public void A_span_that_straddles_a_wrap_point_is_split_across_both_render_lines()
    {
        // "internal" is one 8-cell keyword; a narrow viewport forces it across several render lines.
        var lines = FormatMarkdown("```csharp\ninternal\n```", width: 8);

        var withSpans = lines.Where(l => SpansOn(l).Length > 0).ToArray();

        Assert.True(withSpans.Length > 1, "the keyword should be split across more than one row");
        Assert.All(withSpans, l => Assert.All(SpansOn(l), s => Assert.Equal(SyntaxTokenKind.Keyword, s.Kind)));

        // Every cell of the keyword stays highlighted: the pieces sum back to its full width.
        var highlighted = withSpans.Sum(l => SpansOn(l).Sum(s => s.EndColumn - s.StartColumn));
        Assert.Equal("internal".Length, highlighted);
    }

    [Fact]
    public void Every_span_stays_inside_its_own_render_line()
    {
        var lines = FormatMarkdown("```csharp\npublic static readonly string Greeting = \"hello world\";\n```", width: 12);

        foreach (var line in lines)
        {
            var rowWidth = TerminalCellText.Width(line.Text);
            foreach (var span in SpansOn(line))
            {
                Assert.True(span.StartColumn >= 0, $"span starts before the row: {span}");
                Assert.True(span.EndColumn <= rowWidth, $"span {span} overruns row width {rowWidth}: '{line.Text}'");
                Assert.True(span.StartColumn < span.EndColumn, $"empty or inverted span: {span}");
            }
        }
    }

    [Fact]
    public void Spans_on_a_row_are_ascending_and_never_overlap()
    {
        var lines = FormatMarkdown("```csharp\nvar x = \"hi\"; // note 42\n```");

        foreach (var line in lines)
        {
            var spans = SpansOn(line);
            for (var i = 1; i < spans.Length; i++)
            {
                Assert.True(spans[i - 1].EndColumn <= spans[i].StartColumn, "spans overlap or are unsorted");
            }
        }
    }

    // -----------------------------------------------------------------------
    // Diff bodies
    // -----------------------------------------------------------------------

    private const string CSharpPatch = """
        diff --git a/Foo.cs b/Foo.cs
        --- a/Foo.cs
        +++ b/Foo.cs
        @@ -1,2 +1,2 @@
         public class Foo
        -    int old;
        +    int fresh;
        """;

    [Fact]
    public void Diff_body_rows_highlight_the_language_named_by_the_file_extension()
    {
        var lines = FormatDiff(CSharpPatch);

        var added = Assert.Single(lines, l => l.Text.Contains("int fresh;", StringComparison.Ordinal));

        Assert.Contains(SpansOn(added), s => s.Kind == SyntaxTokenKind.Type);
    }

    [Fact]
    public void Diff_body_spans_are_offset_past_the_line_number_gutter()
    {
        var lines = FormatDiff(CSharpPatch);
        var added = Assert.Single(lines, l => l.Text.Contains("int fresh;", StringComparison.Ordinal));

        var span = SpansOn(added).First(s => s.Kind == SyntaxTokenKind.Type);

        Assert.Equal(added.Text.IndexOf("int", StringComparison.Ordinal), span.StartColumn);
        Assert.True(span.StartColumn >= added.PrefixCells, "a body span must start after the gutter");
    }

    [Fact]
    public void Diff_body_rows_for_an_unknown_extension_carry_no_spans()
    {
        var patch = CSharpPatch.Replace("Foo.cs", "Foo.unknownext", StringComparison.Ordinal);

        var lines = FormatDiff(patch);
        var added = Assert.Single(lines, l => l.Text.Contains("int fresh;", StringComparison.Ordinal));

        Assert.Empty(SpansOn(added));
    }

    [Fact]
    public void Diff_header_and_summary_rows_carry_no_spans()
    {
        var lines = FormatDiff(CSharpPatch);

        foreach (var line in lines.Where(l => l.Role != TranscriptRole.DiffAdded
            && l.Role != TranscriptRole.DiffRemoved
            && l.PrefixCells == 0))
        {
            Assert.Empty(SpansOn(line));
        }
    }

    // -----------------------------------------------------------------------
    // Render-line equality
    // -----------------------------------------------------------------------

    [Fact]
    public void Two_render_lines_differing_only_in_syntax_spans_are_not_equal()
    {
        var bare = new TranscriptRenderLine("public", TranscriptRole.Code);
        var highlighted = bare with { Syntax = [new SyntaxSpan(0, 6, SyntaxTokenKind.Keyword)] };

        Assert.NotEqual(bare, highlighted);
    }

    [Fact]
    public void Render_line_equality_compares_syntax_spans_by_content_not_reference()
    {
        var left = new TranscriptRenderLine("public", TranscriptRole.Code)
        {
            Syntax = [new SyntaxSpan(0, 6, SyntaxTokenKind.Keyword)],
        };
        var right = new TranscriptRenderLine("public", TranscriptRole.Code)
        {
            Syntax = [new SyntaxSpan(0, 6, SyntaxTokenKind.Keyword)],
        };

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}

// ---------------------------------------------------------------------------
// Draw-path integration
// ---------------------------------------------------------------------------

public sealed class SyntaxDrawTests
{
    [Fact]
    public void DrawRow_paints_syntax_spans_with_the_syntax_attribute()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);

        using var shell = ShellTestFactory.CreateFullscreen(app);
        app.Begin(shell);
        app.LayoutAndDraw();

        shell.Transcript.Append(new AssistantTranscriptBlock(
            Guid.NewGuid(), "```csharp\npublic int Count = 42;\n```", Complete: true));
        app.LayoutAndDraw();

        Assert.True(shell.Transcript.SyntaxDrawCount > 0, "syntax spans should be drawn with a syntax attribute");
    }

    [Fact]
    public void An_unhighlighted_transcript_draws_no_syntax_spans()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);

        using var shell = ShellTestFactory.CreateFullscreen(app);
        app.Begin(shell);
        app.LayoutAndDraw();

        shell.Transcript.Append(new AssistantTranscriptBlock(Guid.NewGuid(), "just prose", Complete: true));
        app.LayoutAndDraw();

        Assert.Equal(0, shell.Transcript.SyntaxDrawCount);
    }

    [Theory]
    [InlineData(SyntaxTokenKind.Keyword)]
    [InlineData(SyntaxTokenKind.Type)]
    [InlineData(SyntaxTokenKind.String)]
    [InlineData(SyntaxTokenKind.Number)]
    [InlineData(SyntaxTokenKind.Comment)]
    public void Each_token_kind_resolves_to_a_distinct_foreground(SyntaxTokenKind kind)
    {
        using IApplication app = Application.Create();
        using var view = new VirtualizedTranscriptView(app, theme: TuiTheme.WarmEmber);

        var plain = view.SyntaxAttributeFor(SyntaxTokenKind.Plain, background: default, trueColor: true);
        var coloured = view.SyntaxAttributeFor(kind, background: default, trueColor: true);

        Assert.NotEqual(plain.Foreground, coloured.Foreground);
    }

    [Fact]
    public void A_syntax_attribute_keeps_the_row_background_so_a_diff_band_is_not_punched_out()
    {
        using IApplication app = Application.Create();
        using var view = new VirtualizedTranscriptView(app, theme: TuiTheme.WarmEmber);

        var background = TuiTheme.Resolve(TuiTheme.WarmEmber.DiffAddedBackground, trueColor: true);
        var attr = view.SyntaxAttributeFor(SyntaxTokenKind.Keyword, background, trueColor: true);

        Assert.Equal(background, attr.Background);
    }
}
