using Coda.Tui.Ui.Mode;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for <see cref="TerminalUnicodeDetection.Detect"/>, covering the four detection rules:
/// dumb terminal, Windows code page, POSIX locale variables, and the fallback (nothing set → true).
/// </summary>
public sealed class TerminalUnicodeDetectionTests
{
    // ---------------------------------------------------------------------------
    // Rule 1 — dumb terminal
    // ---------------------------------------------------------------------------

    [Fact]
    public void Dumb_terminal_returns_false_regardless_of_platform()
    {
        Assert.False(TerminalUnicodeDetection.Detect(
            isWindows: false, outputCodePage: 65001, term: "dumb",
            lang: "en_US.UTF-8", lcAll: null, lcCtype: null));
    }

    [Fact]
    public void Dumb_terminal_check_is_case_insensitive()
    {
        Assert.False(TerminalUnicodeDetection.Detect(
            isWindows: false, outputCodePage: 65001, term: "DUMB",
            lang: null, lcAll: null, lcCtype: null));
        Assert.False(TerminalUnicodeDetection.Detect(
            isWindows: false, outputCodePage: 65001, term: "Dumb",
            lang: null, lcAll: null, lcCtype: null));
    }

    // ---------------------------------------------------------------------------
    // Rule 2 — Windows code page
    // ---------------------------------------------------------------------------

    [Fact]
    public void Windows_utf8_codepage_65001_returns_true()
    {
        Assert.True(TerminalUnicodeDetection.Detect(
            isWindows: true, outputCodePage: 65001, term: null,
            lang: null, lcAll: null, lcCtype: null));
    }

    [Fact]
    public void Windows_utf16_codepage_1200_returns_true()
    {
        Assert.True(TerminalUnicodeDetection.Detect(
            isWindows: true, outputCodePage: 1200, term: null,
            lang: null, lcAll: null, lcCtype: null));
    }

    [Fact]
    public void Windows_utf16be_codepage_1201_returns_true()
    {
        Assert.True(TerminalUnicodeDetection.Detect(
            isWindows: true, outputCodePage: 1201, term: null,
            lang: null, lcAll: null, lcCtype: null));
    }

    [Fact]
    public void Windows_legacy_codepage_437_returns_false()
    {
        Assert.False(TerminalUnicodeDetection.Detect(
            isWindows: true, outputCodePage: 437, term: null,
            lang: null, lcAll: null, lcCtype: null));
    }

    [Fact]
    public void Windows_legacy_codepage_1252_returns_false()
    {
        Assert.False(TerminalUnicodeDetection.Detect(
            isWindows: true, outputCodePage: 1252, term: null,
            lang: null, lcAll: null, lcCtype: null));
    }

    // ---------------------------------------------------------------------------
    // Rule 3 — POSIX locale variables
    // ---------------------------------------------------------------------------

    [Fact]
    public void Linux_lang_utf8_returns_true()
    {
        Assert.True(TerminalUnicodeDetection.Detect(
            isWindows: false, outputCodePage: 0, term: null,
            lang: "en_US.UTF-8", lcAll: null, lcCtype: null));
    }

    [Fact]
    public void Linux_lang_UTF_hyphen_8_returns_true()
    {
        Assert.True(TerminalUnicodeDetection.Detect(
            isWindows: false, outputCodePage: 0, term: null,
            lang: "de_DE.UTF-8", lcAll: null, lcCtype: null));
    }

    [Fact]
    public void Linux_lang_C_returns_false()
    {
        Assert.False(TerminalUnicodeDetection.Detect(
            isWindows: false, outputCodePage: 0, term: null,
            lang: "C", lcAll: null, lcCtype: null));
    }

    [Fact]
    public void Linux_lcAll_C_overrides_lang_utf8_returning_false()
    {
        // POSIX: LC_ALL takes precedence over LANG.
        Assert.False(TerminalUnicodeDetection.Detect(
            isWindows: false, outputCodePage: 0, term: null,
            lang: "en_US.UTF-8", lcAll: "C", lcCtype: null));
    }

    [Fact]
    public void Linux_lcCtype_utf8_overrides_lang_C()
    {
        // POSIX: LC_CTYPE takes precedence over LANG.
        Assert.True(TerminalUnicodeDetection.Detect(
            isWindows: false, outputCodePage: 0, term: null,
            lang: "C", lcAll: null, lcCtype: "en_US.UTF-8"));
    }

    [Fact]
    public void Linux_lcAll_utf8_overrides_lcCtype_C()
    {
        // POSIX: LC_ALL takes precedence over LC_CTYPE.
        Assert.True(TerminalUnicodeDetection.Detect(
            isWindows: false, outputCodePage: 0, term: null,
            lang: null, lcAll: "en_US.UTF-8", lcCtype: "C"));
    }

    // ---------------------------------------------------------------------------
    // Rule 4 — nothing set → true
    // ---------------------------------------------------------------------------

    [Fact]
    public void Nothing_set_returns_true()
    {
        Assert.True(TerminalUnicodeDetection.Detect(
            isWindows: false, outputCodePage: 0, term: null,
            lang: null, lcAll: null, lcCtype: null));
    }

    [Fact]
    public void All_empty_strings_treated_as_not_set_returns_true()
    {
        Assert.True(TerminalUnicodeDetection.Detect(
            isWindows: false, outputCodePage: 0, term: null,
            lang: string.Empty, lcAll: string.Empty, lcCtype: string.Empty));
    }
}
