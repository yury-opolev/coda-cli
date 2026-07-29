using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Sdk;
using Engine.Tests.TestSupport;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

// ── Phase 2 Item 1 — volatile system-prompt detection ─────────────────────────

public sealed class CacheVolatilePromptTests
{
    private sealed class OneShotClient(TokenUsage? usage = null) : ILlmClient
    {
        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return AssistantStreamEvent.Finished("end_turn", usage);
        }
    }

    private static AgentLoop MakeLoop(AgentOptions options, ILogger logger)
        => new AgentLoop(
            new OneShotClient(),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            options,
            logger: logger);

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
    }

    [Fact]
    public async Task Varying_system_prompt_detected_and_logged_as_prefix_change()
    {
        var log = new CapturingLogger();
        var options = new AgentOptions
        {
            SystemPrompt = "new prompt",
            WorkingDirectory = ".",
            PreviousSystemPrompt = "different old prompt",
        };

        await MakeLoop(options, log).RunAsync([], new NullSink(), CancellationToken.None);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug
            && e.Message.Contains("cache", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("prefix", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stable_system_prompt_does_not_log_prefix_change()
    {
        var log = new CapturingLogger();
        var options = new AgentOptions
        {
            SystemPrompt = "stable prompt",
            WorkingDirectory = ".",
            PreviousSystemPrompt = "stable prompt",
        };

        await MakeLoop(options, log).RunAsync([], new NullSink(), CancellationToken.None);

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Debug
            && e.Message.Contains("cache", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("prefix", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Shape_AppendSystemPrompt_is_detected_with_append_reason()
    {
        var log = new CapturingLogger();
        var options = new AgentOptions
        {
            SystemPrompt = "base prompt",
            WorkingDirectory = ".",
            // Previous resolved had no append; this turn adds one → counts as changed.
            PreviousSystemPrompt = "base prompt",
        };

        var shape = new TurnShape { AppendSystemPrompt = "per-turn volatile addition" };
        await MakeLoop(options, log).RunAsync([], new NullSink(), CancellationToken.None, shape);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug
            && e.Message.Contains("append", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Shape_StableAppendSystemPrompt_does_not_log_prefix_change()
    {
        // When PreviousSystemPrompt is the fully-resolved prompt from the previous turn
        // (base + the same append), a stable per-turn append must NOT fire a prefix-change log.
        var log = new CapturingLogger();
        const string stableAppend = "stable addition";
        var options = new AgentOptions
        {
            SystemPrompt = "base prompt",
            WorkingDirectory = ".",
            // Previous resolved = base + same append: no difference from this turn.
            PreviousSystemPrompt = "base prompt\n\n" + stableAppend,
        };

        var shape = new TurnShape { AppendSystemPrompt = stableAppend };
        await MakeLoop(options, log).RunAsync([], new NullSink(), CancellationToken.None, shape);

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Debug
            && e.Message.Contains("cache", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("prefix", StringComparison.OrdinalIgnoreCase));
    }
}

// ── Phase 2 Item 3 — copilot_cache_control on the Copilot chat-completions path ──

public sealed class CopilotCacheControlTests
{
    private static string LargeSystem() => new string('x', 5000);
    private static ToolDefinition MakeTool(string name) => new(name, "desc", "{}");
    private static ChatMessage UserMsg(string text) => ChatMessage.UserText(text);
    private static ChatMessage AssistantMsg(string t) => new(ChatRole.Assistant, [new TextBlock(t)]);

    private static int CountMarkers(string json) =>
        System.Text.RegularExpressions.Regex.Matches(json, "copilot_cache_control").Count;

    [Fact]
    public void Build_emits_copilot_cache_control_on_last_tool_for_anthropic_model()
    {
        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            System = LargeSystem(),
            Messages = [UserMsg("u0"), AssistantMsg("a1"), UserMsg("u2")],
            Tools = [MakeTool("first-tool"), MakeTool("last-tool")],
        };

        var body = OpenAiRequest.Build(request);
        var tools = body["tools"]!.AsArray();

        Assert.Null(tools[0]!["copilot_cache_control"]);
        Assert.NotNull(tools[^1]!["copilot_cache_control"]);
        Assert.Equal("ephemeral", (string?)tools[^1]!["copilot_cache_control"]!["type"]);
    }

    [Fact]
    public void Build_emits_copilot_cache_control_on_system_for_anthropic_model()
    {
        var request = new ChatRequest
        {
            Model = "claude-opus-4-8",
            System = LargeSystem(),
            Messages = [UserMsg("hello")],
        };

        var body = OpenAiRequest.Build(request);
        var messages = body["messages"]!.AsArray();
        var systemMsg = messages[0];

        Assert.Equal("system", (string?)systemMsg!["role"]);
        Assert.NotNull(systemMsg["copilot_cache_control"]);
        Assert.Equal("ephemeral", (string?)systemMsg["copilot_cache_control"]!["type"]);
    }

    [Fact]
    public void Build_no_copilot_cache_control_for_non_anthropic_model()
    {
        var request = new ChatRequest
        {
            Model = "gpt-5-sol",
            System = LargeSystem(),
            Messages = [UserMsg("u0"), AssistantMsg("a1"), UserMsg("u2")],
            Tools = [MakeTool("t1")],
        };

        var body = OpenAiRequest.Build(request);
        Assert.Equal(0, CountMarkers(body.ToJsonString()));
    }

    [Fact]
    public void Build_never_more_than_four_copilot_cache_control_markers()
    {
        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            System = LargeSystem(),
            Messages = [UserMsg("u0"), AssistantMsg("a1"), UserMsg("u2")],
            Tools = [MakeTool("t1"), MakeTool("t2")],
        };

        var body = OpenAiRequest.Build(request);
        Assert.True(CountMarkers(body.ToJsonString()) <= 4);
    }
}

// ── Phase 2 Item 4 — OpenAI inverted cached_tokens convention ─────────────────

public sealed class OpenAiSseCacheTests
{
    private static async Task<List<AssistantStreamEvent>> ReadOpenAiSse(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<AssistantStreamEvent>();
        await foreach (var e in OpenAiSseReader.ReadAsync(stream, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    private static async Task<List<AssistantStreamEvent>> ReadResponsesSse(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<AssistantStreamEvent>();
        await foreach (var e in OpenAiResponsesSseReader.ReadAsync(stream, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task OpenAiSseReader_maps_cached_tokens_to_CacheReadTokens_and_subtracts_from_input()
    {
        const string sse =
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":800,\"completion_tokens\":50,\"prompt_tokens_details\":{\"cached_tokens\":300}}}\n\n" +
            "data: [DONE]\n\n";

        var events = await ReadOpenAiSse(sse);
        var done = events.Single(e => e.Kind == AssistantEventKind.Done);

        Assert.NotNull(done.Usage);
        Assert.Equal(500, done.Usage!.InputTokens);       // 800 - 300
        Assert.Equal(300, done.Usage.CacheReadTokens);
        Assert.Equal(50, done.Usage.OutputTokens);
        Assert.Equal(800, done.Usage.TotalInputTokens);   // total unchanged
    }

    [Fact]
    public async Task OpenAiSseReader_no_cached_tokens_leaves_input_unchanged()
    {
        const string sse =
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":600,\"completion_tokens\":30}}\n\n" +
            "data: [DONE]\n\n";

        var events = await ReadOpenAiSse(sse);
        var done = events.Single(e => e.Kind == AssistantEventKind.Done);

        Assert.NotNull(done.Usage);
        Assert.Equal(600, done.Usage!.InputTokens);
        Assert.Equal(0, done.Usage.CacheReadTokens);
        Assert.Equal(600, done.Usage.TotalInputTokens);
    }

    [Fact]
    public async Task OpenAiSseReader_total_unchanged_regardless_of_caching_convention()
    {
        const string withCaching =
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":800,\"completion_tokens\":10,\"prompt_tokens_details\":{\"cached_tokens\":300}}}\n\n" +
            "data: [DONE]\n\n";
        const string withoutCaching =
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":800,\"completion_tokens\":10}}\n\n" +
            "data: [DONE]\n\n";

        var withUsage = (await ReadOpenAiSse(withCaching)).Single(e => e.Kind == AssistantEventKind.Done).Usage!;
        var withoutUsage = (await ReadOpenAiSse(withoutCaching)).Single(e => e.Kind == AssistantEventKind.Done).Usage!;

        Assert.Equal(800, withUsage.TotalInputTokens);
        Assert.Equal(800, withoutUsage.TotalInputTokens);
    }

    [Fact]
    public async Task OpenAiResponsesSseReader_maps_cached_tokens_and_subtracts_from_input()
    {
        // The Responses API uses input_tokens_details (plural) — not prompt_tokens_details.
        const string sse =
            "event: response.output_item.added\n" +
            "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"role\":\"assistant\"}}\n\n" +
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1000,\"output_tokens\":40,\"input_tokens_details\":{\"cached_tokens\":250}}}}\n\n";

        var events = await ReadResponsesSse(sse);
        var done = events.Single(e => e.Kind == AssistantEventKind.Done);

        Assert.NotNull(done.Usage);
        Assert.Equal(750, done.Usage!.InputTokens);   // 1000 - 250
        Assert.Equal(250, done.Usage.CacheReadTokens);
        Assert.Equal(1000, done.Usage.TotalInputTokens);
    }

    [Fact]
    public async Task OpenAiResponsesSseReader_prompt_tokens_details_fallback_still_works()
    {
        // prompt_tokens_details is the Chat Completions field; kept as fallback for compatibility.
        const string sse =
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":800,\"output_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":100}}}}\n\n";

        var events = await ReadResponsesSse(sse);
        var done = events.Single(e => e.Kind == AssistantEventKind.Done);

        Assert.NotNull(done.Usage);
        Assert.Equal(700, done.Usage!.InputTokens);   // 800 - 100
        Assert.Equal(100, done.Usage.CacheReadTokens);
    }

    // ── L1: clamp cached_tokens when it exceeds prompt/input tokens ───────────

    [Fact]
    public async Task OpenAiSseReader_clamps_cached_tokens_that_exceed_prompt_tokens()
    {
        // A malformed or unusual response where cached_tokens > prompt_tokens must not
        // produce a negative InputTokens value.
        const string sse =
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":500,\"completion_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":700}}}\n\n" +
            "data: [DONE]\n\n";

        var events = await ReadOpenAiSse(sse);
        var done = events.Single(e => e.Kind == AssistantEventKind.Done);

        Assert.NotNull(done.Usage);
        Assert.True(done.Usage!.InputTokens >= 0, $"InputTokens must not be negative; got {done.Usage.InputTokens}");
        Assert.Equal(500, done.Usage.CacheReadTokens);   // clamped to promptTokens
        Assert.Equal(0, done.Usage.InputTokens);          // promptTokens - clamped = 0
    }

    [Fact]
    public async Task OpenAiResponsesSseReader_clamps_cached_tokens_that_exceed_input_tokens()
    {
        const string sse =
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":300,\"output_tokens\":10,\"input_tokens_details\":{\"cached_tokens\":500}}}}\n\n";

        var events = await ReadResponsesSse(sse);
        var done = events.Single(e => e.Kind == AssistantEventKind.Done);

        Assert.NotNull(done.Usage);
        Assert.True(done.Usage!.InputTokens >= 0, $"InputTokens must not be negative; got {done.Usage.InputTokens}");
        Assert.Equal(300, done.Usage.CacheReadTokens);   // clamped to inputTokens
        Assert.Equal(0, done.Usage.InputTokens);
    }
}

// ── Phase 2 Item 5 — 1-hour TTL placement ────────────────────────────────────

public sealed class Cache1hTtlTests
{
    private static string LargeSystem() => new string('x', 5000);
    private static ToolDefinition MakeTool(string name) => new(name, "desc", "{}");
    private static ChatMessage UserMsg(string text) => ChatMessage.UserText(text);
    private static ChatMessage AssistantMsg(string t) => new(ChatRole.Assistant, [new TextBlock(t)]);

    private static int CountTtl1h(string json) =>
        System.Text.RegularExpressions.Regex.Matches(json, "\"ttl\":\"1h\"").Count;

    [Fact]
    public void BuildBody_uses_1h_ttl_on_tools_and_system_when_opted_in()
    {
        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            System = LargeSystem(),
            Messages = [UserMsg("u0"), AssistantMsg("a1"), UserMsg("u2")],
            Tools = [MakeTool("t1")],
            UseOnehourTtl = true,
        };

        var body = AnthropicMessagesClient.BuildBody(request);
        var json = body.ToJsonString();

        Assert.True(CountTtl1h(json) >= 2,
            $"Expected >=2 '1h' TTL markers on tools+system. JSON={json}");

        // Message content blocks must NOT carry the 1h TTL.
        var messagesNode = body["messages"]!.AsArray();
        foreach (var msg in messagesNode)
        {
            var content = msg!["content"]?.AsArray();
            if (content is null) continue;
            foreach (var block in content)
            {
                var cc = block!["cache_control"];
                if (cc is null) continue;
                Assert.Null(cc["ttl"]);
            }
        }
    }

    [Fact]
    public void BuildBody_no_1h_ttl_by_default()
    {
        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            System = LargeSystem(),
            Messages = [UserMsg("u0"), AssistantMsg("a1"), UserMsg("u2")],
            Tools = [MakeTool("t1")],
            UseOnehourTtl = false,
        };

        var body = AnthropicMessagesClient.BuildBody(request);
        Assert.Equal(0, CountTtl1h(body.ToJsonString()));
    }
}

// ── Phase 3 — cache hit rate logged per turn ─────────────────────────────────

public sealed class CacheHitRateLoggingTests
{
    private sealed class FixedUsageClient(TokenUsage usage) : ILlmClient
    {
        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return AssistantStreamEvent.Finished("end_turn", usage);
        }
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
    }

    [Fact]
    public async Task Cache_hit_rate_is_logged_at_debug_when_turn_has_cache_activity()
    {
        var log = new CapturingLogger();
        var usage = new TokenUsage(100, 50, CacheReadTokens: 300, CacheWrite5mTokens: 200);
        var loop = new AgentLoop(
            new FixedUsageClient(usage),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "s", WorkingDirectory = "." },
            logger: log);

        await loop.RunAsync([], new NullSink(), CancellationToken.None);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug
            && e.Message.StartsWith("cache:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task No_cache_stats_log_when_turn_has_no_cache_activity()
    {
        var log = new CapturingLogger();
        var usage = new TokenUsage(200, 50);  // zero cache fields
        var loop = new AgentLoop(
            new FixedUsageClient(usage),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "s", WorkingDirectory = "." },
            logger: log);

        await loop.RunAsync([], new NullSink(), CancellationToken.None);

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Debug
            && e.Message.StartsWith("cache: turn=", StringComparison.OrdinalIgnoreCase));
    }
}

