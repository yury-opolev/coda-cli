using Coda.Tui.Clipboard;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

/// <summary>
/// Behavioural coverage for the shell's clipboard-image paste interception: an image on the clipboard is
/// staged and its token inserted into the draft (preferred over text); an absent image falls through to
/// the existing text-paste path unchanged; a rejected image pins a warning without a text fallthrough.
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class ImagePasteShellTests
{
    private sealed class FakeImageReader(ClipboardImage? image) : IClipboardImageReader
    {
        public ClipboardImage? TryRead() => image;
    }

    /// <summary>
    /// Runs the application loop until <paramref name="condition"/> holds. The clipboard image probe is
    /// deliberately performed off the UI thread — a per-OS reader shells out to a helper process — so the
    /// paste completes on a later main-loop iteration rather than inline with the gesture.
    /// </summary>
    private static void PumpUntil(RetainedShellFixture fixture, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            fixture.HostApplication.RaiseIteration();
            Thread.Sleep(1);
        }

        fixture.HostApplication.RaiseIteration();
    }
    private static void RightClickPaste(RetainedShellFixture fixture, int column, int row)
    {
        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.RightButtonPressed,
            Position = new Point(column, row),
        });
        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.RightButtonReleased,
            Position = new Point(column, row),
        });
        fixture.Shell.Composer.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.RightButtonClicked,
            Position = new Point(column, row),
        });
    }

    [Fact]
    public void Pasting_an_image_stages_it_and_inserts_the_token()
    {
        var image = new ClipboardImage("image/png", Convert.ToBase64String([1, 2, 3]), 3);
        string? stagedBase64 = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(image),
            imagePaste: img =>
            {
                stagedBase64 = img.Base64Data;
                return "[Image 1]";
            });

        RightClickPaste(fixture, column: 0, row: 0);

        Assert.Equal(image.Base64Data, stagedBase64);
        Assert.Contains("[Image 1]", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
        Assert.Contains("image", fixture.Shell.Operational.Status.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OperationalTone.Ready, fixture.Shell.Operational.Status.Tone);
    }

    [Fact]
    public void No_image_on_clipboard_falls_back_to_text_paste()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(null),
            imagePaste: _ => "[Image 1]",
            clipboardReader: () => new ClipboardReadResult(true, "hello"));

        RightClickPaste(fixture, column: 0, row: 0);

        Assert.Contains("hello", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
        Assert.DoesNotContain("[Image 1]", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rejected_image_shows_warning_and_does_not_paste_text()
    {
        var image = new ClipboardImage("image/bmp", "AA==", 1);
        var textReadInvoked = false;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(image),
            imagePaste: _ => null,
            clipboardReader: () =>
            {
                textReadInvoked = true;
                return new ClipboardReadResult(true, "hello");
            });

        RightClickPaste(fixture, column: 0, row: 0);

        Assert.False(textReadInvoked);
        Assert.DoesNotContain("hello", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
        Assert.Equal(OperationalTone.Warning, fixture.Shell.Operational.Status.Tone);
    }

    // ── Ctrl+V takes the same image-first path as a right-click ───────────────

    [Fact]
    public void Ctrl_v_pastes_an_image_from_the_clipboard()
    {
        var image = new ClipboardImage("image/png", Convert.ToBase64String([1, 2, 3]), 3);
        string? stagedBase64 = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(image),
            imagePaste: img =>
            {
                stagedBase64 = img.Base64Data;
                return "[Image 1]";
            });

        // Ctrl+V used to fall through to the editor's native paste, which only ever sees text, so a
        // copied image was silently dropped and only a right-click could attach one.
        fixture.Shell.Composer.NewKeyDownEvent(Key.V.WithCtrl);

        Assert.Equal(image.Base64Data, stagedBase64);
        Assert.Contains("[Image 1]", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
        Assert.Equal(OperationalTone.Ready, fixture.Shell.Operational.Status.Tone);
    }

    [Fact]
    public void Ctrl_v_falls_back_to_text_when_no_image_is_on_the_clipboard()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(null),
            imagePaste: _ => "[Image 1]",
            clipboardReader: () => new ClipboardReadResult(true, "hello"));

        fixture.Shell.Composer.NewKeyDownEvent(Key.V.WithCtrl);

        Assert.Equal("hello", fixture.Shell.Composer.GetDraft());
        Assert.DoesNotContain("[Image 1]", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
    }

    [Fact]
    public void Ctrl_v_and_right_click_agree_on_a_rejected_image()
    {
        var image = new ClipboardImage("image/bmp", "AA==", 1);
        var textReadInvoked = false;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(image),
            imagePaste: _ => null,
            clipboardReader: () =>
            {
                textReadInvoked = true;
                return new ClipboardReadResult(true, "hello");
            });

        fixture.Shell.Composer.NewKeyDownEvent(Key.V.WithCtrl);

        // A present-but-rejected image warns; it must not quietly paste the text clipboard instead.
        Assert.False(textReadInvoked);
        Assert.DoesNotContain("hello", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
        Assert.Equal(OperationalTone.Warning, fixture.Shell.Operational.Status.Tone);
    }

    [Fact]
    public void Ctrl_v_from_the_transcript_pastes_into_a_focused_composer()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(null),
            imagePaste: _ => "[Image 1]",
            clipboardReader: () => new ClipboardReadResult(true, "hello"));

        // The transcript forwards keys it does not consume to the shell. The paste must land in the
        // composer AND take focus with it, or the next Enter would toggle a transcript block instead of
        // submitting the freshly pasted prompt.
        fixture.Shell.Transcript.SetFocus();
        fixture.Shell.Transcript.NewKeyDownEvent(Key.V.WithCtrl);

        Assert.Equal("hello", fixture.Shell.Composer.GetDraft());
        Assert.True(fixture.Shell.Composer.HasFocus);
    }

    [Fact]
    public void Ctrl_v_from_the_transcript_does_nothing_while_the_composer_is_disabled()
    {
        var readerInvoked = false;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(null),
            imagePaste: _ => "[Image 1]",
            clipboardReader: () =>
            {
                readerInvoked = true;
                return new ClipboardReadResult(true, "hello");
            });
        fixture.Shell.Composer.InputEnabled = false;
        fixture.Shell.Transcript.SetFocus();

        // Routed through the transcript because a disabled composer swallows its own keys before the
        // shell handler runs — this is the path where the guard actually has to hold.
        fixture.Shell.Transcript.NewKeyDownEvent(Key.V.WithCtrl);

        Assert.False(readerInvoked);
        Assert.Empty(fixture.Shell.Composer.GetDraft());
    }

    // -----------------------------------------------------------------------
    // Alt+V — the binding that actually survives a terminal emulator
    // -----------------------------------------------------------------------

    [Fact]
    public void Alt_v_attaches_an_image_from_the_clipboard()
    {
        var image = new ClipboardImage("image/png", "AAA", 3);
        string? stagedBase64 = null;

        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(image),
            imagePaste: img =>
            {
                stagedBase64 = img.Base64Data;
                return "[Image 1]";
            });

        // Windows Terminal binds both ctrl+v and ctrl+shift+v to its own paste action, so neither key
        // ever reaches the application. Alt+V is left alone by the emulator, which makes it the binding
        // that can actually attach an image.
        fixture.Shell.Composer.NewKeyDownEvent(Key.V.WithAlt);

        Assert.Equal(image.Base64Data, stagedBase64);
        Assert.Contains("[Image 1]", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
    }

    [Fact]
    public void Alt_v_falls_back_to_text_when_no_image_is_on_the_clipboard()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(null),
            imagePaste: _ => "[Image 1]",
            clipboardReader: () => new ClipboardReadResult(true, "hello"));

        fixture.Shell.Composer.NewKeyDownEvent(Key.V.WithAlt);

        Assert.Equal("hello", fixture.Shell.Composer.GetDraft());
    }

    [Fact]
    public void Alt_v_from_the_transcript_pastes_into_the_composer_and_takes_focus()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(null),
            imagePaste: _ => "[Image 1]",
            clipboardReader: () => new ClipboardReadResult(true, "hello"));

        fixture.Shell.Transcript.SetFocus();
        fixture.Shell.Transcript.NewKeyDownEvent(Key.V.WithAlt);

        Assert.Equal("hello", fixture.Shell.Composer.GetDraft());
        Assert.True(fixture.Shell.Composer.HasFocus);
    }

    [Fact]
    public void Alt_v_from_the_transcript_does_nothing_while_the_composer_is_disabled()
    {
        var readerInvoked = false;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new FakeImageReader(null),
            imagePaste: _ => "[Image 1]",
            clipboardReader: () =>
            {
                readerInvoked = true;
                return new ClipboardReadResult(true, "hello");
            });
        fixture.Shell.Composer.InputEnabled = false;
        fixture.Shell.Transcript.SetFocus();

        fixture.Shell.Transcript.NewKeyDownEvent(Key.V.WithAlt);

        Assert.False(readerInvoked);
        Assert.Empty(fixture.Shell.Composer.GetDraft());
    }
}
