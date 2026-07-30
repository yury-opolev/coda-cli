using System.Collections.Immutable;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

/// <summary>
/// Pure unit tests for <see cref="UnifiedDiffParser"/>. No Terminal.Gui types, no rendering
/// concerns — the parser is exercised in isolation so every claim about structural correctness
/// (path, kind, counts, line numbers) can be verified without a terminal.
/// </summary>
public sealed class UnifiedDiffParserTests
{
    // -----------------------------------------------------------------------
    // Single-file modification — path, counts, and per-line old/new numbers
    // -----------------------------------------------------------------------

    [Fact]
    public void Single_file_modification_with_one_hunk_returns_correct_path_counts_and_line_numbers()
    {
        var patch = string.Join('\n',
            "diff --git a/src/foo.ts b/src/foo.ts",
            "index abc123..def456 100644",
            "--- a/src/foo.ts",
            "+++ b/src/foo.ts",
            "@@ -1,4 +1,5 @@",
            " context1",
            "-removed",
            "+added1",
            "+added2",
            " context2");

        var files = UnifiedDiffParser.Parse(patch);

        var file = Assert.Single(files);
        Assert.Equal("src/foo.ts", file.Path);
        Assert.Equal(DiffChangeKind.Modification, file.Kind);
        Assert.Equal(2, file.Added);
        Assert.Equal(1, file.Removed);

        // context1 at hunk start (oldLine=1, newLine=1)
        var ctx1 = file.Lines[0];
        Assert.Equal(DiffLineKind.Context, ctx1.Kind);
        Assert.Equal(1, ctx1.OldLine);
        Assert.Equal(1, ctx1.NewLine);
        Assert.Equal("context1", ctx1.Text);

        // removed line advances OldLine only
        var rem = file.Lines[1];
        Assert.Equal(DiffLineKind.Removed, rem.Kind);
        Assert.Equal(2, rem.OldLine);
        Assert.Null(rem.NewLine);
        Assert.Equal("removed", rem.Text);

        // first added line advances NewLine only (newLine was still at 2 before this)
        var add1 = file.Lines[2];
        Assert.Equal(DiffLineKind.Added, add1.Kind);
        Assert.Null(add1.OldLine);
        Assert.Equal(2, add1.NewLine);
        Assert.Equal("added1", add1.Text);

        // second added line (newLine=3)
        var add2 = file.Lines[3];
        Assert.Equal(DiffLineKind.Added, add2.Kind);
        Assert.Null(add2.OldLine);
        Assert.Equal(3, add2.NewLine);
        Assert.Equal("added2", add2.Text);

        // context2: old is now 3, new is 4
        var ctx2 = file.Lines[4];
        Assert.Equal(DiffLineKind.Context, ctx2.Kind);
        Assert.Equal(3, ctx2.OldLine);
        Assert.Equal(4, ctx2.NewLine);
        Assert.Equal("context2", ctx2.Text);
    }

    // -----------------------------------------------------------------------
    // Multiple hunks — counters reset per hunk from the @@ header
    // -----------------------------------------------------------------------

    [Fact]
    public void Multiple_hunks_in_one_file_reset_counters_to_hunk_header_values()
    {
        var patch = string.Join('\n',
            "diff --git a/app.ts b/app.ts",
            "--- a/app.ts",
            "+++ b/app.ts",
            "@@ -1,3 +1,3 @@",
            " ctx1",
            "-rem1",
            "+add1",
            "@@ -10,3 +10,3 @@",
            " ctx2",
            "-rem2",
            "+add2",
            " ctx3");

        var files = UnifiedDiffParser.Parse(patch);

        var file = Assert.Single(files);
        Assert.Equal(2, file.Added);
        Assert.Equal(2, file.Removed);

        // Hunk 1 lines
        Assert.Equal(1, file.Lines[0].OldLine); // ctx1
        Assert.Equal(1, file.Lines[0].NewLine);
        Assert.Equal(2, file.Lines[1].OldLine); // rem1
        Assert.Null(file.Lines[2].OldLine);     // add1
        Assert.Equal(2, file.Lines[2].NewLine);

        // Hunk 2 — counters reset: ctx2 starts at old=10, new=10
        Assert.Equal(10, file.Lines[3].OldLine); // ctx2 — reset to hunk start
        Assert.Equal(10, file.Lines[3].NewLine);
        Assert.Equal(11, file.Lines[4].OldLine); // rem2
        Assert.Null(file.Lines[5].OldLine);      // add2
        Assert.Equal(11, file.Lines[5].NewLine);
        Assert.Equal(12, file.Lines[6].OldLine); // ctx3
        Assert.Equal(12, file.Lines[6].NewLine);
    }

