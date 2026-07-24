using System.Collections.Immutable;
using System.Text;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Benchmarks;

/// <summary>
/// Deterministic sample-data generators shared by the benchmarks. Everything is seeded so a given
/// size always produces byte-identical inputs, keeping benchmark numbers comparable across runs.
/// </summary>
internal static class SampleData
{
    private static readonly string[] Words =
    [
        "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "coda", "terminal",
        "render", "wrap", "buffer", "stream", "assistant", "composer", "layout", "viewport",
        "grapheme", "unicode", "latency", "allocation", "benchmark", "responsive", "transcript",
    ];

    /// <summary>A prose draft of approximately <paramref name="charCount"/> UTF-16 characters, space-delimited
    /// so it soft-wraps at realistic word boundaries.</summary>
    public static string PlainDraft(int charCount)
    {
        if (charCount <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(charCount + 16);
        var rng = new Random(charCount);
        while (builder.Length < charCount)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Words[rng.Next(Words.Length)]);
        }

        return builder.ToString(0, charCount);
    }

    /// <summary>A markdown-flavoured assistant body of approximately <paramref name="charCount"/> characters,
    /// mixing paragraphs, headings, lists, and a fenced code block so the formatter exercises the real
    /// Markdig parse + wrap path.</summary>
    public static string MarkdownBody(int charCount)
    {
        if (charCount <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(charCount + 64);
        var rng = new Random(charCount);
        var paragraph = 0;
        while (builder.Length < charCount)
        {
            switch (paragraph % 4)
            {
                case 0:
                    builder.Append("## Section ").Append(paragraph).Append('\n').Append('\n');
                    break;
                case 3:
                    builder.Append("```csharp\n");
                    for (var i = 0; i < 4; i++)
                    {
                        builder.Append("var x").Append(i).Append(" = Compute(").Append(rng.Next(1000)).Append(");\n");
                    }

                    builder.Append("```\n\n");
                    break;
                default:
                    var sentenceWords = 24 + rng.Next(24);
                    for (var i = 0; i < sentenceWords; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(' ');
                        }

                        builder.Append(Words[rng.Next(Words.Length)]);
                    }

                    builder.Append(".\n\n");
                    break;
            }

            paragraph++;
        }

        return builder.ToString(0, charCount);
    }

    /// <summary>A transcript of <paramref name="blockCount"/> alternating user/assistant blocks with
    /// small-to-medium bodies, approximating a long conversation.</summary>
    public static ImmutableArray<TranscriptBlock> Transcript(int blockCount)
    {
        var builder = ImmutableArray.CreateBuilder<TranscriptBlock>(blockCount);
        var rng = new Random(blockCount);
        for (var i = 0; i < blockCount; i++)
        {
            if (i % 2 == 0)
            {
                builder.Add(new UserTranscriptBlock(Guid.NewGuid(), PlainDraft(40 + rng.Next(120))));
            }
            else
            {
                builder.Add(new AssistantTranscriptBlock(Guid.NewGuid(), MarkdownBody(120 + rng.Next(400)), Complete: true));
            }
        }

        return builder.ToImmutable();
    }
}
