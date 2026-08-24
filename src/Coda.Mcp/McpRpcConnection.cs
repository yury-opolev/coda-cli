using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coda.Mcp;

/// <summary>
/// A newline-delimited JSON-RPC 2.0 connection (the MCP stdio transport). Sending
/// is via the supplied writer; incoming lines are fed in by <see cref="DispatchLine"/>
/// (driven by <see cref="RunReadLoopAsync"/> over the process stdout, or directly
/// in tests). Requests are correlated to responses by id.
/// </summary>
public sealed class McpRpcConnection
{
    private readonly TextWriter writer;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private long lastId;

    /// <summary>
    /// Set once the transport is gone (the read loop ended, or the owner tore the connection down).
    /// Terminal: a closed connection is never reopened, its client is replaced instead. Without it a
    /// request written into a killed child's pipe is simply never answered, and the caller waits out
    /// the whole <see cref="McpTool.DefaultTimeout"/> — which is what made a restarted server look
    /// permanently broken.
    /// </summary>
    private McpException? closedReason;

    public McpRpcConnection(TextWriter writer)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    /// <summary>True once <see cref="Close"/> has run or the read loop has ended.</summary>
    public bool IsClosed => Volatile.Read(ref this.closedReason) is not null;

    /// <summary>
    /// Put the connection into its terminal state: fail every in-flight request and reject every
    /// later one with <paramref name="reason"/>. Idempotent — the first reason wins, so a teardown
    /// racing the read loop's own EOF does not change the message a caller already saw.
    /// </summary>
    public void Close(McpException reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var effective = Interlocked.CompareExchange(ref this.closedReason, reason, null) ?? reason;
        this.FaultPending(effective);
    }

    public async Task<JsonElement> SendRequestAsync(string method, JsonNode? parameters = null, CancellationToken cancellationToken = default)
    {
        this.ThrowIfClosed();

        var id = Interlocked.Increment(ref this.lastId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pending[id] = tcs;

        // Re-checked after registering: Close faults what it can see, so a request that slipped in
        // behind it has to fail itself or it would wait for a response that can never arrive.
        if (this.IsClosed)
        {
            this.pending.TryRemove(id, out _);
            this.ThrowIfClosed();
        }

        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        try
        {
            await this.WriteLineAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // Nothing was sent, so nothing can answer: drop the registration instead of leaving an
            // entry that only a later Close would ever complete.
            this.pending.TryRemove(id, out _);
            throw;
        }

        using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    public Task SendNotificationAsync(string method, JsonNode? parameters = null)
    {
        this.ThrowIfClosed();

        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return this.WriteLineAsync(message);
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref this.closedReason) is { } reason)
        {
            throw reason;
        }
    }

    /// <summary>Process one incoming JSON-RPC line; completes the matching pending request.</summary>
    public void DispatchLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        // Server-initiated requests/notifications (no numeric id we issued) are ignored.
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        var id = idElement.GetInt64();
        if (!this.pending.TryRemove(id, out var tcs))
        {
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            tcs.TrySetException(new McpException(message ?? "MCP server returned an error."));
        }
        else if (root.TryGetProperty("result", out var result))
        {
            tcs.TrySetResult(result.Clone());
        }
        else
        {
            tcs.TrySetResult(default);
        }
    }

    public async Task RunReadLoopAsync(TextReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                this.DispatchLine(line);
            }
        }
        finally
        {
            // EOF (or a cancelled loop) means nothing will ever answer again: close for good so a
            // later call fails immediately instead of writing into a pipe with no reader.
            this.Close(new McpException("MCP connection closed."));
        }
    }

    private void FaultPending(Exception exception)
    {
        foreach (var (id, tcs) in this.pending)
        {
            tcs.TrySetException(exception);
            this.pending.TryRemove(id, out _);
        }
    }

    private async Task WriteLineAsync(JsonNode message)
    {
        try
        {
            await this.writer.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
            await this.writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe went away mid-write (the child died, or teardown closed stdin): surface it as
            // a transport loss so a tool call fails cleanly instead of a raw I/O exception unwinding
            // the turn.
            throw new McpException("MCP connection closed.", ex);
        }
    }
}
