using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Coda.Client.Framing;

namespace Coda.Client.Transport;

/// <summary>
/// A symmetric JSON-RPC 2.0 transport over any pair of byte streams.
///
/// The connection is duplex on purpose: we send requests and notifications to
/// the engine, and the engine sends us <c>event/*</c> notifications and
/// server-initiated <c>request/*</c> calls we must answer — all interleaved.
/// A reader task and a writer task run independently so that a slow consumer of
/// inbound events can never stall an outbound write, and a long-running
/// <c>session/prompt</c> that is still pending never blocks the delivery of the
/// events and reverse requests that report its progress. That non-blocking
/// interleave is the single behaviour that makes this library usable.
///
/// Failure is always made visible. EOF, a write failure, a fatal framing fault
/// or an oversized/overflowing buffer all end the connection and fail every
/// in-flight request with an exception, so a lost engine surfaces as an error
/// rather than a caller waiting forever.
/// </summary>
public sealed class CodaConnection : IAsyncDisposable
{
    private readonly Stream readStream;
    private readonly Stream writeStream;
    private readonly bool ownsStreams;
    private readonly int maxPendingRequests;

    private readonly Channel<byte[]> outbound =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<CodaInbound> inbound;

    private readonly object gate = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonNode?>> pending = new();
    private bool closed;
    private Exception? closeReason;
    private Exception? closeError;

    private readonly CancellationTokenSource lifetime = new();
    private long nextId;
    private Task readerTask = Task.CompletedTask;
    private Task writerTask = Task.CompletedTask;

