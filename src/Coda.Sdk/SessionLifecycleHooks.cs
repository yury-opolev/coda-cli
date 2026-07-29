using Coda.Agent;
using Coda.Agent.Hooks;
using LlmClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Sdk;

/// <summary>
/// Session-scoped envelope values written into the <c>SessionStart</c> hook payload.
/// </summary>
/// <param name="Model">The model identifier for the session.</param>
/// <param name="PermissionMode">The permission mode string (e.g. <c>"default"</c>).</param>
/// <param name="TranscriptPath">Path the session transcript will be written to.</param>
public readonly record struct SessionStartPayloadContext(
    string Model,
    string PermissionMode,
    string? TranscriptPath);

/// <summary>
/// Owns the session-level hook concern for a <see cref="CodaSession"/>: the session hook runner,
/// the <c>SessionStart</c> / <c>SessionEnd</c> / <c>Notification</c> firings, and the session-scoped
/// application of the <c>SessionStart</c> outputs (append-system-prompt composition, once-only
/// <c>additionalContext</c> injection, and the <c>initialUserMessage</c> pre-turn).
/// </summary>
/// <remarks>
/// Extracted from <see cref="CodaSession"/> so the application logic is reachable without standing
/// up a whole session: every rule below (once-only, session-append-first, fire-at-most-once,
/// notifications-drained-before-SessionEnd) is directly testable here.
/// </remarks>
public sealed partial class SessionLifecycleHooks
{
    /// <summary>
    /// Bounded budget for draining the last in-flight background <c>Notification</c> hook at
    /// dispose. The hook is cancelled first, so this only covers its unwinding.
    /// </summary>
    internal static readonly TimeSpan NotificationDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly object startGate = new();
    private readonly long startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Mutable because the owning session builds its logger factory after constructing this
    // collaborator. The [LoggerMessage] source generator requires a field, not a property.
    private ILogger logger;

    /// <summary>
    /// Session-scoped lifetime for fire-and-forget <c>Notification</c> hooks. Cancelled at the
    /// start of teardown so an in-flight notification subprocess can never outlive
    /// <see cref="FireSessionEndOnceAsync"/> and invert the end-of-life ordering.
    /// </summary>
    private readonly CancellationTokenSource notificationCts = new();

    private Task notificationTask = Task.CompletedTask;
    private Task? startTask;
    private int endFired;
    private int turnCount;
    private volatile string endReason = "exit";
    private string? appendSystemPrompt;
    private string? pendingAdditionalContext;
    private string? pendingInitialUserMessage;
    private int additionalContextInjected;

    /// <summary>Initialises the collaborator.</summary>
    /// <param name="sessionId">The owning session's identifier, used only for log correlation.</param>
    /// <param name="logger">Logger for fail-open hook failures. <see langword="null"/> logs nothing.</param>
    public SessionLifecycleHooks(string sessionId, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        this.SessionId = sessionId;
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Logger for fail-open hook failures. Settable because the owning session builds its logger
    /// factory after the collaborator is constructed.
    /// </summary>
    public ILogger Logger
    {
        get => this.logger;
        set => this.logger = value ?? NullLogger.Instance;
    }

    /// <summary>The owning session's identifier. Updated when the session adopts a resumed id.</summary>
    public string SessionId { get; set; }

    /// <summary>
    /// The runner used for every session-level event. <see langword="null"/> means no session hooks
    /// are configured and every firing is a no-op.
    /// </summary>
    public UserHookRunner? Runner { get; set; }

    /// <summary>
    /// True once the runner has been rebuilt with the http/prompt handlers that need a live
    /// <c>ILlmClient</c>. Guards the at-most-once upgrade.
    /// </summary>
    public bool HandlersUpgraded { get; set; }

    /// <summary>The <c>source</c> field of the <c>SessionStart</c> payload: <c>"new"</c> or <c>"resume"</c>.</summary>
    public string Source { get; set; } = "new";

    /// <summary>The previous session id when this session was resumed, otherwise <see langword="null"/>.</summary>
    public string? ResumedFromId { get; set; }

    /// <summary>Number of completed user turns, reported in the <c>SessionEnd</c> payload.</summary>
    public int TurnCount => Volatile.Read(ref this.turnCount);

    /// <summary>The reason reported in the <c>SessionEnd</c> payload. Defaults to <c>"exit"</c>.</summary>
    public string EndReason
    {
        get => this.endReason;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            this.endReason = value;
        }
    }

