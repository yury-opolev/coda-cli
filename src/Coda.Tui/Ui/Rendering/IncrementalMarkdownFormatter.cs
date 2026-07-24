using System.Collections.Generic;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// A stateful, streaming-aware projection of an assistant Markdown block onto wrapped, attributed lines.
/// It exists to make live streaming cheap: <see cref="TranscriptBlockFormatter"/> re-parses and re-wraps
/// the entire accumulated response on every frame (O(L) per frame, O(L²) over a response), whereas this
/// formatter formats each completed leading Markdown block exactly once and, on each delta, only
/// re-parses/re-wraps the unfinished trailing block.
/// </summary>
/// <remarks>
/// <para>
/// Correctness rests on <see cref="TranscriptBlockFormatter"/> being block-local: it parses the document,
/// then formats each top-level block independently, separating adjacent blocks by a single blank line. So
/// for any split of the source at a true top-level block boundary <c>P|T</c>:
/// <c>Format(P+T) == Format(P) ++ sep ++ Format(T)</c>, where the blank <c>sep</c> is present iff both
/// <c>P</c> and <c>T</c> contain at least one block (any non-whitespace content).
/// </para>
/// <para>
/// A blank line outside a fenced code block is always a top-level block boundary in CommonMark — except
/// inside constructs whose extent or rendering can change as more text arrives (lists, whose looseness is
/// retroactive; block quotes and indented code, which continue across blank lines; HTML blocks; and
/// reference links/definitions, which resolve across the document). This formatter therefore only "seals"
/// a boundary after simple content (paragraphs, ATX/setext headings, and closed fenced code) and freezes
/// sealing the moment any such construct appears, re-formatting everything after the last sealed boundary
/// on each frame. That keeps output identical to a full re-parse (verified by differential tests) while
/// still eliminating the quadratic cost for the common all-prose/all-code responses.
/// </para>
/// <para>Not thread-safe; it is owned and driven by the single UI thread.</para>
/// </remarks>
internal sealed class IncrementalMarkdownFormatter
{
    private static readonly TranscriptRenderLine Separator = new(string.Empty, TranscriptRole.Assistant);

    private Guid blockId;
    private int width = -1;

    // Formatted lines for the FINALIZED prefix — every sealed block except the most recent one, which is
    // kept in the re-parsed active region. Markdig has position-dependent cases (a lone bullet marker is an
    // empty list at a document's start but a paragraph after a block, and differs again after a code block),
    // so the active tail must be parsed with its real predecessor block present. Finalizing with a one-block
    // lag guarantees that without ever re-parsing the whole prefix.
    private readonly List<TranscriptRenderLine> committedLines = new();
    private bool committedHasBlock;
    private int finalizedOffset;   // normalized[..finalizedOffset] == committedLines
    private int pendingSealOffset; // latest confirmed block boundary (>= finalizedOffset)
    private bool frozen;

    /// <summary>
    /// Projects the assistant block <paramref name="id"/>'s accumulated <paramref name="text"/> at the given
    /// cell width, reusing the formatting of already-completed leading blocks. The result is identical to
    /// <see cref="TranscriptBlockFormatter.Format(TranscriptBlock,int)"/> for an
    /// <see cref="AssistantTranscriptBlock"/> carrying the same text.
    /// </summary>
    public IReadOnlyList<TranscriptRenderLine> Update(Guid id, string? text, int width)
    {
        var safeWidth = width > 0 ? width : 1;
        var normalized = NormalizeNewlines(text ?? string.Empty);

        // Reset whenever the assumption of append-only growth for a single block breaks: a different block,
        // a width change (every wrap changes), or the text shrinking below what we already sealed.
        if (id != this.blockId || safeWidth != this.width || normalized.Length < this.pendingSealOffset)
        {
            this.Reset(id, safeWidth);
        }

        if (!this.frozen)
        {
            var (seal, freeze) = ScanForSeal(normalized, this.pendingSealOffset);
            if (seal > this.pendingSealOffset)
            {
                // A new block boundary appeared beyond the pending one, so the previously-pending block is
                // now provably complete and its predecessor context can no longer change: finalize it.
                if (this.pendingSealOffset > this.finalizedOffset)
                {
                    var segment = normalized[this.finalizedOffset..this.pendingSealOffset];
                    var segmentHasBlock = HasBlock(segment);
                    if (this.committedHasBlock && segmentHasBlock)
                    {
                        this.committedLines.Add(Separator);
                    }

                    this.committedLines.AddRange(FormatAssistant(segment, safeWidth));
                    this.committedHasBlock |= segmentHasBlock;
                    this.finalizedOffset = this.pendingSealOffset;
                }

                this.pendingSealOffset = seal;
            }

            if (freeze)
            {
                this.frozen = true;
            }
        }

        // The active region is the last sealed block (if any) plus the in-progress tail, re-parsed each
        // frame so the tail always sees its real predecessor. It is bounded by roughly one block plus the
        // growing tail — never the whole prefix.
        var active = normalized[this.finalizedOffset..];
        var activeLines = FormatAssistant(active, safeWidth);
        var activeHasBlock = HasBlock(active);

        var result = new List<TranscriptRenderLine>(this.committedLines.Count + activeLines.Count + 1);
        result.AddRange(this.committedLines);
        if (this.committedHasBlock && activeHasBlock)
        {
            result.Add(Separator);
        }

        result.AddRange(activeLines);
        return result;
    }

    private void Reset(Guid id, int width)
    {
        this.blockId = id;
        this.width = width;
        this.committedLines.Clear();
        this.committedHasBlock = false;
        this.finalizedOffset = 0;
        this.pendingSealOffset = 0;
        this.frozen = false;
    }

