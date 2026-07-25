using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Agent.Scheduling;
using Coda.JsonRpc;
using Coda.Agent.Settings;
using Coda.Sdk;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Messages;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Engine.Tests.Serve;

/// <summary>
/// Integration tests for the <c>session/scheduleList</c>, <c>session/scheduleCreate</c>, and
/// <c>session/scheduleDelete</c> request methods over in-memory duplex streams. Mirrors the
/// infrastructure pattern in <see cref="ServeHostTests"/>. No LLM turns are needed — the schedule
/// store is always present in a real <see cref="CodaSession"/> and the handlers are synchronous.
/// </summary>
public sealed class ScheduleServeTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private readonly string workDir = Directory.CreateTempSubdirectory("sched_serve_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.workDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Test infrastructure ──────────────────────────────────────────────────

    private sealed class NullHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
            });
    }

    private static CredentialManager SignedInClaude()
    {
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(
            store,
            [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        creds.StoreAsync(ClaudeAiProvider.Id, new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
        }).GetAwaiter().GetResult();
        return creds;
    }

    private Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> MakeFactory()
    {
        return (perm, question, plan) =>
        {
            var options = new SessionOptions
            {
                ProviderId = ClaudeAiProvider.Id,
                Model = "claude-sonnet-4-6",
                WorkingDirectory = this.workDir,
                PermissionMode = PermissionMode.BypassPermissions,
                InteractivePrompt = perm,
                UserQuestionPrompt = question,
                PlanApprover = plan,
            };
            return new CodaSession(
                SignedInClaude(),
                options,
                httpClient: new HttpClient(new NullHandler()));
        };
    }

    // ── Helpers to start a host and run against an orchestrator ─────────────

    /// <summary>
    /// Runs <paramref name="test"/> against a live ServeHost. The host is started on a background
    /// task; no LLM turn is needed, so no initialize call is required (no API key → always authed).
    /// </summary>
    private async Task RunWithHostAsync(
        Func<JsonRpcConnection, Task> test)
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory();

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        await test(orchestrator);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    // ── session/scheduleList ─────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleList_empty_store_returns_empty_array()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.ScheduleList, null, CancellationToken.None)
                .WaitAsync(WaitTimeout);

            Assert.NotNull(result);
            var listResult = ServeJson.FromNode<ScheduleListResult>(result);
            Assert.NotNull(listResult);
            Assert.Empty(listResult!.Schedules);
        });
    }

    [Fact]
    public async Task ScheduleList_returns_created_definition()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            // Create a definition first.
            var createParams = new ScheduleCreateParams(null, "list test prompt", "10m", null, null, "UTC");
            await orchestrator
                .SendRequestAsync(ServeMethods.ScheduleCreate, ServeJson.ToNode(createParams), CancellationToken.None)
                .WaitAsync(WaitTimeout);

            // List should include it.
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.ScheduleList, null, CancellationToken.None)
                .WaitAsync(WaitTimeout);

            var listResult = ServeJson.FromNode<ScheduleListResult>(result);
            Assert.NotNull(listResult);
            var item = Assert.Single(listResult!.Schedules);
            Assert.Equal("list test prompt", item.Prompt);
            Assert.Equal("interval", item.Kind);
            Assert.Equal("idle", item.State);
        });
    }

    // ── session/scheduleCreate ───────────────────────────────────────────────

    [Fact]
    public async Task ScheduleCreate_interval_returns_dto_with_correct_fields()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            var createParams = new ScheduleCreateParams("my task", "run cleanup", "30m", null, null, null);
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.ScheduleCreate, ServeJson.ToNode(createParams), CancellationToken.None)
                .WaitAsync(WaitTimeout);

            Assert.NotNull(result);
            var dto = ServeJson.FromNode<ScheduledTaskDto>(result);
            Assert.NotNull(dto);
            Assert.Equal("my task", dto!.Name);
            Assert.Equal("run cleanup", dto.Prompt);
            Assert.Equal("interval", dto.Kind);
            Assert.Equal("idle", dto.State);
            Assert.NotEmpty(dto.Id);
            Assert.NotEmpty(dto.Rule);
            Assert.Contains("30m", dto.Rule);
            Assert.Null(dto.ActiveTaskId);
            Assert.Null(dto.LastOutcome);
        });
    }

    [Fact]
    public async Task ScheduleCreate_cron_returns_dto_with_cron_kind()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            var createParams = new ScheduleCreateParams(null, "nightly", null, null, "0 0 * * *", "UTC");
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.ScheduleCreate, ServeJson.ToNode(createParams), CancellationToken.None)
                .WaitAsync(WaitTimeout);

            var dto = ServeJson.FromNode<ScheduledTaskDto>(result);
            Assert.NotNull(dto);
            Assert.Equal("cron", dto!.Kind);
            Assert.Equal("UTC", dto.TimeZone);
        });
    }

    [Fact]
    public async Task ScheduleCreate_validation_error_returns_rpc_error_with_parser_message()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            // Zero selectors → parser validation fails.
            var createParams = new ScheduleCreateParams(null, "no-selector", null, null, null, null);
            var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
                () => orchestrator
                    .SendRequestAsync(
                        ServeMethods.ScheduleCreate,
                        ServeJson.ToNode(createParams),
                        CancellationToken.None)
                    .WaitAsync(WaitTimeout));

            // The error message must be the parser's exact text.
            Assert.Contains("exactly one", ex.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ScheduleCreate_blank_prompt_returns_rpc_error()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            var createParams = new ScheduleCreateParams(null, "   ", "5m", null, null, null);
            var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
                () => orchestrator
                    .SendRequestAsync(
                        ServeMethods.ScheduleCreate,
                        ServeJson.ToNode(createParams),
                        CancellationToken.None)
                    .WaitAsync(WaitTimeout));

            Assert.NotEmpty(ex.Message);
        });
    }

    // ── session/scheduleDelete ───────────────────────────────────────────────

    [Fact]
    public async Task ScheduleDelete_existing_id_returns_ok_and_removes_definition()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            // Create first.
            var createResult = await orchestrator
                .SendRequestAsync(
                    ServeMethods.ScheduleCreate,
                    ServeJson.ToNode(new ScheduleCreateParams(null, "to-delete", "5m", null, null, null)),
                    CancellationToken.None)
                .WaitAsync(WaitTimeout);

            var created = ServeJson.FromNode<ScheduledTaskDto>(createResult);
            Assert.NotNull(created);

            // Delete it.
            var deleteResult = await orchestrator
                .SendRequestAsync(
                    ServeMethods.ScheduleDelete,
                    ServeJson.ToNode(new ScheduleDeleteParams(created!.Id)),
                    CancellationToken.None)
                .WaitAsync(WaitTimeout);

            Assert.NotNull(deleteResult);
            var dr = ServeJson.FromNode<ScheduleDeleteResult>(deleteResult);
            Assert.NotNull(dr);
            Assert.True(dr!.Ok);
            Assert.Equal(created.Id, dr.Id);

            // List should now be empty.
            var listResult = await orchestrator
                .SendRequestAsync(ServeMethods.ScheduleList, null, CancellationToken.None)
                .WaitAsync(WaitTimeout);
            var list = ServeJson.FromNode<ScheduleListResult>(listResult);
            Assert.NotNull(list);
            Assert.Empty(list!.Schedules);
        });
    }

    [Fact]
    public async Task ScheduleDelete_not_found_returns_rpc_error()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
                () => orchestrator
                    .SendRequestAsync(
                        ServeMethods.ScheduleDelete,
                        ServeJson.ToNode(new ScheduleDeleteParams("no-such-id")),
                        CancellationToken.None)
                    .WaitAsync(WaitTimeout));

            Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ScheduleDelete_missing_id_returns_rpc_error()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
                () => orchestrator
                    .SendRequestAsync(
                        ServeMethods.ScheduleDelete,
                        ServeJson.ToNode(new ScheduleDeleteParams(null)),
                        CancellationToken.None)
                    .WaitAsync(WaitTimeout));

            Assert.NotEmpty(ex.Message);
        });
    }

    // ── Parity: serve create and ScheduleControlService.Create produce identical definitions ──

    [Fact]
    public async Task ScheduleCreate_via_serve_produces_same_definition_as_direct_service_call()
    {
        // Verify that the serve handler routes through ScheduleControlService identically to a
        // direct service call: same kind, prompt, timezone, and rule for the same request input.
        await RunWithHostAsync(async orchestrator =>
        {
            var createParams = new ScheduleCreateParams("parity", "parity prompt", null, null, "*/5 * * * *", "UTC");
            var serveResult = await orchestrator
                .SendRequestAsync(
                    ServeMethods.ScheduleCreate,
                    ServeJson.ToNode(createParams),
                    CancellationToken.None)
                .WaitAsync(WaitTimeout);

            var dto = ServeJson.FromNode<ScheduledTaskDto>(serveResult);
            Assert.NotNull(dto);

            // Now reproduce via direct service call with the same inputs.
            var store = new ScheduledTaskStore();
            var svc = new ScheduleControlService(store, runtimeView: null);
            var directResult = svc.Create(new ScheduleCreateRequest(
                "parity", "parity prompt", null, null, "*/5 * * * *", "UTC"));

            Assert.True(directResult.IsSuccess);
            var direct = directResult.Task!;

            // Same kind, prompt, timezone, rule, state.
            Assert.Equal(direct.Kind.ToString().ToLowerInvariant(), dto!.Kind);
            Assert.Equal(direct.Prompt, dto.Prompt);
            Assert.Equal(direct.TimeZone, dto.TimeZone);
            Assert.Equal(direct.Rule, dto.Rule);
            Assert.Equal("idle", dto.State); // brand-new definition is always idle
        });
    }
}
