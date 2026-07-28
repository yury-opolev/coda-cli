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
/// Verifies the session-level UserPromptSubmit hook gate wiring in
/// <see cref="CodaSession.RunAsync"/>: block, modified prompt, additional context,
/// and genuine caller cancellation.
/// </summary>
public sealed class CodaSessionHookGateTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_session_gate_hook_").FullName;

    private SessionOptions Options() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    // ---- Fakes -------------------------------------------------------------------------

    private sealed class FakeLlmClient : ILlmClient
    {
        public string ProviderId => ClaudeAiProvider.Id;

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return AssistantStreamEvent.Delta("assistant reply");
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No real HTTP in gate tests.");
    }

    /// <summary>Configurable loop; optionally honours cancellation.</summary>
    private sealed class ConfigurableLoop : IAgentLoop
    {
        private readonly bool checkCancellation;

        public ConfigurableLoop(bool checkCancellation = false)
        {
            this.checkCancellation = checkCancellation;
        }

        public int RunCalls { get; private set; }

        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default, TurnShape? shape = null)
        {
            if (this.checkCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            this.RunCalls++;
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

    /// <summary>Captures <see cref="IAgentSink.OnPromptRewritten"/> calls.</summary>
    private sealed class RewriteCapturingSink : IAgentSink
    {
        public string? CapturedHookCommand { get; private set; }

        public string? CapturedOriginal { get; private set; }

        public string? CapturedModified { get; private set; }

        public void OnPromptRewritten(string hookCommand, string originalPrompt, string modifiedPrompt)
        {
            this.CapturedHookCommand = hookCommand;
            this.CapturedOriginal = originalPrompt;
            this.CapturedModified = modifiedPrompt;
        }

        public void OnAssistantText(string delta) { }

        public void OnAssistantTextComplete() { }

        public void OnToolCall(string toolName, string inputPreview) { }

        public void OnToolResult(string toolName, ToolResult result) { }

        public void OnError(string message) { }
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
    }

    private static UserHookRunner BlockingRunner() =>
        new UserHookRunner(
            [new UserHook("UserPromptSubmit", "block-cmd")],
            execOverride: static (_, _, _) => Task.FromResult((0, """{"decision":"block","reason":"hook blocked it"}""")));

    private static UserHookRunner ModifyingRunner(string modified, string hookCmd = "modify-cmd") =>
        new UserHookRunner(
            [new UserHook("UserPromptSubmit", hookCmd)],
            execOverride: (_, _, _) => Task.FromResult((0, $"{{\"hookSpecificOutput\":{{\"modifiedPrompt\":\"{modified}\"}}}}")));

    private static UserHookRunner AdditionalContextRunner(string context) =>
        new UserHookRunner(
            [new UserHook("UserPromptSubmit", "ctx-cmd")],
            execOverride: (_, _, _) => Task.FromResult((0, $"{{\"hookSpecificOutput\":{{\"additionalContext\":\"{context}\"}}}}")));

    private CodaSession NewSession(
        UserHookRunner hookRunner,
        IAgentLoop? loop = null,
        IAgentSink? extraSink = null) =>
        new CodaSession(
            SignedInClaude(),
            this.Options(),
            httpClient: new HttpClient(new ThrowingHandler()),
            llmClientFactory: new StubClientFactory(new FakeLlmClient()),
            agentLoopFactory: new StubLoopFactory(loop ?? new ConfigurableLoop()),
            userHookRunnerOverride: hookRunner);

    // =========================================================================
    // Test 3 — block leaves history unmutated and returns a non-exceptional result
    // =========================================================================

    [Fact]
    public async Task Block_result_leaves_history_unchanged_and_loop_never_runs()
    {
        var loop = new ConfigurableLoop();
        using var session = this.NewSession(BlockingRunner(), loop);
        var before = session.History.Count;

        var result = await session.RunAsync("this should be blocked");

        Assert.False(result.Success);
        Assert.Equal("hook blocked it", result.Error);
        // History must be untouched: the user message was never appended.
        Assert.Equal(before, session.History.Count);
        // The loop must never run.
        Assert.Equal(0, loop.RunCalls);
    }

    // =========================================================================
    // Test 4 — modifiedPrompt: history holds modified text; OnPromptRewritten fires
    // =========================================================================

    [Fact]
    public async Task ModifiedPrompt_history_holds_modified_text_and_sink_gets_rewrite_notice()
    {
        const string OriginalText = "original user prompt";
        const string ModifiedText = "rewritten by hook";
        const string HookCmd = "my-hook";

        var sink = new RewriteCapturingSink();
        using var session = this.NewSession(ModifyingRunner(ModifiedText, HookCmd));

        await session.RunAsync([new TextBlock(OriginalText)], sink);

        // The user message in history must carry the MODIFIED text (what the model saw).
        var userMsg = session.History.FirstOrDefault(m => m.Role == ChatRole.User);
        Assert.NotNull(userMsg);
        var textBlock = Assert.IsType<TextBlock>(userMsg.Content[0]);
        Assert.Equal(ModifiedText, textBlock.Text);

        // OnPromptRewritten must have fired with both the original and modified texts.
        Assert.Equal(OriginalText, sink.CapturedOriginal);
        Assert.Equal(ModifiedText, sink.CapturedModified);
        Assert.Equal(HookCmd, sink.CapturedHookCommand);
    }

    // =========================================================================
    // Test 5 — additionalContext arrives as a separate synthetic user message
    // =========================================================================

    [Fact]
    public async Task AdditionalContext_is_appended_as_separate_user_message()
    {
        const string UserPrompt = "hello";
        const string ExtraContext = "extra context from hook";

        using var session = this.NewSession(AdditionalContextRunner(ExtraContext));

        await session.RunAsync(UserPrompt);

        // History should have: original user message + synthetic context message (+ assistant).
        var userMessages = session.History.Where(m => m.Role == ChatRole.User).ToList();
        Assert.True(userMessages.Count >= 2, "Expected at least two user messages (prompt + context).");

        var contextMsg = userMessages[1];
        var contextBlock = Assert.IsType<TextBlock>(contextMsg.Content[0]);
        Assert.Equal(ExtraContext, contextBlock.Text);
    }

    // =========================================================================
    // Test 6 — genuine caller cancellation is reported as canceled, not a policy block
    // =========================================================================

    [Fact]
    public async Task Caller_cancellation_returns_canceled_not_a_policy_block()
    {
        // No UserPromptSubmit hooks — so the gate is skipped and the loop runs.
        // A loop that checks cancellationToken ensures the pre-cancelled token is observed.
        var noHooks = new UserHookRunner([]);
        var loop = new ConfigurableLoop(checkCancellation: true);
        using var session = this.NewSession(noHooks, loop);

        // Already-cancelled token: genuine caller cancellation.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await session.RunAsync("hi", cancellationToken: cts.Token);

        Assert.False(result.Success);
        // Must come through the cancellation path, not a hook block.
        Assert.Equal("Canceled.", result.Error);
        // The loop was entered but threw immediately on token check.
        Assert.Equal(0, loop.RunCalls);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* best-effort */ }
    }
}
