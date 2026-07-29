using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Sdk;
using Coda.Tui.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Tui.Tests;

/// <summary>
/// TDD coverage for session-end reason wiring: AgentRunner.SetSessionEndReason and
/// AgentRunner.TriggerSessionEndAsync, which are the building-blocks used by the
/// process-shutdown and interrupt exit paths.
/// </summary>
public sealed class SessionHookExitReasonTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_hook_exit_").FullName;
    private readonly HttpClient http = new(new BlockingHandler());

    public void Dispose()
    {
        this.http.Dispose();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed class HookExecutor(
        Func<string, string, CancellationToken, Task<(int, string, string)>> fn) : IHookExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct) => fn(command, payload, ct);
    }

    private CommandContext BuildContext(out SessionState session)
    {
        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude });
        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };
        session = new SessionState("claude-ai", this.tempDir);
        var registry = new SlashCommandRegistry(Array.Empty<ISlashCommand>());
        return new CommandContext(
            new Spectre.Console.Testing.TestConsole(),
            credentials,
            session,
            providers,
            registry,
            events: new RecordingUiEvents());
    }

    private AgentRunner RunnerWithHooks(UserHookRunner hookRunner) => new(
        extraToolsProvider: null,
        sessionFactory: (ctx, options, currentOptions) => new CodaSession(
            ctx.Credentials,
            options,
            httpClient: this.http,
            history: ctx.Session.History,
            sessionId: ctx.Session.SessionId,
            llmClientFactory: new StubClientFactory(new StubClient()),
            agentLoopFactory: new SingleLoopFactory(new IdleLoop()),
            currentOptionsProvider: currentOptions,
            userHookRunnerOverride: hookRunner));

    [Fact]
    public async Task SetSessionEndReason_Error_PropagatedToSessionEnd()
    {
        // Verifies that AgentRunner.SetSessionEndReason("error") causes the
        // SessionEnd hook to fire with reason="error" when the runner is disposed.
        // This is the unrecoverable-error exit path (TuiShellExitKind.Failed / Exhausted).
        var reasons = new List<string>();
        var executor = new HookExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.GetProperty("event").GetString() == "SessionEnd")
            {
                reasons.Add(doc.RootElement.GetProperty("reason").GetString() ?? "");
            }

            return Task.FromResult((0, "{}", string.Empty));
        });
        var hookRunner = new UserHookRunner([new UserHook("SessionEnd", "cmd")], executor, context: null);

        var context = this.BuildContext(out _);
        using var runner = this.RunnerWithHooks(hookRunner);
        await runner.InitializeSessionAsync(context);

        runner.SetSessionEndReason("error");
        runner.Dispose();

        Assert.Single(reasons);
        Assert.Equal("error", reasons[0]);
    }

    // ── Finding 1: SetSessionEndReason → propagated to SessionEnd payload ─────

    [Fact]
    public async Task SetSessionEndReason_Interrupt_PropagatedToSessionEnd()
    {
        // Verifies that AgentRunner.SetSessionEndReason("interrupt") causes the
        // SessionEnd hook to fire with reason="interrupt" when the runner is disposed.
        var reasons = new List<string>();
        var executor = new HookExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.GetProperty("event").GetString() == "SessionEnd")
            {
                reasons.Add(doc.RootElement.GetProperty("reason").GetString() ?? "");
            }

            return Task.FromResult((0, "{}", string.Empty));
        });
        var hookRunner = new UserHookRunner([new UserHook("SessionEnd", "cmd")], executor, context: null);

        var context = this.BuildContext(out _);
        using var runner = this.RunnerWithHooks(hookRunner);
        await runner.InitializeSessionAsync(context);

        runner.SetSessionEndReason("interrupt");
        runner.Dispose();

        Assert.Single(reasons);
        Assert.Equal("interrupt", reasons[0]);
    }

    [Fact]
    public async Task SetSessionEndReason_DefaultIsExit_WhenNotChanged()
    {
        var reasons = new List<string>();
        var executor = new HookExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.GetProperty("event").GetString() == "SessionEnd")
            {
                reasons.Add(doc.RootElement.GetProperty("reason").GetString() ?? "");
            }

            return Task.FromResult((0, "{}", string.Empty));
        });
        var hookRunner = new UserHookRunner([new UserHook("SessionEnd", "cmd")], executor, context: null);

        var context = this.BuildContext(out _);
        using var runner = this.RunnerWithHooks(hookRunner);
        await runner.InitializeSessionAsync(context);

        // No SetSessionEndReason call → default must be "exit".
        runner.Dispose();

        Assert.Single(reasons);
        Assert.Equal("exit", reasons[0]);
    }

    // ── Finding 1: TriggerSessionEndAsync (process-shutdown path) ─────────────

    [Fact]
    public async Task TriggerSessionEndAsync_FiresSessionEnd_WithShutdownReason()
    {
        // AgentRunner.TriggerSessionEndAsync is the bounded process-shutdown path.
        // After SetSessionEndReason("shutdown"), it must fire SessionEnd with that reason.
        var reasons = new List<string>();
        var executor = new HookExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.GetProperty("event").GetString() == "SessionEnd")
            {
                reasons.Add(doc.RootElement.GetProperty("reason").GetString() ?? "");
            }

            return Task.FromResult((0, "{}", string.Empty));
        });
        var hookRunner = new UserHookRunner([new UserHook("SessionEnd", "cmd")], executor, context: null);

        var context = this.BuildContext(out _);
        using var runner = this.RunnerWithHooks(hookRunner);
        await runner.InitializeSessionAsync(context);

        runner.SetSessionEndReason("shutdown");
        await runner.TriggerSessionEndAsync();

        Assert.Single(reasons);
        Assert.Equal("shutdown", reasons[0]);
    }

    [Fact]
    public async Task TriggerSessionEndAsync_ThenDispose_FiresExactlyOnce()
    {
        // When the process-exit path (TriggerSessionEndAsync) races the main-thread
        // Dispose, SessionEnd must still fire exactly once.
        var callCount = 0;
        var executor = new HookExecutor((_, payload, _) =>
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.GetProperty("event").GetString() == "SessionEnd")
            {
                Interlocked.Increment(ref callCount);
            }

            return Task.FromResult((0, "{}", string.Empty));
        });
        var hookRunner = new UserHookRunner([new UserHook("SessionEnd", "cmd")], executor, context: null);

        var context = this.BuildContext(out _);
        var runner = this.RunnerWithHooks(hookRunner);
        await runner.InitializeSessionAsync(context);

        runner.SetSessionEndReason("shutdown");

        await Task.WhenAll(
            runner.TriggerSessionEndAsync(),
            Task.Run(() => { runner.Dispose(); return Task.CompletedTask; }));

        Assert.Equal(1, callCount);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>HttpHandler that rejects any real HTTP call (no network in these tests).</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network calls expected.");
    }

    private sealed class StubClientFactory(ILlmClient client) : ILlmClientFactory
    {
        public ILlmClient? Create(
            string providerId,
            CredentialManager credentials,
            ClientFingerprint fingerprint,
            HttpClient httpClient,
            ILoggerFactory? loggerFactory = null,
            LlmHttpTimeoutConfig? timeoutConfig = null,
            IStreamProgressSink? progressSink = null) => client;
    }

    private sealed class StubClient : ILlmClient
    {
        public string ProviderId => ClaudeAiProvider.Id;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }

    /// <summary>Loop that never produces output — used for hook-only tests.</summary>
    private sealed class IdleLoop : IAgentLoop
    {
        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(
            List<ChatMessage> history,
            IAgentSink sink,
            CancellationToken cancellationToken = default,
            TurnShape? shape = null)
        {
            sink.OnAssistantText("idle");
            sink.OnAssistantTextComplete();
            sink.OnUsage(new TokenUsage(0, 1));
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }

    private sealed class SingleLoopFactory(IAgentLoop loop) : IAgentLoopFactory
    {
        public IAgentLoop Create(AgentLoopSpec spec) => loop;
    }
}