    /// <summary>Records that this session was resumed from <paramref name="previousSessionId"/>.</summary>
    /// <param name="previousSessionId">The id the session had before adopting the transcript's id.</param>
    public void MarkResumed(string previousSessionId)
    {
        this.ResumedFromId = previousSessionId;
        this.Source = "resume";
    }

    /// <summary>Records one completed user turn.</summary>
    public void RecordTurn() => Interlocked.Increment(ref this.turnCount);

    // ── SessionStart ───────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the <c>SessionStart</c> hooks once and stores their outputs. Every concurrent caller
    /// awaits the same task, so no caller can return before the outputs are applied. Fail-open:
    /// a broken or timed-out hook is logged and ignored.
    /// </summary>
    /// <param name="context">Payload values for this session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ApplySessionStartAsync(SessionStartPayloadContext context, CancellationToken cancellationToken)
    {
        if (this.Runner?.HasSessionStart != true)
        {
            return Task.CompletedTask;
        }

        lock (this.startGate)
        {
            return this.startTask ??= this.ApplySessionStartCoreAsync(context, cancellationToken);
        }
    }

    private async Task ApplySessionStartCoreAsync(
        SessionStartPayloadContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await this.Runner!.RunSessionStartAsync(
                this.Source,
                context.Model,
                context.PermissionMode,
                context.TranscriptPath,
                this.ResumedFromId,
                cancellationToken).ConfigureAwait(false);

            if (result.AppendSystemPrompt is not null)
            {
                this.appendSystemPrompt = result.AppendSystemPrompt;
            }

            if (result.AdditionalContext is not null)
            {
                this.pendingAdditionalContext = result.AdditionalContext;
            }

            if (result.InitialUserMessage is not null)
            {
                this.pendingInitialUserMessage = result.InitialUserMessage;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // SessionStart is fail-open — log and continue.
            this.LogSessionStartHookFailed(this.SessionId, ex);
        }
    }

    // ── SessionStart output application ────────────────────────────────────────

    /// <summary>
    /// Returns the <c>additionalContext</c> from <c>SessionStart</c> the first time it is asked
    /// for, and <see langword="null"/> on every later call. The caller appends it as a synthetic
    /// user message before the first real user turn.
    /// </summary>
    public string? TakeAdditionalContextOnce()
    {
        if (Interlocked.Exchange(ref this.additionalContextInjected, 1) != 0)
        {
            return null;
        }

        return Interlocked.Exchange(ref this.pendingAdditionalContext, null);
    }

    /// <summary>
    /// Returns the <c>initialUserMessage</c> from <c>SessionStart</c> exactly once, clearing it
    /// atomically so a re-entrant turn cannot run it twice.
    /// </summary>
    public string? TakeInitialUserMessage() =>
        Interlocked.Exchange(ref this.pendingInitialUserMessage, null);

    /// <summary>
    /// Layers the session-level <c>appendSystemPrompt</c> from <c>SessionStart</c> onto
    /// <paramref name="turnShape"/>. The session append is the base and comes first; a per-turn
    /// append follows it. Returns <paramref name="turnShape"/> unchanged when no session append
    /// was produced.
    /// </summary>
    /// <param name="turnShape">The turn shape resolved so far, or <see langword="null"/>.</param>
    public TurnShape? ComposeAppendSystemPrompt(TurnShape? turnShape)
    {
        if (this.appendSystemPrompt is not { } sessionAppend)
        {
            return turnShape;
        }

        var perTurnAppend = turnShape?.AppendSystemPrompt;
        var merged = perTurnAppend is not null ? $"{sessionAppend}\n\n{perTurnAppend}" : sessionAppend;
        return (turnShape ?? TurnShape.None) with { AppendSystemPrompt = merged };
    }

