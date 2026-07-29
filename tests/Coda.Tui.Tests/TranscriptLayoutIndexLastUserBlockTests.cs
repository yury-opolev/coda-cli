using System.Collections.Immutable;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Unit tests for <see cref="TranscriptLayoutIndex.LastUserBlock"/>.
/// </summary>
public sealed class TranscriptLayoutIndexLastUserBlockTests
{
    private static TranscriptLayoutIndex NewIndex() =>
        new((block, _) =>
        {
            var text = block switch
            {
                UserTranscriptBlock u => u.Text,
                AssistantTranscriptBlock a => a.Text,
                CommandOutputTranscriptBlock c => c.Text,
                _ => string.Empty,
            };
            return text.Split('|')
                .Where(t => t.Length > 0)
                .Select(t => new TranscriptRenderLine(t, TranscriptRole.Assistant))
                .ToArray();
        });

    [Fact]
    public void LastUserBlock_returns_null_for_empty_index()
    {
        var index = NewIndex();
        index.ReplaceAll(ImmutableArray<TranscriptBlock>.Empty, width: 80);

        Assert.Null(index.LastUserBlock());
    }

    [Fact]
    public void LastUserBlock_returns_null_when_no_user_blocks_present()
    {
        var index = NewIndex();
        index.ReplaceAll(
        [
            new AssistantTranscriptBlock(Guid.NewGuid(), "assistant response", Complete: true),
            new CommandOutputTranscriptBlock(Guid.NewGuid(), "some output"),
        ],
        width: 80);

        Assert.Null(index.LastUserBlock());
    }

    [Fact]
    public void LastUserBlock_returns_the_only_user_block_with_correct_row_range()
    {
        var user = new UserTranscriptBlock(Guid.NewGuid(), "hello world");
        var index = NewIndex();
        index.ReplaceAll([user], width: 80);

        var result = index.LastUserBlock();

        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Value.Block.Id);
        Assert.Equal(0, result.Value.FirstRow);
        // "hello world" → 1 rendered content row. The block's trailing separator row is excluded, so the
        // pin is not suppressed while only that blank row is still on screen.
        Assert.Equal(1, result.Value.EndRowExclusive);
    }

    [Fact]
    public void LastUserBlock_excludes_the_trailing_separator_row()
    {
        var user = new UserTranscriptBlock(Guid.NewGuid(), "hello world");
        var index = NewIndex();
        index.ReplaceAll([user], width: 80);

        var result = index.LastUserBlock();

        Assert.NotNull(result);
        // The index reserves one synthetic blank separator row per block; the reported content range
        // must stop before it.
        Assert.Equal(index.TotalRows - 1, result.Value.EndRowExclusive);
    }

    [Fact]
    public void LastUserBlock_returns_the_last_user_block_when_multiple_exist()
    {
        var user1 = new UserTranscriptBlock(Guid.NewGuid(), "first prompt");
        var assistant = new AssistantTranscriptBlock(Guid.NewGuid(), "response", Complete: true);
        var user2 = new UserTranscriptBlock(Guid.NewGuid(), "second prompt");
        var index = NewIndex();
        index.ReplaceAll([user1, assistant, user2], width: 80);

        var result = index.LastUserBlock();

        Assert.NotNull(result);
        Assert.Equal(user2.Id, result.Value.Block.Id);
    }

    [Fact]
    public void LastUserBlock_row_range_is_consistent_with_total_rows()
    {
        var user1 = new UserTranscriptBlock(Guid.NewGuid(), "hi");
        var assistant = new AssistantTranscriptBlock(Guid.NewGuid(), "response", Complete: true);
        var user2 = new UserTranscriptBlock(Guid.NewGuid(), "follow-up");
        var index = NewIndex();
        index.ReplaceAll([user1, assistant, user2], width: 80);

        var result = index.LastUserBlock();

        Assert.NotNull(result);
        // The last user block's content ends one row before TotalRows (its separator row is excluded).
        Assert.Equal(index.TotalRows - 1, result.Value.EndRowExclusive);
    }

    [Fact]
    public void LastUserBlock_FirstRow_matches_prefix_for_block()
    {
        var user1 = new UserTranscriptBlock(Guid.NewGuid(), "line1|line2");  // 3 rows (2 content + 1 sep)
        var user2 = new UserTranscriptBlock(Guid.NewGuid(), "only line");    // 2 rows (1 content + 1 sep)
        var index = NewIndex();
        index.ReplaceAll([user1, user2], width: 80);

        var result = index.LastUserBlock();

        Assert.NotNull(result);
        Assert.Equal(user2.Id, result.Value.Block.Id);
        // user1 takes 3 rows (rows 0,1,2), so user2 starts at row 3.
        Assert.Equal(3, result.Value.FirstRow);
        Assert.Equal(index.TotalRows - 1, result.Value.EndRowExclusive);
    }

    [Fact]
    public void LastUserBlock_not_affected_by_trailing_non_user_blocks()
    {
        var user = new UserTranscriptBlock(Guid.NewGuid(), "prompt");
        var assistant = new AssistantTranscriptBlock(Guid.NewGuid(), "ongoing", Complete: false);
        var index = NewIndex();
        index.ReplaceAll([user, assistant], width: 80);

        var result = index.LastUserBlock();

        Assert.NotNull(result);
        Assert.Equal(user.Id, result.Value.Block.Id);
        // The block end row must be less than TotalRows since assistant follows.
        Assert.True(result.Value.EndRowExclusive < index.TotalRows);
    }
}