// ── Phase 3 — zero-counters warning fires once per session ───────────────────

public sealed class CacheZeroCountersWarningTests
{
    private sealed class WarningCapturingSink : IAgentSink
    {
        public List<string> Warnings { get; } = [];
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
        public void OnWarning(string message) => this.Warnings.Add(message);
    }

    private sealed class CacheActiveLoop : IAgentLoop
    {
        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default, TurnShape? shape = null)
        {
            sink.OnAssistantText("ok");
            sink.OnAssistantTextComplete();
            sink.OnUsage(new TokenUsage(100, 10, CacheReadTokens: 50));
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }

    private sealed class OverrideFactory(IAgentLoop loop) : IAgentLoopFactory
    {
        public IAgentLoop Create(AgentLoopSpec spec) => loop;
    }

    private static CodaSession BuildSession(IAgentLoopFactory factory) =>
        new CodaSession(
            CredentialFixtures.SignedInClaude(),
            new SessionOptions
            {
                ProviderId = ClaudeAiProvider.Id,
                Model = "claude-sonnet-4-6",
                WorkingDirectory = Path.GetTempPath(),
                PermissionMode = PermissionMode.BypassPermissions,
            },
            httpClient: new HttpClient(new ThrowingHandler()),
            llmClientFactory: new StubClientFactory(new FakeLlmClient()),
            agentLoopFactory: factory);

