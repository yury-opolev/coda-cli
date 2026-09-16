using System.Text.Json.Nodes;

namespace Coda.Client.Transport;

/// <summary>
/// Something the engine sent to us that is not a reply to one of our requests:
/// either a one-way <c>event/*</c> notification or a server-initiated
/// <c>request/*</c> that we must answer. Replies to our own requests never
/// appear here — they complete the pending call directly.
/// </summary>
public abstract class CodaInbound
{
    private protected CodaInbound(string method, JsonNode? @params)
    {
        this.Method = method;
        this.Params = @params;
    }

    /// <summary>The JSON-RPC method, e.g. <c>event/assistantText</c> or
    /// <c>request/permission</c>. Preserved verbatim even when this library has
    /// no typed model for it, so an unknown-but-newer method still reaches the
    /// caller.</summary>
    public string Method { get; }

    /// <summary>The raw params payload, kept as a <see cref="JsonNode"/> so no
    /// field is lost to an older DTO that has never heard of it.</summary>
    public JsonNode? Params { get; }
}

/// <summary>A one-way notification. The engine expects no reply.</summary>
public sealed class CodaInboundNotification : CodaInbound
{
    public CodaInboundNotification(string method, JsonNode? @params)
        : base(method, @params)
    {
    }
}

/// <summary>
/// A server-initiated request. The engine's turn blocks until
/// <see cref="Responder"/> answers, so the consumer must either answer it or
/// dispose it — disposing declines, which is safe.
/// </summary>
public sealed class CodaInboundRequest : CodaInbound
{
    public CodaInboundRequest(string method, JsonNode? @params, ReverseRequestResponder responder)
        : base(method, @params)
    {
        this.Responder = responder;
    }

    public ReverseRequestResponder Responder { get; }
}
