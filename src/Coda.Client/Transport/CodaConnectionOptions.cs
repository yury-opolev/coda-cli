namespace Coda.Client.Transport;

/// <summary>
/// Bounds for a <see cref="CodaConnection"/>. Every one of these exists to turn a
/// runaway condition into a reported failure instead of unbounded memory growth
/// or a silently dropped frame.
/// </summary>
public sealed class CodaConnectionOptions
{
    /// <summary>
    /// How many notifications and reverse requests may sit undelivered before the
    /// connection reports an overflow. The default is generous for an interactive
    /// turn's event burst yet still finite, so a consumer that stops reading is
    /// caught rather than allowed to exhaust memory.
    /// </summary>
    public int InboundBufferCapacity { get; init; } = 4096;

    /// <summary>
    /// The largest number of requests that may be outstanding at once. Reaching
    /// it makes the next <c>SendRequest</c> fail fast rather than register into an
    /// unbounded map, which is the honest response to a caller that is issuing
    /// requests faster than the engine answers them.
    /// </summary>
    public int MaxPendingRequests { get; init; } = 1024;
}
