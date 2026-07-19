using System.Text;

namespace Coda.Tui.Tests;

/// <summary>
/// Guards the startup fix for the mojibake exit card: <see cref="ConsoleOutputEncoding.ConfigureUtf8"/>
/// must force the process' console output onto UTF-8 (no BOM) and replace the current
/// <see cref="Console.Out"/> writer, so nothing downstream (Spectre's captured console, the exit
/// renderer) is ever left holding a stale OEM/CP437 writer. These tests mutate global console state,
/// so they live in a non-parallel collection and each restores the original encoding/writer.
/// </summary>
[Collection("ConsoleState")]
public sealed class ConsoleOutputEncodingTests
{
    [Fact]
    public void ConfigureUtf8_SwitchesEncodingToUtf8AndReplacesWriter()
    {
        var originalOut = Console.Out;
        var originalEncoding = Console.OutputEncoding;
        try
        {
            // Simulate the OEM/CP437 codepage the Windows console starts with before Terminal.Gui
            // flips it to UTF-8. Latin1 is a built-in, single-byte, non-UTF8 encoding.
            Console.OutputEncoding = Encoding.Latin1;
            var staleEncoding = Console.OutputEncoding;
            var staleWriter = Console.Out;

            ConsoleOutputEncoding.ConfigureUtf8();

            Assert.Equal(Encoding.UTF8.CodePage, Console.OutputEncoding.CodePage);
            Assert.IsType<UTF8Encoding>(Console.OutputEncoding);
            // UTF-8 without BOM: no preamble bytes.
            Assert.Empty(Console.OutputEncoding.GetPreamble());
            // The stale CP437-like encoding must actually be replaced, not left in place.
            Assert.NotEqual(staleEncoding.CodePage, Console.OutputEncoding.CodePage);

            // On a real (non-redirected) console — the production path — assigning the encoding rebuilds
            // Console.Out, so the stale writer is discarded and the live writer speaks UTF-8. Under a
            // redirected test host, .NET intentionally keeps the redirected writer, so this side effect
            // is only observable (and only matters) when stdout is a genuine console handle.
            if (!Console.IsOutputRedirected)
            {
                Assert.NotSame(staleWriter, Console.Out);
                Assert.Equal(Encoding.UTF8.CodePage, Console.Out.Encoding.CodePage);
            }
        }
        finally
        {
            Console.OutputEncoding = originalEncoding;
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ConfigureUtf8_EncodesBoxDrawingAndMiddleDotAsValidUtf8()
    {
        var originalOut = Console.Out;
        var originalEncoding = Console.OutputEncoding;
        try
        {
            Console.OutputEncoding = Encoding.Latin1;

            ConsoleOutputEncoding.ConfigureUtf8();

            // Characters the exit card actually emits: box-drawing glyphs and the middle dot separator.
            const string sample = "─│┌┐└┘·";
            var bytes = Console.OutputEncoding.GetBytes(sample);

            // A strict UTF-8 decoder (throwOnInvalidBytes) round-trips only if the bytes are valid UTF-8.
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            Assert.Equal(sample, strictUtf8.GetString(bytes));
        }
        finally
        {
            Console.OutputEncoding = originalEncoding;
            Console.SetOut(originalOut);
        }
    }
}

[CollectionDefinition("ConsoleState", DisableParallelization = true)]
public sealed class ConsoleStateCollection { }
