using Coda.Common;

namespace Coda.Mcp;

/// <summary>
/// Drains a child MCP server's <c>stderr</c> into a bounded, sanitized tail suitable for inclusion
/// in a startup failure message. The drain reads fixed-size character blocks (never unbounded
/// lines), normalizes LF/CRLF/CR line endings across read boundaries, discards whitespace-only
/// lines, and redacts secrets through <see cref="SecretRedactor"/> before a completed line enters
/// the aggregate tail. Memory is bounded independently of how much the child emits:
/// <list type="bullet">
///   <item>an in-progress line retains at most <see cref="maxLineChars"/> characters;</item>
///   <item>the aggregate retains at most <see cref="McpProcessDiagnostics(int,int)"/>'s
///   <c>maxTailChars</c> characters across completed lines.</item>
/// </list>
/// <para>
/// The drain task owns the in-progress line state exclusively; the aggregate tail is guarded so a
/// failure-reporting path may call <see cref="SnapshotTail"/> concurrently while the drain runs.
/// Cancellation and reader errors propagate to the task returned by <see cref="DrainAsync"/>.
/// </para>
/// </summary>
public sealed class McpProcessDiagnostics
{
    /// <summary>Default maximum characters retained across completed diagnostic lines.</summary>
    public const int DefaultMaxTailChars = 4096;

    private const int ReadBlockSize = 1024;

    /// <summary>
    /// Prefixes that mark the start of a value the redactor would mask (matched case-insensitively).
    /// If truncation discards the start of one of these, the retained tail could otherwise expose a
    /// secret suffix the redactor can no longer recognize.
    /// </summary>
    private static readonly string[] SecretPrefixes = ["sk-", "******"];

    private readonly object gate = new();
    private readonly BoundedCharRingBuffer tail;
    private readonly BoundedCharRingBuffer line;
    private readonly int maxLineChars;
    private readonly PrefixMatcher[] matchers;

    private long lineLength;
    private long earliestSecretStart = -1;
    private bool pendingCarriageReturn;

    /// <summary>
    /// Create diagnostics retaining up to <paramref name="maxTailChars"/> characters across completed
    /// lines and up to <paramref name="maxLineChars"/> characters for any single in-progress line.
    /// </summary>
    public McpProcessDiagnostics(int maxTailChars = DefaultMaxTailChars, int maxLineChars = DefaultMaxTailChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTailChars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineChars);

        this.tail = new BoundedCharRingBuffer(maxTailChars);
        this.line = new BoundedCharRingBuffer(maxLineChars);
        this.maxLineChars = maxLineChars;
        this.matchers = new PrefixMatcher[SecretPrefixes.Length];
        for (var i = 0; i < SecretPrefixes.Length; i++)
        {
            this.matchers[i] = new PrefixMatcher(SecretPrefixes[i]);
        }
    }

    /// <summary>
    /// Drain <paramref name="reader"/> until end-of-stream. Reads fixed-size blocks and processes
    /// them incrementally; the returned task faults if <paramref name="reader"/> throws or the drain
    /// is canceled through <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task DrainAsync(TextReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var block = new char[ReadBlockSize];
        while (true)
        {
            var read = await reader.ReadAsync(block.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                this.Consume(block[i]);
            }
        }

        // Flush any final line that had no terminator before EOF.
        this.EndLine();
    }

    /// <summary>Thread-safe snapshot of the sanitized aggregate tail, oldest character first.</summary>
    public string SnapshotTail()
    {
        lock (this.gate)
        {
            return this.tail.ToOrderedString();
        }
    }

    private void Consume(char c)
    {
        if (c == '\n')
        {
            if (this.pendingCarriageReturn)
            {
                // CRLF: the CR already ended the line; swallow the paired LF.
                this.pendingCarriageReturn = false;
            }
            else
            {
                this.EndLine();
            }

            return;
        }

        if (this.pendingCarriageReturn)
        {
            // The previous CR was a lone carriage-return terminator; the line already ended.
            this.pendingCarriageReturn = false;
        }

        if (c == '\r')
        {
            this.EndLine();
            this.pendingCarriageReturn = true;
            return;
        }

        this.Accumulate(c);
    }

    private void Accumulate(char c)
    {
        var index = this.lineLength;
        foreach (var matcher in this.matchers)
        {
            if (matcher.Feed(c))
            {
                var start = index - (matcher.Length - 1);
                if (this.earliestSecretStart < 0 || start < this.earliestSecretStart)
                {
                    this.earliestSecretStart = start;
                }
            }
        }

        this.line.Append(c);
        this.lineLength++;
    }

    private void EndLine()
    {
        var sanitized = this.MaterializeLine();
        this.ResetLine();

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return;
        }

        this.AppendToTail(sanitized);
    }

    private string MaterializeLine()
    {
        var retained = this.line.ToOrderedString();

        if (this.lineLength > this.maxLineChars)
        {
            var windowStart = this.lineLength - this.maxLineChars;
            if (this.earliestSecretStart >= 0 && this.earliestSecretStart < windowStart)
            {
                // Truncation removed the start of a recognized secret: the retained suffix would slip
                // past the redactor, so mask the whole line instead of exposing it.
                return SecretRedactor.Placeholder;
            }
        }

        return SecretRedactor.Redact(retained);
    }

    private void ResetLine()
    {
        this.line.Clear();
        this.lineLength = 0;
        this.earliestSecretStart = -1;
        foreach (var matcher in this.matchers)
        {
            matcher.Reset();
        }
    }

    private void AppendToTail(string sanitizedLine)
    {
        lock (this.gate)
        {
            if (this.tail.Count > 0)
            {
                this.tail.Append('\n');
            }

            foreach (var c in sanitizedLine)
            {
                this.tail.Append(c);
            }
        }
    }

    /// <summary>Streaming, case-insensitive detector for a single literal prefix.</summary>
    private sealed class PrefixMatcher
    {
        private readonly string prefix;
        private int matched;

        public PrefixMatcher(string prefix) => this.prefix = prefix;

        public int Length => this.prefix.Length;

        public void Reset() => this.matched = 0;

        /// <summary>Feed the next character; returns true when the full prefix has just matched.</summary>
        public bool Feed(char c)
        {
            if (this.matched < this.prefix.Length && Same(this.prefix[this.matched], c))
            {
                this.matched++;
            }
            else
            {
                this.matched = Same(this.prefix[0], c) ? 1 : 0;
            }

            if (this.matched == this.prefix.Length)
            {
                this.matched = 0;
                return true;
            }

            return false;
        }

        private static bool Same(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
    }
}
