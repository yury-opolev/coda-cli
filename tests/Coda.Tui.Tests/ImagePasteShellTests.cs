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
    internal sealed class FakeImageReader(ClipboardImage? image) : IClipboardImageReader
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

/// <summary>
/// A terminal that claims Ctrl+V for its own paste can only ever hand the application text. When that
/// text is a path to an image file, treating it as an attachment is what makes the obvious gesture work
/// without the user rebinding anything.
/// </summary>
public sealed class ImagePathPasteShellTests : IDisposable
{
    private readonly string directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "coda-paste-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.directory, recursive: true); } catch (IOException) { }
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private string WritePng(string name = "shot.png")
    {
        var path = Path.Combine(this.directory, name);
        File.WriteAllBytes(path, Png);
        return path;
    }

    [Fact]
    public void Pasting_a_path_to_an_image_attaches_it_instead_of_inserting_the_path()
    {
        var path = this.WritePng();
        string? staged = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new ImagePasteShellTests.FakeImageReader(null),
            imagePaste: img => { staged = img.Base64Data; return "[Image 1]"; });

        fixture.Shell.Composer.NewPasteEvent(path);

        Assert.Equal(Convert.ToBase64String(Png), staged);
        Assert.Contains("[Image 1]", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
        Assert.DoesNotContain(path, fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_path_from_copy_as_path_is_attached()
    {
        var path = this.WritePng();
        string? staged = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new ImagePasteShellTests.FakeImageReader(null),
            imagePaste: img => { staged = img.Base64Data; return "[Image 1]"; });

        // Explorer's Shift+right-click → "Copy as path" quotes the path.
        fixture.Shell.Composer.NewPasteEvent($"\"{path}\"");

        Assert.Equal(Convert.ToBase64String(Png), staged);
    }

    [Fact]
    public void Pasting_ordinary_text_is_still_inserted_as_text()
    {
        var attached = false;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new ImagePasteShellTests.FakeImageReader(null),
            imagePaste: _ => { attached = true; return "[Image 1]"; });

        fixture.Shell.Composer.NewPasteEvent("just some prose");

        Assert.False(attached);
        Assert.Contains("just some prose", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_to_a_non_image_file_is_still_inserted_as_text()
    {
        var path = Path.Combine(this.directory, "notes.txt");
        File.WriteAllText(path, "hello");
        var attached = false;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new ImagePasteShellTests.FakeImageReader(null),
            imagePaste: _ => { attached = true; return "[Image 1]"; });

        fixture.Shell.Composer.NewPasteEvent(path);

        Assert.False(attached);
        Assert.Contains("notes.txt", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_that_names_an_image_but_is_not_there_warns_and_pastes_nothing()
    {
        var missing = Path.Combine(this.directory, "absent.png");
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new ImagePasteShellTests.FakeImageReader(null),
            imagePaste: _ => "[Image 1]");

        fixture.Shell.Composer.NewPasteEvent(missing);

        Assert.Equal(OperationalTone.Warning, fixture.Shell.Operational.Status.Tone);
        Assert.Empty(fixture.Shell.Composer.GetDraft());
    }

    [Fact]
    public void The_inserted_token_is_not_itself_examined_as_a_paste()
    {
        // The shell inserts "[Image 1] " through the same paste machinery; re-entering the handler with
        // its own token would be a loop waiting to happen.
        var path = this.WritePng();
        var calls = 0;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new ImagePasteShellTests.FakeImageReader(null),
            imagePaste: _ => { calls++; return "[Image 1]"; });

        fixture.Shell.Composer.NewPasteEvent(path);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_multi_line_paste_mentioning_an_image_stays_text()
    {
        var path = this.WritePng();
        var attached = false;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            imageReader: new ImagePasteShellTests.FakeImageReader(null),
            imagePaste: _ => { attached = true; return "[Image 1]"; });

        fixture.Shell.Composer.NewPasteEvent($"see this:\n{path}");

        Assert.False(attached);
        Assert.Contains("see this:", fixture.Shell.Composer.GetDraft(), StringComparison.Ordinal);
    }
}
