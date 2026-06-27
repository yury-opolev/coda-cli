using System.Net;
using System.Text;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.JsonRpc;
using Coda.Sdk;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Messages;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests.Serve;

/// <summary>
/// Tests for the <c>session/setGoal</c> method and the <c>goalStatus</c> field on
/// the <c>session/prompt</c> result.
/// </summary>
public sealed class ServeHostGoalTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly string workDir = Directory.CreateTempSubdirectory("serve_goal_").FullName;

    // ── HTTP stub ─────────────────────────────────────────────────────────────

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

    private static string SseText(string text) =>
        $"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{text}\"}}}}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private static CredentialManager SignedInClaude()
    {
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(store, [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        creds.StoreAsync(ClaudeAiProvider.Id, new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
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

    private CodaSession? capturedSession;

    private Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> MakeFactory(
        HttpMessageHandler httpHandler)
    {
        return (perm, question, plan) =>
        {
            var options = this.BaseOptions() with
            {
                InteractivePrompt = perm,
                UserQuestionPrompt = question,
                PlanApprover = plan,
            };
            var session = new CodaSession(
                SignedInClaude(),
                options,
                httpClient: new HttpClient(httpHandler));
            this.capturedSession = session;
            return session;
        };
    }

    // ── session/setGoal tests ─────────────────────────────────────────────────

    [Fact]
    public async Task SetGoal_sets_goal_text_on_session_options()
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(new SeqHandler(SseText("ok")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var result = await orchestrator
            .SendRequestAsync(
                ServeMethods.SetGoal,
                ServeJson.ToNode(new SetGoalParams(Goal: "all tests pass")),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.NotNull(result);
        var setGoalResult = ServeJson.FromNode<SetGoalResult>(result);
        Assert.NotNull(setGoalResult);
        Assert.True(setGoalResult!.Ok);
        Assert.Equal("all tests pass", setGoalResult.Goal);

        // Verify the session's Options were mutated.
        Assert.NotNull(this.capturedSession);
        Assert.Equal("all tests pass", this.capturedSession!.Options.Goal);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task SetGoal_null_goal_clears_existing_goal()
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(new SeqHandler(SseText("ok")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Set a goal first.
        await orchestrator
            .SendRequestAsync(
                ServeMethods.SetGoal,
                ServeJson.ToNode(new SetGoalParams(Goal: "ship it")),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.Equal("ship it", this.capturedSession!.Options.Goal);

        // Now clear it.
        var clearResult = await orchestrator
            .SendRequestAsync(
                ServeMethods.SetGoal,
                ServeJson.ToNode(new SetGoalParams(Goal: null)),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        var sgr = ServeJson.FromNode<SetGoalResult>(clearResult);
        Assert.NotNull(sgr);
        Assert.True(sgr!.Ok);
        Assert.Null(sgr.Goal);
        Assert.Null(this.capturedSession!.Options.Goal);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task SetGoal_empty_string_clears_goal()
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(new SeqHandler(SseText("ok")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        await orchestrator
            .SendRequestAsync(
                ServeMethods.SetGoal,
                ServeJson.ToNode(new SetGoalParams(Goal: "existing")),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        // Empty string should clear the goal.
        await orchestrator
            .SendRequestAsync(
                ServeMethods.SetGoal,
                ServeJson.ToNode(new SetGoalParams(Goal: "")),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.Null(this.capturedSession!.Options.Goal);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task SetGoal_with_valid_maxDuration_is_applied()
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(new SeqHandler(SseText("ok")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var result = await orchestrator
            .SendRequestAsync(
                ServeMethods.SetGoal,
                ServeJson.ToNode(new SetGoalParams(Goal: "ship it", MaxDuration: "30m")),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        var sgr = ServeJson.FromNode<SetGoalResult>(result);
        Assert.NotNull(sgr);
        Assert.True(sgr!.Ok);
        Assert.Equal("ship it", sgr.Goal);
        Assert.Equal("30m", sgr.MaxDuration);

        Assert.Equal(TimeSpan.FromMinutes(30), this.capturedSession!.Options.GoalMaxDuration);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task SetGoal_with_maxContinuations_is_applied()
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(new SeqHandler(SseText("ok")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var result = await orchestrator
            .SendRequestAsync(
                ServeMethods.SetGoal,
                ServeJson.ToNode(new SetGoalParams(Goal: "run it", MaxContinuations: 200)),
                CancellationToken.None)
            .WaitAsync(WaitTimeout);

        var sgr = ServeJson.FromNode<SetGoalResult>(result);
        Assert.NotNull(sgr);
        Assert.Equal(200, sgr!.MaxContinuations);
        Assert.Equal(200, this.capturedSession!.Options.GoalMaxContinuations);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task SetGoal_with_invalid_maxDuration_returns_rpc_error()
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(new SeqHandler(SseText("ok")));

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(() =>
            orchestrator
                .SendRequestAsync(
                    ServeMethods.SetGoal,
                    ServeJson.ToNode(new SetGoalParams(Goal: "run it", MaxDuration: "not-a-duration")),
                    CancellationToken.None)
                .WaitAsync(WaitTimeout));

        Assert.Contains("maxDuration", ex.Message, StringComparison.OrdinalIgnoreCase);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    // ── goalStatus in prompt result ───────────────────────────────────────────

    [Fact]
    public void BuildWireGoalStatus_returns_null_for_None_outcome()
    {
        // Unit-test the serializer helper via PromptResult directly.
        // GoalStatus.None has Outcome = None → should produce no goalStatus field.
        var runResult = new RunResult(true, "done", [], "end_turn", null)
        {
            Goal = GoalStatus.None,
        };

        // PromptResult.GoalStatus should be null when outcome is None.
        var goalStatusNone = runResult.Goal is { Outcome: not GoalOutcome.None } g
            ? new WireGoalStatus(
                g.Outcome.ToString(),
                g.Remaining,
                g.Continuations,
                g.Elapsed.TotalSeconds,
                g.Escalated,
                g.ExtensionUsed)
            : null;

        Assert.Null(goalStatusNone);
    }

    [Fact]
    public void BuildWireGoalStatus_populates_fields_for_Met_outcome()
    {
        var goalStatus = new GoalStatus(
            GoalOutcome.Met,
            null,
            5,
            TimeSpan.FromSeconds(42),
            false,
            false);

        var runResult = new RunResult(true, "done", [], "end_turn", null)
        {
            Goal = goalStatus,
        };

        // Simulate the same logic as ServeHost.BuildWireGoalStatus.
        var wireStatus = runResult.Goal is { Outcome: not GoalOutcome.None } g
            ? new WireGoalStatus(
                g.Outcome.ToString(),
                g.Remaining,
                g.Continuations,
                g.Elapsed.TotalSeconds,
                g.Escalated,
                g.ExtensionUsed)
            : null;

        Assert.NotNull(wireStatus);
        Assert.Equal("Met", wireStatus!.Outcome);
        Assert.Null(wireStatus.Remaining);
        Assert.Equal(5, wireStatus.Continuations);
        Assert.Equal(42.0, wireStatus.ElapsedSeconds, precision: 1);
        Assert.False(wireStatus.Escalated);
        Assert.False(wireStatus.ExtensionUsed);
    }

    [Fact]
    public void BuildWireGoalStatus_populates_fields_for_Unmet_outcome()
    {
        var goalStatus = new GoalStatus(
            GoalOutcome.Unmet,
            "tests still failing",
            10,
            TimeSpan.FromMinutes(5),
            true,
            true);

        var runResult = new RunResult(false, "", [], null, null)
        {
            Goal = goalStatus,
        };

        var wireStatus = runResult.Goal is { Outcome: not GoalOutcome.None } g
            ? new WireGoalStatus(
                g.Outcome.ToString(),
                g.Remaining,
                g.Continuations,
                g.Elapsed.TotalSeconds,
                g.Escalated,
                g.ExtensionUsed)
            : null;

        Assert.NotNull(wireStatus);
        Assert.Equal("Unmet", wireStatus!.Outcome);
        Assert.Equal("tests still failing", wireStatus.Remaining);
        Assert.Equal(10, wireStatus.Continuations);
        Assert.Equal(300.0, wireStatus.ElapsedSeconds, precision: 1);
        Assert.True(wireStatus.Escalated);
        Assert.True(wireStatus.ExtensionUsed);
    }

    [Fact]
    public void PromptResult_goalStatus_is_null_by_default()
    {
        var pr = new PromptResult(true, "end_turn", false);
        Assert.Null(pr.GoalStatus);
    }

    [Fact]
    public void PromptResult_goalStatus_serializes_to_json_when_set()
    {
        var wireStatus = new WireGoalStatus("Met", null, 3, 10.5, false, false);
        var pr = new PromptResult(true, "end_turn", false)
        {
            GoalStatus = wireStatus,
        };

        var node = ServeJson.ToNode(pr);
        var json = node.ToJsonString();

        Assert.Contains("\"goalStatus\"", json);
        Assert.Contains("\"Met\"", json);
        Assert.Contains("\"continuations\":3", json);
    }

    [Fact]
    public void PromptResult_without_goalStatus_omits_field_from_json()
    {
        // ServeJson has WhenWritingNull, so null goalStatus is omitted.
        var pr = new PromptResult(true, "end_turn", false);
        var node = ServeJson.ToNode(pr);
        var json = node.ToJsonString();

        Assert.DoesNotContain("goalStatus", json);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.workDir, recursive: true); } catch { /* ignore */ }
    }
}
