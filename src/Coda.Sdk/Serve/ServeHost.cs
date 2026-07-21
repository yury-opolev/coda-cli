using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.JsonRpc;
using Coda.Sdk.Serve.Messages;
using LlmClient;

namespace Coda.Sdk.Serve;


/// <summary>
/// Hosts a CodaSession behind a JSON-RPC serve protocol.
/// Reads requests from <paramref name="input"/> and writes responses/notifications to
/// <paramref name="output"/> using the serve protocol defined in ServeMethods.
/// </summary>
public sealed class ServeHost : IAsyncDisposable
{
    private readonly Stream input;
    private readonly Stream output;
    private readonly Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> sessionFactory;
    private readonly string? expectedApiKey;
    private volatile bool authenticated;
    private readonly TaskCompletionSource shutdownTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The shared initialization gate. Created BEFORE the read loop starts so a handler dispatched the
    // instant the loop goes live (initialize/prompt) can await the SAME readiness signal instead of
    // racing a not-yet-created task. RunAsync — never a handler — drives CodaSession.InitializeAsync
    // and completes/faults/cancels this gate, so `initialize` awaiting it can never deadlock on itself.
    private readonly TaskCompletionSource initializationGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Guards driving the shared initialization exactly once. Set to 1 by the first driver — either
    // RunAsync (stdio/no-key: drive immediately) or a valid `initialize`/authenticated `prompt`
    // (API-key mode: drive only after authentication). Interlocked so concurrent authenticated
    // handlers can never double-start the runtime.
    private int initializationDriven;

    // Built in RunAsync before handlers are registered.
    private JsonRpcConnection? connection;
    private CodaSession? session;

    // Turn-running guard: 0 = idle, 1 = running.
    private int turnRunning;
    private CancellationTokenSource? turnCts;

    // Guards the {turnCts publish/clear} vs {interrupt} critical sections so an
    // interrupt that races in before the turn publishes its CTS is never lost.
    private readonly object turnLock = new();
    private bool interruptPending;