    private CodaConnection(Stream readStream, Stream writeStream, bool ownsStreams, CodaConnectionOptions options)
    {
        this.readStream = readStream;
        this.writeStream = writeStream;
        this.ownsStreams = ownsStreams;
        this.maxPendingRequests = options.MaxPendingRequests;
        this.inbound = Channel.CreateBounded<CodaInbound>(new BoundedChannelOptions(options.InboundBufferCapacity)
        {
            SingleReader = false,
            SingleWriter = true,
            // The reader loop must never block, so a full buffer is reported as
            // overflow rather than applying backpressure onto the framing loop.
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>
    /// Wires a reader/writer stream pair into a running connection. When
    /// <paramref name="ownsStreams"/> is true the connection disposes both
    /// streams as part of its own disposal; when false the caller keeps that
    /// responsibility, which is the correct choice for streams the caller
    /// created and may reuse.
    /// </summary>
    public static CodaConnection Create(
        Stream readStream,
        Stream writeStream,
        bool ownsStreams = false,
        CodaConnectionOptions? options = null)
    {
        var connection = new CodaConnection(readStream, writeStream, ownsStreams, options ?? new CodaConnectionOptions());
        connection.Start();
        return connection;
    }

    private void Start()
    {
        this.writerTask = Task.Run(WriteLoopAsync);
        this.readerTask = Task.Run(ReadLoopAsync);
    }

    /// <summary>The stream of notifications and server-initiated requests. Replies
    /// to our own requests never appear here; they complete the pending call.</summary>
    public ChannelReader<CodaInbound> Inbound => this.inbound.Reader;

    /// <summary>Completes when both transport loops have finished, i.e. the
    /// connection is fully torn down.</summary>
    public Task Completion => Task.WhenAll(this.readerTask, this.writerTask);

    /// <summary>True once anything has ended the connection: EOF, a failed write,
    /// a framing fault, or an explicit <see cref="Close"/>.</summary>
    public bool IsClosed
    {
        get
        {
            lock (this.gate)
            {
                return this.closed;
            }
        }
    }

    /// <summary>
    /// The fault that ended the connection, when it ended abnormally (a framing
    /// error, an inbound overflow, a write failure). Null for a clean end — EOF or
    /// an explicit close — so a consumer can complete an event stream normally in
    /// that case and surface the fault otherwise.
    /// </summary>
    public Exception? CloseError
    {
        get
        {
            lock (this.gate)
            {
                return this.closeError;
            }
        }
    }

    /// <summary>
    /// Sends a request and awaits its correlated response. The reply may arrive
    /// out of order with respect to other in-flight requests — correlation is by
    /// id, not arrival order — so several requests can be outstanding at once.
    ///
    /// Cancelling <paramref name="cancellationToken"/> abandons the local wait
    /// and stops tracking the id; it does <b>not</b> prove the engine stopped the
    /// corresponding work. A response that arrives after cancellation is simply
    /// discarded as unknown. For a running turn, use an explicit
    /// <c>session/interrupt</c> to affect the engine, not local cancellation.
    /// </summary>
    public async Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params, CancellationToken cancellationToken = default)
    {
        long id = Interlocked.Increment(ref this.nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (this.gate)
        {
            if (this.closed)
            {
                throw ClosedException();
            }

            if (this.pending.Count >= this.maxPendingRequests)
            {
                // Refuse rather than register into a map we are bounding; a
                // caller gets a visible error instead of a request that quietly
                // never leaves.
                throw new CodaClientException(
                    $"too many concurrent requests (limit {this.maxPendingRequests}); refusing \"{method}\"");
            }

            this.pending[id] = completion;
        }

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null)
        {
            request["params"] = @params.DeepClone();
        }

        if (!TryEnqueueOutbound(ContentLengthFraming.Encode(CodaJson.ToUtf8Bytes<JsonNode>(request))))
        {
            // The writer is gone, so this frame — and every other pending one —
            // will never be written. Drop our own waiter first (so it is never
            // faulted-but-unobserved), then close to fail the rest together
            // instead of reporting to this one caller and abandoning them.
            lock (this.gate)
            {
                this.pending.Remove(id);
            }

            Close(this.closeReason);
            throw ClosedException();
        }

        using (cancellationToken.UnsafeRegister(static state => ((CancellationCallback)state!).Cancel(), new CancellationCallback(this, id, completion)))
        {
            return await completion.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Sends a one-way notification. Returns false if the connection is
    /// already closed, so a caller can tell a dropped notification from a sent
    /// one without an exception on a best-effort channel.</summary>
    public bool SendNotification(string method, JsonNode? @params)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (@params is not null)
        {
            notification["params"] = @params.DeepClone();
        }

        return TryEnqueueOutbound(ContentLengthFraming.Encode(CodaJson.ToUtf8Bytes<JsonNode>(notification)));
    }

    /// <summary>
    /// Ends the connection for good: fails every in-flight request at once,
    /// refuses new ones, declines any reverse request still buffered, and lets
    /// the writer drain whatever was already queued (so a reply written a moment
    /// ago still reaches the engine) before it stops. Idempotent.
    /// </summary>
    public void Close(Exception? reason = null)
    {
        TaskCompletionSource<JsonNode?>[] waiters;
        lock (this.gate)
        {
            if (this.closed)
            {
                return;
            }

            this.closed = true;
            this.closeReason = reason;
            this.closeError = reason is null ? null : Wrap(reason);
            waiters = this.pending.Values.ToArray();
            this.pending.Clear();
        }

        Exception failure = reason is null
            ? new CodaConnectionClosedException("the engine connection closed before responding")
            : Wrap(reason);

        foreach (var waiter in waiters)
        {
            waiter.TrySetException(failure);
        }

        // Stop accepting new inbound, then decline anything already buffered so a
        // reverse request the engine is blocked on is never left unanswered.
        this.inbound.Writer.TryComplete();
        DrainAndDeclineInbound();

        // Let the writer flush the declines and any other queued frame, then
        // finish; the loop's own completion cancels the read and disposes.
        this.outbound.Writer.TryComplete();
        this.lifetime.Cancel();
    }

    internal bool TryEnqueueOutbound(byte[] frame) => this.outbound.Writer.TryWrite(frame);

    private async Task ReadLoopAsync()
    {
        var decoder = new FrameDecoder();
        byte[] chunk = new byte[16 * 1024];
        Exception? failure = null;

        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await this.readStream.ReadAsync(chunk, this.lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (read == 0)
                {
                    break; // EOF: a clean end of stream.
                }

                decoder.Feed(chunk.AsSpan(0, read));

                while (decoder.TryReadFrame(out byte[] frame))
                {
                    Dispatch(frame);
                }
            }
        }
        catch (Exception ex)
        {
            // A fatal framing fault or an inbound overflow desynchronises the
            // stream; there is no safe way to keep reading, so the connection
            // ends and every pending caller learns why.
            failure = ex;
        }

        Close(failure);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (byte[] frame in this.outbound.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await this.writeStream.WriteAsync(frame).ConfigureAwait(false);
                await this.writeStream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // The engine's stdin is gone. Nothing further can be written; the
            // frame that failed was already dequeued, so its waiter is only
            // reachable through Close.
            Close(ex);
        }

        Close(this.closeReason);
    }

    private void Dispatch(byte[] frame)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(frame);
        }
        catch (JsonException)
        {
            // A single unparsable body is the peer's problem, not a reason to
            // tear the session down: the framing was intact, so the stream is
            // still aligned. Skip it and keep reading. The raw bytes can carry a
            // prompt or tool result, so nothing about them is logged.
            return;
        }

        if (node is not JsonObject message)
        {
            return;
        }

        bool hasMethod = message.TryGetPropertyValue("method", out JsonNode? methodNode);
        bool hasId = message.TryGetPropertyValue("id", out JsonNode? idNode);

        if (hasMethod && methodNode is not null)
        {
            string method = methodNode.GetValue<string>();
            JsonNode? @params = message.TryGetPropertyValue("params", out JsonNode? p) ? p?.DeepClone() : null;

            if (hasId && idNode is not null)
            {
                // Server-initiated request: it blocks a turn until answered, so
                // hand the caller a responder that declines if abandoned.
                var responder = new ReverseRequestResponder(idNode.DeepClone(), TryEnqueueOutbound);
                Deliver(new CodaInboundRequest(method, @params, responder));
            }
            else
            {
                Deliver(new CodaInboundNotification(method, @params));
            }

            return;
        }

        // Otherwise it is a response to one of our requests.
        if (!hasId || idNode is null || !TryReadId(idNode, out long id))
        {
            return;
        }

        TaskCompletionSource<JsonNode?>? waiter;
        lock (this.gate)
        {
            if (this.pending.Remove(id, out waiter))
            {
                // taken
            }
        }

        if (waiter is null)
        {
            // A response for an id we are no longer tracking — a late reply after
            // local cancellation, or a duplicate. Discarding it is correct.
            return;
        }

        if (message.TryGetPropertyValue("error", out JsonNode? errorNode) && errorNode is JsonObject errorObj)
        {
            waiter.TrySetException(new CodaRpcException(ReadError(errorObj)));
        }
        else
        {
            JsonNode? result = message.TryGetPropertyValue("result", out JsonNode? r) ? r?.DeepClone() : null;
            waiter.TrySetResult(result);
        }
    }

