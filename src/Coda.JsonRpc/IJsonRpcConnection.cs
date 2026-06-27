using System.Text.Json.Nodes;

namespace Coda.JsonRpc;

/// <summary>
/// Abstraction over a full-duplex JSON-RPC channel. Supports outbound requests and
/// notifications, inbound request dispatch (sync and async handlers), and inbound
/// notification dispatch. Disposed asynchronously.
/// </summary>
public interface IJsonRpcConnection : IAsyncDisposable
{
    /// <summary>
    /// Sends a JSON-RPC request and waits for the corresponding response.
    /// Throws <see cref="Exception"/> if the remote side returns an error response.
    /// </summary>
    Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params, CancellationToken ct);

    /// <summary>Sends a JSON-RPC notification (no response expected).</summary>
    Task SendNotificationAsync(string method, JsonNode? @params, CancellationToken ct);

    /// <summary>Registers a handler invoked for each inbound notification with <paramref name="method"/>.</summary>
    void OnNotification(string method, Action<JsonNode?> handler);

    /// <summary>Registers a synchronous handler for inbound requests with <paramref name="method"/>.</summary>
    void OnRequest(string method, Func<JsonNode?, JsonNode?> handler);

    /// <summary>
    /// Registers an async handler for inbound requests with the given method.
    /// The handler runs in a background task so long-running work does not block
    /// the read loop. When both a sync and an async handler are registered for the
    /// same method, the async handler takes precedence.
    /// </summary>
    void OnRequestAsync(string method, Func<JsonNode?, CancellationToken, Task<JsonNode?>> handler);
}
