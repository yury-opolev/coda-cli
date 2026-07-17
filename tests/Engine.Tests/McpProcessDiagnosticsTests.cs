using System.Text;
using Coda.Common;
using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// <see cref="McpProcessDiagnostics"/> drains a child process's stderr into a bounded, sanitized
/// tail. It reads fixed-size character blocks (not unbounded lines), normalizes LF/CRLF/CR endings
/// across read boundaries, drops whitespace-only lines, redacts secrets through
/// <see cref="SecretRedactor"/> before aggregation, and keeps memory constant regardless of child
/// output volume. Cancellation and reader errors surface on the owning drain task.
/// </summary>
public sealed class McpProcessDiagnosticsTests
{
    [Fact]
    public async Task Lf_lines_are_aggregated_newline_separated()
    {
        var diagnostics = new McpProcessDiagnostics();

        await diagnostics.DrainAsync(new ChunkedTextReader("line1\nline2\n"), default);

        Assert.Equal("line1\nline2", diagnostics.SnapshotTail());
    }

    [Fact]
    public async Task Crlf_lines_are_normalized()
    {
        var diagnostics = new McpProcessDiagnostics();

        await diagnostics.DrainAsync(new ChunkedTextReader("line1\r\nline2\r\n"), default);

        Assert.Equal("line1\nline2", diagnostics.SnapshotTail());
    }

    [Fact]
    public async Task Cr_only_lines_are_normalized()
    {
        var diagnostics = new McpProcessDiagnostics();

        await diagnostics.DrainAsync(new ChunkedTextReader("line1\rline2\r"), default);

        Assert.Equal("line1\nline2", diagnostics.SnapshotTail());
    }

    [Fact]
    public async Task Crlf_split_across_read_boundary_is_a_single_break()
    {
        var diagnostics = new McpProcessDiagnostics();

        // The CR ends one block and the LF starts the next: this must not create a blank line.
        await diagnostics.DrainAsync(new ChunkedTextReader("line1\r", "\nline2"), default);

        Assert.Equal("line1\nline2", diagnostics.SnapshotTail());
    }

    [Fact]
    public async Task Cr_then_text_split_across_boundary_is_two_lines()
    {
        var diagnostics = new McpProcessDiagnostics();

        await diagnostics.DrainAsync(new ChunkedTextReader("line1\r", "line2"), default);

        Assert.Equal("line1\nline2", diagnostics.SnapshotTail());
    }

    [Fact]
    public async Task Trailing_line_without_terminator_is_flushed_at_eof()
    {
        var diagnostics = new McpProcessDiagnostics();

        await diagnostics.DrainAsync(new ChunkedTextReader("no-newline"), default);

        Assert.Equal("no-newline", diagnostics.SnapshotTail());
    }

    [Fact]
    public async Task Whitespace_only_lines_are_ignored()
    {
        var diagnostics = new McpProcessDiagnostics();

        await diagnostics.DrainAsync(new ChunkedTextReader("a\n   \n\t\n\nb\n"), default);

        Assert.Equal("a\nb", diagnostics.SnapshotTail());
    }

    [Fact]
    public async Task Aggregate_tail_is_truncated_to_configured_maximum()
    {
        var diagnostics = new McpProcessDiagnostics(maxTailChars: 10, maxLineChars: 64);

        await diagnostics.DrainAsync(new ChunkedTextReader("aaaa\nbbbb\ncccc\n"), default);

        var tail = diagnostics.SnapshotTail();
        Assert.True(tail.Length <= 10, $"tail was {tail.Length} chars: '{tail}'");
        Assert.EndsWith("cccc", tail);
        Assert.DoesNotContain("aaaa", tail);
    }

    [Fact]
    public async Task Individual_line_larger_than_capacity_keeps_only_its_tail()
    {
        var diagnostics = new McpProcessDiagnostics(maxTailChars: 64, maxLineChars: 8);

        await diagnostics.DrainAsync(new ChunkedTextReader("0123456789ABCDEF\n"), default);

        Assert.Equal("89ABCDEF", diagnostics.SnapshotTail());
    }

    [Fact]
    public async Task Very_long_line_stays_within_fixed_memory()
    {
        var diagnostics = new McpProcessDiagnostics(maxTailChars: 64, maxLineChars: 16);
        var huge = new string('x', 1_000_000) + "\n";

        await diagnostics.DrainAsync(new ChunkedTextReader(huge), default);

        var tail = diagnostics.SnapshotTail();
        Assert.Equal(16, tail.Length);
        Assert.Equal(new string('x', 16), tail);
    }

