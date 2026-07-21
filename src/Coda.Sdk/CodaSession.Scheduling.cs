using Coda.Agent.Scheduling;
using Coda.Agent.Tasks;
using Coda.Agent.Lsp;
using Coda.Sdk.Scheduling;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk;

/// <summary>
/// Scheduling-lifecycle half of <see cref="CodaSession"/>: owns the optional schedule runtime and
/// its concrete <see cref="ScheduledAgentHost"/>, drives concurrency-safe idempotent
/// initialization (LSP + runtime), and tears the runtime down strictly before the task manager.
/// Split into its own partial so the core session file stays focused on the per-turn agent loop.
/// </summary>
public sealed partial class CodaSession
{
    // Live options accessor evaluated per scheduled firing (never a construction snapshot) so a
    // mid-session provider/model/effort/tool/permission change is observed by the next run.
    private readonly Func<SessionOptions> currentOptionsProvider;

    // Deterministic time source for the runtime clock; TimeProvider.System in production.
    private readonly TimeProvider timeProvider;

    // Serializes the initialization gate and the disposal flag so init and dispose never race into
    // a double start or a start-after-dispose. Never held across an await.
    private readonly object initGate = new();

    // The single, shared initialization task. Created once by the first InitializeAsync caller; all
    // callers (concurrent or later) await THIS task, so LSP + runtime start exactly once and any
    // failure/cancel is observed identically by everyone.
    private Task? initialization;

    // Set under initGate once disposal begins so a racing InitializeAsync never starts a runtime.
    private bool schedulingDisposed;

    // The session-owned schedule runtime, published only after a successful start. Volatile because
    // it is assigned on the init task and read on turn threads (the pipeline provider), the runtime
    // snapshot, and disposal.
    private volatile IScheduleRuntimeHandle? scheduleRuntime;

    // The concrete host each firing runs through; owned so its lifetime matches the runtime.
    private ScheduledAgentHost? scheduledAgentHost;

    // Number of runtimes created; proves "exactly one" under concurrent/sequential init in tests.
    private int scheduleRuntimeCreations;

    private IScheduleLifecycleSink scheduleLifecycleSink = NullScheduleLifecycleSink.Instance;

    /// <summary>
    /// Sink that receives schedule lifecycle notifications. Interactive/serve hosts set this BEFORE
    /// <see cref="InitializeAsync"/> so runtime events reach the UI/JSON-RPC adapter; the default is
    /// a safe no-op. Read live at every publish (via an internal forwarder), so a value set before
    /// initialization is always observed and no permanently stale sink is captured.
    /// </summary>
    public IScheduleLifecycleSink ScheduleLifecycleSink
    {
        get => Volatile.Read(ref this.scheduleLifecycleSink);
        set => Volatile.Write(ref this.scheduleLifecycleSink, value ?? NullScheduleLifecycleSink.Instance);
    }

    /// <summary>Test seam: the live runtime view, or null before init / when disabled.</summary>
    internal IScheduleRuntimeView? ScheduleRuntimeForTest => this.scheduleRuntime;

    /// <summary>Test seam: number of schedule runtimes created (exactly one after init).</summary>
    internal int ScheduleRuntimeCreationCountForTest => Volatile.Read(ref this.scheduleRuntimeCreations);

    /// <summary>Test seam: overrides runtime construction to inject a fake handle/probe.</summary>
    internal Func<ScheduledTaskStore, TaskManager, ScheduledAgentHost, IScheduleLifecycleSink, TimeProvider, ILogger, IScheduleRuntimeHandle>? ScheduleRuntimeFactoryForTest { get; set; }