    private static readonly HashSet<string> SupportedImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
    };

    public ServeHost(
        Stream input,
        Stream output,
        Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> sessionFactory,
        string? expectedApiKey = null)
    {
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        this.expectedApiKey = expectedApiKey;
        this.authenticated = expectedApiKey is null; // stdio: no auth required.
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Build the connection WITHOUT starting its read loop. Building the session below is
        // slow (tools/telemetry — ~seconds in production), and the orchestrator sends
        // `initialize` the instant it connects. If the read loop were live during that window,
        // the request would land before RegisterHandlers ran and be answered with -32601
        // (Method not found), so initialize would never succeed. Deferring the loop until
        // every handler is registered closes that race.
        this.connection = new JsonRpcConnection(this.input, this.output, startListening: false);

        var perm = new WirePermissionPrompt(this.connection);
        var question = new WireUserQuestionPrompt(this.connection);
        var plan = new WirePlanApprover(this.connection);

        this.session = this.sessionFactory(perm, question, plan);

        // Push LLM stream-progress to the orchestrator as event/streamProgress — the
        // liveness pulse the Bridge watchdog consumes and its status surface shows, so a
        // mid-LLM-call turn reads as "working", not "hung".
        this.session.StreamProgressSink = new WireStreamProgressSink(this.connection);

        // Wire the schedule-lifecycle sink BEFORE initialization so the session-owned schedule
        // runtime (started by InitializeAsync below) publishes typed event/scheduleLifecycle
        // notifications through the live connection. Set before Start; nothing writes until a turn
        // streams or a schedule fires.
        this.session.ScheduleLifecycleSink = new WireScheduleLifecycleSink(this.connection);

        // Observe the shared gate's fault so a failed initialization is never surfaced as an
        // unobserved task exception even if no handler happens to await it.
        ObserveGateFault(this.initializationGate.Task);

        this.RegisterHandlers(cancellationToken);

        // All handlers exist now — safe to start reading inbound requests. The read loop must be
        // live BEFORE InitializeAsync so a schedule that fires during startup can issue a
        // server→client permission/question/plan request (and read its response) without deadlock.
        this.connection.Start();

        // Drive initialization AFTER Start, off the read-loop thread — but ONLY when the peer is
        // already authenticated (stdio / no expectedApiKey). CodaSession.InitializeAsync starts the
        // schedule runtime, which emits lifecycle/stream/tool events and can issue server→client
        // permission/question/plan requests; an unauthenticated peer must never observe those before
        // presenting the API key. In API-key mode the drive is deferred to the `initialize` handler,
        // which triggers it (exactly once, via EnsureInitializationDriven) only after a valid key
        // authenticates. Chosen ordering: the schedule store is project-scoped (it lives under the
        // working directory, not the transcript id) and every scheduled run is isolated (its own
        // history), so the runtime does not depend on the resumed session id/history — driving after
        // auth + resume keeps the reported session id deterministic without constraining the runtime.
        // The `initialize` and `session/prompt` handlers await this same gate before reporting
        // readiness / running a turn.
        if (this.authenticated)
        {
            this.EnsureInitializationDriven(cancellationToken);
        }

        // Wait until shutdown is requested, the CT fires, or the connection closes.
        using var reg = cancellationToken.Register(() => this.shutdownTcs.TrySetResult());

        try
        {
            await this.shutdownTcs.Task.ConfigureAwait(false);
        }
        finally
        {
            // If shutdown arrives before initialization was ever driven (e.g. an API-key peer that
            // never authenticated), complete the shared gate by cancelling it so any handler awaiting
            // it unblocks and teardown never hangs waiting for an initialization that never started.
            // A no-op when the gate already completed/faulted from a real drive.
            this.initializationGate.TrySetCanceled();

            // Cancel-first teardown. Cancel any running turn so its token reaches the
            // in-flight HttpClient.SendAsync and the turn unwinds, THEN await the session's
            // async disposal (no sync-over-async — that previously self-deadlocked/leaked a
            // turn-running session across the host's lifetime). CodaSession.DisposeAsync stops the
            // schedule runtime strictly before the task manager, so no schedule can fire after
            // teardown begins. The connection is disposed LAST so a disposal-time lifecycle event
            // still reaches the orchestrator (never a write after the transport is gone). Bounded by
            // the session's own SyncDisposeBudget (never a shorter, redundant timeout).
            this.CancelTurn();

            if (this.session is not null)
            {
                try
                {
                    await this.session.DisposeAsync()
                        .AsTask()
                        .WaitAsync(CodaSession.SyncDisposeBudget)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort.
                }
            }

            if (this.connection is not null)
            {
                try
                {
                    await this.connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Same cancel-first order as RunAsync's finally: cancel the turn, await the session's async
        // disposal (bounded by the session's own SyncDisposeBudget so the schedule runtime + task
        // manager tear down within a consistent, non-shorter bound), then dispose the connection.
        // Cancel the shared gate first so a never-driven initialization (unauthenticated peer) is
        // completed cleanly and no awaiter hangs.
        this.initializationGate.TrySetCanceled();

        this.CancelTurn();

        if (this.session is not null)
        {
            try
            {
                await this.session.DisposeAsync()
                    .AsTask()
                    .WaitAsync(CodaSession.SyncDisposeBudget)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }
        }

        if (this.connection is not null)
        {
            try
            {
                await this.connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    /// <summary>
    /// Drives the shared initialization exactly once. The first caller — RunAsync (stdio) or a valid
    /// authenticated <c>initialize</c>/<c>prompt</c> handler (API-key mode) — wins the Interlocked
    /// guard and starts <see cref="DriveInitializationAsync"/>; every later caller is a no-op. This
    /// prevents an unauthenticated peer from triggering the runtime and prevents concurrent
    /// authenticated handlers from double-starting it.
    /// </summary>
    private void EnsureInitializationDriven(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref this.initializationDriven, 1, 0) == 0)
        {
            _ = this.DriveInitializationAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Drives the session's initialization (LSP servers + schedule runtime) after the read loop is
    /// live, then completes/faults/cancels the shared <see cref="initializationGate"/> so every
    /// handler awaiting it observes the outcome consistently. A fault is surfaced to those awaiters
    /// (e.g. as an <c>initialize</c> error); the session already disposed any half-started runtime
    /// on a failed start (Task 7), so no runtime is leaked here.
    /// </summary>
    private async Task DriveInitializationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.session!.InitializeAsync(cancellationToken).ConfigureAwait(false);
            this.initializationGate.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            this.initializationGate.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            this.initializationGate.TrySetException(ex);
        }
    }

    /// <summary>
    /// Ensures a faulted initialization gate is always observed, so a start failure that no handler
    /// happens to await never surfaces as an <see cref="TaskScheduler.UnobservedTaskException"/>.
    /// Other awaiters still see the fault (observing here does not consume it).
    /// </summary>
    private static void ObserveGateFault(Task gate) =>
        _ = gate.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void RegisterHandlers(CancellationToken hostCt)
    {
        var conn = this.connection!;
        var sess = this.session!;

        // initialize → validate api key (when required), optionally resume a persisted
        // session, then return protocol version + session id.
        conn.OnRequestAsync(ServeMethods.Initialize, async (p, ct) =>
        {
            var ip = ServeJson.FromNode<InitializeParams>(p);

            if (this.expectedApiKey is not null && !this.authenticated)
            {
                if (!IsKeyValid(ip?.ApiKey, this.expectedApiKey))
                {
                    throw new JsonRpcRequestException(-32001, "unauthorized");
                }

                this.authenticated = true;
            }

            if (ip?.SessionId is { Length: > 0 } resumeId)
            {
                var transcripts = new SessionTranscriptStore(sess.Options.WorkingDirectory);
                var messages = await transcripts.LoadAsync(resumeId, ct).ConfigureAwait(false);
                if (messages is null)
                {
                    throw new JsonRpcRequestException(-32002, "session not found");
                }

                sess.Resume(resumeId, messages);
            }

            // Now that the peer is authenticated (and any resume applied), trigger the shared
            // initialization the host owns. In stdio mode RunAsync already drove it, so this is a
            // no-op; in API-key mode this is the first drive — deferred until exactly here so an
            // unauthenticated peer never starts the schedule runtime. The read loop is already live
            // (connection.Start ran in RunAsync before any handler could dispatch), so a scheduled
            // permission/question/plan request issued during startup can round-trip to this
            // authenticated peer without deadlock.
            this.EnsureInitializationDriven(hostCt);

            // Await the shared initialization (LSP + schedule runtime) the host drives after Start,
            // so the InitializeResult is not returned until the session is actually ready — and a
            // failed init surfaces here as an initialize error. This awaits the SAME gate the host
            // completes from InitializeAsync (never a fresh, self-referential call), so it cannot
            // deadlock on itself. Auth + resume above run first so the session id/history are
            // correct before readiness is reported.
            await this.initializationGate.Task.WaitAsync(ct).ConfigureAwait(false);

            // Report the telemetry log file so the orchestrator can surface/tail it.
            // Null when telemetry is off (no file is opened); omitted from the wire then.
            return ServeJson.ToNode(new InitializeResult(
                ServeMethods.ProtocolVersion,
                sess.SessionId,
                "coda",
                sess.LogFilePath));
        });

        // session/prompt → async (long-running turn).
        conn.OnRequestAsync(ServeMethods.Prompt, async (paramsNode, ct) =>
        {
            this.EnsureAuthenticated();

            // The peer is authenticated, so it is safe to ensure initialization is under way. In
            // stdio mode RunAsync (or a prior initialize) already drove it; in API-key mode a valid
            // initialize authenticated the peer and drove it. Calling here is a race-safe no-op via
            // the once-guard and guarantees a prompt never awaits a gate that was never driven. A
            // prompt that arrives BEFORE authentication is rejected by EnsureAuthenticated above and
            // therefore can never trigger initialization.
            this.EnsureInitializationDriven(hostCt);

            // A turn must never run before initialization: await the shared gate (LSP + schedule
            // runtime up) the host drives after Start. A failed init surfaces its fault here rather
            // than running a turn against a half-initialized session. This enforces the one-turn
            // guard below only once the session is ready.
            await this.initializationGate.Task.WaitAsync(ct).ConfigureAwait(false);

            // Parse and validate BEFORE acquiring the turn guard so a bad image
            // doesn't leave the guard stuck.
            var promptParams = ServeJson.FromNode<PromptParams>(paramsNode);
            var text = promptParams?.Text ?? string.Empty;

            List<ContentBlock>? multimodalContent = null;
            if (promptParams?.Images is { Count: > 0 } images)
            {
                foreach (var img in images)
                {
                    if (!SupportedImageMediaTypes.Contains(img.MediaType))
                    {
                        throw new InvalidOperationException($"unsupported image media type: {img.MediaType}");
                    }

                    if (!TryDecodeBase64(img.Base64, out _))
                    {
                        throw new InvalidOperationException("image base64 is empty or invalid");
                    }

                    var decoded = Convert.FromBase64String(img.Base64);
                    if (decoded.Length > 5 * 1024 * 1024)
                    {
                        throw new InvalidOperationException("image exceeds the 5 MB limit");
                    }
                }

                multimodalContent = new List<ContentBlock>();
                foreach (var img in images)
                {
                    multimodalContent.Add(new ImageBlock(img.MediaType, img.Base64));
                }

                multimodalContent.Add(new TextBlock(text));
            }

            // One turn at a time.
            if (Interlocked.CompareExchange(ref this.turnRunning, 1, 0) != 0)
            {
                throw new InvalidOperationException("busy: a turn is already running");
            }

            // Publish the turn CTS under the lock and honor any interrupt that arrived
            // in the window between winning the guard and publishing the CTS.
            CancellationTokenSource cts;
            lock (this.turnLock)
            {
                cts = CancellationTokenSource.CreateLinkedTokenSource(ct, hostCt);
                this.turnCts = cts;
                if (this.interruptPending)
                {
                    this.interruptPending = false;
                    cts.Cancel();
                }
            }

            // A hung LLM call is bounded inside the LLM client by its HTTP response-headers /
            // stream-idle timeouts (see LlmHttpTimeoutConfig) — surfacing as a normal failed
            // turn with a clear error below. There is intentionally NO turn-level inactivity
            // watchdog here: timeouts live at the layer of the operation they guard, and the
            // orchestrator owns the outer session-level bound.
            var sink = new WireAgentSink(conn);

            try
            {
                // The run honors only the user/host interrupt (and host shutdown).
                var result = multimodalContent is not null
                    ? await sess.RunAsync(multimodalContent, sink, cts.Token).ConfigureAwait(false)
                    : await sess.RunAsync(text, sink, cts.Token).ConfigureAwait(false);

                // CodaSession swallows OCE and returns a failure result; an interrupt is the
                // only cancellation source now.
                var wasInterrupted = cts.IsCancellationRequested;

                if (wasInterrupted)
                {
                    _ = conn.SendNotificationAsync(
                        ServeMethods.EventTurnComplete,
                        ServeJson.ToNode(new TurnCompleteEvent(null, true)),
                        CancellationToken.None);

                    return ServeJson.ToNode(new PromptResult(false, null, true));
                }

                // A failed turn (e.g. a provider error like an HTTP 400 model_not_supported) must
                // surface its reason — both as an event/error and in the result — and to stderr.
                // Otherwise the orchestrator sees a bare ok:false with no idea why.
                if (!result.Success && !string.IsNullOrEmpty(result.Error))
                {
                    _ = conn.SendNotificationAsync(
                        ServeMethods.EventError,
                        ServeJson.ToNode(new Messages.ErrorEvent(result.Error!)),
                        CancellationToken.None);
                    Console.Error.WriteLine($"coda serve: turn failed: {result.Error}");
                }

                // Send turnComplete notification (fire-and-forget from handler).
                _ = conn.SendNotificationAsync(
                    ServeMethods.EventTurnComplete,
                    ServeJson.ToNode(new TurnCompleteEvent(result.StopReason, false)),
                    CancellationToken.None);

                var promptResult = new PromptResult(result.Success, result.StopReason, false)
                {
                    GoalStatus = BuildWireGoalStatus(result.Goal),
                    Error = result.Success ? null : result.Error,
                };
                return ServeJson.ToNode(promptResult);
            }
            catch (OperationCanceledException)
            {
                // The only cancellation source is a user/host interrupt (or host shutdown).
                _ = conn.SendNotificationAsync(
                    ServeMethods.EventTurnComplete,
                    ServeJson.ToNode(new TurnCompleteEvent(null, true)),
                    CancellationToken.None);

                return ServeJson.ToNode(new PromptResult(false, null, true));
            }
            finally
            {
                lock (this.turnLock)
                {
                    Interlocked.Exchange(ref this.turnRunning, 0);
                    var tcs = this.turnCts;
                    this.turnCts = null;
                    this.interruptPending = false;
                    tcs?.Dispose();

                    // Discard any steering comment that raced with turn end so it can never leak into
                    // the next, unrelated turn. Done under the turn lock so it cannot interleave with
                    // an in-flight session/steer (which only enqueues while turnRunning == 1).
                    sess.ClearSteering();
                }
            }
        });

        // session/interrupt → cancel the running turn.
        conn.OnRequest(ServeMethods.Interrupt, _ =>
        {
            this.EnsureAuthenticated();
            this.CancelTurn();
            return ServeJson.ToNode(new InterruptResult(true));
        });

        // session/steer → post a steering comment to the RUNNING turn so the orchestrator can redirect
        // a working session "on the go". Accepted (and enqueued) ONLY while a turn is actually in flight,
        // under the turn lock — so a steer can never leak into a later, unrelated turn (the inbox is also
        // cleared at turn end under the same lock). Returns ok=false when no turn is running, so the
        // orchestrator can fall back to delivering the message as a normal prompt.
        conn.OnRequest(ServeMethods.Steer, p =>
        {
            this.EnsureAuthenticated();
            var sp = ServeJson.FromNode<SteerParams>(p);
            var accepted = false;
            if (!string.IsNullOrWhiteSpace(sp?.Text))
            {
                lock (this.turnLock)
                {
                    if (this.turnRunning == 1)
                    {
                        sess.Steer(sp!.Text);
                        accepted = true;
                    }
                }
            }

            return ServeJson.ToNode(new SteerResult(accepted));
        });

        // session/history → all messages.
        conn.OnRequest(ServeMethods.History, _ =>
        {
            this.EnsureAuthenticated();
            return ServeJson.ToNode(new HistoryResult(this.MapHistory(sess, 0)));
        });

        // session/messages → messages since index.
        conn.OnRequest(ServeMethods.Messages, p =>
        {
            this.EnsureAuthenticated();
            var mp = ServeJson.FromNode<MessagesParams>(p);
            var msgs = this.MapHistory(sess, mp?.SinceIndex ?? 0);
            return ServeJson.ToNode(new MessagesResult(msgs, sess.History.Count));
        });

        // session/models → resolve the provider's model list (live → catalog → built-in).
        conn.OnRequestAsync(ServeMethods.Models, async (p, ct) =>
        {
            this.EnsureAuthenticated();
            var mp = ServeJson.FromNode<ModelsParams>(p);
            var result = await sess.ListModelsAsync(mp?.Refresh ?? false, ct).ConfigureAwait(false);
            var models = result.Models
                .Select(m => new WireModel(m.Id, m.DisplayName, m.ContextLimit))
                .ToList();
            return ServeJson.ToNode(new ModelsResult(result.Source.ToString().ToLowerInvariant(), models));
        });

        // session/setGoal → mutate the session's goal options (persist-until-cleared).
        conn.OnRequest(ServeMethods.SetGoal, p =>
        {
            this.EnsureAuthenticated();
            var sp = ServeJson.FromNode<SetGoalParams>(p);

            // Parse the optional maxDuration; an explicitly-supplied but invalid value is an error.
            TimeSpan? parsedDuration = null;
            if (sp?.MaxDuration is { Length: > 0 } durStr)
            {
                if (!DurationParser.TryParse(durStr, out var dur))
                {
                    throw new JsonRpcRequestException(-32602, $"invalid maxDuration: '{durStr}'. Use a suffix form (e.g. '30m', '2h', '1d') or hh:mm:ss.");
                }

                parsedDuration = dur;
            }

            var goal = string.IsNullOrWhiteSpace(sp?.Goal) ? null : sp!.Goal;
            sess.Options = sess.Options with
            {
                Goal = goal,
                GoalMaxDuration = parsedDuration,
                GoalMaxContinuations = sp?.MaxContinuations,
            };

            var resultDuration = sess.Options.GoalMaxDuration.HasValue
                ? FormatDuration(sess.Options.GoalMaxDuration.Value)
                : null;

            return ServeJson.ToNode(new SetGoalResult(
                Ok: true,
                Goal: sess.Options.Goal,
                MaxDuration: resultDuration,
                MaxContinuations: sess.Options.GoalMaxContinuations));
        });

        // shutdown → signal the run loop to exit.
        conn.OnRequest(ServeMethods.Shutdown, _ =>
        {
            this.EnsureAuthenticated();
            this.shutdownTcs.TrySetResult();
            return ServeJson.ToNode(new InterruptResult(true));
        });
    }

    private IReadOnlyList<WireMessage> MapHistory(CodaSession sess, int sinceIndex)
    {
        var history = sess.History;
        var result = new List<WireMessage>();

        for (var i = sinceIndex; i < history.Count; i++)
        {
            var msg = history[i];
            var role = msg.Role.ToString().ToLowerInvariant();
            var sb = new System.Text.StringBuilder();

            foreach (var block in msg.Content)
            {
                if (block is TextBlock tb)
                {
                    sb.Append(tb.Text);
                }
            }

            result.Add(new WireMessage(role, sb.ToString()));
        }

        return result;
    }

    private static WireGoalStatus? BuildWireGoalStatus(GoalStatus? goal)
    {
        if (goal is null || goal.Outcome == GoalOutcome.None)
        {
            return null;
        }

        return new WireGoalStatus(
            goal.Outcome.ToString(),
            goal.Remaining,
            goal.Continuations,
            goal.Elapsed.TotalSeconds,
            goal.Escalated,
            goal.ExtensionUsed);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        // Emit a human-readable form: prefer suffix shorthand for whole units, else hh:mm:ss.
        if (duration.TotalDays >= 1 && duration == TimeSpan.FromDays(Math.Floor(duration.TotalDays)))
        {
            return $"{(int)duration.TotalDays}d";
        }

        if (duration.TotalHours >= 1 && duration == TimeSpan.FromHours(Math.Floor(duration.TotalHours)))
        {
            return $"{(int)duration.TotalHours}h";
        }

        if (duration.TotalMinutes >= 1 && duration == TimeSpan.FromMinutes(Math.Floor(duration.TotalMinutes)))
        {
            return $"{(int)duration.TotalMinutes}m";
        }

        return duration.ToString(@"hh\:mm\:ss");
    }

    private static bool TryDecodeBase64(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void EnsureAuthenticated()
    {
        if (!this.authenticated)
        {
            throw new JsonRpcRequestException(-32001, "unauthorized");
        }
    }

    private static bool IsKeyValid(string? provided, string expected)
    {
        if (provided is null)
        {
            return false;
        }

        // Hash both sides to a fixed-length digest so the comparison is constant-time
        // regardless of the provided key's length (no length side-channel).
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private void CancelTurn()
    {
        CancellationTokenSource? tcs;
        lock (this.turnLock)
        {
            tcs = this.turnCts;
            if (tcs is null)
            {
                // No live turn CTS yet. The turn may have won the guard but not published its
                // CTS, OR its handler may be dispatched but not yet running (the Task.Run
                // scheduling gap, where turnRunning is still 0). In both cases defer the
                // interrupt so the next turn cancels itself the moment it publishes its CTS —
                // otherwise an interrupt fired immediately after a prompt is lost and the turn
                // hangs. A stray interrupt while truly idle is consumed by the next prompt.
                this.interruptPending = true;
                return;
            }
        }

        // Cancel only — the running turn's finally owns disposal (disposing here could
        // race a turn still using the token, and would double-dispose).
        try
        {
            tcs.Cancel();
        }
        catch
        {
            // Best-effort (already disposed/cancelled).
        }
    }

}
