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

    public McpRpcConnection(TextWriter writer)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public async Task<JsonElement> SendRequestAsync(string method, JsonNode? parameters = null, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref this.lastId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pending[id] = tcs;

        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        await this.WriteLineAsync(message).ConfigureAwait(false);

        using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    public Task SendNotificationAsync(string method, JsonNode? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return this.WriteLineAsync(message);
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
            this.FaultPending(new McpException("MCP connection closed."));
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
        await this.writer.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
        await this.writer.FlushAsync().ConfigureAwait(false);
    }
}
