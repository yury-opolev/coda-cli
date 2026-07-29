using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Agent.Hooks;
using Coda.Agent.Settings;
using Coda.JsonRpc;
using Coda.Sdk;
using Coda.Sdk.Serve;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Engine.Tests.Serve;

/// <summary>
/// Integration tests for the <c>hooks/list</c>, <c>hooks/info</c>, and <c>hooks/trust</c>
/// JSON-RPC methods over in-memory duplex streams (Phase 7 — serve parity).
/// </summary>
public sealed class ServeHookTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private readonly string workDir = Directory.CreateTempSubdirectory("serve_hook_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.workDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    /// <summary>Creates a factory that writes hooks into a project settings file before loading.</summary>
    private Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> MakeFactory(
        string? settingsJson = null)
    {
        if (settingsJson is not null)
        {
            var codaDir = Path.Combine(this.workDir, ".coda");
            Directory.CreateDirectory(codaDir);
            File.WriteAllText(Path.Combine(codaDir, "settings.json"), settingsJson);
        }

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
            return new CodaSession(SignedInClaude(), options, httpClient: new HttpClient(new NullHandler()));
        };
    }

    private async Task RunWithHostAsync(
        Func<JsonRpcConnection, Task> test,
        string? settingsJson = null)
    {
        using var pair = new DuplexStreamPair();
        var factory = this.MakeFactory(settingsJson);

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        await test(orchestrator);

        cts.Cancel();
        try { await hostTask.WaitAsync(WaitTimeout); } catch { /* shutdown */ }
    }

    // ── hooks/list ────────────────────────────────────────────────────────────

    [Fact]
    public async Task HookList_returns_empty_array_when_no_hooks_configured()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.HookList, null, CancellationToken.None)
                .WaitAsync(WaitTimeout);

            Assert.NotNull(result);
            var obj = result as JsonObject;
            Assert.NotNull(obj);
            var hooks = obj!["hooks"] as JsonArray;
            Assert.NotNull(hooks);
            Assert.Empty(hooks!);
        });
    }

    [Fact]
    public async Task HookList_returns_configured_hooks_with_required_fields()
    {
        // Test 10: the serve method returns the hook inventory.
        const string settings = """
            {
              "hooks": {
                "PreToolUse": [{ "command": "check.sh", "matcher": "bash" }]
              }
            }
            """;

        await RunWithHostAsync(async orchestrator =>
        {
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.HookList, null, CancellationToken.None)
                .WaitAsync(WaitTimeout);

            Assert.NotNull(result);
            var obj = result as JsonObject;
            Assert.NotNull(obj);
            var hooks = obj!["hooks"] as JsonArray;
            Assert.NotNull(hooks);
            Assert.Single(hooks!);

            var hook = hooks![0] as JsonObject;
            Assert.NotNull(hook);
            Assert.Equal("PreToolUse", hook!["event"]?.GetValue<string>());
            Assert.Equal("command", hook["handlerType"]?.GetValue<string>());
            Assert.Equal("bash", hook["matcher"]?.GetValue<string>());
        }, settings);
    }

    // ── hooks/info ────────────────────────────────────────────────────────────

    [Fact]
    public async Task HookInfo_returns_detail_for_valid_index()
    {
        const string settings = """
            {
              "hooks": {
                "UserPromptSubmit": [{ "command": "gate.sh", "failOpen": false }]
              }
            }
            """;

        await RunWithHostAsync(async orchestrator =>
        {
            var p = new JsonObject { ["index"] = 0 };
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.HookInfo, p, CancellationToken.None)
                .WaitAsync(WaitTimeout);

            Assert.NotNull(result);
            var detail = result as JsonObject;
            Assert.NotNull(detail);
            Assert.Equal("UserPromptSubmit", detail!["event"]?.GetValue<string>());
        }, settings);
    }

    [Fact]
    public async Task HookInfo_out_of_range_returns_error()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            var p = new JsonObject { ["index"] = 99 };
            await Assert.ThrowsAsync<JsonRpcResponseException>(async () =>
            {
                await orchestrator
                    .SendRequestAsync(ServeMethods.HookInfo, p, CancellationToken.None)
                    .WaitAsync(WaitTimeout);
            });
        });
    }

    // ── hooks/trust ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HookTrust_records_trust_and_returns_ok()
    {
        // Configure a project-scoped hook so the L3 validation can find a matching hash.
        var hook = new UserHook("PreToolUse", "audit.sh", Scope: HookScope.Project);
        var hookHash = HookContentHash.Compute(hook);
        var settingsJson = """
            {
              "hooks": {
                "PreToolUse": [{ "command": "audit.sh", "scope": "project" }]
              }
            }
            """;

        await RunWithHostAsync(async orchestrator =>
        {
            var p = new JsonObject
            {
                ["projectPath"] = this.workDir,
                ["hookHash"] = hookHash,
            };
            var result = await orchestrator
                .SendRequestAsync(ServeMethods.HookTrust, p, CancellationToken.None)
                .WaitAsync(WaitTimeout);

            Assert.NotNull(result);
            var obj = result as JsonObject;
            Assert.NotNull(obj);
            Assert.True(obj!["ok"]?.GetValue<bool>());
        }, settingsJson: settingsJson);
    }

    [Fact]
    public async Task HookTrust_missing_params_returns_error()
    {
        await RunWithHostAsync(async orchestrator =>
        {
            await Assert.ThrowsAsync<JsonRpcResponseException>(async () =>
            {
                await orchestrator
                    .SendRequestAsync(ServeMethods.HookTrust, new JsonObject(), CancellationToken.None)
                    .WaitAsync(WaitTimeout);
            });
        });
    }
}
