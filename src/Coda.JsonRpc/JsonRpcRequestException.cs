namespace Coda.JsonRpc;

/// <summary>
/// Thrown by a request handler to return a specific JSON-RPC error code + message to the
/// caller, instead of the default internal-error (-32603). The connection maps it verbatim.
/// </summary>
public sealed class JsonRpcRequestException : Exception
{
    /// <summary>The JSON-RPC error code returned to the caller verbatim (e.g. -32001).</summary>
    public int Code { get; }

    public JsonRpcRequestException(int code, string message)
        : base(message)
    {
        this.Code = code;
    }
}
