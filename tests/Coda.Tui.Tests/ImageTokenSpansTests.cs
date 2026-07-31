using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

/// <summary>
/// An <c>[Image N]</c> token is a single thing, not eight characters that happen to sit together: it is
/// the draft's reference to a staged image, and <c>AgentRunner</c> attaches the image only while the whole
/// token survives. Deleting into it one character at a time would leave visible wreckage in the prompt and
/// silently drop the attachment, so a delete that would break a token removes all of it instead.
/// </summary>
public sealed class ImageTokenSpansTests
{
    private static (int Start, int End)? Span(string text, int caret, bool forward) =>
        ImageTokenSpans.DeleteSpanAt(text, caret, forward);

    // -----------------------------------------------------------------------
    // Backspace — the character behind the caret
    // -----------------------------------------------------------------------

    [Fact]
    public void Backspace_immediately_after_a_token_removes_the_whole_token()
    {
        var text = "look [Image 1]";

        Assert.Equal((5, 14), Span(text, text.Length, forward: false));
    }

    [Fact]
    public void Backspace_inside_a_token_removes_the_whole_token()
    {
        var text = "look [Image 1] here";

        // Caret between "Imag" and "e" — an ordinary backspace would leave "[Imae 1]".
        Assert.Equal((5, 14), Span(text, 10, forward: false));
    }

    [Fact]
    public void Backspace_immediately_before_a_token_is_an_ordinary_delete()
    {
        var text = "look [Image 1]";

        // The character behind the caret is the space, which belongs to no token.
        Assert.Null(Span(text, 5, forward: false));
    }

    // -----------------------------------------------------------------------
    // Delete — the character ahead of the caret
    // -----------------------------------------------------------------------

    [Fact]
    public void Delete_immediately_before_a_token_removes_the_whole_token()
    {
        var text = "look [Image 1] here";

        Assert.Equal((5, 14), Span(text, 5, forward: true));
    }

    [Fact]
    public void Delete_inside_a_token_removes_the_whole_token()
    {
        var text = "[Image 12] tail";

        Assert.Equal((0, 10), Span(text, 4, forward: true));
    }

    [Fact]
    public void Delete_immediately_after_a_token_is_an_ordinary_delete()
    {
        var text = "[Image 1] tail";

        Assert.Null(Span(text, 9, forward: true));
    }

    // -----------------------------------------------------------------------
    // Ordinary text is never affected
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("plain text", 5, false)]
    [InlineData("plain text", 5, true)]
    [InlineData("", 0, false)]
    [InlineData("", 0, true)]
    public void Text_with_no_token_never_reports_a_span(string text, int caret, bool forward) =>
        Assert.Null(Span(text, caret, forward));

    [Fact]
    public void A_caret_at_the_start_has_nothing_behind_it()
    {
        Assert.Null(Span("[Image 1]", 0, forward: false));
    }

    [Fact]
    public void A_caret_at_the_end_has_nothing_ahead_of_it()
    {
        var text = "[Image 1]";

        Assert.Null(Span(text, text.Length, forward: true));
    }

    [Theory]
    [InlineData("[Image]")]
    [InlineData("[Image ]")]
    [InlineData("[image 1]")]
    [InlineData("[Image one]")]
    [InlineData("Image 1")]
    public void Text_that_only_resembles_a_token_is_left_alone(string text) =>
        Assert.Null(Span(text, text.Length, forward: false));

    // -----------------------------------------------------------------------
    // Several tokens
    // -----------------------------------------------------------------------

    [Fact]
    public void The_token_under_the_caret_is_the_one_removed()
    {
        var text = "[Image 1] and [Image 2]";

        Assert.Equal((14, 23), Span(text, text.Length, forward: false));
        Assert.Equal((0, 9), Span(text, 3, forward: false));
    }

    [Fact]
    public void Adjacent_tokens_do_not_bleed_into_each_other()
    {
        var text = "[Image 1][Image 2]";

        // The caret sits exactly between them: backspace takes the first, delete takes the second.
        Assert.Equal((0, 9), Span(text, 9, forward: false));
        Assert.Equal((9, 18), Span(text, 9, forward: true));
    }

    [Fact]
    public void A_caret_out_of_range_is_tolerated()
    {
        Assert.Null(Span("[Image 1]", 999, forward: true));
        Assert.Null(Span("[Image 1]", -5, forward: false));
    }

    [Fact]
    public void Multi_digit_labels_are_matched_whole()
    {
        var text = "[Image 137]";

        Assert.Equal((0, 11), Span(text, text.Length, forward: false));
    }
}
