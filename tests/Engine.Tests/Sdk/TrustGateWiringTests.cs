using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using static Engine.Tests.TestSupport.CredentialFixtures;

namespace Engine.Tests.Sdk;

/// <summary>
/// Wiring-site integration tests for the trust gate (C1) and run log (M1).
/// These tests drive real <see cref="CodaSession"/>/<c>TurnPipelineBuilder</c> construction
/// without <c>userHookRunnerOverride</c> so they fail if the trust guard or run log are not
/// threaded through the production pipeline.
/// </summary>
public sealed class TrustGateWiringTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_trust_wiring_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { }
    }

    // ---- Fakes -----------------------------------------------------------------

    private sealed class FakeLlmClient : ILlmClient
    {
        public string ProviderId => ClaudeAiProvider.Id;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return AssistantStreamEvent.Delta("ok");
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }

    private sealed class StubClientFactory(ILlmClient? client) : ILlmClientFactory
    {
        public ILlmClient? Create(
            string providerId,
            CredentialManager credentials,
            ClientFingerprint fingerprint,
            HttpClient httpClient,
            Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
            LlmHttpTimeoutConfig? timeoutConfig = null,
            IStreamProgressSink? progressSink = null) => client;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No real HTTP in wiring tests.");
    }

    private sealed class StubLoop : IAgentLoop
    {
        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(
            List<ChatMessage> history,
            IAgentSink sink,
            CancellationToken cancellationToken = default,
            TurnShape? shape = null)
        {
            sink.OnAssistantText("ok");
            sink.OnAssistantTextComplete();
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }

    private sealed class StubLoopFactory(IAgentLoop loop) : IAgentLoopFactory
    {
        public IAgentLoop Create(AgentLoopSpec spec) => loop;
    }

    // ---- Helpers ---------------------------------------------------------------

    /// <summary>
    /// Writes a project-scoped <c>UserPromptSubmit</c> command hook to the working directory.
    /// Uses <c>failOpen:false</c> by default so an untrusted hook blocks (not NoOps).
    /// </summary>
    private void WriteProjectHook(bool failOpen = false)
    {
        var codaDir = Path.Combine(this.root, ".coda");
        Directory.CreateDirectory(codaDir);
        File.WriteAllText(
            Path.Combine(codaDir, "settings.json"),
            $$"""
            {
              "hooks": {
                "UserPromptSubmit": [
                  {
                    "command": "cmd /c echo {}",
                    "scope": "project",
                    "failOpen": {{(failOpen ? "true" : "false")}}
                  }
                ]
              }
            }
            """);
    }

    private SessionOptions Options() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    private HookTrustGuard HeadlessTrustGuard()
    {
        // Separate subdirectory so the trust file never accidentally contains old grants.
        var trustDir = Path.Combine(this.root, ".trust-test");
        Directory.CreateDirectory(trustDir);
        return new HookTrustGuard(new HookTrustStore(trustDir), this.root, promptCallback: null);
    }

    // =========================================================================
    // C1: trust guard blocks a project-scoped hook when wired through the real pipeline
    // =========================================================================

    [Fact]
    public async Task ProjectHook_fails_closed_when_trust_guard_wired_headless()
    {
        // Arrange: project-scoped UserPromptSubmit hook with failOpen:false.
        // "cmd /c echo {}" outputs "{}" which parses as allow/noop, so the turn
        // SUCCEEDS when the hook runs but FAILS when the guard blocks it (fail-closed).
        this.WriteProjectHook(failOpen: false);

        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: new HttpClient(new ThrowingHandler()),
            llmClientFactory: new StubClientFactory(new FakeLlmClient()),
            agentLoopFactory: new StubLoopFactory(new StubLoop()),
            trustGuard: this.HeadlessTrustGuard());

        // Act.
        var result = await session.RunAsync("test");

        // Assert: headless + fail-closed + untrusted → turn must fail.
        Assert.False(result.Success, "project hook must be blocked by the headless trust guard");
        Assert.Contains("untrusted", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // M1: run log records "skipped" when the trust guard blocks a hook
    // =========================================================================

    [Fact]
    public async Task RunLog_records_skipped_when_trust_guard_blocks()
    {
        this.WriteProjectHook(failOpen: false);

        var runLog = new HookRunLog();
        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: new HttpClient(new ThrowingHandler()),
            llmClientFactory: new StubClientFactory(new FakeLlmClient()),
            agentLoopFactory: new StubLoopFactory(new StubLoop()),
            trustGuard: this.HeadlessTrustGuard(),
            runLog: runLog);

        await session.RunAsync("test");

        var entry = runLog.Get(0);
        Assert.NotNull(entry);
        Assert.Equal("skipped", entry.Outcome);
    }
}