    // -----------------------------------------------------------------------
    // Multiple files in one patch
    // -----------------------------------------------------------------------

    [Fact]
    public void Multiple_files_in_one_patch_each_produce_a_separate_DiffFile()
    {
        var patch = string.Join('\n',
            "diff --git a/a.ts b/a.ts",
            "--- a/a.ts",
            "+++ b/a.ts",
            "@@ -1 +1 @@",
            "-old_a",
            "+new_a",
            "diff --git a/b.ts b/b.ts",
            "--- a/b.ts",
            "+++ b/b.ts",
            "@@ -1 +1 @@",
            "-old_b",
            "+new_b");

        var files = UnifiedDiffParser.Parse(patch);

        Assert.Equal(2, files.Count);
        Assert.Equal("a.ts", files[0].Path);
        Assert.Equal(1, files[0].Added);
        Assert.Equal(1, files[0].Removed);
        Assert.Equal("b.ts", files[1].Path);
        Assert.Equal(1, files[1].Added);
        Assert.Equal(1, files[1].Removed);
    }

    // -----------------------------------------------------------------------
    // New file (--- /dev/null) — kind = Addition, path from +++ side
    // -----------------------------------------------------------------------

    [Fact]
    public void New_file_patch_has_Addition_kind_and_uses_new_side_path()
    {
        var patch = string.Join('\n',
            "diff --git a/dev/null b/newfile.ts",
            "new file mode 100644",
            "--- /dev/null",
            "+++ b/newfile.ts",
            "@@ -0,0 +1,2 @@",
            "+line1",
            "+line2");

        var files = UnifiedDiffParser.Parse(patch);

        var file = Assert.Single(files);
        Assert.Equal("newfile.ts", file.Path);
        Assert.Equal(DiffChangeKind.Addition, file.Kind);
        Assert.Equal(2, file.Added);
        Assert.Equal(0, file.Removed);
    }

    // -----------------------------------------------------------------------
    // Deleted file (+++ /dev/null) — kind = Deletion, path from --- side
    // -----------------------------------------------------------------------

    [Fact]
    public void Deleted_file_patch_has_Deletion_kind_and_uses_old_side_path()
    {
        var patch = string.Join('\n',
            "diff --git a/gone.ts b/dev/null",
            "deleted file mode 100644",
            "--- a/gone.ts",
            "+++ /dev/null",
            "@@ -1,2 +0,0 @@",
            "-line1",
            "-line2");

        var files = UnifiedDiffParser.Parse(patch);

        var file = Assert.Single(files);
        Assert.Equal("gone.ts", file.Path);
        Assert.Equal(DiffChangeKind.Deletion, file.Kind);
        Assert.Equal(0, file.Added);
        Assert.Equal(2, file.Removed);
    }

    // -----------------------------------------------------------------------
    // Rename — kind = Rename, path from new side (+++ b/...)
    // -----------------------------------------------------------------------

    [Fact]
    public void Renamed_file_has_Rename_kind_and_uses_new_side_path()
    {
        var patch = string.Join('\n',
            "diff --git a/old.ts b/new.ts",
            "similarity index 95%",
            "rename from old.ts",
            "rename to new.ts",
            "--- a/old.ts",
            "+++ b/new.ts",
            "@@ -1 +1 @@",
            "-line",
            "+line");

        var files = UnifiedDiffParser.Parse(patch);

        var file = Assert.Single(files);
        Assert.Equal("new.ts", file.Path);
        Assert.Equal(DiffChangeKind.Rename, file.Kind);
    }

    // -----------------------------------------------------------------------
    // \ No newline at end of file
    // -----------------------------------------------------------------------

    [Fact]
    public void No_newline_at_eof_produces_a_NoNewline_DiffLine()
    {
        var patch = string.Join('\n',
            "diff --git a/f.ts b/f.ts",
            "--- a/f.ts",
            "+++ b/f.ts",
            "@@ -1 +1 @@",
            "-old",
            "\\ No newline at end of file",
            "+new");

        var files = UnifiedDiffParser.Parse(patch);

        var file = Assert.Single(files);
        var noNewline = file.Lines.Single(l => l.Kind == DiffLineKind.NoNewline);
        Assert.Null(noNewline.OldLine);
        Assert.Null(noNewline.NewLine);
    }

    // -----------------------------------------------------------------------
    // Hunk header with trailing section heading
    // -----------------------------------------------------------------------