    private void Deliver(CodaInbound item)
    {
        if (this.inbound.Writer.TryWrite(item))
        {
            return;
        }

        // The bounded buffer is full: the consumer is not keeping up. Declining a
        // reverse request we cannot deliver keeps the engine from blocking, and
        // an overflow is surfaced rather than silently dropping events and
        // pretending the stream is still continuous.
        if (item is CodaInboundRequest request)
        {
            request.Responder.Dispose();
        }

        throw new CodaInboundOverflowException(
            "inbound event buffer overflowed; the consumer is not draining events fast enough");
    }

    private void DrainAndDeclineInbound()
    {
        while (this.inbound.Reader.TryRead(out CodaInbound? item))
        {
            if (item is CodaInboundRequest request)
            {
                request.Responder.Dispose();
            }
        }
    }

    private static bool TryReadId(JsonNode idNode, out long id)
    {
        try
        {
            id = idNode.GetValue<long>();
            return true;
        }
        catch (Exception)
        {
            // Our own ids are always numeric; a non-numeric id is not ours.
            id = 0;
            return false;
        }
    }

    private static RpcError ReadError(JsonObject errorObj)
    {
        int code = errorObj.TryGetPropertyValue("code", out JsonNode? c) && c is not null
            ? c.GetValue<int>()
            : RpcErrorCodes.InternalError;
        string msg = errorObj.TryGetPropertyValue("message", out JsonNode? m) && m is not null
            ? m.GetValue<string>()
            : "unspecified engine error";
        JsonNode? data = errorObj.TryGetPropertyValue("data", out JsonNode? d) ? d?.DeepClone() : null;
        return new RpcError(code, msg, data);
    }

    private Exception ClosedException() =>
        this.closeReason is null
            ? new CodaConnectionClosedException("the engine connection is closed")
            : Wrap(this.closeReason);

    private static Exception Wrap(Exception reason) =>
        reason is CodaClientException or FramingException
            ? reason
            : new CodaConnectionClosedException($"the engine connection closed: {reason.Message}", reason);

    /// <summary>
    /// Closes the connection and, when this connection owns them, disposes the
    /// underlying streams. Caller-supplied streams are left untouched.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Close(this.closeReason);

        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch
        {
            // Completion never faults, but guard anyway so disposal is quiet.
        }

        this.lifetime.Dispose();

        if (this.ownsStreams)
        {
            await this.readStream.DisposeAsync().ConfigureAwait(false);
            await this.writeStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Carries the state a cancellation callback needs to drop a single
    /// waiter without racing the response that may be completing it.</summary>
    private sealed class CancellationCallback
    {
        private readonly CodaConnection connection;
        private readonly long id;
        private readonly TaskCompletionSource<JsonNode?> completion;

        public CancellationCallback(CodaConnection connection, long id, TaskCompletionSource<JsonNode?> completion)
        {
            this.connection = connection;
            this.id = id;
            this.completion = completion;
        }

        public void Cancel()
        {
            lock (this.connection.gate)
            {
                this.connection.pending.Remove(this.id);
            }

            this.completion.TrySetCanceled();
        }
    }
}
