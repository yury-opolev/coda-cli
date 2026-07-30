using System.Collections.Immutable;
using Coda.Agent;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Formatter-level tests for the rich diff rendering path. These tests exercise
/// <see cref="TranscriptBlockFormatter"/> end-to-end with realistic patch input to assert
/// the structural and semantic correctness of the emitted <see cref="TranscriptRenderLine"/>s:
/// roles, <see cref="TranscriptRenderLine.FillWidth"/>, line-number gutter, and width bounds.
/// </summary>
public sealed class DiffFormatterTests
{
    // A minimal but realistic single-file patch used by several tests below.
    private const string SingleFilePatch =
        "diff --git a/src/app.ts b/src/app.ts\n" +
        "--- a/src/app.ts\n" +
        "+++ b/src/app.ts\n" +
        "@@ -1,3 +1,3 @@\n" +
        " context\n" +
        "-removed\n" +
        "+added\n" +
        " trailing";

    // -----------------------------------------------------------------------
    // File header — "Update(path)" and AgentComplete gutter
    // -----------------------------------------------------------------------

    [Fact]
    public void Header_row_reads_Update_path_and_carries_DiffHeader_role()
    {
        var block = new DiffTranscriptBlock(Guid.NewGuid(), SingleFilePatch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        // The first rendered row is the header: "● Update(src/app.ts)" (with gutter applied).
        var header = lines[0];
        Assert.Equal(TranscriptRole.DiffHeader, header.Role);
        Assert.Contains("Update(src/app.ts)", header.Text, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Summary — "Added N lines, removed M lines"
    // -----------------------------------------------------------------------

    [Fact]
    public void Summary_row_reads_Added_N_removed_M_and_carries_DiffContext_role()
    {
        var block = new DiffTranscriptBlock(Guid.NewGuid(), SingleFilePatch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        // The second rendered row is the summary.
        var summary = lines[1];
        Assert.Equal(TranscriptRole.DiffContext, summary.Role);
        Assert.Contains("Added 1 lines, removed 1 lines", summary.Text, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Added rows — DiffAdded role + FillWidth
    // -----------------------------------------------------------------------

    [Fact]
    public void Added_rows_carry_DiffAdded_role_and_FillWidth()
    {
        var block = new DiffTranscriptBlock(Guid.NewGuid(), SingleFilePatch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        var addedRows = lines.Where(l => l.Role == TranscriptRole.DiffAdded).ToArray();
        Assert.NotEmpty(addedRows);
        Assert.All(addedRows, row => Assert.True(row.FillWidth, "added rows must have FillWidth = true"));
        // Content includes the added text.
        Assert.Contains(addedRows, r => r.Text.Contains("added", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Removed rows — DiffRemoved role + FillWidth
    // -----------------------------------------------------------------------

    [Fact]
    public void Removed_rows_carry_DiffRemoved_role_and_FillWidth()
    {
        var block = new DiffTranscriptBlock(Guid.NewGuid(), SingleFilePatch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        var removedRows = lines.Where(l => l.Role == TranscriptRole.DiffRemoved).ToArray();
        Assert.NotEmpty(removedRows);
        Assert.All(removedRows, row => Assert.True(row.FillWidth, "removed rows must have FillWidth = true"));
        Assert.Contains(removedRows, r => r.Text.Contains("removed", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Context rows — DiffContext role, NOT FillWidth
    // -----------------------------------------------------------------------

    [Fact]
    public void Context_rows_carry_DiffContext_role_and_not_FillWidth()
    {
        var block = new DiffTranscriptBlock(Guid.NewGuid(), SingleFilePatch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        // Filter to body context rows (excluding the summary which is also DiffContext with gutter).
        var bodyContextRows = lines
            .Where(l => l.Role == TranscriptRole.DiffContext && l.Gutter == TranscriptGutterKind.None)
            .ToArray();
        Assert.NotEmpty(bodyContextRows);
        Assert.All(bodyContextRows, row => Assert.False(row.FillWidth, "context rows must NOT have FillWidth"));
    }

    // -----------------------------------------------------------------------
    // Line-number gutter — present, right-aligned, covered by PrefixCells
    // -----------------------------------------------------------------------

    [Fact]
    public void Body_rows_have_a_line_number_gutter_covered_by_PrefixCells()
    {
        var block = new DiffTranscriptBlock(Guid.NewGuid(), SingleFilePatch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        var bodyRows = lines
            .Where(l => l.Gutter == TranscriptGutterKind.None && l.Role != TranscriptRole.Diff)
            .ToArray();
        Assert.NotEmpty(bodyRows);
        Assert.All(bodyRows, row =>
        {
            Assert.True(row.PrefixCells > 0, $"body row \"{row.Text}\" must have PrefixCells > 0");
            Assert.Equal(TranscriptRole.DiffContext, row.PrefixRole);
        });
    }

    [Fact]
    public void Body_rows_have_right_aligned_line_number_in_text()
    {
        var block = new DiffTranscriptBlock(Guid.NewGuid(), SingleFilePatch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        // The removed row (line 2 in the old file) should contain "2" in the line-number gutter.
        var removedRow = lines.Single(l => l.Role == TranscriptRole.DiffRemoved);
        Assert.Contains("2", removedRow.Text, StringComparison.Ordinal);

        // The added row (line 2 in the new file) should also contain "2".
        var addedRow = lines.Single(l => l.Role == TranscriptRole.DiffAdded);
        Assert.Contains("2", addedRow.Text, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Hunk headers produce no body rows
    // -----------------------------------------------------------------------

    [Fact]
    public void Hunk_header_lines_produce_no_rendered_rows()
    {
        // A patch with a visible @@ header — the hunk header must not appear in the output.
        var patch =
            "diff --git a/f.ts b/f.ts\n" +
            "--- a/f.ts\n" +
            "+++ b/f.ts\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new";

        var block = new DiffTranscriptBlock(Guid.NewGuid(), patch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.DoesNotContain(lines, l => l.Text.Contains("@@", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // diff --git / index lines produce no body rows
    // -----------------------------------------------------------------------

    [Fact]
    public void Git_metadata_lines_produce_no_rendered_rows()
    {
        var block = new DiffTranscriptBlock(Guid.NewGuid(), SingleFilePatch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.DoesNotContain(lines, l => l.Text.Contains("diff --git", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Text.StartsWith("--- ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Text.StartsWith("+++ ", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Row width — all rows fit requested width at small widths
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    public void All_diff_rows_fit_the_requested_width(int width)
    {
        var block = new DiffTranscriptBlock(Guid.NewGuid(), SingleFilePatch);
        var lines = TranscriptBlockFormatter.Format(block, width);

        foreach (var line in lines)
        {
            var cells = TerminalCellText.Width(line.Text);
            Assert.True(
                cells <= width,
                $"Row \"{line.Text}\" measures {cells} cells, exceeds {width}-cell viewport.");
        }
    }

    // -----------------------------------------------------------------------
    // New file patch — header reads "Create(path)"
    // -----------------------------------------------------------------------

    [Fact]
    public void New_file_patch_header_reads_Create_path()
    {
        var patch =
            "diff --git a/dev/null b/newfile.ts\n" +
            "new file mode 100644\n" +
            "--- /dev/null\n" +
            "+++ b/newfile.ts\n" +
            "@@ -0,0 +1 @@\n" +
            "+line";

        var block = new DiffTranscriptBlock(Guid.NewGuid(), patch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.Contains(lines, l => l.Text.Contains("Create(newfile.ts)", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Deleted file patch — header reads "Delete(path)"
    // -----------------------------------------------------------------------

    [Fact]
    public void Deleted_file_patch_header_reads_Delete_path()
    {
        var patch =
            "diff --git a/gone.ts b/dev/null\n" +
            "deleted file mode 100644\n" +
            "--- a/gone.ts\n" +
            "+++ /dev/null\n" +
            "@@ -1 +0,0 @@\n" +
            "-line";

        var block = new DiffTranscriptBlock(Guid.NewGuid(), patch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.Contains(lines, l => l.Text.Contains("Delete(gone.ts)", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Multi-file patch — one header+summary block per file
    // -----------------------------------------------------------------------

    [Fact]
    public void Multi_file_patch_emits_one_header_per_file()
    {
        var patch =
            "diff --git a/a.ts b/a.ts\n" +
            "--- a/a.ts\n" +
            "+++ b/a.ts\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new\n" +
            "diff --git a/b.ts b/b.ts\n" +
            "--- a/b.ts\n" +
            "+++ b/b.ts\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new";

        var block = new DiffTranscriptBlock(Guid.NewGuid(), patch);
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.Contains(lines, l => l.Text.Contains("Update(a.ts)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Text.Contains("Update(b.ts)", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Legacy fallback — unparseable input still renders as Diff role
    // -----------------------------------------------------------------------

    [Fact]
    public void Unparseable_input_falls_back_to_flat_legacy_rendering()
    {
        // This patch has no diff --git / --- / +++ headers so the parser yields 0 files.
        var block = new DiffTranscriptBlock(Guid.NewGuid(), "-old\n+new");
        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        // The legacy fallback still renders lines, one per source line.
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Equal(TranscriptRole.Diff, l.Role));
    }
}
