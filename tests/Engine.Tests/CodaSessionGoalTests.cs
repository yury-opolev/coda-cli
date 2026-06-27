using System.Net;
using System.Text;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Verifies that CodaSession builds a GoalSupervisor when a goal is set and
/// surfaces the resulting <see cref="GoalStatus"/> on <see cref="RunResult.Goal"/>.
/// Uses the same SeqHandler / SignedInClaude pattern as CodaSessionTests.
/// </summary>
public sealed class CodaSessionGoalTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_goal_").FullName;

    /// <summary>Feeds canned SSE response bodies sequentially; repeats the last one when exhausted.</summary>
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

    private static CredentialManager SignedInClaude()
    {
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(store, [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        creds.StoreAsync(ClaudeAiProvider.Id, new Credential { ProviderId = ClaudeAiProvider.Id, Kind = CredentialKind.OAuth, AccessToken = "AT" })
             .GetAwaiter().GetResult();
        return creds;
    }

    private static string Sse(string body) =>
        $"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{body}\"}}}}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private SessionOptions Options() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    // -----------------------------------------------------------------------
    // Goal = null → RunResult.Goal is null
    // -----------------------------------------------------------------------

    [Fact]
    public async Task No_goal_run_leaves_Goal_null()
    {
        using var http = new HttpClient(new SeqHandler(Sse("hello")));
        using var session = new CodaSession(SignedInClaude(), this.Options(), httpClient: http);

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Null(result.Goal);
    }

    // -----------------------------------------------------------------------
    // Goal set + judge says DONE on first evaluation → outcome Met
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Goal_met_on_first_evaluation_sets_Outcome_Met()
    {
        // req1: main turn text, req2: judge returns DONE → supervisor stops loop
        using var http = new HttpClient(new SeqHandler(
            Sse("I completed the task"),
            Sse("DONE")));

        var options = this.Options() with { Goal = "ship the feature" };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var result = await session.RunAsync("start");

        Assert.True(result.Success);
        Assert.NotNull(result.Goal);
        Assert.Equal(GoalOutcome.Met, result.Goal!.Outcome);
        Assert.True(result.Goal.IsSuccessful);
    }

    // -----------------------------------------------------------------------
    // Goal set + judge says CONTINUE once then DONE → outcome Met + nudge injected
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Goal_met_after_one_continue_sets_Outcome_Met_and_injects_nudge()
    {
        // req1: main turn, req2: judge CONTINUE, req3: main turn, req4: judge DONE
        using var http = new HttpClient(new SeqHandler(
            Sse("step one"),
            Sse("CONTINUE: still some work left"),
            Sse("step two"),
            Sse("DONE")));

        var options = this.Options() with { Goal = "finish the task" };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var result = await session.RunAsync("start");

        Assert.True(result.Success);
        Assert.Equal("step two", result.FinalText);

        // A nudge containing the goal text was injected as a user message.
        Assert.Contains(session.History, m =>
            m.Role == ChatRole.User
            && m.Content.Any(c => c is TextBlock t && t.Text.Contains("finish the task")));

        Assert.NotNull(result.Goal);
        Assert.Equal(GoalOutcome.Met, result.Goal!.Outcome);
        Assert.Equal(1, result.Goal.Continuations);
    }

    // -----------------------------------------------------------------------
    // RunResult exposes a nullable GoalStatus init-property (record shape check)
    // -----------------------------------------------------------------------

    [Fact]
    public void RunResult_record_accepts_Goal_init_property()
    {
        // Static check: the property exists and is nullable GoalStatus.
        var result = new RunResult(true, "text", [], "end_turn", null)
        {
            Goal = new GoalStatus(GoalOutcome.Met, null, 3, TimeSpan.FromSeconds(10), false, false),
        };

        Assert.NotNull(result.Goal);
        Assert.Equal(GoalOutcome.Met, result.Goal!.Outcome);
    }

    public void Dispose() => Directory.Delete(this.root, recursive: true);
}
