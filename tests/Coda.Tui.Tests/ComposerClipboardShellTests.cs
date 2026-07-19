using System.Collections.Immutable;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

/// <summary>
/// Behavioural coverage for copying the composer selection through a real retained shell: the Ctrl+C
/// precedence over a transcript selection, the shared success/failure copy path used by keyboard and
/// pointer gestures, the modal/startup guards on pointer copy, and the grapheme-aware symbol count.
/// </summary>
public sealed class ComposerClipboardShellTests
{
    private static ImmutableArray<TranscriptBlock> Lines(int count) =>
        Enumerable.Range(0, count)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {index}"))
            .ToImmutableArray();

    /// <summary>Left-drags across row 0 of the shell's composer to natively select <paramref name="text"/>.</summary>
    private static void SelectComposerText(RetainedShellFixture fixture, string text, int fromColumn, int toColumn)
    {
        fixture.Shell.Composer.SetDraft(text, 0);
        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new Point(fromColumn, 0),
        });
        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new Point(toColumn, 0),
        });
        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new Point(toColumn, 0),
        });
    }

    /// <summary>
    /// Left-drags from <paramref name="fromColumn"/> on <paramref name="fromRow"/> to
    /// <paramref name="toColumn"/> on <paramref name="toRow"/> to natively select across rows, used to
    /// isolate the newline separating two composer rows. The draft is set through the composer's own API.
    /// </summary>
    private static void SelectComposerRange(
        RetainedShellFixture fixture,
        string text,
        int fromColumn,
        int fromRow,
        int toColumn,
        int toRow)
    {
        fixture.Shell.Composer.SetDraft(text, 0);
        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new Point(fromColumn, fromRow),
        });
        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new Point(toColumn, toRow),
        });
        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new Point(toColumn, toRow),
        });
    }

    [Fact]
    public void Ctrl_c_with_newline_only_composer_selection_clears_and_copies_zero_symbols()
    {
        var clipboardCalls = 0;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ =>
            {
                clipboardCalls++;
                return true;
            });

        // Select only the newline separating "a" and "b": from the end of row 0 to the start of row 1.
        SelectComposerRange(fixture, "a\nb", fromColumn: 1, fromRow: 0, toColumn: 0, toRow: 1);
        Assert.True(fixture.Shell.Composer.HasComposerSelection);

        // The native TextView reports the row separator using the platform newline (CRLF on Windows); it is a
        // newline-only range that normalises to "\n" and carries zero visible symbols.
        var selectedText = fixture.Shell.Composer.SelectedComposerText;
        Assert.Equal("\n", selectedText.Replace("\r", string.Empty));
        Assert.Equal(0, ClipboardStatusText.CountSymbols(selectedText));

        var draftBefore = fixture.Shell.Composer.GetDraft();
        var caretBefore = fixture.Shell.Composer.GetState().CursorIndex;

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

        // A zero-symbol selection clears with a deterministic confirmation without touching the writer.
        Assert.Equal(0, clipboardCalls);
        Assert.False(fixture.Shell.Composer.HasComposerSelection);
        Assert.Equal("0 symbols copied to clipboard", fixture.Shell.Operational.Status.Text);
        Assert.Equal(draftBefore, fixture.Shell.Composer.GetDraft());
        Assert.Equal(caretBefore, fixture.Shell.Composer.GetState().CursorIndex);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Ctrl_c_with_composer_selection_copies_clears_and_does_not_arm_exit()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });
        SelectComposerText(fixture, "alpha beta", fromColumn: 0, toColumn: 5);
        Assert.True(fixture.Shell.Composer.HasComposerSelection);
        Assert.Equal("alpha", fixture.Shell.Composer.SelectedComposerText);

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.Equal("alpha", copied);
        Assert.False(fixture.Shell.Composer.HasComposerSelection);
        Assert.Equal("5 symbols copied to clipboard", fixture.Shell.Operational.Status.Text);
        Assert.DoesNotContain("Press Ctrl+C again", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Composer_selection_takes_precedence_over_transcript_selection()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });

        fixture.Shell.Transcript.ReplaceAll(Lines(3));
        fixture.Shell.Transcript.BeginSelection(new TranscriptCellPosition(0, 0));
        fixture.Shell.Transcript.UpdateSelection(new TranscriptCellPosition(1, 4));
        Assert.True(fixture.Shell.Transcript.HasSelection);

        SelectComposerText(fixture, "alpha beta", fromColumn: 0, toColumn: 5);

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

        // The composer wins: its text is copied and its selection cleared, while the transcript selection
        // is left untouched for a subsequent Ctrl+C.
        Assert.Equal("alpha", copied);
        Assert.False(fixture.Shell.Composer.HasComposerSelection);
        Assert.True(fixture.Shell.Transcript.HasSelection);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Failed_composer_copy_preserves_selection_and_reports_unavailable()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ => false,
            addTimeout: (_, _) => new object(),
            removeTimeout: _ => true);
        SelectComposerText(fixture, "alpha beta", fromColumn: 0, toColumn: 5);

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.True(fixture.Shell.Composer.HasComposerSelection);
        Assert.Equal("Clipboard unavailable", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Left_click_copy_selection_uses_the_shell_copy_path()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });
        SelectComposerText(fixture, "alpha beta", fromColumn: 0, toColumn: 5);

        // A fresh unshifted left press over the selection raises a semantic CopySelection gesture, which the
        // shell routes through the same copy path as Ctrl+C.
        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new Point(8, 0),
        });

        Assert.Equal("alpha", copied);
        Assert.False(fixture.Shell.Composer.HasComposerSelection);
        Assert.Equal("5 symbols copied to clipboard", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Left_click_copy_with_unavailable_clipboard_preserves_selection()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ => false,
            addTimeout: (_, _) => new object(),
            removeTimeout: _ => true);
        SelectComposerText(fixture, "alpha beta", fromColumn: 0, toColumn: 5);

        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new Point(8, 0),
        });

        Assert.True(fixture.Shell.Composer.HasComposerSelection);
        Assert.Equal("Clipboard unavailable", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Right_click_copy_selection_uses_the_shell_copy_path()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });
        SelectComposerText(fixture, "alpha beta", fromColumn: 0, toColumn: 5);

        // A right press over a selection copies it (context menu is deferred to a right press without one).
        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.RightButtonPressed,
            Position = new Point(2, 0),
        });

        Assert.Equal("alpha", copied);
        Assert.False(fixture.Shell.Composer.HasComposerSelection);
        Assert.Equal("5 symbols copied to clipboard", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Copy_status_counts_graphemes_as_symbols()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });

        // a + combining acute (one grapheme), b, 👍 (one grapheme) => 3 symbols across 4 visual columns.
        // The native editor may normalise the decomposed "a\u0301" to a precomposed "á", but either form is a
        // single grapheme, so the reported symbol count is unaffected.
        SelectComposerText(fixture, "a\u0301b\U0001F44D", fromColumn: 0, toColumn: 6);

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.NotNull(copied);
        Assert.Equal("3 symbols copied to clipboard", fixture.Shell.Operational.Status.Text);
    }

    [Fact]
    public void Copy_status_uses_singular_symbol_for_a_single_selected_element()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ => true);
        SelectComposerText(fixture, "x", fromColumn: 0, toColumn: 1);

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.Equal("1 symbol copied to clipboard", fixture.Shell.Operational.Status.Text);
    }

    [Fact]
    public async Task Prompt_overlay_visible_ignores_pointer_copy()
    {
        var clipboardCalls = 0;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ =>
            {
                clipboardCalls++;
                return true;
            });
        SelectComposerText(fixture, "alpha beta", fromColumn: 0, toColumn: 5);

        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { PendingPrompt = UiPromptRequest.Confirm("Allow?", false) },
            CancellationToken.None);
        Assert.True(fixture.Shell.PromptOverlay.Visible);

        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new Point(8, 0),
        });

        Assert.Equal(0, clipboardCalls);
        Assert.True(fixture.Shell.Composer.HasComposerSelection);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public async Task Startup_disabled_composer_ignores_pointer_copy()
    {
        var clipboardCalls = 0;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ =>
            {
                clipboardCalls++;
                return true;
            });
        SelectComposerText(fixture, "alpha beta", fromColumn: 0, toColumn: 5);

        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with
            {
                ActiveOperation = new ActiveOperation("startup", "Starting…", null),
            },
            CancellationToken.None);

        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new Point(8, 0),
        });

        Assert.Equal(0, clipboardCalls);
        Assert.Empty(fixture.Actions);
    }

    [Theory]
    [InlineData("a\nb", 2)]
    [InlineData("ab\r\ncd", 4)]
    [InlineData("\r\n", 0)]
    [InlineData("a\u0301b\U0001F44D", 3)]
    public void CountSymbols_excludes_cr_lf_and_counts_graphemes(string text, int expected) =>
        Assert.Equal(expected, ClipboardStatusText.CountSymbols(text));

    [Fact]
    public void Copied_uses_singular_for_one_symbol_and_plural_otherwise()
    {
        Assert.Equal("1 symbol copied to clipboard", ClipboardStatusText.Copied("x"));
        Assert.Equal("0 symbols copied to clipboard", ClipboardStatusText.Copied("\r\n"));
        Assert.Equal("2 symbols copied to clipboard", ClipboardStatusText.Copied("ab"));
    }
}
