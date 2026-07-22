using System.Collections.Generic;
using System.Collections.Immutable;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Shells;

/// <summary>A single visible transcript row, carrying enough metadata to draw and hit-test it.</summary>
public readonly record struct TranscriptRow(Guid BlockId, int LocalRow, int GlobalRow, string Text, TranscriptRole Role)
{
    /// <summary>Whether the row paints its background across the full viewport width (user message blocks).</summary>
    public bool FillWidth { get; init; }

    /// <summary>An optional right-aligned annotation (e.g. a sent-time HH:mm) drawn at the row's right edge.</summary>
    public string? RightText { get; init; }

    /// <summary>Whether this is the synthetic, inert blank row after a visible semantic block.</summary>
    public bool IsSeparator { get; init; }
}

/// <summary>
/// A bounded, virtualized layout index over an immutable transcript. It stores the active wrap
/// <see cref="ActiveWidth"/>, an immutable copy of the block list, each block's wrapped row count, and
/// prefix row offsets that support O(log n) binary search from a global row to its block. Fully wrapped
/// lines are kept only in an LRU cache of at most <see cref="MaxCachedBlocks"/> blocks, so a very long
/// conversation stays cheap in memory.
/// </summary>
/// <remarks>
/// <para>
/// Memory/performance tradeoff: retaining the wrapped text of every block would cost O(total characters)
/// for a transcript that can reach tens of thousands of rows. Instead the index keeps only per-block
/// integer row counts (O(n) ints) for the prefix offsets and a small LRU of fully wrapped blocks for the
/// current viewport neighborhood. <see cref="ReplaceAll"/> pays a single O(n) formatting pass to compute
/// row counts (the heavy wrapped lines are discarded, not cached), while <see cref="GetVisibleRows"/>
/// re-formats only the small set of blocks intersecting the viewport plus overscan and caches those. So
/// the formatter callback count during a scroll is bounded by the viewport height, never the transcript
/// length. Streaming uses <see cref="Append"/>/<see cref="ReplaceLast"/>, which touch only the changed
/// tail; a <see cref="Reflow"/> (width change) clears the cache and rebuilds counts exactly once.
/// </para>
/// </remarks>
internal sealed class TranscriptLayoutIndex
{
    /// <summary>Maximum number of fully wrapped blocks retained in the LRU cache.</summary>
    internal const int MaxCachedBlocks = 256;

    private readonly Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>> formatter;
    private readonly Dictionary<Guid, LinkedListNode<CacheEntry>> cacheMap = new();
    private readonly LinkedList<CacheEntry> cacheOrder = new();

    private ImmutableArray<TranscriptBlock> blocks = ImmutableArray<TranscriptBlock>.Empty;
    private readonly List<int> rowCounts = new();
    private readonly List<int> prefix = new() { 0 };
    private int width = 1;

    public TranscriptLayoutIndex(Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>> formatter)
    {
        this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
    }

    /// <summary>Total wrapped rows across every block.</summary>
    public int TotalRows => this.prefix[^1];

    /// <summary>Number of blocks in the transcript.</summary>
    public int BlockCount => this.blocks.Length;

    /// <summary>The active wrap width.</summary>
    public int ActiveWidth => this.width;

    /// <summary>Number of fully wrapped blocks currently held in the LRU cache (for tests).</summary>
    internal int CachedBlockCount => this.cacheMap.Count;

    /// <summary>
    /// Rebuilds the index from <paramref name="newBlocks"/> at <paramref name="newWidth"/>. Formats every
    /// block once to learn its row count but does not retain the wrapped lines, keeping memory bounded.
    /// </summary>
    public void ReplaceAll(ImmutableArray<TranscriptBlock> newBlocks, int width)
    {
        this.blocks = newBlocks.IsDefault ? ImmutableArray<TranscriptBlock>.Empty : newBlocks;
        this.width = width > 0 ? width : 1;
        this.ClearCache();
        this.RebuildRowCounts();
    }

    /// <summary>Appends one block, formatting only the new tail block.</summary>
    public void Append(TranscriptBlock block, int width)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (width > 0 && width != this.width)
        {
            this.Reflow(width);
        }