    [Fact]
    public async Task Ordinary_secret_is_redacted_before_aggregation()
    {
        var diagnostics = new McpProcessDiagnostics();

        await diagnostics.DrainAsync(new ChunkedTextReader("prefix sk-ABCDEFGH12345 suffix\n"), default);

        var tail = diagnostics.SnapshotTail();
        Assert.Contains(SecretRedactor.Placeholder, tail);
        Assert.DoesNotContain("sk-ABCDEFGH12345", tail);
    }

    [Fact]
    public async Task Secret_prefix_partially_removed_by_truncation_yields_placeholder()
    {
        // The last maxLineChars characters exclude "sk"; only "-SECRETKEY..." would remain, which
        // the redactor cannot recognize. The whole retained line must collapse to the placeholder.
        var diagnostics = new McpProcessDiagnostics(maxTailChars: 64, maxLineChars: 10);

        await diagnostics.DrainAsync(new ChunkedTextReader("0123456789012345sk-SECRETKEY\n"), default);

        Assert.Equal(SecretRedactor.Placeholder, diagnostics.SnapshotTail());
        Assert.DoesNotContain("SECRETKEY", diagnostics.SnapshotTail());
    }

    [Fact]
    public async Task Secret_prefix_fully_removed_by_truncation_yields_placeholder()
    {
        var diagnostics = new McpProcessDiagnostics(maxTailChars: 64, maxLineChars: 5);

        await diagnostics.DrainAsync(new ChunkedTextReader("sk-SECRETpadding\n"), default);

        Assert.Equal(SecretRedactor.Placeholder, diagnostics.SnapshotTail());
        Assert.DoesNotContain("dding", diagnostics.SnapshotTail());
    }

    [Fact]
    public async Task Secret_fully_within_retained_tail_is_redacted_normally()
    {
        // sk- stays inside the window, so the redactor handles it without the whole-line placeholder.
        var diagnostics = new McpProcessDiagnostics(maxTailChars: 64, maxLineChars: 32);

        await diagnostics.DrainAsync(new ChunkedTextReader("pad sk-ABCDEFGH12345\n"), default);

        var tail = diagnostics.SnapshotTail();
        Assert.Contains(SecretRedactor.Placeholder, tail);
        Assert.StartsWith("pad ", tail);
    }

    [Fact]
    public async Task Cancellation_propagates_to_the_owning_task()
    {
        var diagnostics = new McpProcessDiagnostics();
        using var cts = new CancellationTokenSource();

        var drain = diagnostics.DrainAsync(new BlockingTextReader(), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
    }

    [Fact]
    public async Task Reader_failure_propagates_to_the_owning_task()
    {
        var diagnostics = new McpProcessDiagnostics();

        var drain = diagnostics.DrainAsync(new FailingTextReader(), default);

        await Assert.ThrowsAsync<IOException>(() => drain);
    }

    [Fact]
    public async Task Concurrent_snapshots_during_drain_are_safe()
    {
        var diagnostics = new McpProcessDiagnostics(maxTailChars: 4096, maxLineChars: 4096);
        var chunks = Enumerable.Range(0, 200).Select(i => $"line{i:D3}\n").ToArray();
        var reader = new ChunkedTextReader(yieldBetweenChunks: true, chunks);

        var drain = diagnostics.DrainAsync(reader, default);
        var reader2 = Task.Run(() =>
        {
            for (var i = 0; i < 5000; i++)
            {
                _ = diagnostics.SnapshotTail();
            }
        });

        await drain;
        await reader2;

        Assert.Contains("line199", diagnostics.SnapshotTail());
    }

    /// <summary>A <see cref="TextReader"/> returning caller-specified chunks, one per read call.</summary>
    private sealed class ChunkedTextReader : TextReader
    {
        private readonly Queue<string> chunks;
        private readonly bool yieldBetweenChunks;
        private string current = string.Empty;
        private int position;

        public ChunkedTextReader(params string[] chunks)
            : this(yieldBetweenChunks: false, chunks)
        {
        }

        public ChunkedTextReader(bool yieldBetweenChunks, params string[] chunks)
        {
            this.chunks = new Queue<string>(chunks);
            this.yieldBetweenChunks = yieldBetweenChunks;
        }

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.position >= this.current.Length)
            {
                if (this.chunks.Count == 0)
                {
                    return 0;
                }

                if (this.yieldBetweenChunks)
                {
                    await Task.Yield();
                }

                this.current = this.chunks.Dequeue();
                this.position = 0;
            }

            var count = Math.Min(buffer.Length, this.current.Length - this.position);
            this.current.AsSpan(this.position, count).CopyTo(buffer.Span);
            this.position += count;
            return count;
        }
    }

    /// <summary>A reader that never completes until its read is canceled.</summary>
    private sealed class BlockingTextReader : TextReader
    {
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    /// <summary>A reader whose read always faults.</summary>
    private sealed class FailingTextReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("reader boom"));
    }
}
