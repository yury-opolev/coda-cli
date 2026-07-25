using Coda.Sdk;

namespace Coda.Tui.Tests;

/// <summary>
/// Unit coverage for <see cref="ImageAttachmentValidation"/>: the MIME allow-list, base64 decoding,
/// and the 5 MB size ceiling shared by the clipboard paste path, /image, and the serve handler.
/// </summary>
public sealed class ImageAttachmentValidationTests
{
    [Fact]
    public void Valid_png_returns_no_error()
    {
        var b64 = Convert.ToBase64String([1, 2, 3]);
        Assert.Null(ImageAttachmentValidation.Validate("image/png", b64));
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("IMAGE/PNG")]
    public void Allowed_mime_types_are_accepted(string mediaType)
    {
        Assert.True(ImageAttachmentValidation.IsAllowedMimeType(mediaType));
    }

    [Fact]
    public void Unsupported_type_returns_error()
    {
        var b64 = Convert.ToBase64String([1]);
        var error = ImageAttachmentValidation.Validate("image/bmp", b64);
        Assert.NotNull(error);
        Assert.Contains("Unsupported", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_base64_returns_error()
    {
        var error = ImageAttachmentValidation.Validate("image/png", "not valid base64 !!!");
        Assert.NotNull(error);
        Assert.Contains("base64", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oversized_image_returns_error()
    {
        var big = Convert.ToBase64String(new byte[ImageAttachmentValidation.MaxBytes + 1]);
        var error = ImageAttachmentValidation.Validate("image/png", big);
        Assert.NotNull(error);
        Assert.Contains("too large", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDecodeBase64_rejects_null_and_empty()
    {
        Assert.False(ImageAttachmentValidation.TryDecodeBase64(null, out _));
        Assert.False(ImageAttachmentValidation.TryDecodeBase64(string.Empty, out _));
    }

    [Fact]
    public void TryDecodeBase64_decodes_valid_value()
    {
        var b64 = Convert.ToBase64String([9, 8, 7]);
        Assert.True(ImageAttachmentValidation.TryDecodeBase64(b64, out var bytes));
        Assert.Equal([9, 8, 7], bytes);
    }
}
