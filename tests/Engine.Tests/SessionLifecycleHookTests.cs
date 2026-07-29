using System.Text.Json;
using System.Threading;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Agent.Tasks;
using Coda.Sdk;
using Engine.Tests.TestSupport;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using static Engine.Tests.TestSupport.CredentialFixtures;
using static Engine.Tests.TestSupport.SseTestHandler;

namespace Engine.Tests;

/// <summary>
/// TDD tests for Phase 2 of the agent-hooks system: session lifecycle events
/// (SessionStart, SessionEnd, Notification). Tests cover bus-level payload/merge
/// logic, policy defaults, and session-level wiring in CodaSession.
/// </summary>
public sealed class SessionLifecycleHookTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_hooks_p2_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { }
    }

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

    private SessionOptions Options(PermissionMode mode = PermissionMode.BypassPermissions) => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = mode,
    };

    /// <summary>Executor that immediately returns a configurable result.</summary>
    private sealed class DelegateExecutor(
        Func<string, string, CancellationToken, Task<(int ExitCode, string Stdout, string Stderr)>> fn)
        : IHookExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct) => fn(command, payload, ct);
    }

    /// <summary>Records every (event, payload) call for later assertion.</summary>
    private sealed class CapturingExecutor : IHookExecutor
    {
        private readonly Func<string, (int ExitCode, string Stdout)> responseForEvent;
        public List<(string Event, string Payload)> Calls { get; } = [];

        public CapturingExecutor(Func<string, (int ExitCode, string Stdout)>? responseForEvent = null)
        {
            this.responseForEvent = responseForEvent ?? (_ => (0, "{}"));
        }

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct)
        {
            var doc = JsonDocument.Parse(payload);
            var eventName = doc.RootElement.GetProperty("event").GetString() ?? "";
            this.Calls.Add((eventName, payload));
            var (code, stdout) = this.responseForEvent(eventName);
            return Task.FromResult((code, stdout, string.Empty));
        }
    }

    /// <summary>
    /// IAgentLoopFactory that records the TurnShape passed to each RunAsync call.
    /// </summary>
    private sealed class CapturingLoopFactory : IAgentLoopFactory
    {
        public List<TurnShape?> CapturedShapes { get; } = [];

        public IAgentLoop Create(AgentLoopSpec spec) => new CapturingLoop(this);

        private sealed class CapturingLoop(CapturingLoopFactory owner) : IAgentLoop
        {
            public GoalStatus? LastGoalStatus => null;

            public Task RunAsync(
                List<ChatMessage> history,
                IAgentSink sink,
                CancellationToken cancellationToken = default,
                TurnShape? shape = null)
            {
                owner.CapturedShapes.Add(shape);
                // Add a minimal assistant message so history is consistent.
                history.Add(new ChatMessage(ChatRole.Assistant, [new TextBlock("ok")]));
                return Task.CompletedTask;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Test 1 — Policy defaults
    // -------------------------------------------------------------------------

    [Fact]
    public void PolicyDefaults_SessionStart_Has10s_FailOpen()
    {
        var policy = HookEventPolicy.Get("SessionStart");
        Assert.Equal(10, policy.TimeoutSeconds);
        Assert.True(policy.FailOpen);
    }

    [Fact]
    public void PolicyDefaults_SessionEnd_Has2s_FailOpen()
    {
        var policy = HookEventPolicy.Get("SessionEnd");
        Assert.Equal(2, policy.TimeoutSeconds);
        Assert.True(policy.FailOpen);
    }

    [Fact]
    public void PolicyDefaults_Notification_Has10s_FailOpen()
    {
        var policy = HookEventPolicy.Get("Notification");
        Assert.Equal(10, policy.TimeoutSeconds);
        Assert.True(policy.FailOpen);
    }

    // -------------------------------------------------------------------------
    // Test 2 — SessionStart payload shape and source="resume"
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SessionStart_Payload_ContainsAllEnvelopeAndEventFields()
    {
        var captured = new List<string>();
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            captured.Add(payload);
            return Task.FromResult((0, "{}", string.Empty));
        });

        var context = new HookContext("sess-42", "/work");
        var bus = new HookBus(
            [new UserHook("SessionStart", "cmd")],
            executor,
            context);

        await bus.RunSessionStartAsync(
            "resume",
            "claude-opus-5",
            "bypassPermissions",
            "/work/.coda/sessions/sess-42.json",
            "sess-prev",
            CancellationToken.None);

        Assert.Single(captured);
        var doc = JsonDocument.Parse(captured[0]);
        var root = doc.RootElement;

        Assert.Equal("SessionStart", root.GetProperty("event").GetString());
        Assert.Equal("sess-42", root.GetProperty("sessionId").GetString());
        Assert.Equal("/work", root.GetProperty("cwd").GetString());
        Assert.Equal("resume", root.GetProperty("source").GetString());
        Assert.Equal("claude-opus-5", root.GetProperty("model").GetString());
        Assert.Equal("bypassPermissions", root.GetProperty("permissionMode").GetString());
        Assert.Equal("/work/.coda/sessions/sess-42.json", root.GetProperty("transcriptPath").GetString());
        Assert.Equal("sess-prev", root.GetProperty("resumedFrom").GetString());
    }

    [Fact]
    public async Task SessionStart_Source_IsResume_AfterResume()
    {
        var sources = new List<string>();
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            sources.Add(doc.RootElement.GetProperty("source").GetString() ?? "");
            return Task.FromResult((0, "{}", string.Empty));
        });

        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("SessionStart", "cmd")],
            executor,
            context: null);

        using var session = new CodaSession(SignedInClaude(), this.Options(), httpClient: http, userHookRunnerOverride: runner);
        session.Resume("resumed-id", [], SessionMetadata.Empty);

        await session.InitializeAsync();

        Assert.Single(sources);
        Assert.Equal("resume", sources[0]);
    }

    // -------------------------------------------------------------------------
    // Test 3 — appendSystemPrompt applies to every turn
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SessionStart_AppendSystemPrompt_AppliesEveryTurn()
    {
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            var eventName = doc.RootElement.GetProperty("event").GetString();
            if (string.Equals(eventName, "SessionStart", StringComparison.Ordinal))
            {
                return Task.FromResult((0,
                    """{"hookSpecificOutput":{"appendSystemPrompt":"session-extra"}}""",
                    string.Empty));
            }

            return Task.FromResult((0, "{}", string.Empty));
        });

        var loopFactory = new CapturingLoopFactory();
        using var http = new HttpClient(new SseTestHandler(TextTurn, TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("SessionStart", "cmd")],
            executor,
            context: null);

        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            agentLoopFactory: loopFactory,
            userHookRunnerOverride: runner);

        await session.InitializeAsync();

        await session.RunAsync("turn 1");
        await session.RunAsync("turn 2");

        Assert.Equal(2, loopFactory.CapturedShapes.Count);
        Assert.All(loopFactory.CapturedShapes, s =>
        {
            Assert.NotNull(s);
            Assert.Equal("session-extra", s!.AppendSystemPrompt);
        });
    }

    // -------------------------------------------------------------------------
    // Test 4 — additionalContext injected exactly once
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SessionStart_AdditionalContext_InjectedOnceBeforeFirstTurn()
    {
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            var eventName = doc.RootElement.GetProperty("event").GetString();
            if (string.Equals(eventName, "SessionStart", StringComparison.Ordinal))
            {
                return Task.FromResult((0,
                    """{"hookSpecificOutput":{"additionalContext":"CTX"}}""",
                    string.Empty));
            }

            return Task.FromResult((0, "{}", string.Empty));
        });

        var loopFactory = new CapturingLoopFactory();
        using var http = new HttpClient(new SseTestHandler(TextTurn, TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("SessionStart", "cmd")],
            executor,
            context: null);

        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            agentLoopFactory: loopFactory,
            userHookRunnerOverride: runner);

        await session.InitializeAsync();

        // Before the first turn, CTX is in history (injected in RunAsync).
        await session.RunAsync("first");

        var ctxCount1 = session.History.Count(m =>
            m.Role == ChatRole.User
            && m.Content is [TextBlock tb]
            && tb.Text == "CTX");

        await session.RunAsync("second");

        var ctxCount2 = session.History.Count(m =>
            m.Role == ChatRole.User
            && m.Content is [TextBlock tb]
            && tb.Text == "CTX");

        // Injected exactly once after both turns.
        Assert.Equal(1, ctxCount1);
        Assert.Equal(1, ctxCount2);

        // Appears before the first user prompt (at position 0).
        var historyList = session.History.ToList();
        var ctxIndex = historyList.FindIndex(m =>
            m.Role == ChatRole.User
            && m.Content is [TextBlock tb]
            && tb.Text == "CTX");
        var firstPromptIndex = historyList.FindIndex(m =>
            m.Role == ChatRole.User
            && m.Content is [TextBlock tb]
            && tb.Text == "first");
        Assert.True(ctxIndex < firstPromptIndex, "additionalContext must precede the first user prompt");
    }

    // -------------------------------------------------------------------------
    // Test 5 — initialUserMessage flows through UserPromptSubmit, no re-trigger
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SessionStart_InitialUserMessage_FlowsThroughUserPromptSubmit_NoRerigger()
    {
        var capturing = new CapturingExecutor(eventName => eventName switch
        {
            "SessionStart" => (0, """{"hookSpecificOutput":{"initialUserMessage":"init"}}"""),
            _ => (0, "{}"),
        });

        var loopFactory = new CapturingLoopFactory();
        using var http = new HttpClient(new SseTestHandler(TextTurn, TextTurn));
        var runner = new UserHookRunner(
            [
                new UserHook("SessionStart", "cmd"),
                new UserHook("UserPromptSubmit", "cmd"),
            ],
            capturing,
            context: null);

        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            agentLoopFactory: loopFactory,
            userHookRunnerOverride: runner);

        await session.InitializeAsync();
        await session.RunAsync("real");

        // SessionStart fires once.
        Assert.Equal(1, capturing.Calls.Count(c => c.Event == "SessionStart"));

        // UserPromptSubmit fires for "init" and "real" (2 turns).
        var upsCalls = capturing.Calls.Where(c => c.Event == "UserPromptSubmit").ToList();
        Assert.Equal(2, upsCalls.Count);

        var prompts = upsCalls
            .Select(c => JsonDocument.Parse(c.Payload).RootElement.GetProperty("prompt").GetString())
            .ToList();
        Assert.Contains("init", prompts);
        Assert.Contains("real", prompts);
    }

    // -------------------------------------------------------------------------
    // Test 6 — InitializeAsync idempotent, SessionStart fires once
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InitializeAsync_IsIdempotent_SessionStartFiresOnce()
    {
        var callCount = 0;
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.GetProperty("event").GetString() == "SessionStart")
            {
                callCount++;
            }

            return Task.FromResult((0, "{}", string.Empty));
        });

        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("SessionStart", "cmd")],
            executor,
            context: null);

        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            userHookRunnerOverride: runner);

        // Call InitializeAsync twice — hook must fire exactly once.
        await session.InitializeAsync();
        await session.InitializeAsync();

        Assert.Equal(1, callCount);
    }

    // -------------------------------------------------------------------------
    // Test 6b — Concurrent ApplySessionStartHookAsync calls: second caller must
    //           await the hook's completion (not skip past before outputs are set)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ApplySessionStartHookAsync_ConcurrentCalls_SecondCallerAwaitsOutputs()
    {
        // Finding 3: the Interlocked guard is set BEFORE the hook completes, so a
        // second concurrent caller returns early without waiting for outputs to be
        // applied.  After the fix (shared-task pattern), the second caller awaits
        // the same Task and only returns once outputs are set.
        var hookStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;

        var executor = new DelegateExecutor(async (_, _, _) =>
        {
            Interlocked.Increment(ref callCount);
            hookStarted.TrySetResult();
            await hookRelease.Task.ConfigureAwait(false);
            return (0, """{"hookSpecificOutput":{"appendSystemPrompt":"from-slow-hook"}}""", string.Empty);
        });

        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("SessionStart", "cmd")],
            executor,
            context: null);

        var loopFactory = new CapturingLoopFactory();
        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            agentLoopFactory: loopFactory,
            userHookRunnerOverride: runner);

        // Caller 1 enters the hook and blocks.
        var t1 = session.ApplySessionStartHookAsync(CancellationToken.None);

        // Wait until caller 1 is inside the (slow) hook body.
        await hookStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Caller 2 races in while the hook is still running.
        var t2 = session.ApplySessionStartHookAsync(CancellationToken.None);

        // With the current (buggy) code the Interlocked guard is set before the
        // await, so t2 returns immediately.  With the fix it awaits the same task.
        var t2CompletedBeforeRelease = t2.IsCompleted;

        // Release the blocking hook so the session can finish.
        hookRelease.TrySetResult();
        await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(5));

        // Hook must fire exactly once.
        Assert.Equal(1, callCount);

        // After the fix: t2 must NOT have completed before the hook was released,
        // proving it waited for the outputs rather than skipping past them.
        Assert.False(
            t2CompletedBeforeRelease,
            "Second concurrent caller returned before hook outputs were applied (guard set before await).");

        // And the outputs must be visible to both callers once both complete.
        await session.RunAsync("probe");
        Assert.Single(loopFactory.CapturedShapes);
        Assert.Equal("from-slow-hook", loopFactory.CapturedShapes[0]?.AppendSystemPrompt);
    }

    // -------------------------------------------------------------------------
    // Test 6c — source="scheduled" emitted for sessions created via SessionSource
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SessionStart_Source_IsScheduled_WhenSessionSourceIsScheduled()
    {
        var sources = new List<string>();
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.GetProperty("event").GetString() == "SessionStart")
            {
                sources.Add(doc.RootElement.GetProperty("source").GetString() ?? "");
            }

            return Task.FromResult((0, "{}", string.Empty));
        });

        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("SessionStart", "cmd")],
            executor,
            context: null);

        // Session created with SessionSource = "scheduled" (e.g. from ScheduledAgentHost).
        var opts = this.Options() with { SessionSource = "scheduled" };
        using var session = new CodaSession(
            SignedInClaude(),
            opts,
            httpClient: http,
            userHookRunnerOverride: runner);

        await session.InitializeAsync();

        Assert.Single(sources);
        Assert.Equal("scheduled", sources[0]);
    }

    [Fact]
    public async Task SessionStart_Source_IsNew_WhenSessionSourceIsNull()
    {
        var sources = new List<string>();
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.GetProperty("event").GetString() == "SessionStart")
            {
                sources.Add(doc.RootElement.GetProperty("source").GetString() ?? "");
            }

            return Task.FromResult((0, "{}", string.Empty));
        });

        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("SessionStart", "cmd")],
            executor,
            context: null);

        // Default session (no SessionSource set) → source must be "new".
        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            userHookRunnerOverride: runner);

        await session.InitializeAsync();

        Assert.Single(sources);
        Assert.Equal("new", sources[0]);
    }

    // -------------------------------------------------------------------------
    // Test 7 — SessionEnd fires with correct default reason and exactly once
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SessionEnd_FiresExactlyOnce_EvenOnDoubleDispose_DefaultReasonIsExit()
    {
        var calls = new List<string>();
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.GetProperty("event").GetString() == "SessionEnd")
            {
                calls.Add(root.GetProperty("reason").GetString() ?? "");
            }

            return Task.FromResult((0, "{}", string.Empty));
        });

        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("SessionEnd", "cmd")],
            executor,
            context: null);

        var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            userHookRunnerOverride: runner);

        // Dispose twice — hook must fire exactly once with the default "exit" reason.
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Single(calls);
        Assert.Equal("exit", calls[0]);
    }

    [Fact]
    public async Task SessionEnd_ConcurrentDisposes_FireExactlyOnce()
    {
        var callCount = 0;
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.GetProperty("event").GetString() == "SessionEnd")
            {
                Interlocked.Increment(ref callCount);
            }

            return Task.FromResult((0, "{}", string.Empty));
        });

        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("SessionEnd", "cmd")],
            executor,
            context: null);

        var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            userHookRunnerOverride: runner);

        // Two concurrent disposes — SessionEnd must fire exactly once.
        await Task.WhenAll(
            Task.Run(() => session.DisposeAsync().AsTask()),
            Task.Run(() => session.DisposeAsync().AsTask()));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task TriggerSessionEndAsync_FiresSessionEnd_WithCurrentReason()
    {
        // Tests the process-shutdown path: TriggerSessionEndAsync fires SessionEnd
        // exactly once with whatever reason was set before calling it.
        var reasons = new List<string>();
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.GetProperty("event").GetString() == "SessionEnd")
            {
                reasons.Add(root.GetProperty("reason").GetString() ?? "");
            }

            return Task.FromResult((0, "{}", string.Empty));
        });

        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("SessionEnd", "cmd")],
            executor,
            context: null);

        await using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            userHookRunnerOverride: runner);

        session.SetSessionEndReason("shutdown");
        await session.TriggerSessionEndAsync();

        // SessionEnd fired with the set reason.
        Assert.Single(reasons);
        Assert.Equal("shutdown", reasons[0]);
    }

    [Fact]
    public async Task TriggerSessionEndAsync_ThenDispose_SessionEndFiresOnce()
    {
        // When TriggerSessionEndAsync fires from the process-exit path AND the main
        // thread later disposes, SessionEnd still fires exactly once.
        var callCount = 0;
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.GetProperty("event").GetString() == "SessionEnd")
            {
                Interlocked.Increment(ref callCount);
            }

            return Task.FromResult((0, "{}", string.Empty));
        });

        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("SessionEnd", "cmd")],
            executor,
            context: null);

        var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            userHookRunnerOverride: runner);

        session.SetSessionEndReason("shutdown");
        await session.TriggerSessionEndAsync();   // process-exit path fires first
        await session.DisposeAsync();              // main-thread path fires second (no-op)

        Assert.Equal(1, callCount);
    }

    // -------------------------------------------------------------------------
    // Test 8 — SessionEnd slow hook does not hang shutdown
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SessionEnd_SlowHook_DoesNotHangShutdown()
    {
        var executor = new DelegateExecutor(async (_, _, ct) =>
        {
            // Hook takes 5 s, but the budget is 2 s.
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return (0, "{}", string.Empty);
        });

        using var http = new HttpClient(new SseTestHandler(MessageStopOnly));
        var runner = new UserHookRunner(
            [new UserHook("SessionEnd", "cmd")],
            executor,
            context: null);

        var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            userHookRunnerOverride: runner);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await session.DisposeAsync();
        sw.Stop();

        // 2 s hard ceiling + tighter headroom that catches a budget regression to ~3-4s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4),
            $"DisposeAsync took {sw.Elapsed.TotalSeconds:F1}s — expected < 4s despite 5s hook");
    }

    // -------------------------------------------------------------------------
    // Test 9 — SessionEnd throwing hook does not propagate
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SessionEnd_ThrowingHook_DoesNotPropagateException()
    {
        var executor = new DelegateExecutor((_, _, _) =>
            Task.FromException<(int, string, string)>(new InvalidOperationException("boom")));

        using var http = new HttpClient(new SseTestHandler(MessageStopOnly));
        var runner = new UserHookRunner(
            [new UserHook("SessionEnd", "cmd")],
            executor,
            context: null);

        var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            userHookRunnerOverride: runner);

        // Must not throw.
        var ex = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask());
        Assert.Null(ex);
    }

    // -------------------------------------------------------------------------
    // Test 10 — Notification fires for all three kinds
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Notification_Idle_FiresAfterSuccessfulTurn()
    {
        var notified = new List<string>();
        var tcs = new TaskCompletionSource();
        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.GetProperty("event").GetString() == "Notification")
            {
                notified.Add(root.GetProperty("kind").GetString() ?? "");
                tcs.TrySetResult();
            }

            return Task.FromResult((0, "{}", string.Empty));
        });

        var loopFactory = new CapturingLoopFactory();
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var runner = new UserHookRunner(
            [new UserHook("Notification", "cmd")],
            executor,
            context: null);

        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            agentLoopFactory: loopFactory,
            userHookRunnerOverride: runner);

        await session.RunAsync("hello");

        // Wait for the fire-and-forget idle notification to land.
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("idle", notified);
    }

    [Fact]
    public async Task Notification_TaskComplete_FiresForBackgroundTask()
    {
        string? notifiedKind = null;
        string? notifiedTaskId = null;
        var tcs = new TaskCompletionSource();

        var tasks = new TaskManager("test-session");
        tasks.NotificationCallback = (kind, taskId) =>
        {
            notifiedKind = kind;
            notifiedTaskId = taskId;
            tcs.TrySetResult();
            return Task.CompletedTask;
        };

        // Register and promote a task to background mode.
        var task = tasks.Register(TaskKind.Subagent, "bg work", parentTaskId: null, TaskExecutionMode.Background);

        tasks.Complete(task.Id, result: "done");

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("task-complete", notifiedKind);
        Assert.Equal(task.Id, notifiedTaskId);
    }

    [Fact]
    public async Task Notification_Approval_FiresBeforePermissionPrompt()
    {
        var notifiedKinds = new List<string>();
        var permissionCalled = new TaskCompletionSource();
        var notificationFired = new TaskCompletionSource();

        var executor = new DelegateExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.GetProperty("event").GetString() == "Notification")
            {
                notifiedKinds.Add(root.GetProperty("kind").GetString() ?? "");
                notificationFired.TrySetResult();
            }

            return Task.FromResult((0, "{}", string.Empty));
        });

        var runner = new UserHookRunner(
            [new UserHook("Notification", "cmd")],
            executor,
            context: null);

        // Non-readonly tool triggers the approval path in AgentLoop.
        var nonReadOnlyTool = new NonReadOnlyTool();
        var toolTurn = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu1", "write_file", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var endTurn = new[]
        {
            AssistantStreamEvent.Finished("end_turn"),
        };

        var loop = new AgentLoop(
            new ScriptedClient(toolTurn, endTurn),
            new ToolRegistry([nonReadOnlyTool]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            userHooks: runner);

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // Wait for the fire-and-forget notification.
        await notificationFired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("approval", notifiedKinds);
    }

    // -------------------------------------------------------------------------
    // Test 11 — No hooks configured → byte-identical behaviour
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NoHooks_NoChangesToBehavior()
    {
        // Session with NO hook runner.
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        using var session = new CodaSession(SignedInClaude(), this.Options(), httpClient: http);

        // InitializeAsync + RunAsync must complete normally.
        await session.InitializeAsync();
        var result = await session.RunAsync("hello");

        Assert.True(result.Success);
        Assert.Equal("hello world", result.FinalText);

        // History contains only real conversation messages (no synthetic additionalContext).
        Assert.DoesNotContain(session.History, m =>
            m.Role == ChatRole.User
            && m.Content is [TextBlock tb]
            && tb.Text == "CTX");

        // Dispose is clean.
        var disposeEx = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask());
        Assert.Null(disposeEx);
    }

    // -------------------------------------------------------------------------
    // Helpers for Test 10 approval
    // -------------------------------------------------------------------------

    private sealed class NonReadOnlyTool : ITool
    {
        public string Name => "write_file";
        public string Description => "writes a file";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => false;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
            => Task.FromResult(new ToolResult("written"));
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
    }

    private sealed class AllowAllPermissionPrompt : IPermissionPrompt
    {
        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;
        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var events = turns[Math.Min(this.turn++, turns.Length - 1)];
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }
}