    [Fact]
    public async Task Zero_cache_counters_warning_fires_exactly_once_not_per_turn()
    {
        var sink = new WarningCapturingSink();
        using var session = FakeSession.New(Path.GetTempPath());

        const int turns = CodaSession.ZeroActivityWarnAfterTurns + 1;
        for (var i = 0; i < turns; i++)
        {
            await session.RunAsync("turn " + i, sink, CancellationToken.None);
        }

        // Exactly one warning about the cache being inactive.
        Assert.Equal(1, sink.Warnings.Count(w =>
            w.Contains("cache", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Warning_does_not_fire_when_cache_is_active()
    {
        var sink = new WarningCapturingSink();
        using var session = BuildSession(new OverrideFactory(new CacheActiveLoop()));

        const int turns = CodaSession.ZeroActivityWarnAfterTurns + 2;
        for (var i = 0; i < turns; i++)
        {
            await session.RunAsync("msg", sink, CancellationToken.None);
        }

        Assert.Empty(sink.Warnings);
    }

    // ── I1: warning surfaces through real production sinks, not a bespoke one ──

    [Fact]
    public void PlainTextSink_forwards_cache_warning_to_stderr()
    {
        var stderr = new StringWriter();
        IAgentSink sink = new PlainTextSink(TextWriter.Null, stderr);

        sink.OnWarning("Prompt cache appears inactive");

        Assert.Contains("Prompt cache appears inactive", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonStreamSink_emits_warning_record_when_OnWarning_called()
    {
        var writer = new StringWriter();
        IAgentSink sink = new JsonStreamSink(writer);

        sink.OnWarning("Prompt cache appears inactive");

        var output = writer.ToString();
        var doc = JsonDocument.Parse(output.Trim());
        Assert.Equal("warning", doc.RootElement.GetProperty("type").GetString());
        Assert.Contains("Prompt cache appears inactive", output, StringComparison.Ordinal);
    }
}

// ── M1: zero-counters warning must be skipped for the Copilot provider ────────

public sealed class CopilotCacheZeroWarningTests
{
    private sealed class WarningCapturingSink : IAgentSink
    {
        public List<string> Warnings { get; } = [];
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
        public void OnWarning(string message) => this.Warnings.Add(message);
    }

    [Fact]
    public async Task Copilot_provider_never_fires_zero_counters_cache_warning()
    {
        var sink = new WarningCapturingSink();
        using var session = new CodaSession(
            CredentialFixtures.SignedInClaudeAndCopilot(),
            new SessionOptions
            {
                ProviderId = GitHubCopilotProvider.Id,
                Model = "gpt-5.6-sol",
                WorkingDirectory = Path.GetTempPath(),
                PermissionMode = PermissionMode.BypassPermissions,
            },
            httpClient: new HttpClient(new ThrowingHandler()),
            llmClientFactory: new StubClientFactory(new FakeLlmClient()),
            agentLoopFactory: new StubLoopFactory(new ConfigurableLoop()));

        // Run more turns than the threshold; the fake loop emits zero cache activity.
        const int turns = CodaSession.ZeroActivityWarnAfterTurns + 2;
        for (var i = 0; i < turns; i++)
        {
            await session.RunAsync("msg " + i, sink, CancellationToken.None);
        }

        Assert.DoesNotContain(sink.Warnings, w => w.Contains("cache", StringComparison.OrdinalIgnoreCase));
    }
}

// ── M3: CodaSession stores the *resolved* prompt (base + append) for next turn ─

public sealed class CacheVolatilePromptSessionTests
{
    // Hook executor that returns appendSystemPrompt from SessionStart.
    private sealed class SessionStartAppendExecutor(string append) : IHookExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct)
        {
            var doc = System.Text.Json.JsonDocument.Parse(payload);
            var eventName = doc.RootElement.GetProperty("event").GetString();
            if (string.Equals(eventName, "SessionStart", StringComparison.Ordinal))
            {
                return Task.FromResult((0,
                    "{\"hookSpecificOutput\":{\"appendSystemPrompt\":\"" + append + "\"}}",
                    string.Empty));
            }

            return Task.FromResult((0, "{}", string.Empty));
        }
    }

    // Loop factory that captures spec.Options.PreviousSystemPrompt on each Create call.
    private sealed class PreviousPromptCapturingFactory : IAgentLoopFactory
    {
        public List<string?> CapturedPreviousPrompts { get; } = [];

        public IAgentLoop Create(AgentLoopSpec spec)
        {
            this.CapturedPreviousPrompts.Add(spec.Options.PreviousSystemPrompt);
            return new IdleLoop();
        }

        private sealed class IdleLoop : IAgentLoop
        {
            public GoalStatus? LastGoalStatus => null;

            public Task RunAsync(
                List<ChatMessage> history,
                IAgentSink sink,
                CancellationToken cancellationToken = default,
                TurnShape? shape = null)
            {
                history.Add(new ChatMessage(ChatRole.Assistant, [new TextBlock("ok")]));
                sink.OnUsage(new TokenUsage(5, 2));
                sink.OnStopReason("end_turn");
                return Task.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task CodaSession_passes_resolved_prompt_as_PreviousSystemPrompt_on_subsequent_turn()
    {
        // M3 regression: CodaSession was storing the BASE prompt only (lastBaseSystemPrompt),
        // not the RESOLVED value (base + sessionAppendSystemPrompt). On turn 2 the loop received
        // PreviousSystemPrompt = base while the resolved prompt was base + append → false-positive
        // "prefix change" log even when the append is stable.
        //
        // After the fix, PreviousSystemPrompt on turn 2 equals the fully-resolved value.
        const string basePrompt = "base-system-prompt";
        const string sessionAppend = "session-extra";
        const string expectedResolved = basePrompt + "\n\n" + sessionAppend;

        var executor = new SessionStartAppendExecutor(sessionAppend);
        var runner = new UserHookRunner(
            [new UserHook("SessionStart", "cmd")],
            executor,
            context: null);

        var loopFactory = new PreviousPromptCapturingFactory();

        using var session = new CodaSession(
            CredentialFixtures.SignedInClaude(),
            new SessionOptions
            {
                ProviderId = ClaudeAiProvider.Id,
                Model = "claude-sonnet-4-6",
                WorkingDirectory = Path.GetTempPath(),
                PermissionMode = PermissionMode.BypassPermissions,
                SystemPromptOverride = basePrompt,
            },
            httpClient: new HttpClient(new ThrowingHandler()),
            llmClientFactory: new StubClientFactory(new FakeLlmClient()),
            agentLoopFactory: loopFactory,
            userHookRunnerOverride: runner);

        await session.RunAsync("turn 1", sink: null, CancellationToken.None);
        await session.RunAsync("turn 2", sink: null, CancellationToken.None);

        Assert.Equal(2, loopFactory.CapturedPreviousPrompts.Count);
        // Turn 2's PreviousSystemPrompt must equal the RESOLVED prompt from turn 1 (base + append).
        Assert.Equal(expectedResolved, loopFactory.CapturedPreviousPrompts[1]);
    }
}
