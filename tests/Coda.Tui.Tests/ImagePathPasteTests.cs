using Coda.Tui.Clipboard;

namespace Coda.Tui.Tests;

/// <summary>
/// Windows Terminal claims Ctrl+V for its own paste, and that paste only ever carries text — so a copied
/// bitmap produces nothing at all. What it does carry is a path, when the file was copied with
/// "Copy as path". Recognising such a paste is what lets Ctrl+V attach an image after all.
/// </summary>
public sealed class ImagePathPasteTests
{
    private static string? Path(string? pasted) =>
        ImagePathPaste.TryGetImagePath(pasted, out var path, out _) ? path : null;

    private static string? Media(string pasted) =>
        ImagePathPaste.TryGetImagePath(pasted, out _, out var mediaType) ? mediaType : null;

    [Theory]
    [InlineData(@"C:\shots\a.png")]
    [InlineData(@"C:\shots\a.PNG")]
    [InlineData("/home/y/a.png")]
    [InlineData("relative/a.png")]
    public void A_path_to_an_image_is_recognised(string pasted) =>
        Assert.Equal(pasted, Path(pasted));

    [Fact]
    public void Explorer_copy_as_path_wraps_the_path_in_quotes_and_they_come_off()
    {
        // Shift+right-click → "Copy as path" produces exactly this shape.
        Assert.Equal(@"C:\shots\a.png", Path("\"C:\\shots\\a.png\""));
    }

    [Fact]
    public void Surrounding_whitespace_is_ignored()
    {
        Assert.Equal(@"C:\shots\a.png", Path("  C:\\shots\\a.png \r\n"));
    }

    [Theory]
    [InlineData(".png", "image/png")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".gif", "image/gif")]
    [InlineData(".webp", "image/webp")]
    public void Each_supported_extension_maps_to_its_media_type(string extension, string expected) =>
        Assert.Equal(expected, Media($"C:\\shots\\a{extension}"));

    [Theory]
    [InlineData("just some prose")]
    [InlineData("C:\\notes\\a.txt")]
    [InlineData("a.png.txt")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_that_is_not_an_image_path_is_left_as_text(string? pasted) =>
        Assert.Null(Path(pasted));

    [Fact]
    public void A_multi_line_paste_is_text_even_when_a_line_looks_like_a_path()
    {
        // Pasting a block of prose that happens to mention a file must never swallow the whole payload.
        Assert.Null(Path("here is a file:\nC:\\shots\\a.png"));
    }

    [Fact]
    public void A_bare_extension_is_not_a_path()
    {
        Assert.Null(Path(".png"));
    }

    [Fact]
    public void A_path_that_is_only_quotes_is_not_a_path()
    {
        Assert.Null(Path("\"\""));
    }
}
