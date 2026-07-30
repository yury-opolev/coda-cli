using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Coda.Tui.Ui.Rendering;

/// <summary>The structural kind of a file-level change in the parsed diff.</summary>
public enum DiffChangeKind
{
    /// <summary>The file existed before and still exists — only its content changed.</summary>
    Modification,

    /// <summary>A new file created on the new side (<c>--- /dev/null</c>).</summary>
    Addition,

    /// <summary>An existing file removed on the new side (<c>+++ /dev/null</c>).</summary>
    Deletion,

    /// <summary>A file that moved path; possibly also has content changes.</summary>
    Rename,
}

/// <summary>The role of a single body line inside a parsed hunk.</summary>
public enum DiffLineKind
{
    /// <summary>A context line present on both sides of the diff.</summary>
    Context,

    /// <summary>A line added on the new side (<c>+</c> marker).</summary>
    Added,

    /// <summary>A line removed from the old side (<c>-</c> marker).</summary>
    Removed,

    /// <summary>The <c>\ No newline at end of file</c> annotation following a file without a trailing newline.</summary>
    NoNewline,

    /// <summary>
    /// An optional section label extracted from the trailing text after the closing <c>@@</c> in a
    /// hunk header (e.g. <c>@@ -1,2 +1,2 @@ class Foo {</c>). These are not body lines — they carry
    /// no old/new line number — but they are rendered as dim context rows for orientation.
    /// </summary>
    SectionHeading,
}

/// <summary>
/// A single body line inside a parsed diff hunk: its structural role, the optional old-side and
/// new-side line numbers (whichever applies), and the raw content text with the leading marker
/// character stripped.
/// </summary>
/// <param name="Kind">How the line participates in the diff.</param>
/// <param name="OldLine">The 1-based line number on the old side, or <see langword="null"/> for added and meta lines.</param>
/// <param name="NewLine">The 1-based line number on the new side, or <see langword="null"/> for removed and meta lines.</param>
/// <param name="Text">The line content without the leading marker character (the <c>+</c>, <c>-</c>, or space).</param>
public readonly record struct DiffLine(DiffLineKind Kind, int? OldLine, int? NewLine, string Text);

/// <summary>
/// A single file entry produced by parsing a unified diff patch: the display path, the structural
/// kind of change, how many lines were added and removed across all hunks, and the ordered sequence
/// of body lines ready for rendering.
/// </summary>
/// <param name="Path">
/// The path to display: the new-side path for modifications/renames/additions, the old-side path for
/// deletions. The <c>a/</c> and <c>b/</c> git prefixes are stripped.
/// </param>
/// <param name="Kind">Whether this file was modified, created, deleted, or renamed.</param>
/// <param name="Added">Total added lines across all hunks (never counting the <c>+++</c> header line).</param>
/// <param name="Removed">Total removed lines across all hunks (never counting the <c>---</c> header line).</param>
/// <param name="Lines">The ordered body lines: context, added, removed, section headings, and no-newline markers.</param>
public readonly record struct DiffFile(
    string Path,
    DiffChangeKind Kind,
    int Added,
    int Removed,
    ImmutableArray<DiffLine> Lines);

