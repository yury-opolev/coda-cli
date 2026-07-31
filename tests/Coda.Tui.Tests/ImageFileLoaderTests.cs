using System.IO;
using Coda.Tui.Clipboard;

namespace Coda.Tui.Tests;

/// <summary>
/// Loads an image named by a pasted path. Exercised against real files on disk rather than a seam,
/// because the things worth pinning here — a missing file, an oversized one, bytes that are not the
/// image the extension promises — are all properties of the file system, and faking them would only
/// test the fake.
/// </summary>
public sealed class ImageFileLoaderTests : IDisposable
{
    private readonly string directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "coda-img-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.directory, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file must never fail the suite.
        }
    }

    /// <summary>The 8-byte PNG signature, which is what the attachment validator checks for.</summary>
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(this.directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void A_real_png_is_loaded_with_its_media_type_and_size()
    {
        var path = this.WriteFile("shot.png", PngHeader);

        Assert.True(ImageFileLoader.TryLoad(path, "image/png", out var image, out var error));

        Assert.Null(error);
        Assert.Equal("image/png", image!.MediaType);
        Assert.Equal(PngHeader.Length, image.ByteLength);
        Assert.Equal(Convert.ToBase64String(PngHeader), image.Base64Data);
    }

    [Fact]
    public void A_missing_file_reports_an_error_rather_than_throwing()
    {
        var path = Path.Combine(this.directory, "absent.png");

        Assert.False(ImageFileLoader.TryLoad(path, "image/png", out var image, out var error));

        Assert.Null(image);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_file_over_the_size_limit_is_refused()
    {
        var big = new byte[(5 * 1024 * 1024) + 1];
        PngHeader.CopyTo(big, 0);
        var path = this.WriteFile("huge.png", big);

        Assert.False(ImageFileLoader.TryLoad(path, "image/png", out var image, out var error));

        Assert.Null(image);
        Assert.Contains("large", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bytes_that_are_not_the_promised_image_are_refused()
    {
        // A .png whose contents are not a PNG must not be attached just because it is named one.
        var path = this.WriteFile("liar.png", "this is plain text"u8.ToArray());

        Assert.False(ImageFileLoader.TryLoad(path, "image/png", out var image, out var error));

        Assert.Null(image);
        Assert.NotNull(error);
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        var path = this.WriteFile("empty.png", []);

        Assert.False(ImageFileLoader.TryLoad(path, "image/png", out var image, out _));

        Assert.Null(image);
    }

    [Fact]
    public void A_directory_is_not_an_image()
    {
        Assert.False(ImageFileLoader.TryLoad(this.directory, "image/png", out var image, out _));

        Assert.Null(image);
    }
}
