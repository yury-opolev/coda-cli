using Coda.Tui.Ui.Tasks;
using Xunit;

namespace Coda.Tui.Tests;

public sealed class TaskTextSanitizerTests
{
    [Fact]
    public void Sanitize_StripsAnsiColorSequences()
    {
        Assert.Equal("RED", TaskTextSanitizer.Sanitize("\x1B[31mRED\x1B[0m"));
    }

    [Fact]
    public void Sanitize_StripsOscHyperlinkSequences()
    {
        // OSC 8 hyperlink: ESC ] 8 ;; URL BEL text ESC ] 8 ;; BEL
        var input = "\x1B]8;;https://example.com\x07link\x1B]8;;\x07";
        Assert.Equal("link", TaskTextSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_StripsOscTitleSequence_TerminatedByStringTerminator()
    {
        // OSC 0 window-title set, terminated by ST (ESC \), a terminal-spoofing vector.
        var input = "\x1B]0;pwned title\x1B\\visible";
        Assert.Equal("visible", TaskTextSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_StripsClearScreenAndCursorMovement()
    {
        // ESC[2J clear screen, ESC[H cursor home, ESC[10;5H absolute move — none may survive.
        var input = "\x1B[2J\x1B[H\x1B[10;5Hbody\x1B[0m";
        var result = TaskTextSanitizer.Sanitize(input);
        Assert.Equal("body", result);
        Assert.DoesNotContain('\u001b', result);
    }

    [Fact]
    public void Sanitize_DropsCarriageReturnsAndControlChars_KeepsTabAndNewline()
    {
        Assert.Equal("a\tb\nc", TaskTextSanitizer.Sanitize("a\tb\r\nc\0\a"));
    }

    [Fact]
    public void Sanitize_NormalizesLoneCarriageReturnProgress_ToNewline()
    {
        // A bare CR (carriage-return progress bar) would rewind the cursor to column 0 and
        // let a task overwrite earlier terminal content. It is normalized to LF, not preserved.
        var result = TaskTextSanitizer.Sanitize("10%\r50%\r100%");
        Assert.Equal("10%\n50%\n100%", result);
        Assert.DoesNotContain('\r', result);
    }

    [Fact]
    public void Sanitize_DropsBackspace()
    {
        // Backspace could visually erase preceding text; it must be removed.
        Assert.Equal("ab", TaskTextSanitizer.Sanitize("a\bb"));
    }

    [Fact]
    public void Sanitize_DropsC1ControlAndDelete()
    {
        // C1 CSI introducer (0x9B) and DEL (0x7F) are control characters and must be dropped;
        // the literal "31m" that followed the C1 introducer stays inert as plain text.
        var result = TaskTextSanitizer.Sanitize("x\u009B31m\u007Fy");
        Assert.Equal("x31my", result);
        Assert.DoesNotContain('\u009B', result);
        Assert.DoesNotContain('\u007F', result);
    }

    [Fact]
    public void Sanitize_MalformedIncompleteEscape_RemovesEscButLeavesInertText()
    {
        // An incomplete CSI (ESC [ 3 1 with no final byte, at end of input) can never act on the
        // terminal once the ESC introducer is gone; the leftover digits are harmless printable text.
        var result = TaskTextSanitizer.Sanitize("before\x1B[31");
        Assert.DoesNotContain('\u001b', result);
        Assert.Equal("before[31", result);
    }

    [Fact]
    public void Sanitize_TrailingLoneEscape_IsDropped()
    {
        var result = TaskTextSanitizer.Sanitize("text\x1B");
        Assert.Equal("text", result);
        Assert.DoesNotContain('\u001b', result);
    }

    [Fact]
    public void Sanitize_PreservesUnicodeBoxDrawingAndEmoji()
    {
        var input = "┌─┐ café 🚀 λ";
        Assert.Equal(input, TaskTextSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TaskTextSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, TaskTextSanitizer.Sanitize(string.Empty));
    }

    [Fact]
    public void SanitizeForMarkup_EscapesSpectreMarkupBrackets()
    {
        // After stripping ANSI, the literal "[bold]" must be escaped so Spectre renders it verbatim.
        Assert.Equal("[[bold]]text", TaskTextSanitizer.SanitizeForMarkup("\x1B[1m[bold]text\x1B[0m"));
    }

    [Fact]
    public void StripAnsiEscapes_RemovesEscapesButKeepsControlCharsAndCarriageReturns()
    {
        // StripAnsiEscapes is the shared primitive used by the plain/Spectre renderers: it removes only
        // escape sequences and leaves surrounding control handling to the caller.
        Assert.Equal("a\tb\r\nc", TaskTextSanitizer.StripAnsiEscapes("a\x1B[31m\tb\r\nc"));
    }
}