/// <summary>
/// Parses standard unified diff text (e.g. from <c>git diff</c>) into a structured model suitable
/// for rich terminal rendering. No Terminal.Gui types are referenced — this class is intentionally
/// host-neutral so it can be exercised in unit tests without any terminal setup.
/// </summary>
/// <remarks>
/// The parser consumes <c>diff --git</c>, <c>index</c>, <c>old/new mode</c>, <c>similarity index</c>,
/// and <c>rename from/to</c> lines without emitting body lines. The <c>---</c> and <c>+++</c> lines
/// provide the file paths (with their <c>a/</c> and <c>b/</c> git prefixes stripped) and determine the
/// change kind (<see cref="DiffChangeKind.Addition"/> when the old side is <c>/dev/null</c>,
/// <see cref="DiffChangeKind.Deletion"/> when the new side is <c>/dev/null</c>). Hunk headers
/// (<c>@@ -old,count +new,count @@</c>) reset the line-number counters and, if they carry a trailing
/// section label, contribute a single <see cref="DiffLineKind.SectionHeading"/> body line.
/// </remarks>
public static class UnifiedDiffParser
{
    // Matches @@ -oldStart[,oldCount] +newStart[,newCount] @@[optional trailing label]
    // The count groups are optional (omitted count defaults to 1).
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses <paramref name="patch"/> into an ordered list of <see cref="DiffFile"/> entries.
    /// Returns an empty list for null, empty, or non-diff input without throwing.
    /// </summary>
    public static IReadOnlyList<DiffFile> Parse(string patch)
    {
        if (string.IsNullOrEmpty(patch))
        {
            return [];
        }

        var result = new List<DiffFile>();

        // Mutable per-file state.
        string? oldPath = null;
        string? newPath = null;
        DiffChangeKind kind = DiffChangeKind.Modification;
        bool isRename = false;
        var bodyLines = ImmutableArray.CreateBuilder<DiffLine>();
        int added = 0;
        int removed = 0;
        bool inFile = false;
        bool inHunk = false;
        int oldLine = 0;
        int newLine = 0;

        // Flush the accumulated file state into result and reset for the next file.
        void FlushFile()
        {
            if (!inFile)
            {
                return;
            }

            // Determine the resolved path: new side for non-deletions, old side for deletions.
            // Strip the a/ or b/ git prefix if present.
            var resolvedPath = kind == DiffChangeKind.Deletion
                ? StripGitPrefix(oldPath ?? string.Empty, "a/")
                : StripGitPrefix(newPath ?? string.Empty, "b/");

            if (!string.IsNullOrEmpty(resolvedPath) || bodyLines.Count > 0)
            {
                result.Add(new DiffFile(resolvedPath, kind, added, removed, bodyLines.ToImmutable()));
            }

            oldPath = null;
            newPath = null;
            kind = DiffChangeKind.Modification;
            isRename = false;
            bodyLines.Clear();
            added = 0;
            removed = 0;
            inFile = false;
            inHunk = false;
            oldLine = 0;
            newLine = 0;
        }

        foreach (var raw in SplitLines(patch))
        {
            // ----------------------------------------------------------------
            // File boundary — start a new file section.
            // ----------------------------------------------------------------
            if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushFile();
                inFile = true;
                continue;
            }

            // ----------------------------------------------------------------
            // Lines consumed without contributing body content.
            // ----------------------------------------------------------------
            if (raw.StartsWith("index ", StringComparison.Ordinal) ||
                raw.StartsWith("old mode ", StringComparison.Ordinal) ||
                raw.StartsWith("new mode ", StringComparison.Ordinal) ||
                raw.StartsWith("new file mode ", StringComparison.Ordinal) ||
                raw.StartsWith("deleted file mode ", StringComparison.Ordinal) ||
                raw.StartsWith("similarity index ", StringComparison.Ordinal) ||
                raw.StartsWith("Binary files ", StringComparison.Ordinal))
            {
                continue;
            }

            if (raw.StartsWith("rename from ", StringComparison.Ordinal))
            {
                isRename = true;
                continue;
            }

            if (raw.StartsWith("rename to ", StringComparison.Ordinal))
            {
                isRename = true;
                continue;
            }

            // ----------------------------------------------------------------
            // File header lines — record paths and derive the change kind.
            // ----------------------------------------------------------------
            if (raw.StartsWith("--- ", StringComparison.Ordinal))
            {
                // Start a file section even if we haven't seen diff --git (lenient mode).
                if (!inFile)
                {
                    inFile = true;
                }

                oldPath = raw[4..];
                inHunk = false;
                continue;
            }

            if (raw.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = raw[4..];

                // Derive the change kind from the /dev/null sentinels.
                if (isRename)
                {
                    kind = DiffChangeKind.Rename;
                }
                else if (oldPath == "/dev/null")
                {
                    kind = DiffChangeKind.Addition;
                }
                else if (newPath == "/dev/null")
                {
                    kind = DiffChangeKind.Deletion;
                }
                else
                {
                    kind = DiffChangeKind.Modification;
                }

                inHunk = false;
                continue;
            }

            // ----------------------------------------------------------------
            // Hunk header — reset line-number counters.
            // ----------------------------------------------------------------
            if (raw.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var match = HunkHeaderRegex.Match(raw);
                if (match.Success)
                {
                    oldLine = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    newLine = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                    inHunk = true;

                    // Optional trailing section label after the closing @@.
                    var label = match.Groups[5].Value.Trim();
                    if (!string.IsNullOrEmpty(label))
                    {
                        bodyLines.Add(new DiffLine(DiffLineKind.SectionHeading, null, null, label));
                    }
                }

                continue;
            }

            // ----------------------------------------------------------------
            // Body lines — only valid after the first @@ hunk header.
            // ----------------------------------------------------------------
            if (!inHunk)
            {
                continue;
            }

            if (raw.StartsWith("\\ ", StringComparison.Ordinal))
            {
                // "\ No newline at end of file" — a meta annotation, no line numbers.
                bodyLines.Add(new DiffLine(DiffLineKind.NoNewline, null, null, raw[2..]));
                continue;
            }

            if (raw.Length == 0 || raw[0] == ' ')
            {
                // Context line: advances both counters.
                var text = raw.Length > 0 ? raw[1..] : string.Empty;
                bodyLines.Add(new DiffLine(DiffLineKind.Context, oldLine, newLine, text));
                oldLine++;
                newLine++;
                continue;
            }

            if (raw[0] == '-')
            {
                // Removed line: advances old counter only.
                bodyLines.Add(new DiffLine(DiffLineKind.Removed, oldLine, null, raw[1..]));
                oldLine++;
                removed++;
                continue;
            }

            if (raw[0] == '+')
            {
                // Added line: advances new counter only.
                bodyLines.Add(new DiffLine(DiffLineKind.Added, null, newLine, raw[1..]));
                newLine++;
                added++;
                continue;
            }
        }

        // Flush the final file.
        FlushFile();

        return result;
    }

    /// <summary>
    /// Strips the leading <paramref name="prefix"/> from <paramref name="path"/> when present,
    /// leaving the suffix unchanged. Used to remove the <c>a/</c> and <c>b/</c> git diff prefixes.
    /// </summary>
    private static string StripGitPrefix(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;

    /// <summary>Splits <paramref name="text"/> on <c>\n</c> and <c>\r\n</c> boundaries.</summary>
    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                var end = i > 0 && text[i - 1] == '\r' ? i - 1 : i;
                yield return text[start..end];
                start = i + 1;
            }
        }

        yield return text[start..];
    }
}
