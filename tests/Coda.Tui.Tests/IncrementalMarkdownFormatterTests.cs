using System.Text;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Differential tests for <see cref="IncrementalMarkdownFormatter"/>: at every streamed prefix its output
/// must be byte-for-byte identical to a full <see cref="TranscriptBlockFormatter"/> re-parse of the same
/// text. These are the correctness guarantee behind reusing completed leading blocks while streaming.
/// </summary>
public sealed class IncrementalMarkdownFormatterTests
{
    private static IReadOnlyList<TranscriptRenderLine> Full(string text, int width) =>
        TranscriptBlockFormatter.Format(new AssistantTranscriptBlock(Guid.NewGuid(), text, Complete: false), width);

    /// <summary>Streams <paramref name="finalText"/> one UTF-16 unit at a time, asserting the incremental
    /// projection equals the full projection at every prefix.</summary>
    private static void AssertStreamMatchesFull(string finalText, int width = 24)
    {
        var formatter = new IncrementalMarkdownFormatter();
        var id = Guid.NewGuid();
        for (var p = 0; p <= finalText.Length; p++)
        {
            var prefix = finalText[..p];
            var actual = formatter.Update(id, prefix, width);
            var expected = Full(prefix, width);
            Assert.True(
                expected.SequenceEqual(actual),
                $"Mismatch at prefix length {p} (\"{prefix.Replace("\n", "\\n")}\")\n" +
                $"expected: [{string.Join(" | ", expected.Select(l => l.Text))}]\n" +
                $"actual:   [{string.Join(" | ", actual.Select(l => l.Text))}]");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("hello world this is a longer paragraph that wraps across a narrow width several times over")]
    [InlineData("# Heading\n\nBody paragraph here.")]
    [InlineData("Para one.\n\nPara two.\n\nPara three.")]
    [InlineData("```\ncode line one\ncode line two\n```\n\nafter the code")]
    [InlineData("```python\nprint('hi')\n\nprint('bye')\n```\n\ndone")]
    [InlineData("```\nunclosed code block still streaming in")]
    [InlineData("intro paragraph\n\n```\nfenced\n```")]
    [InlineData("Heading text\n=========\n\nbody after setext heading")]
    [InlineData("first thematic\n\n---\n\nafter the break")]
    [InlineData("para with emoji \U0001F600 and \u4E2D\u6587 characters that should wrap nicely")]
    [InlineData("line one\r\nline two\r\n\r\nsecond paragraph")]
    public void Simple_content_streams_identically(string finalText)
    {
        AssertStreamMatchesFull(finalText);
    }

    [Theory]
    [InlineData("- a\n- b\n- c")]
    [InlineData("1. one\n2. two\n3. three")]
    [InlineData("- a\n\n- b\n\n- c")]
    [InlineData("- outer\n  - inner\n- outer two")]
    [InlineData("> quote line\n> more quote\n\nafter quote")]
    [InlineData("Para before list\n\n- item one\n- item two\n\nPara after list")]
    [InlineData("Text with [label][id] reference.\n\n[id]: https://example.com")]
    [InlineData("A [collapsed][] link.\n\n[collapsed]: https://example.com")]
    [InlineData("<div>raw html</div>\n\nfollowing paragraph")]
    [InlineData("    indented code line\n    second line\n\nplain paragraph")]
    [InlineData("Para one.\n\n- a list appears\n\nPara two.\n\n- another list\n\nPara three.")]
    public void Complex_content_streams_identically(string finalText)
    {
        AssertStreamMatchesFull(finalText);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(120)]
    public void Wraps_identically_across_widths(int width)
    {
        const string text =
            "# Report\n\nThis is a reasonably long paragraph that will wrap differently at each width.\n\n" +
            "```\nlet total = compute(items);\nlet average = total / count;\n```\n\n" +
            "- first bullet with some length\n- second bullet\n\nClosing remarks paragraph.";
        AssertStreamMatchesFull(text, width);
    }

    [Fact]
    public void Fuzzed_markdown_streams_identically()
    {
        var rng = new Random(20260724);
        for (var trial = 0; trial < 200; trial++)
        {
            var text = GenerateMarkdown(rng);
            var width = 8 + rng.Next(48);
            AssertStreamMatchesFull(text, width);
        }
    }

    [Fact]
    public void Width_change_mid_stream_reformats_at_new_width()
    {
        var formatter = new IncrementalMarkdownFormatter();
        var id = Guid.NewGuid();
        const string text = "# Title\n\nA paragraph that is long enough to wrap at a small width and not at a large one.";

        // Seal some content at a narrow width, then re-measure the whole block at a wider width.
        _ = formatter.Update(id, text[..20], 12);
        _ = formatter.Update(id, text, 12);
        var actual = formatter.Update(id, text, 60);
        Assert.True(Full(text, 60).SequenceEqual(actual));
    }

    [Fact]
    public void New_block_id_resets_committed_state()
    {
        var formatter = new IncrementalMarkdownFormatter();
        const string first = "# One\n\nBody of the first block.\n\nSecond paragraph seals too.";
        _ = formatter.Update(Guid.NewGuid(), first, 20);

        const string second = "Totally different second block content.";
        var actual = formatter.Update(Guid.NewGuid(), second, 20);
        Assert.True(Full(second, 20).SequenceEqual(actual));
    }

    private static string GenerateMarkdown(Random rng)
    {
        string[] words =
        {
            "alpha", "beta", "gamma", "delta", "lorem", "ipsum", "dolor", "sit", "amet", "coda",
            "stream", "render", "\U0001F600", "\u4E2D\u6587", "wrap", "block",
        };

        string Sentence(int n)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(words[rng.Next(words.Length)]);
            }

            return sb.ToString();
        }

        var blocks = new List<string>();
        var count = 1 + rng.Next(6);
        for (var i = 0; i < count; i++)
        {
            switch (rng.Next(6))
            {
                case 0:
                    blocks.Add("# " + Sentence(1 + rng.Next(4)));
                    break;
                case 1:
                    blocks.Add("```\n" + Sentence(2 + rng.Next(3)) + "\n" + Sentence(2 + rng.Next(3)) + "\n```");
                    break;
                case 2:
                    blocks.Add("- " + Sentence(2) + "\n- " + Sentence(2) + "\n- " + Sentence(2));
                    break;
                case 3:
                    blocks.Add("> " + Sentence(3 + rng.Next(4)));
                    break;
                case 4:
                    blocks.Add("---");
                    break;
                default:
                    blocks.Add(Sentence(4 + rng.Next(12)));
                    break;
            }
        }

        return string.Join("\n\n", blocks);
    }
}
