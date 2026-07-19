using System.Text;

namespace Coda.Tui.Tests;

/// <summary>
/// Guards the startup fix for the mojibake exit card: <see cref="ConsoleOutputEncoding.ConfigureUtf8"/>
/// must configure console output onto UTF-8 (no BOM) so nothing downstream (Spectre's captured console,
/// the exit renderer) is ever left holding a stale OEM/CP437 writer. These tests inject the setter seam
/// and therefore never mutate global <see cref="Console.OutputEncoding"/> or <see cref="Console.Out"/>,
/// so they are hermetic and safe to run in parallel.
/// </summary>
public sealed class ConsoleOutputEncodingTests
{
    [Fact]
    public void ConfigureUtf8_ConfiguresUtf8NoBom()
    {
        Encoding? configured = null;

        ConsoleOutputEncoding.ConfigureUtf8(encoding => configured = encoding);

        Assert.NotNull(configured);
        Assert.Equal(Encoding.UTF8.CodePage, configured!.CodePage);
        Assert.IsType<UTF8Encoding>(configured);
        // UTF-8 without BOM: no preamble bytes.
        Assert.Empty(configured.GetPreamble());
    }

    [Fact]
    public void ConfigureUtf8_EncodesBoxDrawingAndMiddleDotAsValidUtf8()
    {
        Encoding? configured = null;

        ConsoleOutputEncoding.ConfigureUtf8(encoding => configured = encoding);

        Assert.NotNull(configured);
        // Characters the exit card actually emits: box-drawing glyphs and the middle dot separator.
        const string sample = "─│┌┐└┘·";
        var bytes = configured!.GetBytes(sample);

        // A strict UTF-8 decoder (throwOnInvalidBytes) round-trips only if the bytes are valid UTF-8.
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        Assert.Equal(sample, strictUtf8.GetString(bytes));
    }

    [Fact]
    public void ConfigureUtf8_SwallowsIOExceptionFromSetter()
    {
        // Mirrors a host with no real console handle, where assigning Console.OutputEncoding throws.
        // A startup encoding tweak must never abort the program.
        var exception = Record.Exception(() =>
            ConsoleOutputEncoding.ConfigureUtf8(_ => throw new IOException("no console")));

        Assert.Null(exception);
    }
}
