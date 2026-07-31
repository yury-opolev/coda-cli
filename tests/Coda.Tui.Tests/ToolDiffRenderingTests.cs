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

/// <summary>
/// A patch rarely arrives alone. <c>git show</c> puts a commit header in front of it, and a model
/// writes prose around a fenced one. Rendering the diff must never cost the text around it, and a
/// block the model explicitly labelled <c>diff</c> should be rendered as one.
/// </summary>
public sealed class DiffInContextTests
{
    private const string GitShow = """
        commit 98e5e45
        Author: yury <yury@example.com>
        Date:   Fri Jul 31 07:23:13 2026 +0200

            chore: bump version to 0.1.94

        diff --git a/version.json b/version.json
        index a10bbe1..518a052 100644
        --- a/version.json
        +++ b/version.json
        @@ -1,3 +1,3 @@
         {
        -  "build": 93
        +  "build": 94
         }
        """;

    private static TranscriptRenderLine[] FormatTool(string result) =>
        TranscriptBlockFormatter.Format(
            new ToolTranscriptBlock(Guid.NewGuid(), "shell", "{}", 1, result, false, true), 100).ToArray();

    private static TranscriptRenderLine[] FormatMarkdown(string markdown) =>
        TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), markdown, true), 100).ToArray();

    // -----------------------------------------------------------------------
    // The text around a diff must survive
    // -----------------------------------------------------------------------

    [Fact]
    public void A_commit_header_in_front_of_a_diff_is_preserved()
    {
        var lines = FormatTool(GitShow);

        Assert.Contains(lines, l => l.Text.Contains("commit 98e5e45", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Text.Contains("Author: yury", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Text.Contains("chore: bump version to 0.1.94", StringComparison.Ordinal));
    }

    [Fact]
    public void The_diff_after_a_commit_header_is_still_rendered_richly()
    {
        var lines = FormatTool(GitShow);

        Assert.Contains(lines, l => l.Role == TranscriptRole.DiffAdded);
        Assert.Contains(lines, l => l.Role == TranscriptRole.DiffRemoved);
    }

    [Fact]
    public void The_preamble_is_not_mistaken_for_diff_content()
    {
        var lines = FormatTool(GitShow);

        var author = Assert.Single(lines, l => l.Text.Contains("Author: yury", StringComparison.Ordinal));
        Assert.NotEqual(TranscriptRole.DiffAdded, author.Role);
        Assert.NotEqual(TranscriptRole.DiffRemoved, author.Role);
    }

    // -----------------------------------------------------------------------
    // A fenced block the model labelled "diff"
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("diff")]
    [InlineData("patch")]
    public void A_fenced_block_labelled_as_a_diff_is_rendered_as_one(string info)
    {
        var lines = FormatMarkdown($"Here:\n\n```{info}\n{GitShow}\n```\n");

        Assert.Contains(lines, l => l.Role == TranscriptRole.DiffAdded);
        Assert.Contains(lines, l => l.Role == TranscriptRole.DiffRemoved);
    }

    [Fact]
    public void The_prose_around_a_fenced_diff_survives()
    {
        var lines = FormatMarkdown($"Here:\n\n```diff\n{GitShow}\n```\n\nThat is the bump.\n");

        Assert.Contains(lines, l => l.Text.Contains("Here:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Text.Contains("That is the bump.", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Text.Contains("commit 98e5e45", StringComparison.Ordinal));
    }

    [Fact]
    public void A_diff_fence_holding_no_real_diff_stays_ordinary_code()
    {
        var lines = FormatMarkdown("```diff\njust some text\nnot a patch\n```\n");

        Assert.DoesNotContain(lines, l => l.Role is TranscriptRole.DiffAdded or TranscriptRole.DiffRemoved);
        Assert.Contains(lines, l => l.Text.Contains("just some text", StringComparison.Ordinal));
    }

    [Fact]
    public void A_csharp_fence_is_still_highlighted_rather_than_diffed()
    {
        var lines = FormatMarkdown("```csharp\npublic int X = 1;\n```\n");

        Assert.DoesNotContain(lines, l => l.Role is TranscriptRole.DiffAdded or TranscriptRole.DiffRemoved);
        Assert.Contains(lines, l => l.Syntax is { Count: > 0 });
    }
}
