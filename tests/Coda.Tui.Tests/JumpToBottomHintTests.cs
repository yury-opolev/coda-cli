using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class JumpToBottomHintTests
{
    [Theory]
    [InlineData(0, "Jump to bottom (Ctrl+End) ↓")]
    [InlineData(1, "1 new message (Ctrl+End) ↓")]
    [InlineData(2, "2 new messages (Ctrl+End) ↓")]
    public void Hint_text_uses_block_count_and_pluralization(int unseenBlocks, string expected) =>
        Assert.Equal(expected, JumpToBottomHint.HintText(unseenBlocks));
}