    /// <summary>
    /// Completes async initialization: starts LSP servers (when configured) AND the schedule runtime
    /// (when <see cref="SessionOptions.EnableScheduleRuntime"/> is set). Concurrency-safe and
    /// idempotent — concurrent or repeated calls run the work exactly once and await the same task,
    /// observing the same success/failure. Safe (no-op) when neither LSP nor the runtime is
    /// configured. Bound to the first caller's <paramref name="cancellationToken"/>.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (this.initGate)
        {
            // Once disposal has begun, never start anything: this makes init-vs-dispose deterministic.
            if (this.schedulingDisposed)
            {
                return Task.CompletedTask;
            }

            // Lazily create the one shared task. The async method's synchronous prefix is trivial and
            // suspends at its first real await, so no lock is held while the awaited work runs.
            return this.initialization ??= this.InitializeCoreAsync(cancellationToken);
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        // LSP first, exactly as before: start servers then wire passive-feedback handlers.
        if (this.lspManager is not null)
        {
            await this.lspManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
            LspPassiveFeedback.RegisterNotificationHandlers(
                this.lspManager,
                this.lspDiagnostics!,
                this.loggerFactory.CreateLogger("Coda.Lsp.PassiveFeedback"));
        }

        // Then the schedule runtime, only when enabled. LSP-only sessions behave exactly as before.
        if (this.Options.EnableScheduleRuntime)
        {
            await this.StartScheduleRuntimeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartScheduleRuntimeAsync(CancellationToken cancellationToken)
    {
        // Concrete host: live options per firing + the session's factories/credentials/fingerprint/
        // shared HTTP/logger/builder, and the stream-progress sink set before init (never a stale
        // null when serve set it beforehand).
        var host = new ScheduledAgentHost(
            this.currentOptionsProvider,
            this.llmClientFactory,
            this.agentLoopFactory,
            this.credentials,
            this.fingerprint,
            this.http,
            this.loggerFactory,
            this.turnPipelineBuilder,
            this.StreamProgressSink);

        // A forwarder reads the CONFIGURED sink at every publish, so a sink set before init (or
        // swapped afterwards) is always observed rather than one captured stale at construction.
        var lifecycle = new ForwardingLifecycleSink(this);
        var logger = this.loggerFactory.CreateLogger("Coda.Schedule.Runtime");

        var runtime = this.ScheduleRuntimeFactoryForTest is { } factory
            ? factory(this.schedules, this.tasks, host, lifecycle, this.timeProvider, logger)
            : new ScheduleRuntimeHandle(
                new ScheduleRuntime(this.schedules, this.tasks, host, lifecycle, this.timeProvider, logger));

        Interlocked.Increment(ref this.scheduleRuntimeCreations);
        this.scheduledAgentHost = host;

        try
        {
            await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Half-owned runtime on a failed/canceled start: dispose it and clear ownership before
            // propagating, so a failed init never leaves a running runtime behind. Not a silent
            // catch — the original exception is rethrown to every awaiter of the shared init task.
            await runtime.DisposeAsync().ConfigureAwait(false);
            this.scheduledAgentHost = null;
            throw;
        }

        // Publish only after a successful start: schedule_list and the runtime snapshot see the live
        // runtime from here on.
        this.scheduleRuntime = runtime;
    }

    /// <summary>
    /// First step of session teardown: stops further initialization, awaits any in-flight
    /// initialization so a concurrently-created runtime is fully owned, then disposes the runtime
    /// BEFORE the caller disposes the task manager — a due firing can never register after task
    /// shutdown begins. Bounded: the runtime's own disposal cancels its loop and returns promptly.
    /// </summary>
    private async ValueTask ShutdownScheduleRuntimeAsync()
    {
        Task? pendingInit;
        lock (this.initGate)
        {
            this.schedulingDisposed = true;
            pendingInit = this.initialization;
        }

        if (pendingInit is not null)
        {
            try
            {
                await pendingInit.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Init was canceled; nothing to own.
            }
            catch (Exception ex)
            {
                // A failed init is surfaced to its own awaiters; teardown still proceeds. Logged (not
                // silently swallowed) so a start fault during dispose remains diagnosable.
                this.LogScheduleInitFailedDuringDispose(this.SessionId, ex);
            }
        }

        var runtime = this.scheduleRuntime;
        if (runtime is not null)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "schedule runtime initialization failed (observed during dispose): session={sessionId}")]
    private partial void LogScheduleInitFailedDuringDispose(string sessionId, Exception ex);

    /// <summary>Production handle delegating to a real <see cref="ScheduleRuntime"/>.</summary>
    private sealed class ScheduleRuntimeHandle(ScheduleRuntime runtime) : IScheduleRuntimeHandle
    {
        public Task StartAsync(CancellationToken cancellationToken = default) => runtime.StartAsync(cancellationToken);

        public bool TryGetState(string scheduleId, out ScheduleRuntimeState state) => runtime.TryGetState(scheduleId, out state);

        public IReadOnlyList<ScheduleRuntimeSnapshot> GetSnapshot() => runtime.GetSnapshot();

        public ValueTask DisposeAsync() => runtime.DisposeAsync();
    }

    /// <summary>
    /// Forwards lifecycle events to the session's CURRENTLY configured sink at publish time, so a
    /// sink set before (or after) initialization is observed rather than a stale one captured when
    /// the runtime was built. Resilient by contract — the runtime isolates sink faults.
    /// </summary>
    private sealed class ForwardingLifecycleSink(CodaSession session) : IScheduleLifecycleSink
    {
        public ValueTask PublishAsync(ScheduleLifecycleEvent value, CancellationToken cancellationToken = default) =>
            session.ScheduleLifecycleSink.PublishAsync(value, cancellationToken);
    }
}

/// <summary>
/// Internal host over the session-owned schedule runtime: exposes the read-only view surfaced to
/// <c>schedule_list</c> plus the start/dispose levers the session drives. A single seam lets tests
/// substitute a fake/probe while production wraps the real <see cref="ScheduleRuntime"/>.
/// </summary>
internal interface IScheduleRuntimeHandle : IScheduleRuntimeView, IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
}
