using System.Net;
using System.Text;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.ToolSearch;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests;

public sealed class EffortAndContextTests
{
    // ── EffortSupport rules ──────────────────────────────────────────────────

    [Theory]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-haiku-4-5", false)]
    public void ModelSupportsEffort_matches_reference_allowlist(string model, bool expected)
    {
        Assert.Equal(expected, EffortSupport.ModelSupportsEffort(model));
    }

    [Theory]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-sonnet-4-6", false)]
    [InlineData("claude-haiku-4-5", false)]
    public void ModelSupportsMaxEffort_is_opus_only(string model, bool expected)
    {
        Assert.Equal(expected, EffortSupport.ModelSupportsMaxEffort(model));
    }

    [Fact]
    public void ResolveAppliedEffort_clamps_max_to_high_on_non_opus()
    {
        Assert.Equal("high", EffortSupport.ResolveAppliedEffort("claude-sonnet-4-6", "max"));
        Assert.Equal("max", EffortSupport.ResolveAppliedEffort("claude-opus-4-8", "max"));
    }

    [Fact]
    public void ResolveAppliedEffort_returns_null_when_unsupported_or_unset()
    {
        Assert.Null(EffortSupport.ResolveAppliedEffort("claude-haiku-4-5", "high"));
        Assert.Null(EffortSupport.ResolveAppliedEffort("claude-sonnet-4-6", "auto"));
        Assert.Null(EffortSupport.ResolveAppliedEffort("claude-sonnet-4-6", null));
    }

    // ── Effort in the Anthropic request body ─────────────────────────────────

    [Fact]
    public void BuildBody_adds_output_config_effort_for_supported_model()
    {
        var body = AnthropicMessagesClient.BuildBody(new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            Effort = "high",
            Messages = [ChatMessage.UserText("hi")],
        });