    // ── SessionEnd ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires <c>SessionEnd</c> hooks exactly once. Hard-coded 2 s deadline; never throws.
    /// </summary>
    /// <param name="usage">Accumulated token usage for the session.</param>
    /// <param name="transcriptPath">Path the session transcript was written to.</param>
    public async Task FireSessionEndOnceAsync(TokenUsage usage, string? transcriptPath)
    {
        if (Interlocked.Exchange(ref this.endFired, 1) != 0)
        {
            return;
        }

        if (this.Runner?.HasSessionEnd != true)
        {
            return;
        }

        try
        {
            var durationMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - this.startedAtMs;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await this.Runner.RunSessionEndAsync(
                this.EndReason,
                durationMs,
                this.TurnCount,
                usage,
                transcriptPath,
                cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // SessionEnd is fail-open and runs during shutdown — never propagate.
            try { this.LogSessionEndHookFailed(this.SessionId, ex); } catch { }
        }
    }

    // ── Notification ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fires a <c>Notification("idle")</c> hook in the background after a successful turn.
    /// Fire-and-forget so notification latency never blocks the caller; bound to the session
    /// lifetime so it cannot outlive teardown.
    /// </summary>
    public void FireIdleNotificationBackground()
    {
        if (this.Runner?.HasNotification != true)
        {
            return;
        }

        if (!this.TryGetNotificationToken(out var token))
        {
            return;
        }

        this.notificationTask = Task.Run(async () =>
        {
            try
            {
                await this.Runner.RunNotificationAsync(
                    "idle",
                    "Agent is ready.",
                    taskId: null,
                    token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { this.LogNotificationHookFailed(this.SessionId, ex); } catch { }
            }
        });
    }

    /// <summary>
    /// Runs a <c>Notification</c> hook for a completed background task, bound to the session
    /// lifetime. Returns a completed task when no notification hook is configured or the session
    /// is already tearing down.
    /// </summary>
    /// <param name="kind">The notification kind (e.g. <c>"task-complete"</c>).</param>
    /// <param name="taskId">The background task's identifier, or <see langword="null"/>.</param>
    public Task RunTaskNotificationAsync(string kind, string? taskId)
    {
        if (this.Runner?.HasNotification != true || !this.TryGetNotificationToken(out var token))
        {
            return Task.CompletedTask;
        }

        return this.Runner.RunNotificationAsync(
            kind,
            taskId is not null ? $"Background task {taskId} completed." : "A background task completed.",
            taskId,
            token);
    }

    /// <summary>
    /// Cancels the session-scoped notification lifetime and drains the last in-flight background
    /// notification, bounded by <see cref="NotificationDrainTimeout"/>. Never throws. Call this
    /// before <see cref="FireSessionEndOnceAsync"/> so the end-of-life ordering holds.
    /// </summary>
    public async Task DrainBackgroundNotificationsAsync()
    {
        try
        {
            await this.notificationCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await this.notificationTask.WaitAsync(NotificationDrainTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Cancellation, a hook fault, or an overrun of the drain budget: all best-effort.
            try { this.LogNotificationHookFailed(this.SessionId, ex); } catch { }
        }
    }

    /// <summary>Releases the notification lifetime. Call last, after the drain.</summary>
    public void Dispose() => this.notificationCts.Dispose();

    /// <summary>
    /// Reads the session-scoped notification token, returning <see langword="false"/> once the
    /// session is tearing down so no new background notification is started.
    /// </summary>
    private bool TryGetNotificationToken(out CancellationToken token)
    {
        try
        {
            token = this.notificationCts.Token;
            return !token.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            token = new CancellationToken(canceled: true);
            return false;
        }
    }

    // ── Logger messages ────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "SessionStart hook failed (fail-open): session={sessionId}")]
    private partial void LogSessionStartHookFailed(string sessionId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SessionEnd hook failed (fail-open): session={sessionId}")]
    private partial void LogSessionEndHookFailed(string sessionId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification hook failed (fire-and-forget): session={sessionId}")]
    private partial void LogNotificationHookFailed(string sessionId, Exception ex);
}
