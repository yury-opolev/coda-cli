namespace Coda.Mcp;

/// <summary>
/// A fixed-size character ring buffer with O(1) appends. Once <see cref="Capacity"/> is reached
/// each further append evicts the oldest retained character, so <see cref="Count"/> never exceeds
/// <see cref="Capacity"/> and the buffer keeps only the most recent tail. Used to bound MCP stderr
/// diagnostics to a constant amount of memory regardless of how much a child process emits.
/// <para>
/// Not thread-safe: callers that share an instance across threads (for example a drain task and a
/// failure-reporting path) must serialize access themselves.
/// </para>
/// </summary>
public sealed class BoundedCharRingBuffer
{
    private readonly char[] buffer;
    private int start;
    private int count;

    /// <summary>Create a ring buffer retaining up to <paramref name="capacity"/> characters.</summary>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="capacity"/> is not positive.</exception>
    public BoundedCharRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.buffer = new char[capacity];
    }

    /// <summary>Maximum number of characters retained.</summary>
    public int Capacity => this.buffer.Length;

    /// <summary>Number of characters currently retained, capped at <see cref="Capacity"/>.</summary>
    public int Count => this.count;

    /// <summary>
    /// Append one character. Below capacity this grows <see cref="Count"/>; at capacity it
    /// overwrites the oldest retained character. Both paths are O(1).
    /// </summary>
    public void Append(char value)
    {
        if (this.count < this.buffer.Length)
        {
            this.buffer[(this.start + this.count) % this.buffer.Length] = value;
            this.count++;
        }
        else
        {
            this.buffer[this.start] = value;
            this.start = (this.start + 1) % this.buffer.Length;
        }
    }

    /// <summary>Discard all retained characters so the buffer can be reused.</summary>
    public void Clear()
    {
        this.start = 0;
        this.count = 0;
    }

    /// <summary>Materialize the retained characters in append order (oldest first).</summary>
    public string ToOrderedString()
    {
        if (this.count == 0)
        {
            return string.Empty;
        }

        return string.Create(this.count, this, static (span, self) =>
        {
            var capacity = self.buffer.Length;
            for (var i = 0; i < self.count; i++)
            {
                span[i] = self.buffer[(self.start + i) % capacity];
            }
        });
    }

    /// <inheritdoc />
    public override string ToString() => this.ToOrderedString();
}
