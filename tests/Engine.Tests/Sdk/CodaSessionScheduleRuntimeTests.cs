using System.Diagnostics;
using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Agent.Scheduling;
using Coda.Agent.Tasks;
using Coda.Sdk;
using Coda.Sdk.Scheduling;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging;
using static Engine.Tests.TestSupport.CredentialFixtures;

namespace Engine.Tests.Sdk;

/// <summary>
/// Task 7: coverage for the session-owned schedule runtime lifecycle. Verifies that
/// <see cref="CodaSession.InitializeAsync"/> creates and starts a single
/// <see cref="ScheduleRuntime"/> only when <see cref="SessionOptions.EnableScheduleRuntime"/> is set,
/// that the concrete <see cref="ScheduledAgentHost"/> reads the live options / stream sink per
/// firing, that <c>schedule_list</c> sees the live runtime view after init, that the runtime state
/// is surfaced through <see cref="CodaSession.GetRuntimeSnapshot"/>, and that disposal orders the
/// runtime strictly before the task manager. All scheduling is driven by overdue definitions that
/// fire immediately, so no test sleeps on wall-clock time; bounded waits only guard against hangs.
/// </summary>
public sealed class CodaSessionScheduleRuntimeTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    private readonly string root = Directory.CreateTempSubdirectory("coda_session_sched_").FullName;

    private SessionOptions Options(
        bool enable = true,
        string model = "claude-sonnet-4-6",
        string? effort = null,
        IReadOnlyList<ITool>? extraTools = null,
        PermissionMode mode = PermissionMode.BypassPermissions) => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = model,
        WorkingDirectory = this.root,
        PermissionMode = mode,
        Effort = effort,
        ExtraTools = extraTools ?? [],
        EnableScheduleRuntime = enable,
    };

    private CodaSession NewSession(
        SessionOptions options,
        ILlmClientFactory clientFactory,
        IAgentLoopFactory loopFactory,
        Func<SessionOptions>? currentOptionsProvider = null,
        TimeProvider? timeProvider = null) =>
        new(
            SignedInClaude(),
            options,
            httpClient: new HttpClient(new ThrowingHandler()),
            llmClientFactory: clientFactory,
            agentLoopFactory: loopFactory,
            currentOptionsProvider: currentOptionsProvider,
            timeProvider: timeProvider);

    private static ScheduleDefinitionDraft OverdueAt(string prompt = "run") =>
        new(null, ScheduleKind.At, prompt, null,
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5), null, "UTC",
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5));

    private static ScheduleDefinitionDraft OverdueInterval(string prompt = "run") =>
        new(null, ScheduleKind.Interval, prompt, TimeSpan.FromHours(1), null, null, "UTC",
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5));

    // ── 1. Option default ─────────────────────────────────────────────────

    [Fact]
    public void EnableScheduleRuntime_defaults_to_false()
    {
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "m",
            WorkingDirectory = this.root,
        };

        Assert.False(options.EnableScheduleRuntime);
    }

    // ── 2/3. Enable/disable gating ────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_enabled_without_lsp_starts_the_runtime()
    {
        var sink = new RecordingLifecycleSink();
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        session.ScheduleLifecycleSink = sink;
        var def = session.Schedules.Add(OverdueAt(), DateTimeOffset.UtcNow);

        await session.InitializeAsync();

        Assert.NotNull(session.ScheduleRuntimeForTest);
        Assert.Equal(1, session.ScheduleRuntimeCreationCountForTest);
        await sink.WaitForCountAsync(1, Guard);
        Assert.Equal(ScheduleLifecycleKind.Started, sink.Events[0].Kind);
        Assert.Equal(def.Id, sink.Events[0].DefinitionId);
    }

    [Fact]
    public async Task InitializeAsync_disabled_creates_no_runtime()
    {
        var sink = new RecordingLifecycleSink();
        using var session = this.NewSession(this.Options(enable: false), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        session.ScheduleLifecycleSink = sink;
        session.Schedules.Add(OverdueAt(), DateTimeOffset.UtcNow);

        await session.InitializeAsync();

        Assert.Null(session.ScheduleRuntimeForTest);
        Assert.Equal(0, session.ScheduleRuntimeCreationCountForTest);
        // Give any (erroneously started) loop a moment; nothing should ever fire.
        await Task.Delay(100);
        Assert.Empty(sink.Events);
        Assert.Empty(session.Tasks.List());
    }

    // ── 4. Idempotent / concurrent init ───────────────────────────────────

    [Fact]
    public async Task InitializeAsync_is_idempotent_and_creates_a_single_runtime()
    {
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));

        await session.InitializeAsync();
        var first = session.ScheduleRuntimeForTest;
        await session.InitializeAsync();
        var second = session.ScheduleRuntimeForTest;

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, session.ScheduleRuntimeCreationCountForTest);
    }

    [Fact]
    public async Task Concurrent_InitializeAsync_creates_a_single_runtime()
    {
        var sink = new RecordingLifecycleSink();
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        session.ScheduleLifecycleSink = sink;
        session.Schedules.Add(OverdueAt(), DateTimeOffset.UtcNow);

        await Task.WhenAll(
            Task.Run(() => session.InitializeAsync()),
            Task.Run(() => session.InitializeAsync()),
            Task.Run(() => session.InitializeAsync()));

        Assert.Equal(1, session.ScheduleRuntimeCreationCountForTest);
        Assert.NotNull(session.ScheduleRuntimeForTest);

        // A single one-shot fired by a single loop yields exactly one Started event.
        await sink.WaitForCountAsync(1, Guard);
        await Task.Delay(150);
        Assert.Single(sink.Events, e => e.Kind == ScheduleLifecycleKind.Started);
    }

    // ── 5. Failure / cancel semantics ─────────────────────────────────────

    [Fact]
    public async Task Initialization_failure_disposes_the_partial_runtime_and_propagates()
    {
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        FakeScheduleRuntimeHandle? fake = null;
        session.ScheduleRuntimeFactoryForTest = (schedules, tasks, host, lifecycle, tp, logger) =>
            fake = new FakeScheduleRuntimeHandle(tasks) { StartError = new InvalidOperationException("start boom") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.InitializeAsync());

        Assert.Equal("start boom", ex.Message);
        Assert.NotNull(fake);
        Assert.Equal(1, fake!.StartCount);
        Assert.Equal(1, fake.DisposeCount);            // partial runtime cleaned up
        Assert.Null(session.ScheduleRuntimeForTest);   // ownership cleared
    }

    [Fact]
    public async Task Initialization_failure_is_observed_by_concurrent_callers()
    {
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        session.ScheduleRuntimeFactoryForTest = (schedules, tasks, host, lifecycle, tp, logger) =>
            new FakeScheduleRuntimeHandle(tasks) { StartError = new InvalidOperationException("shared boom") };

        var a = session.InitializeAsync();
        var b = session.InitializeAsync();

        var exA = await Assert.ThrowsAsync<InvalidOperationException>(() => a);
        var exB = await Assert.ThrowsAsync<InvalidOperationException>(() => b);
        Assert.Equal("shared boom", exA.Message);
        Assert.Equal("shared boom", exB.Message);
    }

    // ── 6. Lifecycle sink wiring ──────────────────────────────────────────

    [Fact]
    public async Task Lifecycle_sink_set_before_init_receives_started_event()
    {
        var sink = new RecordingLifecycleSink();
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        session.ScheduleLifecycleSink = sink;
        var def = session.Schedules.Add(OverdueAt(), DateTimeOffset.UtcNow);

        await session.InitializeAsync();
        await sink.WaitForCountAsync(1, Guard);

        Assert.Contains(sink.Events, e => e.Kind == ScheduleLifecycleKind.Started && e.DefinitionId == def.Id);
    }

    [Fact]
    public async Task Default_lifecycle_sink_is_safe()
    {
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        session.Schedules.Add(OverdueAt(), DateTimeOffset.UtcNow);

        // Default sink (NullScheduleLifecycleSink) must not throw while the runtime fires.
        await session.InitializeAsync();
        await Task.Delay(150);

        Assert.NotNull(session.ScheduleRuntimeForTest);
    }

    // ── 7. Live options per firing ────────────────────────────────────────

    [Fact]
    public async Task Firing_reads_current_options_after_mutation()
    {
        var factory = new RecordingLoopFactory(Immediate());
        using var session = this.NewSession(this.Options(model: "claude-sonnet-4-6"), new StubClientFactory(new InertClient()), factory);

        await session.InitializeAsync();

        var probe = new MarkerTool();
        session.Options = this.Options(model: "claude-opus-4-1", effort: "high", extraTools: [probe]);
        session.Schedules.Add(OverdueAt(), DateTimeOffset.UtcNow);

        await factory.WaitForScheduledSpecAsync(1, Guard);
        var spec = factory.ScheduledSpecs[0];

        Assert.Equal("claude-opus-4-1", spec.Options.Model);
        Assert.Equal("high", spec.Options.Effort);
        Assert.Contains(MarkerTool.ToolName, spec.Tools.All.Select(t => t.Name));
    }

    [Fact]
    public async Task CurrentOptionsProvider_is_invoked_per_firing()
    {
        var factory = new RecordingLoopFactory(Immediate());
        var calls = 0;
        var options = this.Options();
        using var session = this.NewSession(
            options,
            new StubClientFactory(new InertClient()),
            factory,
            currentOptionsProvider: () => { Interlocked.Increment(ref calls); return options; });

        await session.InitializeAsync();

        session.Schedules.Add(OverdueAt("a"), DateTimeOffset.UtcNow);
        await factory.WaitForScheduledSpecAsync(1, Guard);
        session.Schedules.Add(OverdueAt("b"), DateTimeOffset.UtcNow);
        await factory.WaitForScheduledSpecAsync(2, Guard);

        Assert.True(calls >= 2, $"provider invoked {calls} times; expected one per firing");
    }

    // ── 8. Stream progress sink ───────────────────────────────────────────

    [Fact]
    public async Task Scheduled_client_uses_stream_progress_sink_set_before_init()
    {
        var clientFactory = new StubClientFactory(new InertClient());
        using var session = this.NewSession(this.Options(), clientFactory, new StubLoopFactory(Immediate()));
        var progress = new FakeStreamProgressSink();
        session.StreamProgressSink = progress;

        await session.InitializeAsync();
        session.Schedules.Add(OverdueAt(), DateTimeOffset.UtcNow);
        await clientFactory.WaitForCreateAsync(1, Guard);

        Assert.Contains(progress, clientFactory.ProgressSinks);
    }

    // ── 9. Main BuildSpec runtime view ────────────────────────────────────

    [Fact]
    public async Task Main_build_spec_sees_runtime_after_init()
    {
        var factory = new RecordingLoopFactory(ImmediateText());
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), factory);

        await session.InitializeAsync();
        await session.RunAsync("hi");

        var mainSpec = factory.MainSpecs.Single();
        Assert.NotNull(mainSpec.ScheduleRuntime);
        Assert.Same(session.ScheduleRuntimeForTest, mainSpec.ScheduleRuntime);
    }

    [Fact]
    public async Task Main_build_spec_runtime_null_before_init()
    {
        var factory = new RecordingLoopFactory(ImmediateText());
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), factory);

        await session.RunAsync("hi");

        Assert.Null(factory.MainSpecs.Single().ScheduleRuntime);
    }

    [Fact]
    public async Task Main_build_spec_runtime_null_when_disabled()
    {
        var factory = new RecordingLoopFactory(ImmediateText());
        using var session = this.NewSession(this.Options(enable: false), new StubClientFactory(new InertClient()), factory);

        await session.InitializeAsync();
        await session.RunAsync("hi");

        Assert.Null(factory.MainSpecs.Single().ScheduleRuntime);
    }

    // ── 10. Runtime snapshot ──────────────────────────────────────────────

    [Fact]
    public async Task GetRuntimeSnapshot_includes_running_scheduled_execution()
    {
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new RecordingLoopFactory(new ProgrammableLoop(async (_, _, ct) => await block.Task.WaitAsync(ct)));
        var sink = new RecordingLifecycleSink();
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), factory);
        session.ScheduleLifecycleSink = sink;
        var def = session.Schedules.Add(OverdueInterval(), DateTimeOffset.UtcNow);

        await session.InitializeAsync();
        await sink.WaitForCountAsync(1, Guard);

        var snapshot = await WaitForRunningExecutionAsync(session, def.Id, Guard);
        Assert.Equal(ScheduleRuntimeStatus.Running, snapshot.Status);
        Assert.False(string.IsNullOrEmpty(snapshot.ActiveTaskId));

        block.TrySetResult();
    }

    [Fact]
    public async Task GetRuntimeSnapshot_scheduled_executions_empty_when_disabled()
    {
        using var session = this.NewSession(this.Options(enable: false), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        session.Schedules.Add(OverdueAt(), DateTimeOffset.UtcNow);

        await session.InitializeAsync();

        Assert.Empty(session.GetRuntimeSnapshot().ScheduledExecutions);
    }

    // ── 11. Dispose ordering and races ────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_disposes_runtime_before_task_manager()
    {
        var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        FakeScheduleRuntimeHandle? fake = null;
        session.ScheduleRuntimeFactoryForTest = (schedules, tasks, host, lifecycle, tp, logger) =>
            fake = new FakeScheduleRuntimeHandle(tasks);

        await session.InitializeAsync();
        await session.DisposeAsync();

        Assert.NotNull(fake);
        Assert.Equal(1, fake!.DisposeCount);
        // At runtime-dispose time the task manager was still live (disposed strictly afterwards).
        Assert.True(fake.TaskManagerLiveAtDispose);
    }

    [Fact]
    public async Task InitializeAsync_after_dispose_starts_no_runtime()
    {
        var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        var created = 0;
        session.ScheduleRuntimeFactoryForTest = (schedules, tasks, host, lifecycle, tp, logger) =>
        {
            Interlocked.Increment(ref created);
            return new FakeScheduleRuntimeHandle(tasks);
        };

        await session.DisposeAsync();
        await session.InitializeAsync();

        Assert.Equal(0, created);
        Assert.Null(session.ScheduleRuntimeForTest);
    }

    [Fact]
    public async Task Concurrent_init_and_dispose_disposes_runtime_exactly_once()
    {
        var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeScheduleRuntimeHandle? fake = null;
        session.ScheduleRuntimeFactoryForTest = (schedules, tasks, host, lifecycle, tp, logger) =>
            fake = new FakeScheduleRuntimeHandle(tasks) { StartGate = gate };

        var init = session.InitializeAsync();
        await fake!.StartEntered.Task.WaitAsync(Guard);  // init reached (blocked in) StartAsync

        var dispose = session.DisposeAsync().AsTask();    // must await the in-flight init
        gate.TrySetResult();                              // release the blocked start

        await Task.WhenAll(init, dispose).WaitAsync(Guard);

        Assert.Equal(1, fake.StartCount);
        Assert.Equal(1, fake.DisposeCount);
    }

    // ── 12. Synchronous dispose bounds ────────────────────────────────────

    [Fact]
    public async Task Dispose_sync_tears_down_runtime_within_budget()
    {
        var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(Immediate()));
        await session.InitializeAsync();

        var sw = Stopwatch.StartNew();
        var disposeTask = Task.Run(() => session.Dispose());
        var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(30))) == disposeTask;
        if (completed)
        {
            await disposeTask; // observe completion (Dispose swallows internally, but await is cleaner)
        }

        sw.Stop();

        Assert.True(completed, "synchronous Dispose did not return");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(25), $"sync Dispose took {sw.Elapsed}");
        session.Dispose(); // idempotent
    }

    // ── 13. Main run behavior unchanged ───────────────────────────────────

    [Fact]
    public async Task RunAsync_does_not_start_the_runtime()
    {
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(ImmediateText()));

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Null(session.ScheduleRuntimeForTest); // RunAsync never starts the scheduler implicitly
    }

    [Fact]
    public async Task RunAsync_behaves_unchanged_with_runtime_enabled()
    {
        using var session = this.NewSession(this.Options(), new StubClientFactory(new InertClient()), new StubLoopFactory(ImmediateText()));
        await session.InitializeAsync();

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Equal(ImmediateLoop.CannedText, result.FinalText);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ProgrammableLoop Immediate() => new((_, _, _) => Task.CompletedTask);

    private static ImmediateLoop ImmediateText() => new();

    private static async Task<ScheduleRuntimeSnapshot> WaitForRunningExecutionAsync(CodaSession session, string id, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var match = session.GetRuntimeSnapshot().ScheduledExecutions
                .FirstOrDefault(s => s.DefinitionId == id && s.Status == ScheduleRuntimeStatus.Running);
            if (match is not null)
            {
                return match;
            }

            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException($"scheduled execution '{id}' never reached Running");
            }

            await Task.Delay(20);
        }
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No real HTTP should be issued: all collaborators are faked.");
    }

    private sealed class InertClient : ILlmClient, IDisposable
    {
        public string ProviderId => ClaudeAiProvider.Id;

        public void Dispose() { }

        public IAsyncEnumerable<AssistantStreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("InertClient must not be streamed.");
    }

    private sealed class StubClientFactory(ILlmClient? client) : ILlmClientFactory
    {
        private readonly object gate = new();
        private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IStreamProgressSink?> ProgressSinks { get; } = [];

        public ILlmClient? Create(
            string providerId,
            CredentialManager credentials,
            ClientFingerprint fingerprint,
            HttpClient httpClient,
            ILoggerFactory? loggerFactory = null,
            LlmHttpTimeoutConfig? timeoutConfig = null,
            IStreamProgressSink? progressSink = null)
        {
            TaskCompletionSource wake;
            lock (this.gate)
            {
                this.ProgressSinks.Add(progressSink);
                wake = this.signal;
                this.signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            wake.TrySetResult();
            return client;
        }

        public async Task WaitForCreateAsync(int count, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Task wait;
                lock (this.gate)
                {
                    if (this.ProgressSinks.Count >= count)
                    {
                        return;
                    }

                    wait = this.signal.Task;
                }

                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"client Create calls < {count}");
                }

                await wait.WaitAsync(remaining);
            }
        }
    }

    private sealed class ProgrammableLoop(Func<List<ChatMessage>, IAgentSink, CancellationToken, Task> body) : IAgentLoop
    {
        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default) =>
            body(history, sink, cancellationToken);
    }

    private sealed class ImmediateLoop : IAgentLoop
    {
        public const string CannedText = "from scheduled fake loop";

        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            sink.OnAssistantText(CannedText);
            sink.OnAssistantTextComplete();
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }

    private sealed class StubLoopFactory(IAgentLoop loop) : IAgentLoopFactory
    {
        public IAgentLoop Create(AgentLoopSpec spec) => loop;
    }

    private sealed class RecordingLoopFactory(IAgentLoop loop) : IAgentLoopFactory
    {
        private readonly object gate = new();
        private readonly List<AgentLoopSpec> specs = [];
        private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<AgentLoopSpec> MainSpecs
        {
            get { lock (this.gate) return this.specs.Where(s => s.CurrentTaskId is null).ToList(); }
        }

        public IReadOnlyList<AgentLoopSpec> ScheduledSpecs
        {
            get { lock (this.gate) return this.specs.Where(s => s.CurrentTaskId is not null).ToList(); }
        }

        public IAgentLoop Create(AgentLoopSpec spec)
        {
            TaskCompletionSource wake;
            lock (this.gate)
            {
                this.specs.Add(spec);
                wake = this.signal;
                this.signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            wake.TrySetResult();
            return loop;
        }

        public async Task WaitForScheduledSpecAsync(int count, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Task wait;
                lock (this.gate)
                {
                    if (this.specs.Count(s => s.CurrentTaskId is not null) >= count)
                    {
                        return;
                    }

                    wait = this.signal.Task;
                }

                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"scheduled specs < {count}");
                }

                await wait.WaitAsync(remaining);
            }
        }
    }

    private sealed class RecordingLifecycleSink : IScheduleLifecycleSink
    {
        private readonly object gate = new();
        private readonly List<ScheduleLifecycleEvent> events = [];
        private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ScheduleLifecycleEvent> Events { get { lock (this.gate) return this.events.ToList(); } }

        public ValueTask PublishAsync(ScheduleLifecycleEvent value, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource wake;
            lock (this.gate)
            {
                this.events.Add(value);
                wake = this.signal;
                this.signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            wake.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Task wait;
                lock (this.gate)
                {
                    if (this.events.Count >= count)
                    {
                        return;
                    }

                    wait = this.signal.Task;
                }

                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"lifecycle events < {count}");
                }

                await wait.WaitAsync(remaining);
            }
        }
    }

    private sealed class FakeStreamProgressSink : IStreamProgressSink
    {
        public void OnFirstToken(long latencyMs) { }

        public void OnChunk(int totalChunks, int totalChars, long elapsedMs) { }

        public void OnCompleted(int totalChunks, int totalChars, long elapsedMs, string? stopReason) { }
    }

    private sealed class MarkerTool : ITool
    {
        public const string ToolName = "marker_probe";

        public string Name => ToolName;

        public string Description => "marker";

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult(string.Empty));
    }

    private sealed class FakeScheduleRuntimeHandle(TaskManager tasks) : IScheduleRuntimeHandle
    {
        public int StartCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool TaskManagerLiveAtDispose { get; private set; }

        public Exception? StartError { get; init; }

        public TaskCompletionSource? StartGate { get; init; }

        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            this.StartCount++;
            this.StartEntered.TrySetResult();
            if (this.StartGate is not null)
            {
                await this.StartGate.Task.WaitAsync(cancellationToken);
            }

            if (this.StartError is not null)
            {
                throw this.StartError;
            }
        }

        public bool TryGetState(string scheduleId, out ScheduleRuntimeState state)
        {
            state = new ScheduleRuntimeState(ScheduleRuntimeStatus.Idle, null);
            return false;
        }

        public IReadOnlyList<ScheduleRuntimeSnapshot> GetSnapshot() => [];

        public ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            try
            {
                var probe = tasks.Register(TaskKind.Subagent, "dispose-order-probe", parentTaskId: null);
                tasks.Complete(probe.Id, null);
                this.TaskManagerLiveAtDispose = true;
            }
            catch (InvalidOperationException)
            {
                this.TaskManagerLiveAtDispose = false;
            }

            return ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* best-effort */ }
    }
}