    private static IReadOnlyList<TranscriptRenderLine> FormatAssistant(string text, int width) =>
        TranscriptBlockFormatter.Format(new AssistantTranscriptBlock(Guid.Empty, text, Complete: false), width);

    /// <summary>Whether <paramref name="text"/> contains at least one Markdown block (any non-whitespace).</summary>
    private static bool HasBlock(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scans <paramref name="s"/> from <paramref name="from"/> and returns the largest offset that is a
    /// safe top-level block boundary after only "simple" content (paragraphs, headings, closed fenced code),
    /// plus whether a construct was reached that forbids sealing any further (a list, block quote, indented
    /// code, HTML block, or reference link/definition). The returned offset is always at the start of a
    /// blank line that closes a simple block, so <c>s[..offset]</c> is a whole number of top-level blocks.
    /// </summary>
    private static (int Seal, bool Freeze) ScanForSeal(string s, int from)
    {
        var seal = from;
        var i = from;
        var fenceOpen = false;
        var fenceChar = '\0';
        var fenceLen = 0;
        var segmentHasContent = false;

        while (i < s.Length)
        {
            var lineEnd = s.IndexOf('\n', i);
            if (lineEnd < 0)
            {
                lineEnd = s.Length;
            }

            var line = s.AsSpan(i, lineEnd - i);
            var nextStart = lineEnd + 1;

            if (fenceOpen)
            {
                if (IsFenceClose(line, fenceChar, fenceLen))
                {
                    fenceOpen = false;
                }

                segmentHasContent = true;
                i = nextStart;
                continue;
            }

            if (IsBlank(line))
            {
                if (segmentHasContent)
                {
                    // A blank line outside a fence closes the current simple block: seal it here.
                    seal = i;
                }

                segmentHasContent = false;
                i = nextStart;
                continue;
            }

            var leading = LeadingSpaces(line);
            var content = line[leading..];

            if (IsFenceOpen(content, leading, out fenceChar, out fenceLen))
            {
                fenceOpen = true;
                segmentHasContent = true;
                i = nextStart;
                continue;
            }

            if (leading >= 4
                || IsListMarker(content)
                || IsBlockQuote(content)
                || IsHtmlStart(content)
                || HasReferenceSyntax(line))
            {
                // A construct whose extent/rendering can change as more text arrives. Never seal past it.
                return (seal, true);
            }

            segmentHasContent = true;
            i = nextStart;
        }

        // Reached the end (or an open fence) without hitting a freezing construct; more text may still
        // arrive, so leave sealing un-frozen.
        return (seal, false);
    }

    private static bool IsBlank(ReadOnlySpan<char> line)
    {
        foreach (var ch in line)
        {
            if (!char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static int LeadingSpaces(ReadOnlySpan<char> line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static bool IsFenceOpen(ReadOnlySpan<char> content, int leading, out char marker, out int length)
    {
        marker = '\0';
        length = 0;

        // A fence indented four or more spaces would be indented code, which freezes separately.
        if (leading > 3 || content.Length < 3)
        {
            return false;
        }

        var ch = content[0];
        if (ch != '`' && ch != '~')
        {
            return false;
        }

        var run = 0;
        while (run < content.Length && content[run] == ch)
        {
            run++;
        }

        if (run < 3)
        {
            return false;
        }

        // A backtick info string may not itself contain a backtick (CommonMark); tilde has no such rule.
        if (ch == '`' && content[run..].Contains('`'))
        {
            return false;
        }

        marker = ch;
        length = run;
        return true;
    }

    private static bool IsFenceClose(ReadOnlySpan<char> line, char marker, int minLength)
    {
        var trimmed = line.TrimStart(' ');
        var run = 0;
        while (run < trimmed.Length && trimmed[run] == marker)
        {
            run++;
        }

        if (run < minLength)
        {
            return false;
        }

        // A closing fence carries no info string: only trailing spaces may follow the marker run.
        return trimmed[run..].TrimEnd(' ').IsEmpty;
    }

    private static bool IsListMarker(ReadOnlySpan<char> content)
    {
        if (content.IsEmpty)
        {
            return false;
        }

        var first = content[0];
        if (first is '-' or '+' or '*')
        {
            // A bullet marker, or a lone bullet on its own line (an empty list item).
            return content.Length == 1 || content[1] == ' ' || content[1] == '\t';
        }

        // Ordered list: one or more digits then '.' or ')' then a space (or end of line).
        var digits = 0;
        while (digits < content.Length && char.IsDigit(content[digits]))
        {
            digits++;
        }

        if (digits == 0 || digits >= content.Length)
        {
            return false;
        }

        var delimiter = content[digits];
        if (delimiter != '.' && delimiter != ')')
        {
            return false;
        }

        var after = digits + 1;
        return after >= content.Length || content[after] == ' ' || content[after] == '\t';
    }

    private static bool IsBlockQuote(ReadOnlySpan<char> content) =>
        !content.IsEmpty && content[0] == '>';

    private static bool IsHtmlStart(ReadOnlySpan<char> content) =>
        !content.IsEmpty && content[0] == '<';

    /// <summary>
    /// Whether the line carries reference-link syntax (<c>][</c>), a shortcut/collapsed reference tail
    /// (<c>]:</c>), so that any content whose rendering could change when a later reference definition
    /// arrives is never sealed. Conservative: it may also match unrelated text, which only reduces reuse.
    /// </summary>
    private static bool HasReferenceSyntax(ReadOnlySpan<char> line) =>
        line.Contains("][", StringComparison.Ordinal) || line.Contains("]:", StringComparison.Ordinal);

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n');
}
