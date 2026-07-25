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
public sealed class ImagePasteShellTests
{
    private sealed class FakeImageReader(ClipboardImage? image) : IClipboardImageReader
    {
        public ClipboardImage? TryRead() => image;
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
}
