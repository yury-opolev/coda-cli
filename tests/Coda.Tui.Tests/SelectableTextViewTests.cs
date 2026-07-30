using System.Collections.Immutable;
using Coda.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Unit tests for <see cref="SelectableTextView"/>: content management, selection/copy model, mouse gestures,
/// theme application, and integration with the shell clipboard path via the header.
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class SelectableTextViewTests : IClassFixture<TerminalGuiApplicationFixture>
{
    private readonly TerminalGuiApplicationFixture appFixture;

    public SelectableTextViewTests(TerminalGuiApplicationFixture appFixture)
    {
        this.appFixture = appFixture;
    }

    // -----------------------------------------------------------------------
    // Content management
    // -----------------------------------------------------------------------

    [Fact]
    public void SetText_splits_on_newlines_and_stores_lines()
    {
        var view = CreateView();
        view.SetText("alpha\nbeta\ngamma");
        Assert.Equal(3, view.Lines.Count);
        Assert.Equal("alpha", view.Lines[0]);
        Assert.Equal("beta", view.Lines[1]);
        Assert.Equal("gamma", view.Lines[2]);
    }

    [Fact]
    public void SetText_normalises_cr_lf_before_splitting()
    {
        var view = CreateView();
        view.SetText("alpha\r\nbeta\rgamma");
        Assert.Equal(3, view.Lines.Count);
        Assert.Equal("alpha", view.Lines[0]);
        Assert.Equal("beta", view.Lines[1]);
        Assert.Equal("gamma", view.Lines[2]);
    }

    [Fact]
    public void SetText_null_is_treated_as_empty()
    {
        var view = CreateView();
        view.SetText(null);
        Assert.Single(view.Lines);
        Assert.Equal(string.Empty, view.Lines[0]);
    }

    [Fact]
    public void SetLines_stores_sanitized_copies()
    {
        var view = CreateView();
        view.SetLines(["alpha", "beta"]);
        Assert.Equal(2, view.Lines.Count);
        Assert.Equal("alpha", view.Lines[0]);
    }

    [Fact]
    public void SetLines_sanitizes_control_sequences()
    {
        var view = CreateView();
        // An escape sequence must be stripped before reaching the terminal.
        view.SetLines(["\x1b[1mhello\x1b[0m"]);
        Assert.DoesNotContain("\x1b", view.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SetLines_clears_any_active_selection()
    {
        var view = CreateView();
        view.SetLines(["one two three"]);
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(5, 0));
        Assert.True(view.HasSelection);

        view.SetLines(["new content"]);
        Assert.False(view.HasSelection);
    }

    [Fact]
    public void AllText_returns_every_line()
    {
        var view = CreateView();
        view.SetText("first\nsecond");
        Assert.Equal("first\nsecond", view.AllText);
    }

    [Fact]
    public void Text_property_is_empty_when_no_lines_set()
    {
        var view = CreateView();
        Assert.Equal(string.Empty, view.Text);
    }

    // -----------------------------------------------------------------------
    // Selection model
    // -----------------------------------------------------------------------

    [Fact]
    public void HasSelection_is_false_initially()
    {
        var view = CreateView();
        view.SetText("hello world");
        Assert.False(view.HasSelection);
    }

    [Fact]
    public void SelectedText_is_empty_when_nothing_selected()
    {
        var view = CreateView();
        view.SetText("hello world");
        Assert.Equal(string.Empty, view.SelectedText);
    }

    [Fact]
    public void ClearSelection_removes_active_selection()
    {
        var view = CreateView();
        view.SetText("hello world");
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(5, 0));
        Assert.True(view.HasSelection);

        view.ClearSelection();
        Assert.False(view.HasSelection);
        Assert.Equal(string.Empty, view.SelectedText);
    }

    [Fact]
    public void Drag_from_left_to_right_selects_cells()
    {
        var view = CreateView();
        view.SetText("hello world");
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(4, 0));
        Assert.True(view.HasSelection);
        Assert.Equal("hello", view.SelectedText);
    }

    [Fact]
    public void Drag_right_to_left_produces_the_same_selection_text()
    {
        var view = CreateView();
        view.SetText("hello world");
        view.ProcessMouse(LeftPress(4, 0));
        view.ProcessMouse(DragMove(0, 0));
        Assert.True(view.HasSelection);
        Assert.Equal("hello", view.SelectedText);
    }

    [Fact]
    public void Fresh_left_press_over_existing_selection_starts_new_selection()
    {
        var view = CreateView();
        view.SetText("hello world");
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(4, 0));
        view.ProcessMouse(Release(4, 0));
        Assert.True(view.HasSelection);

        // Second press clears old selection and begins a new anchor.
        view.ProcessMouse(LeftPress(6, 0));
        // New drag extending the anchor.
        view.ProcessMouse(DragMove(10, 0));
        Assert.True(view.HasSelection);
        Assert.Equal("world", view.SelectedText);
    }

    [Fact]
    public void Multi_line_drag_selects_across_rows()
    {
        var view = CreateView();
        view.SetLines(["abc", "def"]);
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(2, 1));
        Assert.True(view.HasSelection);
        var text = view.SelectedText;
        Assert.Contains("abc", text, StringComparison.Ordinal);
        Assert.Contains("def", text, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Right-click copies
    // -----------------------------------------------------------------------

    [Fact]
    public void Right_click_with_selection_raises_CopyRequested_with_selected_text()
    {
        var view = CreateView();
        view.SetText("hello world");
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(4, 0));
        view.ProcessMouse(Release(4, 0));

        string? received = null;
        view.CopyRequested += text => received = text;

        var handled = view.ProcessMouse(RightClick(2, 0));

        Assert.True(handled);
        Assert.Equal("hello", received);
    }

    [Fact]
    public void Right_click_with_selection_leaves_clearing_to_the_host()
    {
        var view = CreateView();
        view.SetText("hello world");
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(4, 0));
        view.ProcessMouse(Release(4, 0));

        view.CopyRequested += _ => { };
        view.ProcessMouse(RightClick(2, 0));

        // The host owns clearing, because it keeps the selection when the clipboard write fails. A host
        // that ignores the event (as here) therefore leaves the selection standing.
        Assert.True(view.HasSelection);
    }

    [Fact]
    public void Right_click_selection_is_cleared_once_the_host_clears_it()
    {
        var view = CreateView();
        view.SetText("hello world");
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(4, 0));
        view.ProcessMouse(Release(4, 0));

        view.CopyRequested += _ => view.ClearSelection();
        view.ProcessMouse(RightClick(2, 0));

        Assert.False(view.HasSelection);
    }

    [Fact]
    public void Right_click_without_selection_does_not_raise_CopyRequested()
    {
        var view = CreateView();
        view.SetText("hello world");
        var raised = false;
        view.CopyRequested += _ => raised = true;

        view.ProcessMouse(RightClick(2, 0));

        Assert.False(raised);
    }

    [Fact]
    public void Right_click_with_selection_is_consumed()
    {
        var view = CreateView();
        view.SetText("hello world");
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(4, 0));
        view.ProcessMouse(Release(4, 0));

        view.CopyRequested += _ => { };
        var handled = view.ProcessMouse(RightClick(2, 0));

        Assert.True(handled);
    }

    [Fact]
    public void Right_click_without_selection_is_not_consumed()
    {
        var view = CreateView();
        view.SetText("hello world");
        var handled = view.ProcessMouse(RightClick(2, 0));
        Assert.False(handled);
    }

    // -----------------------------------------------------------------------
    // Left-click does not copy
    // -----------------------------------------------------------------------

    [Fact]
    public void Left_click_does_not_raise_CopyRequested()
    {
        var view = CreateView();
        view.SetText("hello world");
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(4, 0));
        view.ProcessMouse(Release(4, 0));

        var raised = false;
        view.CopyRequested += _ => raised = true;
        view.ProcessMouse(LeftPress(2, 0));

        Assert.False(raised);
    }

    // -----------------------------------------------------------------------
    // Shell clipboard integration via header
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Header_right_click_copies_via_shell_clipboard_path()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });
        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { SessionId = "ses-001", Model = "gpt-4" },
            CancellationToken.None);

        // Drag-select in the header.
        fixture.Shell.Header.ProcessMouse(LeftPress(0, 0));
        fixture.Shell.Header.ProcessMouse(DragMove(6, 0));
        fixture.Shell.Header.ProcessMouse(Release(6, 0));
        Assert.True(fixture.Shell.Header.HasSelection);

        // Right-click triggers copy via shell clipboard path.
        fixture.Shell.Header.ProcessMouse(RightClick(2, 0));

        Assert.NotNull(copied);
        Assert.False(fixture.Shell.Header.HasSelection);
        Assert.Contains("copied to clipboard", fixture.Shell.Operational.Status.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ctrl_C_with_header_selection_copies_via_shell_path()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });
        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { SessionId = "ses-001", Model = "gpt-4" },
            CancellationToken.None);

        // Select text in the header.
        fixture.Shell.Header.ProcessMouse(LeftPress(0, 0));
        fixture.Shell.Header.ProcessMouse(DragMove(6, 0));
        fixture.Shell.Header.ProcessMouse(Release(6, 0));
        Assert.True(fixture.Shell.Header.HasSelection);

        // Ctrl+C with header selection should copy header text, not arm exit chord.
        fixture.Shell.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.NotNull(copied);
        Assert.False(fixture.Shell.Header.HasSelection);
        Assert.DoesNotContain("Press Ctrl+C again", fixture.Shell.Operational.Status.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ctrl_C_without_header_selection_does_not_copy_header_and_arms_chord()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });
        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { SessionId = "ses-001", Model = "gpt-4" },
            CancellationToken.None);

        Assert.False(fixture.Shell.Header.HasSelection);

        // Ctrl+C with no selection anywhere should arm the exit chord.
        fixture.Shell.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.Null(copied);
        Assert.Contains("Press Ctrl+C again", fixture.Shell.Operational.Status.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Header_selection_takes_precedence_over_transcript_for_ctrl_c()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });
        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { SessionId = "ses-001" },
            CancellationToken.None);

        var transcriptLines = Enumerable.Range(0, 3)
            .Select(i => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"tline {i}"))
            .ToImmutableArray();
        fixture.Shell.Transcript.ReplaceAll(transcriptLines);

        // Create a transcript selection.
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(0, 0),
        });
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new System.Drawing.Point(4, 0),
        });
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new System.Drawing.Point(4, 0),
        });
        Assert.True(fixture.Shell.Transcript.HasSelection);

        // Also create a header selection.
        fixture.Shell.Header.ProcessMouse(LeftPress(0, 0));
        fixture.Shell.Header.ProcessMouse(DragMove(6, 0));
        fixture.Shell.Header.ProcessMouse(Release(6, 0));
        Assert.True(fixture.Shell.Header.HasSelection);

        // Ctrl+C: header selection wins.
        fixture.Shell.NewKeyDownEvent(Key.C.WithCtrl);

        // Header was copied (starts with session id "ses-001").
        Assert.StartsWith("ses-001", copied, StringComparison.Ordinal);
        Assert.False(fixture.Shell.Header.HasSelection);
        // Transcript selection should still be present (header wins, not transcript).
        Assert.True(fixture.Shell.Transcript.HasSelection);
    }

    [Fact]
    public async Task UpdateHeader_sets_readable_text()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { SessionId = "ses-abc", Model = "gpt-5" },
            CancellationToken.None);

        Assert.Contains("ses-abc", fixture.Shell.Header.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Header_is_not_focusable()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        await fixture.Shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
        Assert.False(fixture.Shell.Header.CanFocus);
    }

    [Fact]
    public async Task Header_has_no_explicit_scheme()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        await fixture.Shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
        Assert.False(fixture.Shell.Header.HasScheme);
    }

    // -----------------------------------------------------------------------
    // A failed clipboard write must not lose the selection
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Header_right_click_keeps_the_selection_when_the_clipboard_is_unavailable()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ => false);
        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { SessionId = "ses-001", Model = "gpt-4" },
            CancellationToken.None);

        fixture.Shell.Header.ProcessMouse(LeftPress(0, 0));
        fixture.Shell.Header.ProcessMouse(DragMove(6, 0));
        fixture.Shell.Header.ProcessMouse(Release(6, 0));
        Assert.True(fixture.Shell.Header.HasSelection);

        fixture.Shell.Header.ProcessMouse(RightClick(2, 0));

        // The write failed, so the selection is kept for a retry rather than silently discarded.
        Assert.True(fixture.Shell.Header.HasSelection);
        Assert.Contains("Clipboard unavailable", fixture.Shell.Operational.Status.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcript_right_click_keeps_the_selection_when_the_clipboard_is_unavailable()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ => false);
        await fixture.Shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);

        var lines = Enumerable.Range(0, 3)
            .Select(i => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"tline {i}"))
            .ToImmutableArray();
        fixture.Shell.Transcript.ReplaceAll(lines);

        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(0, 0),
        });
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new System.Drawing.Point(4, 0),
        });
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new System.Drawing.Point(4, 0),
        });
        Assert.True(fixture.Shell.Transcript.HasSelection);

        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.RightButtonClicked,
            Position = new System.Drawing.Point(2, 0),
        });

        Assert.True(fixture.Shell.Transcript.HasSelection);
        Assert.Contains("Clipboard unavailable", fixture.Shell.Operational.Status.Text, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // A selection survives repeated identical content
    // -----------------------------------------------------------------------

    [Fact]
    public void Re_setting_identical_text_keeps_the_selection()
    {
        var view = CreateView();
        view.SetText("ses-001 · gpt-4");
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(6, 0));
        view.ProcessMouse(Release(6, 0));
        Assert.True(view.HasSelection);

        // Hosts re-apply their text on every frame; identical content must not destroy the selection.
        view.SetText("ses-001 · gpt-4");

        Assert.True(view.HasSelection);
    }

    [Fact]
    public void Changed_text_clears_the_selection_and_ends_any_drag()
    {
        var view = CreateView();
        view.SetText("ses-001 · gpt-4");
        view.ProcessMouse(LeftPress(0, 0));
        view.ProcessMouse(DragMove(6, 0));
        Assert.True(view.HasSelection);

        view.SetText("ses-002 · gpt-4");

        Assert.False(view.HasSelection);

        // The drag ended with the content swap, so a further move re-anchors rather than selecting from
        // a stale zeroed anchor.
        view.ProcessMouse(DragMove(9, 0));
        Assert.False(view.HasSelection);
    }

    [Fact]
    public async Task Header_selection_survives_an_applied_frame_and_a_scroll()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        var snapshot = UiSessionSnapshot.Empty with { SessionId = "ses-001", Model = "gpt-4" };
        await fixture.Shell.ApplyAsync(snapshot, CancellationToken.None);

        fixture.Shell.Header.ProcessMouse(LeftPress(0, 0));
        fixture.Shell.Header.ProcessMouse(DragMove(6, 0));
        fixture.Shell.Header.ProcessMouse(Release(6, 0));
        Assert.True(fixture.Shell.Header.HasSelection);

        // Every applied frame and every transcript scroll rewrites the header text. Neither may destroy a
        // selection the user is in the middle of copying.
        await fixture.Shell.ApplyAsync(snapshot, CancellationToken.None);
        fixture.Shell.Transcript.ReplaceAll(
            Enumerable.Range(0, 40)
                .Select(i => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {i}"))
                .ToImmutableArray());
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.WheeledUp,
            Position = new System.Drawing.Point(0, 0),
        });

        Assert.True(fixture.Shell.Header.HasSelection);
    }

    // -----------------------------------------------------------------------
    // Native terminal selection wins
    // -----------------------------------------------------------------------

    [Fact]
    public void Shift_drag_is_left_to_the_terminal()
    {
        var view = CreateView();
        view.SetText("hello world");

        var handled = view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.Shift,
            Position = new System.Drawing.Point(0, 0),
        });

        Assert.False(handled);
        Assert.False(view.HasSelection);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private SelectableTextView CreateView() =>
        new(this.appFixture.App);

    private static Mouse LeftPress(int x, int y) => new()
    {
        Flags = MouseFlags.LeftButtonPressed,
        Position = new System.Drawing.Point(x, y),
    };

    private static Mouse DragMove(int x, int y) => new()
    {
        Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
        Position = new System.Drawing.Point(x, y),
    };

    private static Mouse Release(int x, int y) => new()
    {
        Flags = MouseFlags.LeftButtonReleased,
        Position = new System.Drawing.Point(x, y),
    };

    private static Mouse RightClick(int x, int y) => new()
    {
        Flags = MouseFlags.RightButtonClicked,
        Position = new System.Drawing.Point(x, y),
    };
}

/// <summary>Provides a single long-lived <see cref="IApplication"/> instance for the test class.</summary>
public sealed class TerminalGuiApplicationFixture : IDisposable
{
    private bool disposed;

    public TerminalGuiApplicationFixture()
    {
        this.App = Application.Create();
        this.App.AppModel = AppModel.FullScreen;
        this.App.Init(DriverRegistry.Names.ANSI);
        this.App.Driver!.SetScreenSize(80, 24);
    }

    internal IApplication App { get; }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.App.Dispose();
    }
}
