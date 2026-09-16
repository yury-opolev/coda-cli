using System.Text.Json.Nodes;

namespace Coda.Client.Transport;

/// <summary>
/// The well-known JSON-RPC 2.0 error codes plus the two Coda-specific ones this
/// transport originates itself. The engine's own extended codes
/// (<c>-32001</c> unauthorized, request-registry codes, and so on) are surfaced
/// as-is inside <see cref="CodaRpcException"/> and are not enumerated here,
/// because inventing a friendly name for a code the engine may change is worse
/// than reporting the number the engine actually sent.
/// </summary>
public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>
    /// Sent when this client declines a server-initiated request. It is the
    /// exact code the Rust reference client uses so the engine applies the same
    /// fail-closed default (deny / reject / no-answer) it would for any decline.
    /// </summary>
    public const int RequestCancelled = -32800;
}

/// <summary>
/// A JSON-RPC error object as it appears on the wire. Kept as a plain carrier so
/// the transport can hand the engine's exact <see cref="Code"/>,
/// <see cref="Message"/> and optional <see cref="Data"/> to a caller without
/// reinterpreting them.
/// </summary>
public sealed class RpcError
{
    public RpcError(int code, string message, JsonNode? data = null)
    {
        this.Code = code;
        this.Message = message;
        this.Data = data;
    }

    public int Code { get; }

    public string Message { get; }

    public JsonNode? Data { get; }
}