// =========================================================================
// BUG-FIX: MINOR-2 — prompt/agent-type session lifecycle hooks silently no-op
// =========================================================================

/// <summary>
/// Regression tests for MINOR-2: the session-level <see cref="UserHookRunner"/> was
/// constructed without <c>promptHandler</c> or <c>agentHandler</c>, so a
/// <c>SessionStart</c> hook declared as <c>type: prompt</c> hit the null-handler branch
/// in <see cref="HookBus"/> and silently no-oped.
/// </summary>
public sealed partial class SessionLifecycleHookTests_PromptHandlerWiring : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_hooks_ph_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { }
    }

    private SessionOptions Options() => new()
    {
        ProviderId = LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    /// <summary>
    /// A fake <see cref="IHookHandler"/> that returns a pre-configured <see cref="HookOutput"/>
    /// without calling a real model.
    /// </summary>
    private sealed class FakePromptHandler(HookOutput output) : IHookHandler
    {
        public bool WasCalled { get; private set; }

        public Task<HookOutput> HandleAsync(UserHook hook, string payload, CancellationToken ct)
        {
            this.WasCalled = true;
            return Task.FromResult(output);
        }
    }

    /// <summary>
    /// A <c>prompt</c>-type <c>SessionStart</c> hook must fire and its
    /// <c>additionalContext</c> must be injected into the session history.
    ///
    /// <para>Before the fix: <c>sessionHookRunner</c> was built without <c>promptHandler</c>.
    /// The bus hit the null-handler branch and returned <c>HookOutput.NoOp</c>,
    /// so no synthetic additionalContext message appeared.</para>
    ///
    /// <para>After the fix: <c>sessionPromptHandlerOverride</c> (test seam) is wired into the
    /// runner at construction; the handler fires and <c>additionalContext</c> is injected
    /// before the first user turn.</para>
    /// </summary>
    [Fact]
    public async Task PromptType_SessionStart_hook_fires_and_injects_additionalContext()
    {
        var fakeHandler = new FakePromptHandler(new HookOutput
        {
            HookSpecificOutput = new System.Text.Json.Nodes.JsonObject
            {
                ["additionalContext"] = "injected-by-prompt-hook",
            },
        });

        var hookList = new List<UserHook>
        {
            new UserHook("SessionStart", Command: null, HandlerType: "prompt", HookPrompt: "always allow"),
        };

        var loopFactory = new CapturingLoopFactory2();
        using var http = new HttpClient(new SseTestHandler(TextTurn));

        // NOT using userHookRunnerOverride: exercises the real sessionHookRunner construction.
        // sessionPromptHandlerOverride is the minimal seam — only the handler is injected, not the whole runner.
        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: http,
            agentLoopFactory: loopFactory,
            hookList: hookList,
            sessionPromptHandlerOverride: fakeHandler);

        await session.RunAsync("first turn");

        Assert.True(fakeHandler.WasCalled, "prompt handler was never called — sessionHookRunner is missing promptHandler");

        var historyList = session.History.ToList();
        var ctxIdx = historyList.FindIndex(m =>
            m.Role == ChatRole.User
            && m.Content is [TextBlock tb]
            && tb.Text == "injected-by-prompt-hook");
        Assert.True(ctxIdx >= 0, "additionalContext was not injected into session history");

        var firstTurnIdx = historyList.FindIndex(m =>
            m.Role == ChatRole.User
            && m.Content is [TextBlock tb]
            && tb.Text == "first turn");
        Assert.True(ctxIdx < firstTurnIdx, "additionalContext must precede the first user turn");
    }

    // Minimal capturing loop factory scoped to this test class.
    private sealed class CapturingLoopFactory2 : IAgentLoopFactory
    {
        public IAgentLoop Create(AgentLoopSpec spec) => new CapturingLoop2();

        private sealed class CapturingLoop2 : IAgentLoop
        {
            public GoalStatus? LastGoalStatus => null;

            public Task RunAsync(
                List<ChatMessage> history,
                IAgentSink sink,
                CancellationToken cancellationToken = default,
                TurnShape? shape = null)
            {
                history.Add(new ChatMessage(ChatRole.Assistant, [new TextBlock("ok")]));
                return Task.CompletedTask;
            }
        }
    }
}
