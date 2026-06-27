using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.JsonRpc;
using Coda.Agent.Settings;
using Coda.Sdk;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Messages;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Microsoft.Extensions.Logging;

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

        public void Release() => this.gate.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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

    public void Dispose()
    {
        try { Directory.Delete(this.workDir, recursive: true); } catch { /* ignore */ }
    }
}
