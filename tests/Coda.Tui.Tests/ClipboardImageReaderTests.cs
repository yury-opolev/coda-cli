using Coda.Tui.Clipboard;

namespace Coda.Tui.Tests;

/// <summary>
/// Verifies the per-OS clipboard image readers construct the expected child-process command/arguments
/// (through a fake <see cref="IProcessRunner"/>), parse the returned base64, and degrade to null on
/// invalid base64 or a failed/absent process.
/// </summary>
public sealed class ClipboardImageReaderTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<(string File, string Args)> Calls { get; } = [];

        public Func<string, string, string?>? Handler { get; set; }

        public Task<string?> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            this.Calls.Add((fileName, arguments));
            return Task.FromResult(this.Handler?.Invoke(fileName, arguments));
        }
    }

    private static string ValidPngBase64() =>
        Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    // ── Windows ──────────────────────────────────────────────────────────────

    [Fact]
    public void Windows_reader_invokes_powershell_getimage_and_parses_base64()
    {
        var b64 = ValidPngBase64();
        var runner = new FakeProcessRunner { Handler = (_, _) => b64 };

        var image = new WindowsClipboardImageReader(runner).TryRead();

        Assert.NotNull(image);
        Assert.Equal("image/png", image!.MediaType);
        Assert.Equal(b64, image.Base64Data);
        Assert.Equal(8, image.ByteLength);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("powershell", call.File);
        Assert.Contains("-sta", call.Args);
        Assert.Contains("GetImage()", call.Args);
    }

    [Fact]
    public void Windows_reader_returns_null_on_invalid_base64()
    {
        var runner = new FakeProcessRunner { Handler = (_, _) => "!!!not-base64!!!" };
        Assert.Null(new WindowsClipboardImageReader(runner).TryRead());
    }

    [Fact]
    public void Windows_reader_returns_null_when_process_returns_null()
    {
        var runner = new FakeProcessRunner { Handler = (_, _) => null };
        Assert.Null(new WindowsClipboardImageReader(runner).TryRead());
    }

    // ── macOS ────────────────────────────────────────────────────────────────

    [Fact]
    public void MacOs_reader_invokes_osascript_then_base64_and_parses()
    {
        var b64 = ValidPngBase64();
        var runner = new FakeProcessRunner
        {
            Handler = (file, _) =>
                file == MacOsClipboardImageReader.OsaScriptFileName ? "ok" : b64,
        };

        var image = new MacOsClipboardImageReader(runner).TryRead();

        Assert.NotNull(image);
        Assert.Equal("image/png", image!.MediaType);
        Assert.Equal(b64, image.Base64Data);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal("osascript", runner.Calls[0].File);
        Assert.Contains("PNGf", runner.Calls[0].Args);
        Assert.Equal("base64", runner.Calls[1].File);
    }

    [Fact]
    public void MacOs_reader_returns_null_when_osascript_fails()
    {
        var runner = new FakeProcessRunner { Handler = (_, _) => null };
        Assert.Null(new MacOsClipboardImageReader(runner).TryRead());
    }

    // ── Linux ────────────────────────────────────────────────────────────────

    [Fact]
    public void Linux_reader_tries_wl_paste_first_via_bash()
    {
        var b64 = ValidPngBase64();
        var runner = new FakeProcessRunner { Handler = (_, _) => b64 };

        var image = new LinuxClipboardImageReader(runner).TryRead();

        Assert.NotNull(image);
        Assert.Equal("image/png", image!.MediaType);
        var call = runner.Calls[0];
        Assert.Equal("bash", call.File);
        Assert.Contains("wl-paste", call.Args);
        Assert.Contains("image/png", call.Args);
        Assert.Contains("base64", call.Args);
    }

    [Fact]
    public void Linux_reader_falls_back_to_xclip_when_wl_paste_empty()
    {
        var b64 = ValidPngBase64();
        var runner = new FakeProcessRunner
        {
            Handler = (_, args) => args.Contains("wl-paste") ? null : b64,
        };

        var image = new LinuxClipboardImageReader(runner).TryRead();

        Assert.NotNull(image);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains("wl-paste", runner.Calls[0].Args);
        Assert.Contains("xclip", runner.Calls[1].Args);
    }

    [Fact]
    public void Linux_reader_returns_null_when_no_tool_present()
    {
        var runner = new FakeProcessRunner { Handler = (_, _) => null };
        Assert.Null(new LinuxClipboardImageReader(runner).TryRead());
    }

    // ── Selector ─────────────────────────────────────────────────────────────

    [Fact]
    public void Selector_creates_a_reader_for_the_running_os()
    {
        var runner = new FakeProcessRunner { Handler = (_, _) => null };
        var reader = ClipboardImageReaderSelector.Create(runner);
        Assert.NotNull(reader);
        // Never throws even when nothing is on the clipboard.
        Assert.Null(reader.TryRead());
    }
}
