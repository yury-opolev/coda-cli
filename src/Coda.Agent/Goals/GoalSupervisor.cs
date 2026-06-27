using System.Text;
using Coda.Agent.Watchers;
using LlmClient;

namespace Coda.Agent.Goals;

/// <summary>
/// The autonomous "keep going until the goal is truly met, else ask" lever. Consulted by
/// the agent loop at each natural stop. Owns the judge (with retry/backoff), the budget,
/// and the verdict log. Failure of the judge fails CLOSED (keep working) because the
/// budget guarantees termination.
/// </summary>
/// <remarks>Not thread-safe; consulted from the single agent loop.</remarks>
public sealed class GoalSupervisor
{
    private readonly IForkedAgent judge;
    private readonly string goal;
    private readonly GoalBudget budget;
    private readonly GoalRetryPolicy retry;

    private GoalOutcome outcome = GoalOutcome.None;
    private string? lastRemaining;
    private bool escalated;

    public GoalSupervisor(IForkedAgent judge, string goal, GoalBudget budget, GoalRetryPolicy? retry = null)
    {
        this.judge = judge ?? throw new ArgumentNullException(nameof(judge));
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        this.goal = goal;
        this.budget = budget ?? throw new ArgumentNullException(nameof(budget));
        this.retry = retry ?? new GoalRetryPolicy();
    }

    public GoalBudget Budget => this.budget;

    public GoalStatus Status => new(
        this.outcome,
        this.lastRemaining,
        this.budget.Continuations,
        this.budget.Elapsed,
        this.escalated,
        this.budget.ExtensionUsed);

    /// <summary>
    /// Decide what happens when the model wants to stop. When the return is a
    /// <see cref="GoalVerdict.Escalate"/>, the caller MUST invoke either
    /// <see cref="TryGrantExtension"/> (then continue) or <see cref="MarkStoppedUnmet"/>
    /// (then stop) before calling this again — otherwise the exhausted budget re-escalates
    /// indefinitely.
    /// </summary>
    public async Task<GoalVerdict> EvaluateAsync(ReplHookContext context, CancellationToken cancellationToken)
    {
        if (this.budget.IsExhausted)
        {
            if (this.budget.ExtensionUsed)
            {
                this.outcome = GoalOutcome.Unmet;
                return new GoalVerdict.Stop(Met: false);
            }

            // The goal is not met at the bound. Mark Unmet now so Status is consistent for a
            // caller inspecting it on the Escalate verdict; it is overwritten to Met only if
            // an extension is granted and the judge later returns DONE.
            this.escalated = true;
            this.outcome = GoalOutcome.Unmet;
            return new GoalVerdict.Escalate(this.BuildEscalationQuestion(), this.lastRemaining);
        }

        var recent = LastAssistantText(context.Messages);
        var userMessage = GoalJudgePrompt.BuildUserMessage(this.goal, recent);

        var (ok, response) = await this.retry.RunAsync(
            ct => this.judge.RunAsync(GoalJudgePrompt.SystemPrompt, [ChatMessage.UserText(userMessage)], ct),
            cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            // Fail closed: a flaky judge must never end an unfinished run. Budget bounds it.
            this.budget.RecordContinuation();
            return new GoalVerdict.Continue(
                "The completion judge is temporarily unavailable. Keep working toward the goal:\n" + this.goal);
        }

        if (GoalJudgePrompt.IsComplete(response))
        {
            this.outcome = GoalOutcome.Met;
            return new GoalVerdict.Stop(Met: true);
        }

        this.lastRemaining = GoalJudgePrompt.Remaining(response);
        this.budget.RecordContinuation();
        return new GoalVerdict.Continue(
            $"The goal is not yet complete. Still remaining: {this.lastRemaining}\nKeep working toward the goal, then stop only when it is fully done:\n{this.goal}");
    }

    /// <summary>Called by the loop after an answered escalation, to extend the budget once.</summary>
    public bool TryGrantExtension() => this.budget.GrantExtension();

    /// <summary>Called by the loop when an escalation goes unanswered (or the operator stops).</summary>
    public void MarkStoppedUnmet() => this.outcome = GoalOutcome.Unmet;

    private string BuildEscalationQuestion() =>
        $"""
        I've reached my autonomy budget and the goal is not fully met.
        Goal: {this.goal}
        Outstanding: {this.lastRemaining ?? "unspecified"}
        How should I proceed? Provide guidance to continue, or say to stop.
        """;

    private static string LastAssistantText(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != ChatRole.Assistant)
            {
                continue;
            }

            var builder = new StringBuilder();
            foreach (var block in messages[i].Content)
            {
                if (block is TextBlock text)
                {
                    builder.AppendLine(text.Text);
                }
            }

            return builder.ToString().Trim();
        }

        return string.Empty;
    }
}