        Assert.Equal("high", (string?)body["output_config"]!["effort"]);
    }

    [Fact]
    public void BuildBody_omits_effort_for_unsupported_model()
    {
        var body = AnthropicMessagesClient.BuildBody(new ChatRequest
        {
            Model = "claude-haiku-4-5",
            Effort = "high",
            Messages = [ChatMessage.UserText("hi")],
        });

        Assert.Null(body["output_config"]);
    }

    [Fact]
    public void BuildBody_clamps_max_effort_to_high_on_sonnet()
    {
        var body = AnthropicMessagesClient.BuildBody(new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            Effort = "max",
            Messages = [ChatMessage.UserText("hi")],
        });

        Assert.Equal("high", (string?)body["output_config"]!["effort"]);
    }

    [Fact]
    public void BuildCountTokensBody_strips_stream_maxtokens_and_outputconfig()
    {
        var body = AnthropicMessagesClient.BuildCountTokensBody(new ChatRequest
        {
            Model = "claude-opus-4-8",
            Effort = "max",
            Messages = [ChatMessage.UserText("hi")],
        });

        Assert.Null(body["stream"]);
        Assert.Null(body["max_tokens"]);
        Assert.Null(body["output_config"]);
        Assert.Equal("claude-opus-4-8", (string?)body["model"]);
        Assert.NotNull(body["messages"]);
    }

    // ── CountTokensAsync ─────────────────────────────────────────────────────

    private sealed class JsonHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

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

    [Fact]
    public async Task CountTokensAsync_returns_input_tokens_on_success()
    {
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.OK, """{"input_tokens":1234}"""));
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http);

        var count = await client.CountTokensAsync(new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [ChatMessage.UserText("hello")],
        });

        Assert.Equal(1234, count);
    }

    [Fact]
    public async Task CountTokensAsync_returns_null_on_error_status()
    {
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, """{"error":"nope"}"""));
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http);

        var count = await client.CountTokensAsync(new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [ChatMessage.UserText("hello")],
        });

        Assert.Null(count);
    }

    // ── CodaSession.AnalyzeContextAsync ──────────────────────────────────────

    private sealed class CountSeqHandler(params int[] counts) : HttpMessageHandler
    {
        private int index;

        public List<string> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Bodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            var value = counts[Math.Min(this.index, counts.Length - 1)];
            this.index++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"input_tokens":{{value}}}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private readonly string root = Directory.CreateTempSubdirectory("coda_ctx_").FullName;

    [Fact]
    public async Task AnalyzeContextAsync_uses_count_api_and_isolates_categories()
    {
        // Calls in order: baseline, system, tools (history empty → no messages call).
        using var http = new HttpClient(new CountSeqHandler(10, 200, 500));
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            // A model not in the catalog → window falls back to the nominal default.
            Model = "test-only-unknown-model",
            WorkingDirectory = this.root,
            AutoCompactTokenThreshold = 0, // no reserved buffer for a clean assertion
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var report = await session.AnalyzeContextAsync();

        Assert.True(report.IsExact);
        Assert.Equal(190, report.Categories.Single(c => c.Name == "System prompt").Tokens); // 200 - 10
        Assert.Equal(490, report.Categories.Single(c => c.Name == "System tools").Tokens); // 500 - 10
        Assert.Equal(680, report.UsedTokens);
        Assert.Equal(CodaSession.ContextWindowTokens, report.MaxTokens);
        Assert.Contains(report.Categories, c => c.Name == "Free space");
    }

    [Fact]
    public async Task AnalyzeContextAsync_sends_the_exact_system_prompt_override_to_count_tokens()
    {
        const string exact = "CONTEXT-EXACT-OVERRIDE";
        var handler = new CountSeqHandler(10, 20, 30);
        using var http = new HttpClient(handler);
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "test-only-unknown-model",
            WorkingDirectory = this.root,
            SystemPromptOverride = exact,
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        await session.AnalyzeContextAsync();

        Assert.Contains(handler.Bodies, body => body.Contains(exact, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeContextAsync_falls_back_to_estimate_when_count_api_unavailable()
    {
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}"));
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var report = await session.AnalyzeContextAsync();

        Assert.False(report.IsExact);
        // System prompt is non-empty, so an estimated category is present.
        Assert.Contains(report.Categories, c => c.Name == "System prompt" && c.Tokens > 0);
    }

    private sealed class FakeTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "A fake tool for token-accounting tests with some description text.";
        public string InputSchemaJson => """{"type":"object","properties":{"q":{"type":"string"}}}""";
        public bool IsReadOnly => true;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult("ok"));
    }

    [Fact]
    public async Task AnalyzeContextAsync_uses_catalog_context_window_for_known_model()
    {
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}")); // estimate path
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var report = await session.AnalyzeContextAsync();

        var expected = ModelCatalog.Default.Get(ClaudeAiProvider.Id, "claude-sonnet-4-6")?.ContextLimit;
        Assert.NotNull(expected);
        // Proves the window came from the catalog, not the nominal 200k fallback const.
        Assert.NotEqual(CodaSession.ContextWindowTokens, report.MaxTokens);
        Assert.Equal(expected, report.MaxTokens);
    }

    [Fact]
    public async Task AnalyzeContextAsync_reports_mcp_tools_as_a_separate_category()
    {
        // Force the estimate path so we can assert on category presence deterministically.
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}"));
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
            ExtraTools = [new FakeTool("mcp__demo__search")],
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var report = await session.AnalyzeContextAsync();

        Assert.Contains(report.Categories, c => c.Name == "MCP tools" && c.Tokens > 0);
        Assert.Contains(report.Categories, c => c.Name == "System tools" && c.Tokens > 0);
    }

    // ── Headless --effort parsing ────────────────────────────────────────────

    [Theory]
    [InlineData("high", "high")]
    [InlineData("MAX", "max")]
    public void HeadlessOptions_parses_effort_level(string input, string expected)
    {
        var ok = HeadlessOptions.TryParse(["-p", "hi", "--effort", input], out var options, out var error);
        Assert.True(ok, error);
        Assert.Equal(expected, options.Effort);
    }

    [Fact]
    public void HeadlessOptions_effort_auto_clears_to_null()
    {
        var ok = HeadlessOptions.TryParse(["-p", "hi", "--effort", "auto"], out var options, out _);
        Assert.True(ok);
        Assert.Null(options.Effort);
    }

    [Fact]
    public void HeadlessOptions_rejects_invalid_effort()
    {
        var ok = HeadlessOptions.TryParse(["-p", "hi", "--effort", "turbo"], out _, out var error);
        Assert.False(ok);
        Assert.Contains("Invalid value for --effort", error);
    }

    // ── Tool-search deferral: reported == transmitted ────────────────────────

    /// <summary>
    /// A realistic-scale fake MCP tool that opts into deferral.  Large Description and
    /// InputSchemaJson ensure Standard mode produces hundreds of estimated tokens while
    /// all-deferred mode produces zero, making magnitude assertions meaningful.
    /// </summary>
    private sealed class FakeDeferredTool(string name) : ITool
    {
        public string Name => name;
        public string Description =>
            "Executes a structured database operation and returns results as JSON. " +
            "Supports SELECT, INSERT, UPDATE, and DELETE with named parameters. " +
            "Large result sets are paginated automatically. " +
            "Connections are pooled and returned after the query completes.";
        public string InputSchemaJson =>
            """{"type":"object","required":["query"],"properties":{"query":{"type":"string","description":"The SQL query string, optionally using named placeholders"},"params":{"type":"object","additionalProperties":true,"description":"Named parameters bound to the query placeholders"},"database":{"type":"string","description":"Target database name; defaults to the session default"},"timeout_ms":{"type":"integer","description":"Maximum execution time in milliseconds before the query is cancelled"},"page":{"type":"integer","description":"Zero-based page index for paginated results"},"page_size":{"type":"integer","description":"Rows per page; defaults to 100"}}}""";
        public bool IsReadOnly => true;
        public bool ShouldDefer => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult("ok"));
    }

    private static FakeDeferredTool[] FiveLargeMcpTools() =>
    [
        new FakeDeferredTool("mcp__svc__tool1"),
        new FakeDeferredTool("mcp__svc__tool2"),
        new FakeDeferredTool("mcp__svc__tool3"),
        new FakeDeferredTool("mcp__svc__tool4"),
        new FakeDeferredTool("mcp__svc__tool5"),
    ];

    private SessionOptions McpOptions(params ITool[] extraTools) => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "test-only-unknown-model",
        WorkingDirectory = this.root,
        AutoCompactTokenThreshold = 0,
        ExtraTools = extraTools,
    };

    [Fact]
    public async Task AnalyzeContextAsync_tst_mode_all_deferred_estimate_branch_reports_zero_mcp_tokens()
    {
        // All-deferred mode: no tool schemas on the wire → zero MCP tokens, no "MCP tools" category.
        // The informational deferred entry is present with 0 cost and excluded from UsedTokens.
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}"));
        using var session = new CodaSession(
            SignedInClaude(), McpOptions(new FakeDeferredTool("mcp__svc__op")),
            httpClient: http, toolSearchCoordinatorOverride: coordinator);

        var report = await session.AnalyzeContextAsync();

        Assert.False(report.IsExact);
        // No wire schemas → no "MCP tools" category.
        Assert.DoesNotContain(report.Categories, c => c.Name == "MCP tools");
        // Informational deferred entry with 0 cost.
        Assert.Contains(report.Categories, c => c.Name == "MCP tools (deferred, 1 tools)" && c.Tokens == 0);
        // UsedTokens excludes the 0-cost deferred entry.
        var sumUsed = report.Categories
            .Where(c => c.Name != "Free space"
                     && c.Name != "Autocompact buffer"
                     && !c.Name.StartsWith("MCP tools (deferred", StringComparison.Ordinal))
            .Sum(c => c.Tokens);
        Assert.Equal(report.UsedTokens, sumUsed);
    }

    [Fact]
    public async Task AnalyzeContextAsync_standard_mode_estimate_branch_reports_full_mcp_schema()
    {
        // Standard mode: deferral is off, so every schema is on the wire.
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}"));
        using var session = new CodaSession(
            SignedInClaude(), McpOptions(FiveLargeMcpTools()),
            httpClient: http,
            toolSearchCoordinatorOverride: new ToolSearchCoordinator(ToolSearchMode.Standard));

        var report = await session.AnalyzeContextAsync();

        // Full schemas on wire → "MCP tools" category with substantial token cost.
        var mcpCat = report.Categories.Single(c => c.Name == "MCP tools");
        Assert.True(mcpCat.Tokens > 100, $"Expected > 100 tokens for 5 large schemas, got {mcpCat.Tokens}");
        // No deferred entry (nothing was withheld).
        Assert.DoesNotContain(report.Categories, c => c.Name.StartsWith("MCP tools (deferred", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeContextAsync_estimate_branch_deferred_mcp_is_dramatically_smaller_than_standard()
    {
        // With many large-schema tools, all-deferred mode charges zero MCP tokens while
        // Standard mode charges hundreds — more than a 10× difference.
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}"));
        var tools = FiveLargeMcpTools();

        using var deferredSession = new CodaSession(
            SignedInClaude(), McpOptions(tools),
            httpClient: http,
            toolSearchCoordinatorOverride: new ToolSearchCoordinator(ToolSearchMode.Tst));

        // Standard: deferral off, so every schema stays on the wire.
        using var standardSession = new CodaSession(
            SignedInClaude(), McpOptions(tools),
            httpClient: http,
            toolSearchCoordinatorOverride: new ToolSearchCoordinator(ToolSearchMode.Standard));

        var deferredReport = await deferredSession.AnalyzeContextAsync();
        var standardReport = await standardSession.AnalyzeContextAsync();

        // All-deferred: no wire schemas → MCP category absent (reported as 0).
        var deferredMcp = deferredReport.Categories.FirstOrDefault(c => c.Name == "MCP tools")?.Tokens ?? 0;
        Assert.Equal(0, deferredMcp);

        // Standard: all schemas on wire → clearly large MCP cost.
        var standardMcp = standardReport.Categories.Single(c => c.Name == "MCP tools").Tokens;
        Assert.True(standardMcp > 100, $"Expected > 100 MCP tokens in Standard mode, got {standardMcp}");

        // Dramatic difference: all-deferred is more than 10× smaller.
        Assert.True(deferredMcp < standardMcp / 10,
            $"Expected deferred ({deferredMcp}) < standard/10 ({standardMcp / 10})");
    }

    [Fact]
    public async Task AnalyzeContextAsync_tst_mode_after_discovered_estimate_branch_rises_by_schema()
    {
        // Once a tool is discovered, its schema joins the wire cost.
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);
        coordinator.AddDiscovered(["mcp__svc__op"]);
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}"));
        using var session = new CodaSession(
            SignedInClaude(), McpOptions(new FakeDeferredTool("mcp__svc__op"), new FakeDeferredTool("mcp__svc__op2")),
            httpClient: http, toolSearchCoordinatorOverride: coordinator);

        var report = await session.AnalyzeContextAsync();

        // One discovered tool → "MCP tools" category with that tool's schema cost (> 0).
        Assert.Contains(report.Categories, c => c.Name == "MCP tools" && c.Tokens > 0);
        // One tool still deferred → informational entry with 0 tokens.
        Assert.Contains(report.Categories, c => c.Name == "MCP tools (deferred, 1 tools)" && c.Tokens == 0);
    }

    [Fact]
    public async Task AnalyzeContextAsync_tst_mode_all_deferred_exact_branch_reports_no_mcp_tokens()
    {
        // All-deferred: no mcpDefs on the wire → count-tokens is NOT called for MCP,
        // no "MCP tools" category appears, and no reminder is submitted.
        // Call order: baseline(10), system(200), builtin(500) — no MCP count call.
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);
        var handler = new CountSeqHandler(10, 200, 500);
        using var http = new HttpClient(handler);
        using var session = new CodaSession(
            SignedInClaude(), McpOptions(new FakeDeferredTool("mcp__svc__op")),
            httpClient: http, toolSearchCoordinatorOverride: coordinator);

        var report = await session.AnalyzeContextAsync();

        Assert.True(report.IsExact);
        // No wire definitions → no "MCP tools" category.
        Assert.DoesNotContain(report.Categories, c => c.Name == "MCP tools");
        // Informational deferred entry with 0 tokens.
        Assert.Equal(0, report.Categories.Single(c => c.Name == "MCP tools (deferred, 1 tools)").Tokens);
        // UsedTokens = system + builtin only; no MCP tokens counted.
        Assert.Equal(190 + 490, report.UsedTokens);
        // No body submitted the reminder to count-tokens (Finding 1: reminder removed from MCP figure).
        Assert.DoesNotContain(handler.Bodies, b => b.Contains("<deferred-tools>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeContextAsync_standard_mode_exact_branch_reports_full_mcp_schema()
    {
        // Standard mode: deferral is off, so all MCP schemas are on the wire and
        // count-tokens captures them without a reminder.
        // Call order: baseline(10), system(200), builtin(500), mcp-full-schema(120).
        var handler = new CountSeqHandler(10, 200, 500, 120);
        using var http = new HttpClient(handler);
        using var session = new CodaSession(
            SignedInClaude(), McpOptions(new FakeDeferredTool("mcp__svc__op")),
            httpClient: http,
            toolSearchCoordinatorOverride: new ToolSearchCoordinator(ToolSearchMode.Standard));

        var report = await session.AnalyzeContextAsync();

        Assert.True(report.IsExact);
        Assert.Equal(110, report.Categories.Single(c => c.Name == "MCP tools").Tokens); // 120 - 10
        Assert.DoesNotContain(report.Categories, c => c.Name.StartsWith("MCP tools (deferred", StringComparison.Ordinal));
        Assert.Equal(190 + 490 + 110, report.UsedTokens);
        // The MCP count request carries the tool schema; no reminder is submitted (Finding 1).
        Assert.Contains(handler.Bodies, b => b.Contains("mcp__svc__op", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Bodies, b => b.Contains("<deferred-tools>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeContextAsync_tst_mode_after_discovered_exact_branch_rises_by_schema()
    {
        // After discovery, the wire includes that tool's schema only — no reminder (Finding 1).
        // 2 deferred tools, 1 discovered → mcpDefs has 1 tool.
        // Call order: baseline(10), system(200), builtin(500), mcp-schema-only(80).
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);
        coordinator.AddDiscovered(["mcp__svc__op"]);
        var handler = new CountSeqHandler(10, 200, 500, 80);
        using var http = new HttpClient(handler);
        using var session = new CodaSession(
            SignedInClaude(), McpOptions(new FakeDeferredTool("mcp__svc__op"), new FakeDeferredTool("mcp__svc__op2")),
            httpClient: http, toolSearchCoordinatorOverride: coordinator);

        var report = await session.AnalyzeContextAsync();

        Assert.True(report.IsExact);
        // Schema cost only (80 - 10 = 70), no reminder overhead.
        Assert.Equal(70, report.Categories.Single(c => c.Name == "MCP tools").Tokens);
        // The other tool is still deferred.
        Assert.Equal(0, report.Categories.Single(c => c.Name == "MCP tools (deferred, 1 tools)").Tokens);
        Assert.Equal(190 + 490 + 70, report.UsedTokens);
        // The MCP count request carries only the discovered tool's schema — no reminder, no other tool.
        Assert.Contains(handler.Bodies, b => b.Contains("mcp__svc__op", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Bodies, b => b.Contains("mcp__svc__op2", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Bodies, b => b.Contains("<deferred-tools>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeContextAsync_null_coordinator_reports_full_mcp_schema()
    {
        // The production path where no coordinator exists at all: ENABLE_TOOL_SEARCH explicitly off
        // makes CodaSession leave toolSearchCoordinator null, so AnalyzeContextAsync falls back to
        // registry.Definitions. Nothing is withheld, so the full schema is reported and no
        // informational deferred entry appears.
        using var env = new EnvVarScope("ENABLE_TOOL_SEARCH", "false");
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}"));
        using var session = new CodaSession(
            SignedInClaude(), McpOptions(FiveLargeMcpTools()), httpClient: http);

        Assert.Null(session.ToolSearchCoordinator);

        var report = await session.AnalyzeContextAsync();

        var mcpCat = report.Categories.Single(c => c.Name == "MCP tools");
        Assert.True(mcpCat.Tokens > 100, $"Expected > 100 tokens for 5 large schemas, got {mcpCat.Tokens}");
        Assert.DoesNotContain(report.Categories, c => c.Name.StartsWith("MCP tools (deferred", StringComparison.Ordinal));
    }

    /// <summary>Sets an environment variable for the duration of a test and restores it afterwards.</summary>
    private sealed class EnvVarScope : IDisposable
    {
        private readonly string name;
        private readonly string? original;

        public EnvVarScope(string name, string? value)
        {
            this.name = name;
            this.original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(this.name, this.original);
    }

    [Fact]
    public async Task AnalyzeContextAsync_deferred_category_zero_tokens_excluded_from_used_tokens()
    {
        // The "MCP tools (deferred, N tools)" category is purely informational:
        // its 0 tokens must not be added to UsedTokens or affect the Free space computation.
        // Call order: baseline(10), system(200), builtin(500) — no MCP count (all-deferred).
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);
        using var http = new HttpClient(new CountSeqHandler(10, 200, 500));
        using var session = new CodaSession(
            SignedInClaude(), McpOptions(new FakeDeferredTool("mcp__svc__op")),
            httpClient: http, toolSearchCoordinatorOverride: coordinator);

        var report = await session.AnalyzeContextAsync();

        var deferredCat = Assert.Single(report.Categories, c => c.Name.StartsWith("MCP tools (deferred", StringComparison.Ordinal));
        Assert.Equal(0, deferredCat.Tokens);

        // UsedTokens is the sum of cost-bearing categories only —
        // free space, autocompact buffer, and the deferred informational entry are excluded.
        var sumUsed = report.Categories
            .Where(c => c.Name != "Free space"
                     && c.Name != "Autocompact buffer"
                     && !c.Name.StartsWith("MCP tools (deferred", StringComparison.Ordinal))
            .Sum(c => c.Tokens);
        Assert.Equal(report.UsedTokens, sumUsed);

        // Free space = MaxTokens - UsedTokens - autocompact reserve (if any).
        var freeCat = report.Categories.Single(c => c.Name == "Free space");
        var autocompactCat = report.Categories.FirstOrDefault(c => c.Name == "Autocompact buffer");
        Assert.Equal(report.MaxTokens - report.UsedTokens - (autocompactCat?.Tokens ?? 0), freeCat.Tokens);
    }

    [Fact]
    public async Task AnalyzeContextAsync_discovered_tool_schema_is_on_wire_and_deferred_count_excludes_it()
    {
        // Finding 2 seam: a coordinator whose discovered set already contains a tool causes
        // that tool's schema to appear on the wire (positive MCP tokens) while the remaining
        // tools are reported in the informational deferred category with count = total - discovered.
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.Tst);
        coordinator.AddDiscovered(["mcp__svc__op"]);
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}"));
        using var session = new CodaSession(
            SignedInClaude(),
            McpOptions(
                new FakeDeferredTool("mcp__svc__op"),
                new FakeDeferredTool("mcp__svc__op2"),
                new FakeDeferredTool("mcp__svc__op3")),
            httpClient: http, toolSearchCoordinatorOverride: coordinator);

        var report = await session.AnalyzeContextAsync();

        // Discovered tool's schema is on the wire → positive MCP token cost.
        Assert.Contains(report.Categories, c => c.Name == "MCP tools" && c.Tokens > 0);
        // 2 of 3 tools remain deferred → deferredCount = 2.
        Assert.Contains(report.Categories, c => c.Name == "MCP tools (deferred, 2 tools)" && c.Tokens == 0);
        // No "deferred, 3 tools" entry — the discovered tool is excluded from the deferred count.
        Assert.DoesNotContain(report.Categories, c => c.Name == "MCP tools (deferred, 3 tools)");
    }
}