    [Fact]
    public void Hunk_header_with_section_heading_produces_a_SectionHeading_DiffLine()
    {
        var patch = string.Join('\n',
            "diff --git a/f.ts b/f.ts",
            "--- a/f.ts",
            "+++ b/f.ts",
            "@@ -1,2 +1,2 @@ class Foo {",
            " unchanged",
            "-old",
            "+new");

        var files = UnifiedDiffParser.Parse(patch);

        var file = Assert.Single(files);
        // The section heading is the first DiffLine.
        Assert.Equal(DiffLineKind.SectionHeading, file.Lines[0].Kind);
        Assert.Equal("class Foo {", file.Lines[0].Text);
        // Hunk counters still start from the @@ values (unchanged at OldLine=1, NewLine=1).
        Assert.Equal(DiffLineKind.Context, file.Lines[1].Kind);
        Assert.Equal(1, file.Lines[1].OldLine);
        Assert.Equal(1, file.Lines[1].NewLine);
    }

    // -----------------------------------------------------------------------
    // Content that legitimately starts with +/-/@ after the marker
    // -----------------------------------------------------------------------

    [Fact]
    public void Content_starting_with_plus_or_minus_after_marker_is_not_mis_parsed()
    {
        var patch = string.Join('\n',
            "diff --git a/f.ts b/f.ts",
            "--- a/f.ts",
            "+++ b/f.ts",
            "@@ -1,4 +1,4 @@",
            "+-- comment",
            " unchanged",
            "-@deprecated",
            "+@@mention");

        var files = UnifiedDiffParser.Parse(patch);

        var file = Assert.Single(files);
        var lines = file.Lines;

        // "+-- comment" → Added with text "-- comment"
        Assert.Equal(DiffLineKind.Added, lines[0].Kind);
        Assert.Equal("-- comment", lines[0].Text);

        // " unchanged" → Context with text "unchanged"
        Assert.Equal(DiffLineKind.Context, lines[1].Kind);
        Assert.Equal("unchanged", lines[1].Text);

        // "-@deprecated" → Removed with text "@deprecated"
        Assert.Equal(DiffLineKind.Removed, lines[2].Kind);
        Assert.Equal("@deprecated", lines[2].Text);

        // "+@@mention" → Added with text "@@mention"
        Assert.Equal(DiffLineKind.Added, lines[3].Kind);
        Assert.Equal("@@mention", lines[3].Text);
    }

    // -----------------------------------------------------------------------
    // Empty / garbage input
    // -----------------------------------------------------------------------

    [Fact]
    public void Empty_input_does_not_throw_and_returns_empty()
    {
        var files = UnifiedDiffParser.Parse(string.Empty);
        Assert.Empty(files);
    }

    [Fact]
    public void Garbage_input_does_not_throw_and_returns_empty()
    {
        var files = UnifiedDiffParser.Parse("not a diff at all\nrandom text");
        Assert.Empty(files);
    }

    [Fact]
    public void Null_equivalent_whitespace_only_input_does_not_throw()
    {
        var files = UnifiedDiffParser.Parse("   \n\n   ");
        Assert.Empty(files);
    }

    // -----------------------------------------------------------------------
    // Hunk header with omitted counts (@@ -1 +1 @@) — defaults to 1
    // -----------------------------------------------------------------------

    [Fact]
    public void Hunk_header_with_omitted_counts_defaults_to_one_line()
    {
        var patch = string.Join('\n',
            "diff --git a/f.ts b/f.ts",
            "--- a/f.ts",
            "+++ b/f.ts",
            "@@ -5 +5 @@",
            "-old",
            "+new");

        var files = UnifiedDiffParser.Parse(patch);

        var file = Assert.Single(files);
        Assert.Equal(1, file.Added);
        Assert.Equal(1, file.Removed);
        // OldLine and NewLine start from 5 (the hunk start).
        Assert.Equal(5, file.Lines[0].OldLine);  // removed starts at oldStart=5
        Assert.Equal(5, file.Lines[1].NewLine);  // added starts at newStart=5
    }

    // -----------------------------------------------------------------------
    // a/ and b/ prefix stripping
    // -----------------------------------------------------------------------

    [Fact]
    public void Paths_with_a_and_b_prefixes_are_stripped()
    {
        var patch = string.Join('\n',
            "diff --git a/src/component.tsx b/src/component.tsx",
            "--- a/src/component.tsx",
            "+++ b/src/component.tsx",
            "@@ -1 +1 @@",
            "-old",
            "+new");

        var files = UnifiedDiffParser.Parse(patch);

        Assert.Equal("src/component.tsx", files[0].Path);
    }

    // -----------------------------------------------------------------------
    // Counts for +++ and --- header lines are not counted as body lines
    // -----------------------------------------------------------------------

    [Fact]
    public void File_header_lines_are_not_counted_in_added_removed_totals()
    {
        // The +++ and --- lines are file headers and must never be counted as added/removed body lines.
        var patch = string.Join('\n',
            "diff --git a/f.ts b/f.ts",
            "--- a/f.ts",
            "+++ b/f.ts",
            "@@ -1,2 +1,2 @@",
            "-rem",
            "+add");

        var files = UnifiedDiffParser.Parse(patch);

        var file = Assert.Single(files);
        Assert.Equal(1, file.Added);
        Assert.Equal(1, file.Removed);
    }

