using Coda.Tui.Ui.Rendering;
using Xunit;

namespace Coda.Tui.Tests;

public sealed class TerminalTextSanitizerTests
{
    [Fact]
    public void Sanitize_StripsAnsiColorSequences()
    {
        Assert.Equal("RED", TerminalTextSanitizer.Sanitize("\x1B[31mRED\x1B[0m"));
    }

    [Fact]
    public void Sanitize_StripsOscHyperlinkSequences()
    {
        // OSC 8 hyperlink: ESC ] 8 ;; URL BEL text ESC ] 8 ;; BEL
        var input = "\x1B]8;;https://example.com\x07link\x1B]8;;\x07";
        Assert.Equal("link", TerminalTextSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_StripsOscTitleSequence_TerminatedByStringTerminator()
    {
        // OSC 0 window-title set, terminated by ST (ESC \), a terminal-spoofing vector.
        var input = "\x1B]0;pwned title\x1B\\visible";
        Assert.Equal("visible", TerminalTextSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_StripsClearScreenAndCursorMovement()
    {
        // ESC[2J clear screen, ESC[H cursor home, ESC[10;5H absolute move — none may survive.
        var input = "\x1B[2J\x1B[H\x1B[10;5Hbody\x1B[0m";
        var result = TerminalTextSanitizer.Sanitize(input);
        Assert.Equal("body", result);
        Assert.DoesNotContain('\u001b', result);
    }

    [Fact]
    public void Sanitize_DropsCarriageReturnsAndControlChars_KeepsTabAndNewline()
    {
        Assert.Equal("a\tb\nc", TerminalTextSanitizer.Sanitize("a\tb\r\nc\0\a"));
    }

    [Fact]
    public void Sanitize_NormalizesLoneCarriageReturnProgress_ToNewline()
    {
        // A bare CR (carriage-return progress bar) would rewind the cursor to column 0 and
        // let a task overwrite earlier terminal content. It is normalized to LF, not preserved.
        var result = TerminalTextSanitizer.Sanitize("10%\r50%\r100%");
        Assert.Equal("10%\n50%\n100%", result);
        Assert.DoesNotContain('\r', result);
    }

    [Fact]
    public void Sanitize_DropsBackspace()
    {
        // Backspace could visually erase preceding text; it must be removed.
        Assert.Equal("ab", TerminalTextSanitizer.Sanitize("a\bb"));
    }

    [Fact]
    public void Sanitize_DropsC1ControlAndDelete()
    {
        // C1 CSI introducer (0x9B) and DEL (0x7F) are control characters and must be dropped;
        // the literal "31m" that followed the C1 introducer stays inert as plain text.
        var result = TerminalTextSanitizer.Sanitize("x\u009B31m\u007Fy");
        Assert.Equal("x31my", result);
        Assert.DoesNotContain('\u009B', result);
        Assert.DoesNotContain('\u007F', result);
    }

    [Fact]
    public void Sanitize_MalformedIncompleteEscape_RemovesEscButLeavesInertText()
    {
        // An incomplete CSI (ESC [ 3 1 with no final byte, at end of input) can never act on the
        // terminal once the ESC introducer is gone; the leftover digits are harmless printable text.
        var result = TerminalTextSanitizer.Sanitize("before\x1B[31");
        Assert.DoesNotContain('\u001b', result);
        Assert.Equal("before[31", result);
    }

    [Fact]
    public void Sanitize_TrailingLoneEscape_IsDropped()
    {
        var result = TerminalTextSanitizer.Sanitize("text\x1B");
        Assert.Equal("text", result);
        Assert.DoesNotContain('\u001b', result);
    }

    [Fact]
    public void Sanitize_PreservesUnicodeBoxDrawingAndEmoji()
    {
        var input = "┌─┐ café 🚀 λ";
        Assert.Equal(input, TerminalTextSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TerminalTextSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, TerminalTextSanitizer.Sanitize(string.Empty));
    }

    [Fact]
    public void SanitizeForMarkup_EscapesSpectreMarkupBrackets()
    {
        // After stripping ANSI, the literal "[bold]" must be escaped so Spectre renders it verbatim.
        Assert.Equal("[[bold]]text", TerminalTextSanitizer.SanitizeForMarkup("\x1B[1m[bold]text\x1B[0m"));
    }

    [Fact]
    public void StripAnsiEscapes_RemovesEscapesButKeepsControlCharsAndCarriageReturns()
    {
        // StripAnsiEscapes is the shared primitive used by the plain/Spectre renderers: it removes only
        // escape sequences and leaves surrounding control handling to the caller.
        Assert.Equal("a\tb\r\nc", TerminalTextSanitizer.StripAnsiEscapes("a\x1B[31m\tb\r\nc"));
    }

    [Fact]
    public void StripAnsiEscapes_DoubleEscapedClearScreen_DoesNotReformALiveSequence()
    {
        // ESC ESC [2J [2J: the regex only matches the ESC[2J starting at the SECOND ESC, so a naive
        // single pass leaves the first ESC glued to the trailing literal "[2J" — reforming a live
        // clear-screen (ESC [ 2 J). No ESC and no live CSI may survive.
        var result = TerminalTextSanitizer.StripAnsiEscapes("\u001B\u001B[2J[2J");

        Assert.DoesNotContain('\u001B', result);
        Assert.DoesNotContain("\u001B[", result, StringComparison.Ordinal);
        Assert.Equal("[2J", result);
    }

    [Fact]
    public void StripAnsiEscapes_DoubleEscapedTwoCharThenCsi_DoesNotReformALiveSequence()
    {
        // ESC ESC A [2J: the inner "ESC A" two-char escape is removed, again leaving the leading ESC
        // adjacent to a literal "[2J" that would reform ESC [ 2 J.
        var result = TerminalTextSanitizer.StripAnsiEscapes("\u001B\u001BA[2J");

        Assert.DoesNotContain('\u001B', result);
        Assert.DoesNotContain("\u001B[", result, StringComparison.Ordinal);
        Assert.Equal("[2J", result);
    }

    [Fact]
    public void StripAnsiEscapes_MalformedTrailingEscape_LeavesNoEscape()
    {
        // An incomplete CSI at end of input (ESC [ 3 1 with no final byte) must not leave a bare ESC
        // that could later splice onto appended text.
        var result = TerminalTextSanitizer.StripAnsiEscapes("before\u001B[31");

        Assert.DoesNotContain('\u001B', result);
        Assert.Equal("before[31", result);
    }

    [Fact]
    public void StripAnsiEscapes_PreservesCrLfAndTab()
    {
        // Generic renderers rely on StripAnsiEscapes keeping CR/LF/tab untouched (Sanitize normalizes
        // them later); only escapes and residual ESCs are removed.
        Assert.Equal("a\r\nb\tc", TerminalTextSanitizer.StripAnsiEscapes("a\r\n\u001Bb\tc"));
    }

    [Fact]
    public void StripAnsiEscapes_IsLinearOnAdversarialInput()
    {
        // Guard against catastrophic backtracking (ReDoS): a long run of ESC introducers plus CSI
        // parameter bytes must complete near-instantly.
        var adversarial = new string('\u001B', 50_000) + new string('[', 50_000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = TerminalTextSanitizer.StripAnsiEscapes(adversarial);
        sw.Stop();

        Assert.DoesNotContain('\u001B', result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Sanitize_DoubleEscapedClearScreen_LeavesNoEscapeOrLiveSequence()
    {
        var result = TerminalTextSanitizer.Sanitize("\u001B\u001B[2J[2J");

        Assert.DoesNotContain('\u001B', result);
        Assert.DoesNotContain("\u001B[", result, StringComparison.Ordinal);
        Assert.Equal("[2J", result);
    }

    [Fact]
    public void Sanitize_RemovesBidiOverrideAndIsolateControls()
    {
        // Bidi override/isolate controls can visually reorder text to spoof output (e.g. make a
        // dangerous command render as a harmless one). They must be dropped while the visible text
        // stays intact.
        var input = "rm\u202E gpj.exe\u202C safe\u2066x\u2069y\u200Ez\u200Fw";
        var result = TerminalTextSanitizer.Sanitize(input);

        foreach (var c in new[] { '\u202A', '\u202B', '\u202C', '\u202D', '\u202E', '\u2066', '\u2067', '\u2068', '\u2069', '\u200E', '\u200F' })
        {
            Assert.DoesNotContain(c, result);
        }

        Assert.Equal("rm gpj.exe safexyzw", result);
    }

    [Fact]
    public void Sanitize_PreservesEmojiZeroWidthJoinerSequences()
    {
        // The ZWJ (U+200D) binds emoji into a single glyph (family, profession, flags). It is NOT a
        // bidi control and must survive so emoji ZWJ sequences keep rendering.
        var family = "\U0001F468\u200D\U0001F469\u200D\U0001F467"; // 👨‍👩‍👧
        var result = TerminalTextSanitizer.Sanitize(family);

        Assert.Equal(family, result);
        Assert.Contains('\u200D', result);
    }
}
