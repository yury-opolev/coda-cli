using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Coda.Client.Process;
using Coda.Client.Protocol;
using Coda.Client.Transport;

namespace Coda.Client;

/// <summary>
/// A typed, safe client for the Coda <c>serve</c> JSON-RPC protocol.
///
/// It layers three things over a <see cref="CodaConnection"/>: a typed surface
/// for the routed methods a non-TUI orchestrator needs; an <see
/// cref="IAsyncEnumerable{T}"/> of <see cref="CodaEvent"/> notifications that
/// keeps flowing while a long <c>session/prompt</c> is still outstanding; and a
/// reverse-request pump that routes every server-initiated request to the
/// mandatory handler, declining by default so nothing is ever silently granted.
///
/// Two ownership modes exist. <see cref="Launch"/> owns a child engine process
/// and stops it on disposal; <see cref="Attach"/> drives caller-supplied streams
/// and never disposes them unless explicitly told to. That distinction is
/// deliberate: bounding and killing an owned process is a different act from
/// closing a stream a caller still owns.
/// </summary>
public sealed class CodaClient : IAsyncDisposable
{
    private readonly CodaConnection connection;
    private readonly CodaEngine? engine;
    private readonly bool ownsConnection;
    private readonly IReverseRequestHandler reverseHandler;
    private readonly TimeSpan shutdownGrace;

    private readonly Channel<CodaEvent> events;
    private readonly CancellationTokenSource pumpLifetime = new();
    private readonly Task pumpTask;
    private readonly ConcurrentDictionary<Task, byte> reverseTasks = new();

    private int disposed;

