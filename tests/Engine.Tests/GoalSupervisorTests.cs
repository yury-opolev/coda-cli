using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Agent.Watchers;
using LlmClient;
using Xunit;

namespace Engine.Tests;

public sealed class GoalSupervisorTests
{
    private sealed class FakeJudge : IForkedAgent
    {
        private readonly Queue<Func<string>> responses;
        public int Calls { get; private set; }
        public FakeJudge(params Func<string>[] responses) => this.responses = new(responses);
        public Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
        {
            this.Calls++;
            var next = this.responses.Count > 0 ? this.responses.Dequeue() : (() => "DONE");
            return Task.FromResult(next());
        }
    }

    private static ReplHookContext Ctx(string assistantText) => new()
    {
        Messages = [new ChatMessage(ChatRole.Assistant, [new TextBlock(assistantText)])],
        SystemPrompt = "sys",
        WorkingDirectory = ".",
    };

    private static GoalSupervisor Make(IForkedAgent judge, GoalBudget budget)
        => new(judge, "ship the feature", budget,
               new GoalRetryPolicy(maxAttempts: 2, delay: (_, _) => Task.CompletedTask));

    private static GoalBudget Budget(int turns = 100, TimeSpan? elapsed = null)
        => new(TimeSpan.FromHours(1), turns, 0.25, () => elapsed ?? TimeSpan.Zero);

    [Fact]
    public async Task Done_Verdict_Stops_As_Met()
    {
        var sup = Make(new FakeJudge(() => "DONE"), Budget());
        var verdict = await sup.EvaluateAsync(Ctx("did it"), default);

        var stop = Assert.IsType<GoalVerdict.Stop>(verdict);
        Assert.True(stop.Met);
        Assert.Equal(GoalOutcome.Met, sup.Status.Outcome);
        Assert.Equal(0, sup.Status.Continuations); // DONE is not a nudge
    }

    [Fact]
    public async Task Continue_Verdict_Records_Remaining_And_Nudges()
    {
        var sup = Make(new FakeJudge(() => "CONTINUE: write the tests"), Budget());
        var verdict = await sup.EvaluateAsync(Ctx("partial"), default);

        var cont = Assert.IsType<GoalVerdict.Continue>(verdict);
        Assert.Contains("write the tests", cont.Nudge);
        Assert.Equal(1, sup.Status.Continuations);
    }

    [Fact]
    public async Task Judge_Failure_Fails_Closed_To_Continue()
    {
        var judge = new FakeJudge(() => throw new InvalidOperationException("down"),
                                  () => throw new InvalidOperationException("down"));
        var sup = Make(judge, Budget());

        var verdict = await sup.EvaluateAsync(Ctx("x"), default);

        Assert.IsType<GoalVerdict.Continue>(verdict);
    }

    [Fact]
    public async Task Exhausted_Budget_Escalates()
    {
        var sup = Make(new FakeJudge(() => "CONTINUE: more"), Budget(turns: 0));
        var verdict = await sup.EvaluateAsync(Ctx("x"), default);

        Assert.IsType<GoalVerdict.Escalate>(verdict);
        Assert.True(sup.Status.Escalated);
        Assert.Equal(GoalOutcome.Unmet, sup.Status.Outcome); // Status consistent on Escalate
    }

    [Fact]
    public async Task Exhausted_After_Extension_Stops_Unmet()
    {
        var sup = Make(new FakeJudge(() => "CONTINUE: more"), Budget(turns: 0));
        await sup.EvaluateAsync(Ctx("x"), default);   // escalates
        sup.MarkStoppedUnmet();

        Assert.Equal(GoalOutcome.Unmet, sup.Status.Outcome);
    }

    [Fact]
    public async Task Second_Evaluate_After_Granted_Extension_Still_Exhausted_Stops_Unmet()
    {
        // Time-exhausted budget: extension raises the ceiling but elapsed still exceeds it.
        var budget = new GoalBudget(TimeSpan.FromMinutes(1), 100, 0.25, () => TimeSpan.FromMinutes(10));
        var sup = Make(new FakeJudge(() => "CONTINUE: more"), budget);

        var first = await sup.EvaluateAsync(Ctx("x"), default);
        Assert.IsType<GoalVerdict.Escalate>(first);
        Assert.True(sup.TryGrantExtension());

        var second = await sup.EvaluateAsync(Ctx("x"), default);
        var stop = Assert.IsType<GoalVerdict.Stop>(second);
        Assert.False(stop.Met);
        Assert.Equal(GoalOutcome.Unmet, sup.Status.Outcome);
        Assert.True(sup.Status.ExtensionUsed);
    }

    [Fact]
    public async Task Escalation_Question_Carries_Prior_Remaining()
    {
        // First evaluate records "finish the parser"; the second exhausts (turns: 1) and escalates.
        var sup = Make(
            new FakeJudge(() => "CONTINUE: finish the parser", () => "CONTINUE: finish the parser"),
            Budget(turns: 1));

        await sup.EvaluateAsync(Ctx("x"), default);            // CONTINUE, records remaining
        var verdict = await sup.EvaluateAsync(Ctx("x"), default); // budget now exhausted → escalate

        var escalate = Assert.IsType<GoalVerdict.Escalate>(verdict);
        Assert.Equal("finish the parser", escalate.Remaining);
        Assert.Contains("finish the parser", escalate.Question);
    }
}
