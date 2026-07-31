using System;
using System.Linq;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// A patch does not only arrive through <c>/diff</c>. When the agent runs <c>git diff</c> through a
/// shell tool, or a tool returns a patch of its own, the result reaches the transcript as ordinary tool
/// output — and used to be drawn as flat monochrome text. Output that really is a unified diff should
/// get the same rendering wherever it came from.
/// </summary>
public sealed class ToolDiffRenderingTests
{
    private const string Patch = """
        diff --git a/Foo.cs b/Foo.cs
        --- a/Foo.cs
        +++ b/Foo.cs
        @@ -1,2 +1,2 @@
         public class Foo
        -    int old;
        +    int fresh;
        """;

    private static TranscriptRenderLine[] FormatTool(string? result, int width = 80) =>
        TranscriptBlockFormatter.Format(
            new ToolTranscriptBlock(Guid.NewGuid(), "shell", """{"command":"git diff"}""", 12, result, false, true),
            width).ToArray();

    [Fact]
    public void A_tool_result_that_is_a_unified_diff_renders_with_diff_roles()
    {
        var lines = FormatTool(Patch);

        Assert.Contains(lines, l => l.Role == TranscriptRole.DiffAdded);
        Assert.Contains(lines, l => l.Role == TranscriptRole.DiffRemoved);
    }

    [Fact]
    public void A_diff_tool_result_carries_line_numbers_and_syntax_spans()
    {
        var lines = FormatTool(Patch);

        var added = Assert.Single(lines, l => l.Text.Contains("int fresh;", StringComparison.Ordinal));

        Assert.True(added.PrefixCells > 0, "the added row should carry a line-number gutter");
        Assert.NotNull(added.Syntax);
        Assert.Contains(added.Syntax!, s => s.Kind == SyntaxTokenKind.Type);
    }

    [Fact]
    public void A_diff_tool_result_still_shows_the_tool_header()
    {
        var lines = FormatTool(Patch);

        // The rich body replaces the flat text, not the tool's own identity line.
        Assert.Contains(lines, l => l.Text.Contains("shell", StringComparison.Ordinal));
    }

    [Fact]
    public void Ordinary_tool_output_is_untouched()
    {
        var lines = FormatTool("just some output\nsecond line");

        Assert.DoesNotContain(lines, l => l.Role is TranscriptRole.DiffAdded or TranscriptRole.DiffRemoved);
        Assert.Contains(lines, l => l.Text.Contains("just some output", StringComparison.Ordinal));
    }

    [Fact]
    public void Output_that_merely_mentions_diff_markers_is_not_treated_as_a_diff()
    {
        // Prose and code frequently contain "---" or lines starting with "+" or "-". Without a real hunk
        // header none of it is a patch, and mis-detecting would mangle ordinary output.
        var lines = FormatTool("--- section ---\n- a bullet\n+ another bullet\n+++ emphasis");

        Assert.DoesNotContain(lines, l => l.Role is TranscriptRole.DiffAdded or TranscriptRole.DiffRemoved);
        Assert.Contains(lines, l => l.Text.Contains("a bullet", StringComparison.Ordinal));
    }

    [Fact]
    public void An_error_result_is_never_reinterpreted_as_a_diff()
    {
        var block = new ToolTranscriptBlock(
            Guid.NewGuid(), "shell", "{}", 5, Patch, IsError: true, Complete: true);

        var lines = TranscriptBlockFormatter.Format(block, 80).ToArray();

        Assert.DoesNotContain(lines, l => l.Role is TranscriptRole.DiffAdded or TranscriptRole.DiffRemoved);
    }

    [Fact]
    public void Escape_sequences_in_a_diff_tool_result_never_reach_a_render_line()
    {
        var hostile = Patch.Replace("int fresh;", "int fresh;\u001b]52;c;Y2FsYw==\u0007", StringComparison.Ordinal);

        foreach (var line in FormatTool(hostile))
        {
            Assert.DoesNotContain('\u001b', line.Text);
            Assert.DoesNotContain('\u0007', line.Text);
        }
    }
}
