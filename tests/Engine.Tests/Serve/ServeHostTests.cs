using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Agent.Scheduling;
using Coda.Agent.Tasks;
using Coda.JsonRpc;
using Coda.Agent.Settings;
using Coda.Sdk;
using Coda.Sdk.Scheduling;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Messages;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Microsoft.Extensions.Logging;
using DomainScheduleLifecycleEvent = Coda.Sdk.Scheduling.ScheduleLifecycleEvent;

namespace Engine.Tests.Serve;

/// <summary>
/// Integration tests for ServeHost over in-memory duplex streams.
/// The "orchestrator" side is driven by an JsonRpcConnection acting as the JSON-RPC client.
/// The "coda" side is a ServeHost with an HTTP-stubbed CodaSession.
/// No ConfigureAwait(false) in test files.
/// All awaits guarded by WaitAsync(5s).
/// </summary>
public sealed class ServeHostTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    // A valid 64-hex API key for the auth-gated schedule tests below.
    private const string ApiKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string workDir = Directory.CreateTempSubdirectory("serve_host_").FullName;

    // ── HTTP stub helpers ─────────────────────────────────────────────────────

    /// <summary>Serves the next canned SSE body per request.</summary>
    private sealed class SeqHandler(params string[] sseBodies) : HttpMessageHandler
    {
        private int index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = sseBodies[Math.Min(this.index, sseBodies.Length - 1)];
            this.index++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    /// <summary>
    /// An HTTP handler that blocks until a gate is released, then returns the body.
    /// Supports optional cancellation: if the request is cancelled, it throws OCE.
    /// </summary>
    private sealed class BlockingHandler(string body) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when <see cref="SendAsync"/> is entered (the turn reached the LLM call).</summary>
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => this.gate.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Entered.TrySetResult(true);
            await this.gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    /// <summary>Returns a fixed non-success status + JSON error body (e.g. a 400 model_not_supported).</summary>
    private sealed class ErrorHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string SseText(string text) =>
        $"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{text}\"}}}}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private static string SseToolUse(string toolId, string toolName, string inputJson) =>
        $"data: {{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{{\"type\":\"tool_use\",\"id\":\"{toolId}\",\"name\":\"{toolName}\"}}}}\n\n" +
        $"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":\"{EscapeJson(inputJson)}\"}}}}\n\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private static string SseMaxTokens(string text) =>
        $"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{text}\"}}}}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private static string EscapeJson(string s) => s.Replace("\"", "\\\"");

    private static CredentialManager SignedInClaude()
    {
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(store, [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        creds.StoreAsync(ClaudeAiProvider.Id, new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT"
        }).GetAwaiter().GetResult();
        return creds;
    }

    private SessionOptions BaseOptions() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.workDir,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    /// <summary>
    /// Builds a ServeHost factory that creates a CodaSession backed by the given HTTP handler.
    /// </summary>
    private Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> MakeFactory(
        HttpMessageHandler httpHandler,
        PermissionMode permissionMode = PermissionMode.BypassPermissions)
    {
        return (perm, question, plan) =>
        {
            var options = this.BaseOptions() with
            {
                InteractivePrompt = perm,
                UserQuestionPrompt = question,
                PlanApprover = plan,
                PermissionMode = permissionMode,
            };
            return new CodaSession(
                SignedInClaude(),
                options,
                httpClient: new HttpClient(httpHandler));
        };
    }

    /// <summary>
    /// Builds a factory whose CodaSession forces telemetry on (writing into the test's
    /// temp working dir, never <c>~/.coda/logs</c>) so <c>LogFilePath</c> is non-null.
    /// </summary>
    private Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> MakeTelemetryFactory(
        HttpMessageHandler httpHandler)
    {
        var logDir = Path.Combine(this.workDir, "logs");
        Directory.CreateDirectory(logDir);
        var telemetry = TelemetrySettings.Disabled with { Enabled = true, MinLevel = LogLevel.Information, DirectoryOverride = logDir };

        return (perm, question, plan) =>
        {
            var options = this.BaseOptions() with
            {
                InteractivePrompt = perm,
                UserQuestionPrompt = question,
                PlanApprover = plan,
                TelemetryOverride = telemetry,
            };
            return new CodaSession(
                SignedInClaude(),
                options,
                httpClient: new HttpClient(httpHandler));
        };
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_returns_telemetry_log_path_when_telemetry_on()
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeTelemetryFactory(new SeqHandler(SseText("hi")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var result = await orchestrator
            .SendRequestAsync(ServeMethods.Initialize, ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null)), CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.NotNull(result);
        var init = ServeJson.FromNode<InitializeResult>(result);
        Assert.NotNull(init);
        Assert.NotNull(init!.TelemetryLogPath);
        Assert.NotEmpty(init.TelemetryLogPath!);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    // Note: the "telemetry off → omitted" wire behavior is asserted deterministically at the
    // DTO level in ServeProtocolTests (InitializeResult_null_telemetry_log_path_omitted). An
    // integration variant here would be flaky because CodaSession merges the machine's
    // ~/.coda/settings.json + CODA_LOG_* env, which may enable telemetry on the dev/CI host.

    [Fact]
    public async Task Initialize_is_answered_even_when_it_arrives_during_a_slow_session_build()
    {
        // Regression for the serve startup race: the orchestrator sends `initialize`
        // the instant it connects, which lands while the (slow) session build is still
        // running — i.e. BEFORE handlers are registered if the read loop is live early.
        // The host must defer reading inbound messages until every handler exists, so
        // initialize gets a proper InitializeResult (not -32601 Method not found / hang).
        using var pair = new DuplexStreamPair();

        var buildStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = this.MakeFactory(new SeqHandler(SseText("hi")));

        // A factory that blocks (simulating the ~13s prod build: tools/teams/telemetry).
        // It blocks SYNCHRONOUSLY, so ServeHost.RunAsync — whose synchronous prefix builds
        // the session inline before its first await — does not return until the gate is
        // released. The test therefore must NOT await RunAsync on its own thread; it runs
        // RunAsync on a separate Task and releases the gate from yet another task, so the
        // test thread stays free to drive `initialize` into the slow-build window.
        Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> slowFactory = (perm, question, plan) =>
        {
            buildStarted.TrySetResult(true);
            // Block the build until the orchestrator has already sent initialize.
            releaseBuild.Task.GetAwaiter().GetResult();
            return inner(perm, question, plan);
        };

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, slowFactory);
        using var cts = new CancellationTokenSource();

        // Run the host on its own task so the synchronous, blocking session build does not
        // wedge the test thread (RunAsync only yields after the session is built).
        var hostTask = Task.Run(() => host.RunAsync(cts.Token));

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Wait until the slow build is in flight, then fire initialize into that window.
        await buildStarted.Task.WaitAsync(WaitTimeout);
        var initTask = orchestrator.SendRequestAsync(
            ServeMethods.Initialize,
            ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null)),
            CancellationToken.None);

        // Let the request land on the wire while the build is still blocked, then release
        // the build from a background task (never from the thread awaiting initTask).
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            releaseBuild.TrySetResult(true);
        });

        var result = await initTask.WaitAsync(WaitTimeout);
        Assert.NotNull(result);
        var init = ServeJson.FromNode<InitializeResult>(result);
        Assert.NotNull(init);
        Assert.Equal(ServeMethods.ProtocolVersion, init!.ProtocolVersion);
        Assert.NotEmpty(init.SessionId);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Initialize_returns_session_and_version()
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(new SeqHandler(SseText("hi")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var result = await orchestrator
            .SendRequestAsync(ServeMethods.Initialize, ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null)), CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.NotNull(result);
        var init = ServeJson.FromNode<InitializeResult>(result);
        Assert.NotNull(init);
        Assert.Equal(ServeMethods.ProtocolVersion, init!.ProtocolVersion);
        Assert.NotEmpty(init.SessionId);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Models_returns_resolved_list_with_source()
    {
        using var pair = new DuplexStreamPair();
        // /v1/models hits the stub (non-JSON) → live empty → catalog fallback.
        var factory = this.MakeFactory(new SeqHandler(SseText("hi")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var result = await orchestrator
            .SendRequestAsync(ServeMethods.Models, ServeJson.ToNode(new ModelsParams(false)), CancellationToken.None)
            .WaitAsync(WaitTimeout);

        var models = ServeJson.FromNode<ModelsResult>(result);
        Assert.NotNull(models);
        Assert.Equal("catalog", models!.Source);
        Assert.NotEmpty(models.Models);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Prompt_streams_text_and_completes()
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(new SeqHandler(SseText("hi there")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var deltas = new List<string>();
        var turnCompleteTcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);

        orchestrator.OnNotification(ServeMethods.EventAssistantText, node =>
        {
            var delta = node?["delta"]?.GetValue<string>();
            if (delta is not null)
            {
                lock (deltas) { deltas.Add(delta); }
            }
        });
        orchestrator.OnNotification(ServeMethods.EventTurnComplete, node =>
            turnCompleteTcs.TrySetResult(node));

        var promptResult = await orchestrator
            .SendRequestAsync(
                ServeMethods.Prompt,
                ServeJson.ToNode(new PromptParams { Text = "hello" }),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.NotNull(promptResult);
        var pr = ServeJson.FromNode<PromptResult>(promptResult);
        Assert.NotNull(pr);
        Assert.True(pr!.Ok);
        Assert.False(pr.Interrupted);

        await turnCompleteTcs.Task.WaitAsync(WaitTimeout);
        Assert.Equal("hi there", string.Join("", deltas));

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    // ── Disposal-leak / log-collision repro (FIX 2) ────────────────────────────
    // Runs TWO complete turn-running serve sessions back-to-back in the SAME process,
    // both writing telemetry into ONE shared log directory. This deterministically
    // exercised the bug: the first session opened its per-session log file (FileShare.Read,
    // i.e. no second writer), and because the file stem was coda-<yyyyMMdd-HHmmss>-<pid>
    // the second same-second/same-pid session resolved to the IDENTICAL filename. Its
    // FileMode.Create then threw IOException inside the CodaSession ctor, faulting
    // ServeHost.RunAsync's synchronous prefix BEFORE its read loop started — so the second
    // prompt was never read and the request hung forever (5s test timeout).
    //
    // The fix makes the log stem unique per session, so two same-process sessions never
    // collide. (The companion fix — ServeHost awaiting session.DisposeAsync with
    // cancel-first instead of a sync-over-async bridge — keeps disposal from blocking.)
    [Fact]
    public async Task Two_turn_running_sessions_in_one_process_do_not_collide_or_hang()
    {
        var logDir = Path.Combine(this.workDir, "repro-logs");
        Directory.CreateDirectory(logDir);

        // Two sessions sharing one log directory, started back-to-back (same second).
        await RunOneTurnAsync(this.workDir, logDir);
        await RunOneTurnAsync(this.workDir, logDir);
    }

    /// <summary>
    /// Runs a single complete prompt turn against a fresh ServeHost and tears it down,
    /// forcing telemetry ON into <paramref name="logDir"/> (never <c>~/.coda/logs</c>) so the
    /// per-session log file is actually opened. All awaits are bounded by
    /// <see cref="WaitTimeout"/> so a disposal hang or a faulted host surfaces as a fast
    /// failure rather than blocking the run.
    /// </summary>
    private static async Task RunOneTurnAsync(string workDir, string logDir)
    {
        using var pair = new DuplexStreamPair();

        var telemetry = TelemetrySettings.Disabled with
        {
            Enabled = true,
            MinLevel = LogLevel.Information,
            DirectoryOverride = logDir,
        };

        var creds = SignedInClaude();
        Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> factory =
            (perm, question, plan) =>
            {
                var options = new SessionOptions
                {
                    ProviderId = ClaudeAiProvider.Id,
                    Model = "claude-sonnet-4-6",
                    WorkingDirectory = workDir,
                    PermissionMode = PermissionMode.BypassPermissions,
                    InteractivePrompt = perm,
                    UserQuestionPrompt = question,
                    PlanApprover = plan,
                    TelemetryOverride = telemetry,
                };
                return new CodaSession(
                    creds,
                    options,
                    httpClient: new HttpClient(new SeqHandler(SseText("hi there"))));
            };

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var promptResult = await orchestrator
            .SendRequestAsync(
                ServeMethods.Prompt,
                ServeJson.ToNode(new PromptParams { Text = "hello" }),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        var pr = ServeJson.FromNode<PromptResult>(promptResult);
        Assert.NotNull(pr);
        Assert.True(pr!.Ok);

        cts.Cancel();
        // The host loop's finally disposes the session; this await must complete fast.
        await hostTask.WaitAsync(WaitTimeout);
    }

    [Fact]
    public async Task Prompt_while_busy_errors()
    {
        using var pair = new DuplexStreamPair();
        var blocker = new BlockingHandler(SseText("eventual"));
        var factory = this.MakeFactory(blocker);

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // First prompt — blocks until we release.
        var prompt1 = orchestrator.SendRequestAsync(
            ServeMethods.Prompt,
            ServeJson.ToNode(new PromptParams { Text = "first" }),
            CancellationToken.None);

        // Give the handler time to start.
        await Task.Delay(100);

        // Second prompt while first is running — must get an error response.
        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
            () => orchestrator
                .SendRequestAsync(
                    ServeMethods.Prompt,
                    ServeJson.ToNode(new PromptParams { Text = "second" }),
                    CancellationToken.None)
                .WaitAsync(WaitTimeout));

        Assert.Contains("busy", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Release the first prompt.
        blocker.Release();
        await prompt1.WaitAsync(WaitTimeout);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Interrupt_cancels_turn()
    {
        using var pair = new DuplexStreamPair();
        var blocker = new BlockingHandler(SseText("eventual"));
        var factory = this.MakeFactory(blocker);

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Start a prompt that blocks.
        var promptTask = orchestrator.SendRequestAsync(
            ServeMethods.Prompt,
            ServeJson.ToNode(new PromptParams { Text = "long work" }),
            CancellationToken.None);

        // Give the handler time to start.
        await Task.Delay(100);

        // Interrupt it.
        var interruptResult = await orchestrator
            .SendRequestAsync(ServeMethods.Interrupt, null, CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.NotNull(interruptResult);
        var ir = ServeJson.FromNode<InterruptResult>(interruptResult);
        Assert.NotNull(ir);
        Assert.True(ir!.Ok);

        // The blocked HTTP handler will remain blocked, but the CTS cancellation propagates.
        // The prompt task should resolve as interrupted.
        var promptResult = await promptTask.WaitAsync(WaitTimeout);
        Assert.NotNull(promptResult);
        var pr = ServeJson.FromNode<PromptResult>(promptResult);
        Assert.NotNull(pr);
        Assert.True(pr!.Interrupted);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Interrupt_immediately_after_prompt_is_not_lost()
    {
        // Races the interrupt against the turn's CTS publication (no delay). The
        // deferred-interrupt flag must ensure the turn is still cancelled — otherwise
        // the blocked HTTP handler would never return and the prompt would hang.
        using var pair = new DuplexStreamPair();
        var blocker = new BlockingHandler(SseText("eventual"));
        var factory = this.MakeFactory(blocker);

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Fire prompt then interrupt back-to-back with NO delay (maximize the race).
        var promptTask = orchestrator.SendRequestAsync(
            ServeMethods.Prompt,
            ServeJson.ToNode(new PromptParams { Text = "long work" }),
            CancellationToken.None);
        var interruptTask = orchestrator.SendRequestAsync(
            ServeMethods.Interrupt, null, CancellationToken.None);

        await interruptTask.WaitAsync(WaitTimeout);

        // The turn must unwind as interrupted within the timeout (no hang).
        var promptResult = await promptTask.WaitAsync(WaitTimeout);
        var pr = ServeJson.FromNode<PromptResult>(promptResult);
        Assert.NotNull(pr);
        Assert.True(pr!.Interrupted);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task History_returns_messages_after_prompt()
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(new SeqHandler(SseText("hi there")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Run a prompt to populate history.
        await orchestrator
            .SendRequestAsync(
                ServeMethods.Prompt,
                ServeJson.ToNode(new PromptParams { Text = "hello" }),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        // Request history.
        var historyNode = await orchestrator
            .SendRequestAsync(ServeMethods.History, null, CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.NotNull(historyNode);
        var history = ServeJson.FromNode<HistoryResult>(historyNode);
        Assert.NotNull(history);
        Assert.True(history!.Messages.Count >= 2); // at least user + assistant
        Assert.Contains(history.Messages, m => m.Role == "user" && m.Content.Contains("hello"));
        Assert.Contains(history.Messages, m => m.Role == "assistant");

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Permission_request_round_trips()
    {
        // Use list_dir (IsReadOnly=true) → no permission needed.
        // For a permission round-trip test we need a non-readonly tool call.
        // We use Default permission mode so the host sends request/permission to orchestrator.
        // The model SSE: first turn calls list_dir (read-only, auto-allowed), then text.
        // But to test permission we need a mutating tool. Use run_command with Default mode
        // so the permission prompt fires.
        using var pair = new DuplexStreamPair();

        // Turn 1: run_command (mutating) → requires permission.
        // Turn 2: final text after tool result.
        var runCommandSse =
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu1\",\"name\":\"run_command\"}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\\\"command\\\\\":\\\\\"echo hello\\\\\"}\"}}\n\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}\n\n" +
            "data: {\"type\":\"message_stop\"}\n\n";

        var factory = this.MakeFactory(
            new SeqHandler(runCommandSse, SseText("done")),
            PermissionMode.Default);

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Orchestrator answers permission requests with "allow".
        orchestrator.OnRequest(ServeMethods.RequestPermission, _ =>
            ServeJson.ToNode(new PermissionResponse(true)));

        var toolResultTcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.OnNotification(ServeMethods.EventToolResult, node =>
            toolResultTcs.TrySetResult(node));

        // Send prompt and wait.
        var promptResult = await orchestrator
            .SendRequestAsync(
                ServeMethods.Prompt,
                ServeJson.ToNode(new PromptParams { Text = "do work" }),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.NotNull(promptResult);
        var pr = ServeJson.FromNode<PromptResult>(promptResult);
        Assert.NotNull(pr);
        Assert.True(pr!.Ok);

        // Tool result must have been observed.
        var toolResultNode = await toolResultTcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(toolResultNode);
        Assert.Equal("run_command", toolResultNode!["toolName"]!.GetValue<string>());

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Prompt_provider_error_surfaces_reason()
    {
        // Simulate the incident: the provider rejects the model with HTTP 400 model_not_supported.
        // The turn must NOT silently fail with no reason — the error text must reach the
        // orchestrator both as an event/error notification and in the prompt result.
        using var pair = new DuplexStreamPair();
        var body = "{\"error\":{\"message\":\"model_not_supported\",\"code\":\"model_not_supported\"}}";
        var factory = this.MakeFactory(new ErrorHandler(HttpStatusCode.BadRequest, body));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var errorTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.OnNotification(ServeMethods.EventError, node =>
            errorTcs.TrySetResult(node?["message"]?.GetValue<string>()));

        var promptResult = await orchestrator
            .SendRequestAsync(
                ServeMethods.Prompt,
                ServeJson.ToNode(new PromptParams { Text = "hello" }),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.NotNull(promptResult);
        var pr = ServeJson.FromNode<PromptResult>(promptResult);
        Assert.NotNull(pr);
        Assert.False(pr!.Ok);
        Assert.False(pr.Interrupted);
        Assert.NotNull(pr.Error);
        Assert.Contains("model_not_supported", pr.Error!, StringComparison.OrdinalIgnoreCase);

        // The reason was also surfaced as an event/error notification.
        var errorMessage = await errorTcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(errorMessage);
        Assert.Contains("model_not_supported", errorMessage!, StringComparison.OrdinalIgnoreCase);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Prompt_max_tokens_truncation_emits_limit_reached_not_error()
    {
        // The fitbit-sync regression: a max_tokens truncation is a recoverable soft stop, not a
        // fatal error. It must reach the orchestrator as event/limitReached (kind=max_tokens),
        // NOT event/error (which the bridge maps to a terminal Crashed state).
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(new SeqHandler(SseMaxTokens("partial output")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var limitTcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawError = false;
        orchestrator.OnNotification(ServeMethods.EventLimitReached, node => limitTcs.TrySetResult(node));
        orchestrator.OnNotification(ServeMethods.EventError, _ => sawError = true);

        var promptResult = await orchestrator
            .SendRequestAsync(
                ServeMethods.Prompt,
                ServeJson.ToNode(new PromptParams { Text = "hello" }),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.NotNull(promptResult);

        var limit = await limitTcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(limit);
        Assert.Equal("max_tokens", limit!["kind"]!.GetValue<string>());
        Assert.False(sawError, "max_tokens truncation must be a recoverable limit, not an event/error");

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Steer_when_no_turn_running_is_rejected_and_does_not_leak_into_next_turn()
    {
        // A steer is only valid for a turn actually in flight. With no turn running it must be rejected
        // (ok=false) and never enqueued, so it cannot leak into a later, unrelated turn's history.
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(new SeqHandler(SseText("ok")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var steerResult = await orchestrator
            .SendRequestAsync(
                ServeMethods.Steer,
                ServeJson.ToNode(new SteerParams("late steer for no turn")),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);
        Assert.NotNull(steerResult);
        var sr = ServeJson.FromNode<SteerResult>(steerResult);
        Assert.NotNull(sr);
        Assert.False(sr!.Ok); // rejected — no turn running

        // A subsequent prompt's history must NOT contain the rejected steer (no leak).
        var turnCompleteTcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.OnNotification(ServeMethods.EventTurnComplete, node => turnCompleteTcs.TrySetResult(node));

        await orchestrator
            .SendRequestAsync(ServeMethods.Prompt, ServeJson.ToNode(new PromptParams { Text = "hi" }), CancellationToken.None)
            .WaitAsync(WaitTimeout);
        await turnCompleteTcs.Task.WaitAsync(WaitTimeout);

        var historyResult = await orchestrator
            .SendRequestAsync(ServeMethods.History, null, CancellationToken.None)
            .WaitAsync(WaitTimeout);
        var history = ServeJson.FromNode<HistoryResult>(historyResult);
        Assert.NotNull(history);
        Assert.DoesNotContain(history!.Messages, m => m.Content.Contains("late steer for no turn"));

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Prompt_iteration_cap_emits_limit_reached_not_error()
    {
        // Every model call returns a tool_use, so with MaxIterations=1 the loop exits via the
        // iteration cap — which must surface as event/limitReached (kind=max_tool_iterations), NOT
        // event/error (the fitbit-sync "max 20 tool iterations" crash).
        using var pair = new DuplexStreamPair();
        Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> factory =
            (perm, question, plan) =>
            {
                var options = this.BaseOptions() with
                {
                    InteractivePrompt = perm,
                    UserQuestionPrompt = question,
                    PlanApprover = plan,
                    MaxIterations = 1,
                };
                return new CodaSession(
                    SignedInClaude(),
                    options,
                    httpClient: new HttpClient(new SeqHandler(SseToolUse("tu_1", "read_file", "{}"))));
            };

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var limitTcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawError = false;
        orchestrator.OnNotification(ServeMethods.EventLimitReached, node => limitTcs.TrySetResult(node));
        orchestrator.OnNotification(ServeMethods.EventError, _ => sawError = true);

        await orchestrator
            .SendRequestAsync(ServeMethods.Prompt, ServeJson.ToNode(new PromptParams { Text = "hello" }), CancellationToken.None)
            .WaitAsync(WaitTimeout);

        var limit = await limitTcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(limit);
        Assert.Equal("max_tool_iterations", limit!["kind"]!.GetValue<string>());
        Assert.False(sawError, "iteration cap must be a recoverable limit, not an event/error");

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Prompt_hung_llm_call_surfaces_clear_error_via_client_timeout()
    {
        // The timeout now lives in the LLM client (an HTTP response-headers / stream-idle
        // bound), NOT in a ServeHost turn-level watchdog. Simulate an unusable provider
        // whose first network call hangs: the client's headers timeout (set tiny here)
        // trips and the turn surfaces a clear error to the orchestrator instead of hanging
        // until the 10-minute HTTP client timeout.
        using var pair = new DuplexStreamPair();
        var blocker = new BlockingHandler(SseText("never")); // never released → headers never arrive.

        Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> factory =
            (perm, question, plan) =>
            {
                var options = this.BaseOptions() with
                {
                    InteractivePrompt = perm,
                    UserQuestionPrompt = question,
                    PlanApprover = plan,
                    // Tiny header bound so a real hang trips almost immediately.
                    LlmHttpTimeoutOverride = new LlmHttpTimeoutConfig(
                        ResponseHeadersTimeout: TimeSpan.FromMilliseconds(200),
                        StreamIdleTimeout: TimeSpan.FromMilliseconds(200)),
                };
                return new CodaSession(SignedInClaude(), options, httpClient: new HttpClient(blocker));
            };

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var errorTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.OnNotification(ServeMethods.EventError, node =>
            errorTcs.TrySetResult(node?["message"]?.GetValue<string>()));

        var promptResult = await orchestrator
            .SendRequestAsync(
                ServeMethods.Prompt,
                ServeJson.ToNode(new PromptParams { Text = "hello" }),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        // The turn fails (not interrupted) with a clear, timeout-shaped reason — fast.
        Assert.NotNull(promptResult);
        var pr = ServeJson.FromNode<PromptResult>(promptResult);
        Assert.NotNull(pr);
        Assert.False(pr!.Ok);
        Assert.False(pr.Interrupted);
        Assert.NotNull(pr.Error);
        Assert.Contains("LLM response headers not received", pr.Error!, StringComparison.OrdinalIgnoreCase);

        // The same reason is surfaced as an event/error notification.
        var errorMessage = await errorTcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(errorMessage);
        Assert.Contains("LLM response headers not received", errorMessage!, StringComparison.OrdinalIgnoreCase);

        blocker.Release(); // let the blocked handler unwind.

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    // ── Schedule runtime lifecycle over serve (Task 9) ──────────────────────────
    //
    // These exercise the serve enablement path: ServeHost sets the schedule-lifecycle sink,
    // starts the JSON-RPC read loop BEFORE driving CodaSession.InitializeAsync (so a scheduled
    // firing can round-trip a server→client request), and gates initialize/prompt on a shared
    // initialization task. A fake IScheduleRuntimeHandle (ProbeHandle) stands in for the real
    // ScheduleRuntime so the wiring can be asserted deterministically without real schedules.

    /// <summary>Captures the CodaSession and its injected fake runtime handle from the factory closure.</summary>
    private sealed class ScheduleProbe
    {
        public CodaSession? Session;
        public ProbeHandle? Handle;
    }

    /// <summary>
    /// Factory whose CodaSession enables the schedule runtime and injects a <see cref="ProbeHandle"/>
    /// via <c>ScheduleRuntimeFactoryForTest</c>. The handle is built by <paramref name="configure"/>,
    /// which receives the runtime's wired lifecycle sink (a session forwarder that ends at the
    /// ServeHost's <see cref="WireScheduleLifecycleSink"/>) and the serve permission prompt.
    /// </summary>
    private Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> MakeScheduleFactory(
        ScheduleProbe probe,
        HttpMessageHandler httpHandler,
        Func<IScheduleLifecycleSink, IPermissionPrompt, ProbeHandle> configure)
    {
        return (perm, question, plan) =>
        {
            var options = this.BaseOptions() with
            {
                InteractivePrompt = perm,
                UserQuestionPrompt = question,
                PlanApprover = plan,
                EnableScheduleRuntime = true,
            };
            var session = new CodaSession(
                SignedInClaude(),
                options,
                httpClient: new HttpClient(httpHandler));

            session.ScheduleRuntimeFactoryForTest = (schedules, tasks, host, lifecycle, tp, logger) =>
            {
                var handle = configure(lifecycle, perm);
                probe.Handle = handle;
                return handle;
            };

            probe.Session = session;
            return session;
        };
    }

    private static async Task WaitForStartEnteredAsync(ScheduleProbe probe, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (probe.Handle is null || !probe.Handle.StartEntered.Task.IsCompleted)
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException("schedule runtime StartAsync was never entered");
            }

            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Read_loop_is_live_during_init_so_a_scheduled_permission_request_round_trips()
    {
        // Proves ServeHost starts the connection's read loop BEFORE driving InitializeAsync: the
        // fake runtime's StartAsync (invoked from InitializeAsync) issues a server→client
        // permission request and requires its response. It also emits a "started" lifecycle
        // notification while the host is otherwise idle (no prompt in flight).
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = this.MakeScheduleFactory(
            probe,
            new SeqHandler(SseText("hi")),
            (lifecycle, perm) => new ProbeHandle(lifecycle, permission: perm, startGate: startGate));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        var lifecycle = new LifecycleCollector();
        lifecycle.Register(orchestrator);
        orchestrator.OnRequest(ServeMethods.RequestPermission, _ => ServeJson.ToNode(new PermissionResponse(true)));

        // Wait until the runtime start is entered (and blocked), so the orchestrator handlers are
        // registered before the permission request is issued, then release the start.
        await WaitForStartEnteredAsync(probe, WaitTimeout);
        startGate.TrySetResult();

        var started = await lifecycle.WaitForStateAsync("started", WaitTimeout);
        Assert.Equal("def-serve", started["definitionId"]!.GetValue<string>());
        Assert.True(probe.Handle!.PermissionGranted);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Initialize_waits_for_shared_initialization_before_returning()
    {
        // initialize must await the SAME shared initialization task the host drives after Start —
        // not return before the schedule runtime (and LSP) are up.
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = this.MakeScheduleFactory(
            probe,
            new SeqHandler(SseText("hi")),
            (lifecycle, perm) => new ProbeHandle(lifecycle, startGate: startGate));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        await WaitForStartEnteredAsync(probe, WaitTimeout);

        var initTask = orchestrator.SendRequestAsync(
            ServeMethods.Initialize,
            ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null)),
            CancellationToken.None);

        // Initialization is still blocked → initialize must not have returned yet.
        await Task.Delay(150);
        Assert.False(initTask.IsCompleted, "initialize returned before initialization completed");

        startGate.TrySetResult();
        var result = await initTask.WaitAsync(WaitTimeout);
        var init = ServeJson.FromNode<InitializeResult>(result);
        Assert.NotNull(init);
        Assert.Equal(ServeMethods.ProtocolVersion, init!.ProtocolVersion);
        Assert.NotEmpty(init.SessionId);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Concurrent_initialize_and_prompt_start_the_runtime_exactly_once()
    {
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = this.MakeScheduleFactory(
            probe,
            new SeqHandler(SseText("done")),
            (lifecycle, perm) => new ProbeHandle(lifecycle, startGate: startGate));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        await WaitForStartEnteredAsync(probe, WaitTimeout);

        // Both handlers arrive while init is blocked; both await the SAME shared task. Neither drives
        // a second InitializeAsync.
        var initTask = orchestrator.SendRequestAsync(
            ServeMethods.Initialize,
            ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null)),
            CancellationToken.None);
        var promptTask = orchestrator.SendRequestAsync(
            ServeMethods.Prompt,
            ServeJson.ToNode(new PromptParams { Text = "hi" }),
            CancellationToken.None);

        await Task.Delay(150);

        // Both await the shared gate: neither returns while initialization is still blocked.
        Assert.False(initTask.IsCompleted, "initialize returned before initialization completed");
        Assert.False(promptTask.IsCompleted, "prompt ran before initialization completed");

        startGate.TrySetResult();

        await Task.WhenAll(initTask, promptTask).WaitAsync(WaitTimeout);

        Assert.Equal(1, probe.Handle!.StartCount);
        Assert.Equal(1, probe.Session!.ScheduleRuntimeCreationCountForTest);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Initialization_failure_is_reported_to_initialize_and_leaves_no_running_runtime()
    {
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var factory = this.MakeScheduleFactory(
            probe,
            new SeqHandler(SseText("hi")),
            (lifecycle, perm) => new ProbeHandle(lifecycle, startError: new InvalidOperationException("start boom")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // The shared gate faults with the runtime start failure → initialize gets a JSON-RPC error.
        await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.SendRequestAsync(
                ServeMethods.Initialize,
                ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null)),
                CancellationToken.None)
            .WaitAsync(WaitTimeout));

        // The half-started runtime was disposed and ownership cleared (CodaSession Task 7 cleanup);
        // no leaked runtime remains.
        await WaitForStartEnteredAsync(probe, WaitTimeout);
        Assert.Equal(1, probe.Handle!.DisposeCount);
        Assert.Null(probe.Session!.ScheduleRuntimeForTest);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Schedule_lifecycle_event_is_delivered_while_a_prompt_turn_is_running()
    {
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new BlockingHandler(SseText("done"));
        var factory = this.MakeScheduleFactory(
            probe,
            blocking,
            (lifecycle, perm) => new ProbeHandle(lifecycle, startGate: startGate));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        var lifecycle = new LifecycleCollector();
        lifecycle.Register(orchestrator);

        // Bring the runtime up deterministically (orchestrator ready before the "started" publish).
        await WaitForStartEnteredAsync(probe, WaitTimeout);
        startGate.TrySetResult();
        await lifecycle.WaitForStateAsync("started", WaitTimeout);

        // Start a prompt; the blocking HTTP handler keeps the turn in-flight.
        var promptTask = orchestrator.SendRequestAsync(
            ServeMethods.Prompt,
            ServeJson.ToNode(new PromptParams { Text = "hi" }),
            CancellationToken.None);

        await blocking.Entered.Task.WaitAsync(WaitTimeout);
        Assert.False(promptTask.IsCompleted);

        // Emit a lifecycle event WHILE the turn runs; it must still reach the orchestrator (the
        // connection's write lock serializes it against the streaming turn).
        await probe.Handle!.Lifecycle.PublishAsync(new DomainScheduleLifecycleEvent(
                "def-serve", "nightly", "task-serve", ScheduleLifecycleKind.Completed, DateTimeOffset.UtcNow, "ok"))
            .AsTask().WaitAsync(WaitTimeout);

        var completed = await lifecycle.WaitForStateAsync("completed", WaitTimeout);
        Assert.Equal("ok", completed["summary"]!.GetValue<string>());

        blocking.Release();
        await promptTask.WaitAsync(WaitTimeout);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Shutdown_disposes_runtime_and_session_before_the_connection()
    {
        // On shutdown the host cancels the turn, awaits CodaSession.DisposeAsync (which stops the
        // runtime strictly before the task manager), THEN disposes the connection. A lifecycle event
        // published from the runtime's disposal therefore still reaches the orchestrator — proving
        // the connection outlives the runtime/session teardown (no event write after connection
        // disposal).
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = this.MakeScheduleFactory(
            probe,
            new SeqHandler(SseText("hi")),
            (lifecycle, perm) => new ProbeHandle(lifecycle, startGate: startGate) { PublishStoppedOnDispose = true });

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        var lifecycle = new LifecycleCollector();
        lifecycle.Register(orchestrator);

        await WaitForStartEnteredAsync(probe, WaitTimeout);
        startGate.TrySetResult();
        await lifecycle.WaitForStateAsync("started", WaitTimeout);

        cts.Cancel();

        // The disposal-time "stopped" event flushed to the orchestrator before the connection closed.
        var stopped = await lifecycle.WaitForStateAsync("stopped", WaitTimeout);
        Assert.Equal("def-serve", stopped["definitionId"]!.GetValue<string>());

        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
        Assert.Equal(1, probe.Handle!.DisposeCount);
    }

    // ── Auth-gated schedule runtime over serve (Task 9 security finding) ─────────
    //
    // In API-key mode the shared initialization must NOT drive CodaSession.InitializeAsync (which
    // starts the schedule runtime) until a valid `initialize` authenticates. An unauthenticated
    // connected peer must never receive schedule lifecycle/stream/tool events, nor be asked to
    // answer a server→client permission/question/plan request, before presenting the API key.

    [Fact]
    public async Task ApiKey_mode_emits_no_schedule_events_or_requests_before_initialize()
    {
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var factory = this.MakeScheduleFactory(
            probe,
            new SeqHandler(SseText("hi")),
            (lifecycle, perm) => new ProbeHandle(lifecycle, permission: perm));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory, ApiKey);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        var lifecycle = new LifecycleCollector();
        lifecycle.Register(orchestrator);
        var sawPermission = false;
        orchestrator.OnRequest(ServeMethods.RequestPermission, _ =>
        {
            sawPermission = true;
            return ServeJson.ToNode(new PermissionResponse(true));
        });

        // No `initialize` is sent. Give the host ample time to (erroneously) drive initialization.
        await Task.Delay(300);

        Assert.Null(probe.Handle);            // schedule runtime never created / started.
        Assert.Equal(0, lifecycle.Count);     // no schedule lifecycle notification reached the peer.
        Assert.False(sawPermission);          // no server→client permission request was issued.

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Invalid_initialize_key_is_unauthorized_and_starts_no_runtime()
    {
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var factory = this.MakeScheduleFactory(
            probe,
            new SeqHandler(SseText("hi")),
            (lifecycle, perm) => new ProbeHandle(lifecycle));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory, ApiKey);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(() => orchestrator.SendRequestAsync(
                ServeMethods.Initialize,
                ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null, "wrong-but-long-enough-0123456789abcdef0123456789abcdef0123456789")),
                CancellationToken.None)
            .WaitAsync(WaitTimeout));
        Assert.Equal(-32001, ex.Code);

        // An unauthorized initialize must not start the schedule runtime.
        await Task.Delay(200);
        Assert.Null(probe.Handle);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Valid_initialize_starts_runtime_once_then_lifecycle_and_permission_flow()
    {
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = this.MakeScheduleFactory(
            probe,
            new SeqHandler(SseText("hi")),
            (lifecycle, perm) => new ProbeHandle(lifecycle, permission: perm, startGate: startGate));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory, ApiKey);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        var lifecycle = new LifecycleCollector();
        lifecycle.Register(orchestrator);
        orchestrator.OnRequest(ServeMethods.RequestPermission, _ => ServeJson.ToNode(new PermissionResponse(true)));

        // Before authentication the runtime must not have been created.
        await Task.Delay(200);
        Assert.Null(probe.Handle);

        // A valid initialize authenticates and drives initialization exactly once.
        var initTask = orchestrator.SendRequestAsync(
            ServeMethods.Initialize,
            ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null, ApiKey)),
            CancellationToken.None);

        await WaitForStartEnteredAsync(probe, WaitTimeout);
        startGate.TrySetResult();

        var result = await initTask.WaitAsync(WaitTimeout);
        Assert.NotNull(ServeJson.FromNode<InitializeResult>(result));

        var started = await lifecycle.WaitForStateAsync("started", WaitTimeout);
        Assert.Equal("def-serve", started["definitionId"]!.GetValue<string>());
        Assert.True(probe.Handle!.PermissionGranted);
        Assert.Equal(1, probe.Handle!.StartCount);
        Assert.Equal(1, probe.Session!.ScheduleRuntimeCreationCountForTest);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Concurrent_valid_initialize_requests_start_the_runtime_once()
    {
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = this.MakeScheduleFactory(
            probe,
            new SeqHandler(SseText("hi")),
            (lifecycle, perm) => new ProbeHandle(lifecycle, startGate: startGate));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory, ApiKey);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // No runtime before authentication.
        await Task.Delay(150);
        Assert.Null(probe.Handle);

        var init1 = orchestrator.SendRequestAsync(
            ServeMethods.Initialize,
            ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null, ApiKey)),
            CancellationToken.None);
        var init2 = orchestrator.SendRequestAsync(
            ServeMethods.Initialize,
            ServeJson.ToNode(new InitializeParams(ServeMethods.ProtocolVersion, null, ApiKey)),
            CancellationToken.None);

        await WaitForStartEnteredAsync(probe, WaitTimeout);
        await Task.Delay(100);       // give a would-be second drive a chance to race.
        startGate.TrySetResult();

        await Task.WhenAll(init1, init2).WaitAsync(WaitTimeout);

        Assert.Equal(1, probe.Handle!.StartCount);
        Assert.Equal(1, probe.Session!.ScheduleRuntimeCreationCountForTest);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Shutdown_before_authentication_completes_promptly_and_starts_no_runtime()
    {
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var factory = this.MakeScheduleFactory(
            probe,
            new SeqHandler(SseText("hi")),
            (lifecycle, perm) => new ProbeHandle(lifecycle));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory, ApiKey);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Never authenticate. The host must not have driven initialization.
        await Task.Delay(150);
        Assert.Null(probe.Handle);

        // Shutdown must complete promptly and not hang awaiting an initialization that never started.
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await hostTask.WaitAsync(WaitTimeout);
        sw.Stop();

        Assert.True(sw.Elapsed < WaitTimeout, $"shutdown took {sw.Elapsed}");
        Assert.Null(probe.Handle);   // runtime never started.
    }

    [Fact]
    public async Task Stdio_drives_initialization_immediately_after_start()
    {
        // Regression: with no expectedApiKey (stdio), the shared initialization is driven immediately
        // after connection.Start — no `initialize` is required for an overdue schedule to run.
        using var pair = new DuplexStreamPair();
        var probe = new ScheduleProbe();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = this.MakeScheduleFactory(
            probe,
            new SeqHandler(SseText("hi")),
            (lifecycle, perm) => new ProbeHandle(lifecycle, startGate: startGate));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory); // no key.
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        var lifecycle = new LifecycleCollector();
        lifecycle.Register(orchestrator);

        // Without sending initialize, StartAsync is entered because stdio drives init at Start.
        await WaitForStartEnteredAsync(probe, WaitTimeout);
        startGate.TrySetResult();
        await lifecycle.WaitForStateAsync("started", WaitTimeout);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    /// <summary>Records every <c>event/scheduleLifecycle</c> notification and awaits a given state.</summary>
    private sealed class LifecycleCollector
    {
        private readonly object gate = new();
        private readonly List<JsonNode> events = [];

        public void Register(JsonRpcConnection conn) =>
            conn.OnNotification(ServeMethods.EventScheduleLifecycle, node =>
            {
                if (node is null)
                {
                    return;
                }

                lock (this.gate)
                {
                    this.events.Add(node);
                }
            });

        /// <summary>Number of schedule lifecycle notifications observed so far.</summary>
        public int Count { get { lock (this.gate) { return this.events.Count; } } }

        public async Task<JsonNode> WaitForStateAsync(string state, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                lock (this.gate)
                {
                    var match = this.events.FirstOrDefault(e => e["state"]!.GetValue<string>() == state);
                    if (match is not null)
                    {
                        return match;
                    }
                }

                if (sw.Elapsed > timeout)
                {
                    throw new TimeoutException($"no schedule lifecycle event with state '{state}'");
                }

                await Task.Delay(20);
            }
        }
    }

    /// <summary>
    /// A fake <see cref="IScheduleRuntimeHandle"/> substituted for the real ScheduleRuntime. Its
    /// <see cref="StartAsync"/> runs on the InitializeAsync path, so it can (optionally) block on a
    /// gate, round-trip a permission request through the live connection, publish a "started"
    /// lifecycle event, or fault the start.
    /// </summary>
    private sealed class ProbeHandle : IScheduleRuntimeHandle
    {
        private readonly IScheduleLifecycleSink lifecycle;
        private readonly IPermissionPrompt? permission;
        private int startCount;
        private int disposeCount;

        public ProbeHandle(
            IScheduleLifecycleSink lifecycle,
            IPermissionPrompt? permission = null,
            TaskCompletionSource? startGate = null,
            Exception? startError = null)
        {
            this.lifecycle = lifecycle;
            this.permission = permission;
            this.StartGate = startGate;
            this.StartError = startError;
        }

        public IScheduleLifecycleSink Lifecycle => this.lifecycle;

        public TaskCompletionSource? StartGate { get; }

        public Exception? StartError { get; }

        public bool PublishStoppedOnDispose { get; init; }

        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCount => Volatile.Read(ref this.startCount);

        public int DisposeCount => Volatile.Read(ref this.disposeCount);

        public bool PermissionGranted { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.startCount);
            this.StartEntered.TrySetResult();

            if (this.StartGate is not null)
            {
                await this.StartGate.Task.WaitAsync(cancellationToken);
            }

            if (this.StartError is not null)
            {
                throw this.StartError;
            }

            if (this.permission is not null)
            {
                this.PermissionGranted = await this.permission.RequestAsync(new ProbeTool(), "scheduled preview", cancellationToken);
            }

            await this.lifecycle.PublishAsync(
                new DomainScheduleLifecycleEvent("def-serve", "nightly", "task-serve", ScheduleLifecycleKind.Started, DateTimeOffset.UtcNow, null),
                cancellationToken);
        }

        public bool TryGetState(string scheduleId, out ScheduleRuntimeState state)
        {
            state = new ScheduleRuntimeState(ScheduleRuntimeStatus.Idle, null);
            return false;
        }

        public IReadOnlyList<ScheduleRuntimeSnapshot> GetSnapshot() => [];

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref this.disposeCount);
            if (this.PublishStoppedOnDispose)
            {
                await this.lifecycle.PublishAsync(new DomainScheduleLifecycleEvent(
                    "def-serve", "nightly", "task-serve", ScheduleLifecycleKind.Stopped, DateTimeOffset.UtcNow, null));
            }
        }
    }

    private sealed class ProbeTool : ITool
    {
        public string Name => "scheduled_probe";

        public string Description => string.Empty;

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => false;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    public void Dispose()
    {
        try { Directory.Delete(this.workDir, recursive: true); } catch { /* ignore */ }
    }
}
