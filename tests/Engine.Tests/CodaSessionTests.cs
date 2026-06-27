using Coda.Agent;
using Coda.Sdk;
using Engine.Tests.TestSupport;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using static Engine.Tests.TestSupport.CredentialFixtures;
using static Engine.Tests.TestSupport.SseTestHandler;

namespace Engine.Tests;

public sealed class CodaSessionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_session_").FullName;

    private const string RunCommandToolTurn = """
        data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"t1","name":"run_command"}}

        data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\":\"rm -rf /\"}"}}

        data: {"type":"content_block_stop","index":0}

        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"}}

        data: {"type":"message_stop"}

        """;

    private sealed class DenyPrompt : IPermissionPrompt
    {
        public int Calls { get; private set; }

        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult(false);
        }
    }

    private const string ToolTurn = """
        data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"t1","name":"list_dir"}}

        data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{}"}}

        data: {"type":"content_block_stop","index":0}

        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"}}

        data: {"type":"message_stop"}

        """;

    private SessionOptions Options(PermissionMode mode = PermissionMode.BypassPermissions) => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = mode,
    };

    [Fact]
    public async Task RunAsync_returns_final_text_and_keeps_history()
    {
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        using var session = new CodaSession(SignedInClaude(), this.Options(), httpClient: http);

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Equal("hello world", result.FinalText);
        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal(2, session.History.Count); // user + assistant
    }

    [Fact]
    public async Task RunAsync_multi_turn_accumulates_history()
    {
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        using var session = new CodaSession(SignedInClaude(), this.Options(), httpClient: http);

        await session.RunAsync("first");
        await session.RunAsync("second");

        Assert.Equal(4, session.History.Count);
    }

    [Fact]
    public async Task Bypass_runs_a_tool_without_a_prompt_and_records_it()
    {
        // Turn 1 -> list_dir (mutating? no, read-only — but exercises the tool path),
        // Turn 2 -> final text.
        using var http = new HttpClient(new SseTestHandler(ToolTurn, TextTurn));
        using var session = new CodaSession(SignedInClaude(), this.Options(PermissionMode.BypassPermissions), httpClient: http);

        var result = await session.RunAsync("list the dir");

        Assert.True(result.Success);
        Assert.Contains(result.ToolCalls, c => c.Name == "list_dir");
        Assert.Equal("hello world", result.FinalText);
    }

    [Fact]
    public async Task Unknown_provider_returns_failure()
    {
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        var options = this.Options() with { ProviderId = "github-copilot" }; // no creds/handler will 401 -> but factory supports it; use a bogus id instead
        var bogus = options with { ProviderId = "nope" };
        using var session = new CodaSession(SignedInClaude(), bogus, httpClient: http);

        var result = await session.RunAsync("hi");

        Assert.False(result.Success);
        Assert.Contains("No chat client", result.Error);
    }

    [Fact]
    public async Task Goal_stop_hook_continues_until_the_judge_says_done()
    {
        // req1 main: "step one" (end_turn) -> req2 judge: CONTINUE -> req3 main: "step two"
        // (end_turn) -> req4 judge: DONE -> stop.
        using var http = new HttpClient(new SseTestHandler(
            Text("step one"),
            Text("CONTINUE: not finished"),
            Text("step two"),
            Text("DONE")));
        var options = this.Options() with { Goal = "finish the task", MaxStopContinuations = 5 };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var result = await session.RunAsync("start");

        Assert.True(result.Success);
        // The nudge was injected once and a second assistant turn happened.
        Assert.Contains(session.History, m => m.Role == ChatRole.User
            && m.Content.Any(c => c is TextBlock t && t.Text.Contains("finish the task")));
        Assert.Equal("step two", result.FinalText);
    }

    [Fact]
    public async Task Bypass_classifier_escalates_a_risky_command_to_the_interactive_prompt()
    {
        // req1 main: run_command rm -rf (tool_use) -> req2 classifier: ASK -> inner prompt
        // denies -> tool denied -> req3 main: end_turn.
        using var http = new HttpClient(new SseTestHandler(
            RunCommandToolTurn,
            Text("ASK: rm -rf is destructive"),
            Text("stopped")));
        var deny = new DenyPrompt();
        var options = this.Options(PermissionMode.BypassPermissions) with
        {
            EnableBypassClassifier = true,
            InteractivePrompt = deny,
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var result = await session.RunAsync("clean up");

        Assert.True(result.Success);
        Assert.Equal(1, deny.Calls); // the risky action was escalated, not blanket-allowed
        Assert.Contains(result.ToolCalls, c => c.Name == "run_command" && c.IsError); // and was denied
    }

    [Fact]
    public async Task Defaults_off_no_hooks_no_classifier_behaves_as_before()
    {
        using var http = new HttpClient(new SseTestHandler(TextTurn));
        using var session = new CodaSession(SignedInClaude(), this.Options(), httpClient: http);

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Equal("hello world", result.FinalText);
        Assert.Equal(2, session.History.Count); // user + assistant, no injected turns
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