        var lines = this.Format(block);
        this.blocks = this.blocks.Add(block);
        var count = EffectiveRowCount(lines.Count);
        this.rowCounts.Add(count);
        this.prefix.Add(this.prefix[^1] + count);
        this.Cache(block.Id, lines);
    }

    /// <summary>Replaces the last (streaming) block, reformatting only that tail block.</summary>
    public void ReplaceLast(TranscriptBlock block, int width)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (this.blocks.IsEmpty)
        {
            this.Append(block, width);
            return;
        }

        this.ReplaceAt(this.blocks.Length - 1, block, width);
    }

    /// <summary>
    /// Replaces the block at <paramref name="position"/>, reformatting only that block and shifting the
    /// subsequent prefix offsets by its row-count delta (no other block is re-wrapped).
    /// </summary>
    public void ReplaceAt(int position, TranscriptBlock block, int width)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (position < 0 || position >= this.blocks.Length)
        {
            return;
        }

        if (width > 0 && width != this.width)
        {
            this.Reflow(width);
        }

        this.Evict(this.blocks[position].Id);
        var lines = this.Format(block);
        var count = EffectiveRowCount(lines.Count);
        var delta = count - this.rowCounts[position];
        this.blocks = this.blocks.SetItem(position, block);
        this.rowCounts[position] = count;
        if (delta != 0)
        {
            for (var j = position + 1; j < this.prefix.Count; j++)
            {
                this.prefix[j] += delta;
            }
        }

        this.Cache(block.Id, lines);
    }

    /// <summary>Re-wraps the whole transcript for a new width, clearing the cache and rebuilding counts once.</summary>
    public void Reflow(int width)
    {
        var target = width > 0 ? width : 1;
        if (target == this.width)
        {
            return;
        }

        this.width = target;
        this.ClearCache();
        this.RebuildRowCounts();
    }

    /// <summary>
    /// Returns the rows for an arbitrary half-open global range starting at <paramref name="firstRow"/>
    /// for up to <paramref name="count"/> rows, clamped only to the transcript bounds and never to the
    /// current viewport. Only the blocks intersecting the range are formatted (and cached). This may
    /// materialize a large span because copy operations need the underlying text; normal drawing uses the
    /// viewport-bounded <see cref="GetVisibleRows"/> instead.
    /// </summary>
    public IReadOnlyList<TranscriptRow> GetRows(int firstRow, int count)
    {
        var start = Math.Clamp(firstRow, 0, this.TotalRows);
        var end = Math.Min(this.TotalRows, start + Math.Max(0, count));
        return this.CollectRows(start, end);
    }

    /// <summary>
    /// Returns the rows visible for a viewport starting at <paramref name="firstRow"/> of the given
    /// <paramref name="height"/>, plus <paramref name="overscan"/> rows on each side. Only the blocks that
    /// intersect this window are formatted (and cached); the formatter is never invoked for off-screen
    /// blocks, so the call count is bounded by the viewport, not the transcript length.
    /// </summary>
    public IReadOnlyList<TranscriptRow> GetVisibleRows(int firstRow, int height, int overscan)
    {
        var total = this.TotalRows;
        if (total == 0 || height <= 0)
        {
            return Array.Empty<TranscriptRow>();
        }

        var pad = Math.Max(0, overscan);
        var maxTop = Math.Max(0, total - height);
        var effectiveFirst = Math.Clamp(firstRow, 0, maxTop);
        return this.CollectRows(
            Math.Max(0, effectiveFirst - pad),
            Math.Min(total, effectiveFirst + height + pad));
    }

    private IReadOnlyList<TranscriptRow> CollectRows(int start, int end)
    {
        if (start >= end || this.TotalRows == 0)
        {
            return Array.Empty<TranscriptRow>();
        }

        var rows = new List<TranscriptRow>(end - start);
        var blockIndex = this.FindBlock(start);
        while (blockIndex < this.blocks.Length && this.prefix[blockIndex] < end)
        {
            var blockStart = this.prefix[blockIndex];
            var count = this.rowCounts[blockIndex];
            if (count == 0)
            {
                blockIndex++;
                continue;
            }

            var block = this.blocks[blockIndex];
            var lines = this.GetLines(blockIndex, block);
            for (var local = 0; local < count; local++)
            {
                var global = blockStart + local;
                if (global < start)
                {
                    continue;
                }

                if (global >= end)
                {
                    break;
                }

                if (local == count - 1)
                {
                    rows.Add(new TranscriptRow(block.Id, local, global, string.Empty, default)
                    {
                        IsSeparator = true,
                    });
                    continue;
                }

                var line = lines[local];
                rows.Add(new TranscriptRow(block.Id, local, global, line.Text ?? string.Empty, line.Role)
                {
                    FillWidth = line.FillWidth,
                    RightText = line.RightText,
                });
            }

            blockIndex++;
        }

        return rows;
    }

    /// <summary>Finds the id of the block that owns <paramref name="globalRow"/>, or null if out of range.</summary>
    public Guid? BlockIdAt(int globalRow)
    {
        if (globalRow < 0 || globalRow >= this.TotalRows)
        {
            return null;
        }

        var blockIndex = this.FindBlock(globalRow);
        return blockIndex < this.blocks.Length ? this.blocks[blockIndex].Id : null;
    }

    private void RebuildRowCounts()
    {
        this.rowCounts.Clear();
        this.prefix.Clear();
        this.prefix.Add(0);
        foreach (var block in this.blocks)
        {
            var count = EffectiveRowCount(this.formatter(block, this.width).Count);
            this.rowCounts.Add(count);
            this.prefix.Add(this.prefix[^1] + count);
        }
    }

    /// <summary>Largest block index whose prefix offset is &lt;= <paramref name="globalRow"/>.</summary>
    private int FindBlock(int globalRow)
    {
        var low = 0;
        var high = this.blocks.Length - 1;
        var result = 0;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (this.prefix[mid] <= globalRow)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private IReadOnlyList<TranscriptRenderLine> GetLines(int blockIndex, TranscriptBlock block)
    {
        if (this.cacheMap.TryGetValue(block.Id, out var node))
        {
            this.cacheOrder.Remove(node);
            this.cacheOrder.AddFirst(node);
            return node.Value.Lines;
        }

        var lines = this.Format(block);
        this.Cache(block.Id, lines);
        return lines;
    }

    private IReadOnlyList<TranscriptRenderLine> Format(TranscriptBlock block) => this.formatter(block, this.width);

    private static int EffectiveRowCount(int formattedRowCount) =>
        formattedRowCount > 0 ? formattedRowCount + 1 : 0;

    private void Cache(Guid id, IReadOnlyList<TranscriptRenderLine> lines)
    {
        if (this.cacheMap.TryGetValue(id, out var existing))
        {
            existing.Value = new CacheEntry(id, lines);
            this.cacheOrder.Remove(existing);
            this.cacheOrder.AddFirst(existing);
            return;
        }

        var node = new LinkedListNode<CacheEntry>(new CacheEntry(id, lines));
        this.cacheOrder.AddFirst(node);
        this.cacheMap[id] = node;

        if (this.cacheMap.Count > MaxCachedBlocks)
        {
            var last = this.cacheOrder.Last;
            if (last is not null)
            {
                this.cacheOrder.RemoveLast();
                this.cacheMap.Remove(last.Value.Id);
            }
        }
    }

    private void Evict(Guid id)
    {
        if (this.cacheMap.TryGetValue(id, out var node))
        {
            this.cacheOrder.Remove(node);
            this.cacheMap.Remove(id);
        }
    }

    private void ClearCache()
    {
        this.cacheMap.Clear();
        this.cacheOrder.Clear();
    }

    private sealed class CacheEntry
    {
        public CacheEntry(Guid id, IReadOnlyList<TranscriptRenderLine> lines)
        {
            this.Id = id;
            this.Lines = lines;
        }

        public Guid Id { get; }

        public IReadOnlyList<TranscriptRenderLine> Lines { get; }
    }
}
