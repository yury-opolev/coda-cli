namespace Coda.JsonRpc;

/// <summary>
/// Thrown when the remote side returns a JSON-RPC error response to an outbound request.
/// </summary>
public sealed class JsonRpcResponseException : Exception
{
    /// <summary>The JSON-RPC error code from the response.</summary>
    public int Code { get; }

    public JsonRpcResponseException(int code, string message)
        : base($"JSON-RPC error {code}: {message}")
    {
        this.Code = code;
    }
}