    private CodaClient(CodaConnection connection, CodaEngine? engine, bool ownsConnection, CodaClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.reverseHandler = options.ReverseRequestHandler
            ?? throw new ArgumentException("a reverse-request handler is required", nameof(options));

        this.connection = connection;
        this.engine = engine;
        this.ownsConnection = ownsConnection;
        this.shutdownGrace = options.ShutdownGrace;
        this.events = Channel.CreateBounded<CodaEvent>(new BoundedChannelOptions(options.EventBufferCapacity)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        this.pumpTask = Task.Run(PumpAsync);
    }

    /// <summary>Launches and owns a <c>coda serve</c> child process, driving it over
    /// its stdio. Disposal shuts the process down within the configured grace and
    /// then kills it if it has not exited.</summary>
    public static CodaClient Launch(EngineCommand command, CodaClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        CodaEngine engine = CodaEngine.Start(command);
        try
        {
            // The engine owns its own streams, so the connection must not dispose
            // them; the bounded process shutdown closes stdin instead.
            var connection = CodaConnection.Create(engine.StandardOutput, engine.StandardInput, ownsStreams: false, options.ConnectionOptions);
            return new CodaClient(connection, engine, ownsConnection: true, options);
        }
        catch
        {
            _ = engine.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Drives an engine over caller-supplied streams (a pipe, a socket, an
    /// in-memory pair for tests). When <paramref name="ownsStreams"/> is false —
    /// the default — the streams are the caller's to dispose; the client never
    /// closes them.
    /// </summary>
    public static CodaClient Attach(Stream readStream, Stream writeStream, CodaClientOptions options, bool ownsStreams = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connection = CodaConnection.Create(readStream, writeStream, ownsStreams, options.ConnectionOptions);
        return new CodaClient(connection, engine: null, ownsConnection: true, options);
    }

    /// <summary>The owned engine process, or null when driving caller-supplied
    /// streams. Exposed for stderr diagnostics and exit inspection.</summary>
    public CodaEngine? Engine => this.engine;

    /// <summary>The underlying transport, for raw or advanced use alongside the
    /// typed surface.</summary>
    public CodaConnection Connection => this.connection;

    /// <summary>The result of the most recent successful
    /// <see cref="InitializeAsync(Protocol.InitializeParams, CancellationToken)"/>,
    /// which carries the capability map — the runtime authority for what this
    /// engine supports.</summary>
    public InitializeResult? LastInitializeResult { get; private set; }

    // ── Event stream ────────────────────────────────────────────────────────

    /// <summary>
    /// The engine's <c>event/*</c> notifications, delivered as they arrive —
    /// including while a <c>session/prompt</c> is still pending. Completes when the
    /// connection ends; faults if the stream was cut by a framing error or an
    /// overflow, so a lost or truncated event stream is visible rather than a
    /// silent stall.
    /// </summary>
    public IAsyncEnumerable<CodaEvent> ReadEventsAsync(CancellationToken cancellationToken = default) =>
        this.events.Reader.ReadAllAsync(cancellationToken);

    /// <summary>The event stream as a channel reader, for callers that prefer to
    /// poll or select over it.</summary>
    public ChannelReader<CodaEvent> Events => this.events.Reader;

    // ── Typed operations ────────────────────────────────────────────────────

    /// <summary>Negotiates the connection. Read the returned
    /// <see cref="InitializeResult.Capabilities"/> to decide what is supported;
    /// do not infer support from a product version.</summary>
    public async Task<InitializeResult> InitializeAsync(InitializeParams parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        InitializeResult result = await CallAsync<InitializeResult>(Methods.Initialize, CodaJson.ToNode(parameters), cancellationToken).ConfigureAwait(false);
        this.LastInitializeResult = result;
        return result;
    }

    /// <summary>Negotiates the connection with default parameters (protocol version
    /// only, legacy event behaviour).</summary>
    public Task<InitializeResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        InitializeAsync(new InitializeParams(), cancellationToken);

    /// <summary>
    /// Runs one foreground turn. The returned task completes when the turn ends;
    /// events and reverse requests continue to flow the whole time. Check
    /// <see cref="PromptResult.Ok"/> and <see cref="PromptResult.Error"/> — a
    /// successful round-trip can still report a failed turn.
    /// </summary>
    public Task<PromptResult> PromptAsync(PromptParams parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return CallAsync<PromptResult>(Methods.Prompt, CodaJson.ToNode(parameters), cancellationToken);
    }

    /// <summary>Runs one foreground turn from plain prompt text.</summary>
    public Task<PromptResult> PromptAsync(string text, CancellationToken cancellationToken = default) =>
        PromptAsync(new PromptParams { Text = text }, cancellationToken);

    /// <summary>
    /// Requests cancellation of the active turn. The acknowledgement is <b>not</b>
    /// proof the turn stopped: observe the prompt's own result, or an authoritative
    /// <see cref="GetStateAsync"/>, to confirm the terminal outcome.
    /// </summary>
    public Task<OkResult> InterruptAsync(CancellationToken cancellationToken = default) =>
        CallAsync<OkResult>(Methods.Interrupt, null, cancellationToken);

    /// <summary>
    /// Configures the session goal and its budgets. Each call replaces the whole
    /// configuration: a budget left at its <c>Default</c> restores the engine
    /// default rather than keeping a prior override. Setting a goal does not send a
    /// prompt.
    /// </summary>
    public Task<SetGoalResult> SetGoalAsync(
        string goal,
        ContinuationBudget continuations = default,
        DurationBudget duration = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        var parameters = new SetGoalParams
        {
            Goal = goal,
            MaxContinuations = continuations.ToWire(),
            MaxDuration = duration.ToWire(),
        };
        return CallAsync<SetGoalResult>(Methods.SetGoal, CodaJson.ToNode(parameters), cancellationToken);
    }

    /// <summary>Configures the session goal from a raw parameter object, for callers
    /// that want to speak the wire sentinels directly.</summary>
    public Task<SetGoalResult> SetGoalAsync(SetGoalParams parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return CallAsync<SetGoalResult>(Methods.SetGoal, CodaJson.ToNode(parameters), cancellationToken);
    }

    /// <summary>
    /// Clears the active goal and restores both budgets to their defaults. This is
    /// not an interrupt: a running turn is unaffected.
    /// </summary>
    public Task<SetGoalResult> ClearGoalAsync(CancellationToken cancellationToken = default) =>
        CallAsync<SetGoalResult>(Methods.SetGoal, CodaJson.ToNode(new SetGoalParams()), cancellationToken);

    /// <summary>Reads the authoritative state snapshot. Kept raw plus convenience
    /// accessors so its large, additive shape is never lossily narrowed.</summary>
    public async Task<StateSnapshot> GetStateAsync(CancellationToken cancellationToken = default)
    {
        // `sections` filtering is unsupported and rejected, so no params are sent.
        JsonNode? node = await this.connection.SendRequestAsync(Methods.GetState, null, cancellationToken).ConfigureAwait(false);
        return new StateSnapshot(node);
    }

    /// <summary>Reads a bounded page of replayed events for a matching engine
    /// instance.</summary>
    public Task<GetEventsResult> GetEventsAsync(GetEventsParams parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return CallAsync<GetEventsResult>(Methods.GetEvents, CodaJson.ToNode(parameters), cancellationToken);
    }

    /// <summary>Reads a page of rich committed history, with the instance/epoch/length
    /// fences a paging client carries between reads.</summary>
    public async Task<HistoryResult> GetHistoryAsync(GetHistoryParams parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        JsonNode? node = await this.connection.SendRequestAsync(Methods.GetHistory, CodaJson.ToNode(parameters), cancellationToken).ConfigureAwait(false);
        return new HistoryResult(node);
    }

    /// <summary>Reads the outstanding permission/question/plan requests without
    /// resolving them.</summary>
    public Task<GetPendingRequestsResult> GetPendingRequestsAsync(CancellationToken cancellationToken = default) =>
        CallAsync<GetPendingRequestsResult>(Methods.GetPendingRequests, null, cancellationToken);

    /// <summary>Resolves a pending request out of band, i.e. not by replying to the
    /// original <c>request/*</c> round-trip. A stale or consumed handle is an
    /// error, never a second grant.</summary>
    public Task<ResolveRequestResult> ResolveRequestAsync(ResolveRequestParams parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return CallAsync<ResolveRequestResult>(Methods.ResolveRequest, CodaJson.ToNode(parameters), cancellationToken);
    }

    /// <summary>Resolves a pending permission request by id.</summary>
    public Task<ResolveRequestResult> ResolvePermissionAsync(string requestId, bool allow, CancellationToken cancellationToken = default) =>
        ResolveRequestAsync(new ResolveRequestParams { RequestId = requestId, Outcome = CodaJson.ToNode(new PermissionResponse { Allow = allow }) }, cancellationToken);

    /// <summary>Resolves a pending question request by id.</summary>
    public Task<ResolveRequestResult> ResolveQuestionAsync(string requestId, string answer, CancellationToken cancellationToken = default) =>
        ResolveRequestAsync(new ResolveRequestParams { RequestId = requestId, Outcome = CodaJson.ToNode(new QuestionResponse { Answer = answer }) }, cancellationToken);

    /// <summary>Resolves a pending plan-approval request by id.</summary>
    public Task<ResolveRequestResult> ResolvePlanApprovalAsync(string requestId, bool approve, CancellationToken cancellationToken = default) =>
        ResolveRequestAsync(new ResolveRequestParams { RequestId = requestId, Outcome = CodaJson.ToNode(new PlanApprovalResponse { Approve = approve }) }, cancellationToken);

    /// <summary>Applies a pending request's fail-closed default (deny / no-answer /
    /// reject) and stops waiting on it. Does not shut the connection down.</summary>
    public Task<CancelRequestResult> CancelRequestAsync(string requestId, string? reason = null, CancellationToken cancellationToken = default) =>
        CallAsync<CancelRequestResult>(Methods.CancelRequest, CodaJson.ToNode(new CancelRequestParams { RequestId = requestId, Reason = reason }), cancellationToken);

    /// <summary>Stops the engine. This is not a user/browser detach. An owned engine
    /// may close its stdout immediately afterwards, ending the connection.</summary>
    public Task<OkResult> ShutdownAsync(CancellationToken cancellationToken = default) =>
        CallAsync<OkResult>(Methods.Shutdown, null, cancellationToken);

    // ── Raw escape hatch ────────────────────────────────────────────────────

    /// <summary>
    /// Sends an arbitrary request and returns its raw result node — the escape
    /// hatch that makes a newly-added server method usable before a typed helper
    /// exists. A JSON-RPC error still surfaces as <see cref="CodaRpcException"/>.
    /// </summary>
    public Task<JsonNode?> SendRequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default) =>
        this.connection.SendRequestAsync(method, parameters, cancellationToken);

    /// <summary>Sends an arbitrary one-way notification. Returns false if the
    /// connection is already closed.</summary>
    public bool SendNotification(string method, JsonNode? parameters) =>
        this.connection.SendNotification(method, parameters);

    // ── Internals ───────────────────────────────────────────────────────────

    private async Task<TResult> CallAsync<TResult>(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        JsonNode? node = await this.connection.SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        TResult? result = CodaJson.FromNode<TResult>(node);
        if (result is null)
        {
            throw new CodaClientException($"the engine returned an empty or unparsable result for \"{method}\"");
        }

        return result;
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (CodaInbound item in this.connection.Inbound.ReadAllAsync(this.pumpLifetime.Token).ConfigureAwait(false))
            {
                switch (item)
                {
                    case CodaInboundNotification notification:
                        if (!this.events.Writer.TryWrite(new CodaEvent(notification.Method, notification.Params)))
                        {
                            // The consumer is not draining events. Report the
                            // overflow rather than dropping frames and pretending
                            // the stream stayed continuous, and end the connection
                            // so pending callers fail visibly too.
                            var overflow = new CodaInboundOverflowException(
                                "the event stream overflowed; the consumer is not reading events fast enough");
                            this.events.Writer.TryComplete(overflow);
                            this.connection.Close(overflow);
                            return;
                        }

                        break;

                    case CodaInboundRequest request:
                        DispatchReverseRequest(request);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal cancelled the pump; the event stream completes below.
        }
        catch (Exception ex)
        {
            this.events.Writer.TryComplete(ex);
            return;
        }

        // Inbound completed because the connection closed. Surface an abnormal
        // close (framing fault, overflow) to the event stream; complete it
        // normally for a clean EOF or an explicit close.
        this.events.Writer.TryComplete(this.connection.CloseError);
    }

    private void DispatchReverseRequest(CodaInboundRequest inbound)
    {
        // Handlers run off the dispatch loop, so a slow or reentrant handler
        // (one that calls back into the client, e.g. to interrupt before
        // declining) can never stall the delivery of further events or requests.
        Task task = Task.Run(async () =>
        {
            using var request = new ReverseRequest(inbound);
            try
            {
                await this.reverseHandler.HandleAsync(request, this.pumpLifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A throwing handler must not leave the engine blocked, and must
                // not accidentally grant: decline explicitly with the reason.
                if (!request.IsAnswered)
                {
                    request.Decline($"the reverse-request handler failed: {ex.Message}");
                }
            }

            // Disposing `request` here declines if the handler returned without
            // answering — a forgotten answer can never become a silent grant.
        });

        this.reverseTasks.TryAdd(task, 0);
        _ = task.ContinueWith(t => this.reverseTasks.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// Stops the pump, ends the connection, shuts an owned engine down within the
    /// grace period, and disposes what this client owns. Caller-supplied streams
    /// are not touched unless <see cref="Attach"/> was told it owned them.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        // End the connection first: this completes the inbound stream, declines any
        // buffered reverse request, and fails every pending caller.
        this.connection.Close();
        this.pumpLifetime.Cancel();

        await Quietly(this.pumpTask).ConfigureAwait(false);
        await Quietly(this.connection.Completion).ConfigureAwait(false);
        await Quietly(Task.WhenAll(this.reverseTasks.Keys.ToArray())).ConfigureAwait(false);

        if (this.engine is not null)
        {
            // A bounded process shutdown — distinct from closing a caller's stream.
            await Quietly(this.engine.ShutdownAsync(this.shutdownGrace)).ConfigureAwait(false);
            await this.engine.DisposeAsync().ConfigureAwait(false);
        }

        if (this.ownsConnection)
        {
            await this.connection.DisposeAsync().ConfigureAwait(false);
        }

        this.pumpLifetime.Dispose();
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Disposal is best-effort; a fault from an already-ending task is not
            // something a caller disposing the client can act on.
        }
    }
}