    // ── Regressions found in review ───────────────────────────────────────────

    [Theory]
    [InlineData("-", "--- get user by id")]   // an SQL/Lua/Haskell comment on a removed line
    [InlineData("+", "+++ i;")]               // a C++ increment on an added line
    public void A_body_line_whose_content_starts_with_a_marker_does_not_end_the_hunk(string marker, string bodyLine)
    {
        // "--- x" and "+++ x" inside a hunk are body lines, not file headers. Treating them as headers
        // dropped every remaining line of the hunk and clobbered the file's path.
        var patch = string.Join('\n',
            "diff --git a/q.sql b/q.sql",
            "--- a/q.sql",
            "+++ b/q.sql",
            "@@ -1,5 +1,5 @@",
            " SELECT 1;",
            bodyLine,
            " SELECT 2;",
            " SELECT 3;",
            " SELECT 4;");

        var file = Assert.Single(UnifiedDiffParser.Parse(patch));

        Assert.Equal("q.sql", file.Path);
        Assert.Equal(5, file.Lines.Length);
        Assert.Equal(marker == "+" ? 1 : 0, file.Added);
        Assert.Equal(marker == "-" ? 1 : 0, file.Removed);
        Assert.Equal("SELECT 4;", file.Lines[^1].Text);
    }

    [Fact]
    public void A_trailing_newline_does_not_invent_a_context_line()
    {
        // Real `git diff` output always ends with a newline; yielding the final empty segment made it a
        // context line one past the end of the file.
        var patch = "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1,1 +1,1 @@\n-old\n+new\n";

        var file = Assert.Single(UnifiedDiffParser.Parse(patch));

        Assert.Equal(2, file.Lines.Length);
        Assert.DoesNotContain(file.Lines, l => l.Kind == DiffLineKind.Context);
    }

    [Fact]
    public void A_binary_stanza_still_reports_its_file()
    {
        // A binary stanza carries no ---/+++ headers, so without a diff --git fallback the file
        // vanished entirely from a mixed diff.
        var patch = string.Join('\n',
            "diff --git a/img.png b/img.png",
            "index 1234567..89abcde 100644",
            "Binary files a/img.png and b/img.png differ");

        var file = Assert.Single(UnifiedDiffParser.Parse(patch));

        Assert.Equal("img.png", file.Path);
        Assert.Contains(file.Lines, l => l.Text.Contains("Binary", StringComparison.Ordinal));
    }

    [Fact]
    public void A_mode_only_stanza_still_reports_its_file()
    {
        var patch = string.Join('\n',
            "diff --git a/run.sh b/run.sh",
            "old mode 100644",
            "new mode 100755");

        var file = Assert.Single(UnifiedDiffParser.Parse(patch));

        Assert.Equal("run.sh", file.Path);
        Assert.Equal(2, file.Lines.Length);
    }

    [Fact]
    public void A_combined_merge_diff_is_surfaced_rather_than_rendered_empty()
    {
        // A combined diff has one marker column per parent, so the single-marker grammar cannot number
        // it. Showing the raw hunk beats showing a file with no rows at all.
        var patch = string.Join('\n',
            "diff --cc f.txt",
            "index 1111111,2222222..0000000",
            "--- a/f.txt",
            "+++ b/f.txt",
            "@@@ -1,1 -1,1 +1,3 @@@",
            "++<<<<<<< HEAD",
            " +ours",
            "++=======");

        var file = Assert.Single(UnifiedDiffParser.Parse(patch));

        Assert.NotEmpty(file.Lines);
        Assert.Contains(file.Lines, l => l.Text.Contains("<<<<<<<", StringComparison.Ordinal));
    }

    [Fact]
    public void Line_numbers_stay_correct_across_a_second_hunk()
    {
        var patch = string.Join('\n',
            "diff --git a/a.txt b/a.txt",
            "--- a/a.txt",
            "+++ b/a.txt",
            "@@ -1,2 +1,2 @@",
            " one",
            "-two",
            "+TWO",
            "@@ -50,2 +50,2 @@",
            " fifty",
            "-fiftyone",
            "+FIFTYONE");

        var file = Assert.Single(UnifiedDiffParser.Parse(patch));

        var second = file.Lines[3];
        Assert.Equal(DiffLineKind.Context, second.Kind);
        Assert.Equal(50, second.OldLine);
        Assert.Equal(50, second.NewLine);
        Assert.Equal(51, file.Lines[4].OldLine);
        Assert.Equal(51, file.Lines[5].NewLine);
    }}
