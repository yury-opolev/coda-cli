using Coda.Client.Transport;

namespace Coda.Client;

/// <summary>
/// Configuration for a <see cref="CodaClient"/>. The reverse-request handler is
/// <c>required</c> on purpose: the compiler forces every caller to state, up
/// front, how server-initiated permission/question/plan requests are answered.
/// There is no way to construct a client that will silently grant one.
/// </summary>
public sealed class CodaClientOptions
{
    /// <summary>
    /// How server-initiated requests are answered. Use
    /// <see cref="ReverseRequestHandlers.Decline"/> for a non-interactive worker,
    /// or supply your own to route requests to a human or a policy. Mandatory —
    /// see the type summary for why.
    /// </summary>
    public required IReverseRequestHandler ReverseRequestHandler { get; init; }

    /// <summary>
    /// How many events may sit unread on the client's event stream before an
    /// overflow is reported. Bounds memory for a consumer that stops reading
    /// events while turns keep producing them.
    /// </summary>
    public int EventBufferCapacity { get; init; } = 4096;

    /// <summary>The grace period for a bounded shutdown of an owned engine process
    /// before it is force-killed.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Transport-level bounds (pending-request count, inbound buffer).</summary>
    public CodaConnectionOptions ConnectionOptions { get; init; } = new();
}
