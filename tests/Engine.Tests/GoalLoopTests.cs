using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Agent.Watchers;
using Engine.Tests.TestSupport;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Integration tests that drive a real <see cref="AgentLoop"/> with a
/// <see cref="GoalSupervisor"/>, verifying the goal path, unmet path, and
/// in-loop compaction.
/// </summary>
public sealed class GoalLoopTests
{
    // -----------------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------------

    /// <summary>
    /// A scripted client: each call returns the next pre-baked turn's events.
    /// When all scripted turns are exhausted, repeats the last one.
    /// </summary>
    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var index = Math.Min(this.turn, turns.Length - 1);
            this.turn++;
            foreach (var e in turns[index])
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    /// <summary>A judge whose responses are scripted from a queue; defaults to "DONE" when empty.</summary>
    private sealed class FakeJudge : IForkedAgent
    {
        private readonly Queue<string> responses;

        public FakeJudge(params string[] responses) => this.responses = new(responses);

        public Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            var next = this.responses.Count > 0 ? this.responses.Dequeue() : "DONE";
            return Task.FromResult(next);
        }
    }

    /// <summary>A user-question prompt that always returns a fixed answer and counts asks.</summary>
    private sealed class StubUserQuestion(string answer) : IUserQuestionPrompt
    {
        public int Asks { get; private set; }

        public Task<string> AskAsync(string question, IReadOnlyList<string> options, bool multiSelect, CancellationToken cancellationToken = default)
        {
            this.Asks++;
            return Task.FromResult(answer);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AgentOptions Options() => new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    private static AgentOptions OptionsWithThreshold(int threshold) =>
        new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m", AutoCompactTokenThreshold = threshold };

    private static GoalBudget Budget(int turns = 100)
        => new(TimeSpan.FromHours(1), turns, 0.25, () => TimeSpan.Zero);

    private static GoalRetryPolicy NoSleepRetry()
        => new(maxAttempts: 1, delay: (_, _) => Task.CompletedTask);

    private static GoalSupervisor MakeSupervisor(IForkedAgent judge, GoalBudget? budget = null)
        => new(judge, "ship the feature", budget ?? Budget(), NoSleepRetry());

    /// <summary>A text-only stop turn (no tool calls).</summary>
    private static IReadOnlyList<AssistantStreamEvent> TextTurn(string text = "done")
        =>
        [
            AssistantStreamEvent.Delta(text),
            AssistantStreamEvent.Finished("end_turn"),
        ];

    // -----------------------------------------------------------------------
    // (a) Met path: judge returns CONTINUE once then DONE
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Met_path_injects_one_nudge_then_sets_LastGoalStatus_Met()
    {
        // The model produces a text-only stop turn every iteration.
        // Judge: first call → CONTINUE (nudge injected); second call → DONE (loop exits Met).
        var judge = new FakeJudge("CONTINUE: not done yet", "DONE");
        var supervisor = MakeSupervisor(judge);

        var loop = new AgentLoop(
            new ScriptedClient(TextTurn()),    // repeats last turn
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            goal: supervisor);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.NotNull(loop.LastGoalStatus);
        Assert.Equal(GoalOutcome.Met, loop.LastGoalStatus!.Outcome);

        // One nudge message was injected from the CONTINUE verdict.
        var nudgeMessages = history
            .Where(m => m.Role == ChatRole.User
                && m.Content.Count == 1
                && m.Content[0] is TextBlock t
                && t.Text.Contains("not done yet"))
            .ToList();
        Assert.Single(nudgeMessages);
    }

    // -----------------------------------------------------------------------
    // (b) Exhausted + no userQuestion → Unmet
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Exhausted_budget_with_null_userQuestion_sets_LastGoalStatus_Unmet()
    {
        // Budget of 0 turns → exhausted on first stop; no userQuestion → MarkStoppedUnmet.
        var judge = new FakeJudge("DONE"); // judge never reached (budget is immediately exhausted)
        var supervisor = MakeSupervisor(judge, Budget(turns: 0));

        var loop = new AgentLoop(
            new ScriptedClient(TextTurn()),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            goal: supervisor);

        // userQuestion is null (default) — escalation goes straight to MarkStoppedUnmet.
        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.NotNull(loop.LastGoalStatus);
        Assert.Equal(GoalOutcome.Unmet, loop.LastGoalStatus!.Outcome);
    }

    // -----------------------------------------------------------------------
    // (c) In-loop compaction is invoked when history exceeds the threshold
    // -----------------------------------------------------------------------

    [Fact]
    public async Task In_loop_compaction_callback_is_invoked_when_history_exceeds_threshold()
    {
        var compactCalled = false;

        Task CompactAsync(List<ChatMessage> h, CancellationToken ct)
        {
            compactCalled = true;
            return Task.CompletedTask;
        }

        // Use a threshold of 1 token so any non-empty history triggers compaction.
        var judge = new FakeJudge("DONE");
        var supervisor = MakeSupervisor(judge);

        var loop = new AgentLoop(
            new ScriptedClient(TextTurn("a long assistant message that exceeds one token easily")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            OptionsWithThreshold(threshold: 1),
            goal: supervisor,
            compactAsync: CompactAsync);

        // Seed some non-trivial history so the estimator fires.
        var history = new List<ChatMessage>
        {
            ChatMessage.UserText("a reasonably long user message that pushes the token estimate over 1"),
        };

        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.True(compactCalled, "compactAsync should have been called because the history exceeded the threshold.");
    }

    // -----------------------------------------------------------------------
    // (d) Escalation answered with guidance → one bounded extension → completes Met
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // (e) In-loop compaction failure is swallowed AND logged at Debug
    // -----------------------------------------------------------------------

    [Fact]
    public async Task In_loop_compaction_failure_is_swallowed_and_logged_at_debug()
    {
        // compactAsync throws → the run must continue (best-effort) AND a Debug
        // line must surface the swallowed failure.
        static Task CompactAsync(List<ChatMessage> h, CancellationToken ct)
            => throw new InvalidOperationException("compaction boom");

        var judge = new FakeJudge("DONE");
        var supervisor = MakeSupervisor(judge);
        var logger = new CapturingLogger();

        var loop = new AgentLoop(
            new ScriptedClient(TextTurn("a long assistant message that exceeds one token easily")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            OptionsWithThreshold(threshold: 1),
            goal: supervisor,
            compactAsync: CompactAsync,
            logger: logger);

        var history = new List<ChatMessage>
        {
            ChatMessage.UserText("a reasonably long user message that pushes the token estimate over 1"),
        };

        // The run completes normally despite the compaction failure (swallow preserved).
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);
        Assert.NotNull(loop.LastGoalStatus);
        Assert.Equal(GoalOutcome.Met, loop.LastGoalStatus!.Outcome);

        var compactionLine = Assert.Single(logger.Entries, e => e.Message.Contains("in-loop compaction failed"));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, compactionLine.Level);
        Assert.Contains("iteration=", compactionLine.Message);
        Assert.NotNull(compactionLine.Exception);
        Assert.Contains("compaction boom", compactionLine.Exception!.Message);
    }

    [Fact]
    public async Task Escalation_answered_grants_extension_then_completes_Met()
    {
        // Budget of 0 turns → exhausted on the first stop → escalate. The operator answers
        // with guidance (not "Stop"), so the loop grants the single extension and continues;
        // the judge then returns DONE on the next stop → Met.
        var judge = new FakeJudge("DONE"); // first real judge call happens after the extension
        var supervisor = MakeSupervisor(judge, Budget(turns: 0));
        var userQuestion = new StubUserQuestion("Provide guidance and continue");

        var loop = new AgentLoop(
            new ScriptedClient(TextTurn()),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            goal: supervisor,
            userQuestion: userQuestion);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.Equal(1, userQuestion.Asks);
        Assert.NotNull(loop.LastGoalStatus);
        Assert.Equal(GoalOutcome.Met, loop.LastGoalStatus!.Outcome);
        Assert.True(loop.LastGoalStatus.ExtensionUsed);

        // The operator's guidance was injected back into the conversation.
        Assert.Contains(history, m => m.Role == ChatRole.User
            && m.Content[0] is TextBlock t
            && t.Text.Contains("Operator guidance"));
    }
}
