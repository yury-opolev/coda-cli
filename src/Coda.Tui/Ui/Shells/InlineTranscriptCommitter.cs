using System.Collections.Immutable;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// Tracks which transcript blocks have already been committed to the inline shell's native
/// scrollback so each semantic block is appended exactly once. Only <em>completed</em> blocks are
/// eligible: a streaming assistant turn or an in-flight tool call is deliberately withheld until it
/// finishes, because the inline region shows those temporarily and would otherwise commit partial
/// text. The committer is host-neutral — it holds no Terminal.Gui state and can be unit tested on
/// its own.
/// </summary>
public sealed class InlineTranscriptCommitter
{
    private readonly HashSet<Guid> committed = [];
    private readonly List<TranscriptBlock> queue = [];

    /// <summary>
    /// Queues <paramref name="block"/> for a one-time commit when it is a completed semantic block
    /// that has not been queued before. Returns <see langword="false"/> for incomplete
    /// assistant/tool blocks and for any block whose id was already committed, so a block id is
    /// never appended to scrollback twice.
    /// </summary>
    public bool TryQueue(TranscriptBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (!IsCommittable(block))
        {
            return false;
        }

        if (!this.committed.Add(block.Id))
        {
            return false;
        }

        this.queue.Add(block);
        return true;
    }

    /// <summary>
    /// Returns the blocks queued since the last drain as a fresh, immutable list and clears the
    /// pending queue. The set of committed ids is preserved, so draining never re-permits a block.
    /// </summary>
    public ImmutableArray<TranscriptBlock> Drain()
    {
        if (this.queue.Count == 0)
        {
            return [];
        }

        var drained = this.queue.ToImmutableArray();
        this.queue.Clear();
        return drained;
    }

    /// <summary>
    /// Forgets every committed id and clears the pending queue. Called only at a genuine session
    /// boundary (a console/transcript clear that replaces the whole history), after which the same
    /// block ids may legitimately be committed again without being treated as duplicates.
    /// </summary>
    public void Reset()
    {
        this.committed.Clear();
        this.queue.Clear();
    }

    private static bool IsCommittable(TranscriptBlock block) => block switch
    {
        AssistantTranscriptBlock assistant => assistant.Complete,
        ToolTranscriptBlock tool => tool.Complete,
        PermissionTranscriptBlock permission => permission.Allowed is not null,
        UserQuestionTranscriptBlock question => question.Answer is not null,
        _ => true,
    };
}
