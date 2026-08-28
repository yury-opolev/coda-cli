//! Goal supervisor: autonomous "keep going until done, else ask" lever.
//!
//! The supervisor is consulted at every natural stop while a goal is active.
//! It owns the judge (with retry / backoff), the budget, and the escalation
//! state machine.  Failures of the judge fail **open** (return `Continue`)
//! because the budget guarantees eventual termination.
//!
//! Not thread-safe; owned and mutated exclusively by the single agent loop.

use coda_llm::{Content, Message, Role};
use tokio_util::sync::CancellationToken;

pub mod budget;
pub mod judge;
pub mod retry;
pub mod verdict;

pub use budget::GoalBudget;
pub use retry::GoalRetryPolicy;
pub use verdict::{GoalOutcome, GoalStatus, GoalVerdict};

use judge::SYSTEM_PROMPT;

/// An isolated forked-agent call used by the goal judge.
///
/// Implementors spawn (or simulate) a separate LLM call and return the raw
/// text response.  The loop provides a real implementation backed by
/// `coda_llm::LlmClient`; tests inject a mock.
#[async_trait::async_trait]
pub trait ForkedAgent: Send + Sync {
    async fn run(
        &self,
        system: &str,
        messages: Vec<Message>,
        cancel: CancellationToken,
    ) -> anyhow::Result<String>;
}

/// Autonomous goal supervisor.
///
/// Call [`GoalSupervisor::evaluate`] at every natural stop.  When the verdict
/// is [`GoalVerdict::Escalate`], the caller MUST resolve it with exactly one of:
/// - [`GoalSupervisor::try_grant_extension`] (extend the budget; continue), or
/// - [`GoalSupervisor::mark_stopped_unmet`] (accept failure; stop).
pub struct GoalSupervisor {
    judge: Box<dyn ForkedAgent>,
    goal: String,
    budget: GoalBudget,
    retry: GoalRetryPolicy,
    outcome: GoalOutcome,
    last_remaining: Option<String>,
    /// `true` after the first `Escalate` verdict is returned, so callers
    /// (tests and the serve layer) can observe the escalation lifecycle.
    escalated: bool,
}

impl GoalSupervisor {
    pub fn new(
        judge: Box<dyn ForkedAgent>,
        goal: impl Into<String>,
        budget: GoalBudget,
        retry: Option<GoalRetryPolicy>,
    ) -> Self {
        let goal = goal.into();
        assert!(!goal.trim().is_empty(), "goal must not be empty");
        Self {
            judge,
            goal,
            budget,
            retry: retry.unwrap_or_default(),
            outcome: GoalOutcome::None,
            last_remaining: None,
            escalated: false,
        }
    }

    /// Current status snapshot (for the loop to expose as `LastGoalStatus`).
    pub fn status(&self) -> GoalStatus {
        GoalStatus {
            outcome: self.outcome,
            remaining: self.last_remaining.clone(),
            continuations: self.budget.continuations(),
            elapsed: self.budget.elapsed(),
            escalated: self.escalated,
            extension_used: self.budget.extension_used(),
        }
    }

    /// Decide what happens at a natural stop.
    ///
    /// When the result is `GoalVerdict::Escalate`, the caller MUST call
    /// `try_grant_extension` (then continue) or `mark_stopped_unmet` (then
    /// stop) before calling `evaluate` again; otherwise the exhausted budget
    /// re-escalates indefinitely.
    ///
    /// Cancellation during the judge call is treated as a judge failure
    /// (fail-open → `Continue`).  The loop's own cancel-check prevents further
    /// iterations, so the run still terminates promptly.
    pub async fn evaluate(
        &mut self,
        recent_assistant_text: &str,
        cancel: CancellationToken,
    ) -> GoalVerdict {
        if self.budget.is_exhausted() {
            if self.budget.extension_used() {
                // Extension already spent — budget is truly exhausted.
                self.outcome = GoalOutcome::Unmet;
                return GoalVerdict::Stop { met: false };
            }

            // Mark Unmet now so `status()` is consistent on the Escalate path;
            // it is overwritten to Met only if the judge later returns DONE.
            self.escalated = true;
            self.outcome = GoalOutcome::Unmet;
            return GoalVerdict::Escalate {
                question: self.build_escalation_question(),
                remaining: self.last_remaining.clone(),
            };
        }

        let user_msg = judge::build_user_message(&self.goal, recent_assistant_text);
        let messages = vec![Message::user(user_msg)];

        // Fail-open: if the judge can't be reached, keep working.  Budget
        // ensures the loop still terminates.
        let judge_ref = &*self.judge;
        let result = self
            .retry
            .run(
                |ct| {
                    let msgs = messages.clone();
                    async move { judge_ref.run(SYSTEM_PROMPT, msgs, ct).await }
                },
                cancel,
            )
            .await;

        let nudge_unavailable = format!(
            "The completion judge is temporarily unavailable. Keep working toward the goal:\n{}",
            self.goal
        );

        match result {
            // Cancellation or all-attempts-failed — fail-open.
            Err(_) | Ok((false, _)) => {
                // §8 item 19: judge failure fails open — never stops an unfinished run.
                self.budget.record_continuation();
                GoalVerdict::Continue { nudge: nudge_unavailable }
            }
            Ok((true, Some(response))) => {
                if judge::is_complete(&response) {
                    self.outcome = GoalOutcome::Met;
                    return GoalVerdict::Stop { met: true };
                }

                self.last_remaining = Some(judge::remaining(&response));
                self.budget.record_continuation();
                GoalVerdict::Continue {
                    nudge: format!(
                        "The goal is not yet complete. Still remaining: {}\n\
                         Keep working toward the goal, then stop only when it is fully done:\n{}",
                        self.last_remaining.as_deref().unwrap_or("unspecified"),
                        self.goal
                    ),
                }
            }
            Ok((true, None)) => {
                // Shouldn't happen (true + None) but fail-open.
                self.budget.record_continuation();
                GoalVerdict::Continue { nudge: nudge_unavailable }
            }
        }
    }

    /// Called by the loop after an answered escalation: extend the budget.
    /// Returns `false` when the extension was already spent.
    pub fn try_grant_extension(&mut self) -> bool {
        self.budget.grant_extension()
    }

    /// Called by the loop when an escalation goes unanswered (headless) or
    /// the operator chose to stop.
    pub fn mark_stopped_unmet(&mut self) {
        self.outcome = GoalOutcome::Unmet;
    }

    fn build_escalation_question(&self) -> String {
        format!(
            "I've reached my autonomy budget and the goal is not fully met.\n\
             Goal: {}\nOutstanding: {}\n\
             How should I proceed? Provide guidance to continue, or say to stop.",
            self.goal,
            self.last_remaining.as_deref().unwrap_or("unspecified")
        )
    }
}

/// Extract the last assistant turn's text blocks from a history slice.
pub fn last_assistant_text(history: &[Message]) -> String {
    for msg in history.iter().rev() {
        if msg.role != Role::Assistant {
            continue;
        }
        let parts: Vec<&str> = msg
            .content
            .iter()
            .filter_map(|b| if let Content::Text(t) = b { Some(t.as_str()) } else { None })
            .collect();
        return parts.join("\n").trim().to_owned();
    }
    String::new()
}

/// Namespace for the judge prompt helpers (mirrors the C# static class).
pub struct GoalJudgePrompt;

impl GoalJudgePrompt {
    pub fn is_complete(response: &str) -> bool {
        judge::is_complete(response)
    }
    pub fn remaining(response: &str) -> String {
        judge::remaining(response)
    }
    pub fn build_user_message(goal: &str, recent_output: &str) -> String {
        judge::build_user_message(goal, recent_output)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    struct AlwaysFailsJudge;

    #[async_trait::async_trait]
    impl ForkedAgent for AlwaysFailsJudge {
        async fn run(&self, _: &str, _: Vec<Message>, _: CancellationToken) -> anyhow::Result<String> {
            Err(anyhow::anyhow!("unavailable"))
        }
    }

    struct ScriptedJudge {
        responses: std::sync::Mutex<std::collections::VecDeque<&'static str>>,
    }

    impl ScriptedJudge {
        fn new(responses: Vec<&'static str>) -> Self {
            Self { responses: std::sync::Mutex::new(responses.into_iter().collect()) }
        }
    }

    #[async_trait::async_trait]
    impl ForkedAgent for ScriptedJudge {
        async fn run(&self, _: &str, _: Vec<Message>, _: CancellationToken) -> anyhow::Result<String> {
            let r = self.responses.lock().unwrap().pop_front().expect("ScriptedJudge ran out");
            Ok(r.to_owned())
        }
    }

    fn budget_cont(max_cont: u32) -> GoalBudget {
        GoalBudget::new(Duration::MAX, max_cont, 0.5, || Duration::ZERO)
    }

    // §8 item 19: judge failure fails open → Continue, RecordContinuation.
    #[tokio::test]
    async fn judge_failure_fails_open() {
        let mut sup = GoalSupervisor::new(
            Box::new(AlwaysFailsJudge),
            "finish tests",
            budget_cont(10),
            Some(GoalRetryPolicy::for_tests()),
        );
        let verdict = sup.evaluate("nothing done", CancellationToken::new()).await;
        assert!(
            matches!(verdict, GoalVerdict::Continue { .. }),
            "expected Continue, got {verdict:?}"
        );
        assert_eq!(sup.budget.continuations(), 1);
    }

    // §8 item 20: Escalate, then TryGrantExtension raises both ceilings.
    #[tokio::test]
    async fn escalation_and_extension_once() {
        // Budget with 0 allowed continuations: immediately exhausted.
        let mut sup = GoalSupervisor::new(
            Box::new(AlwaysFailsJudge),
            "finish tests",
            budget_cont(0),
            Some(GoalRetryPolicy::for_tests()),
        );
        // First evaluate: budget exhausted, extension unused → Escalate.
        let v = sup.evaluate("nothing", CancellationToken::new()).await;
        assert!(matches!(v, GoalVerdict::Escalate { .. }), "expected Escalate, got {v:?}");
        assert!(sup.escalated);

        // Grant extension: succeeds once.
        assert!(sup.try_grant_extension());
        assert!(sup.budget.extension_used());
        assert!(!sup.budget.is_exhausted(), "budget should not be exhausted right after extension");

        // Second grant attempt fails.
        assert!(!sup.try_grant_extension());
    }

    // §8 item 20: after extension, judge failure forces continuation until
    // the raised ceiling is hit, at which point Stop(false) is returned.
    #[tokio::test]
    async fn after_extension_exhaustion_gives_stop_not_met() {
        // Budget: 0 continuations, extension_fraction = 0.5.
        // After grant_extension: max_continuations becomes 1 (raised by at least 1).
        let mut sup = GoalSupervisor::new(
            Box::new(AlwaysFailsJudge),
            "goal",
            budget_cont(0),
            Some(GoalRetryPolicy::for_tests()),
        );
        // Escalate.
        let _ = sup.evaluate("nothing", CancellationToken::new()).await;
        sup.try_grant_extension();

        // judge fails → record_continuation → continuations becomes 1 → exhausted (1 >= 1).
        let v = sup.evaluate("still nothing", CancellationToken::new()).await;
        // continuations = 1 >= max_continuations = 1 → next call sees exhausted + extension used
        // But evaluate is called once more below:
        assert!(matches!(v, GoalVerdict::Continue { .. }), "first call after extension fails open");

        // Now continuations = 1, max_continuations = 1 → exhausted + extension used → Stop.
        let v2 = sup.evaluate("still nothing", CancellationToken::new()).await;
        assert!(matches!(v2, GoalVerdict::Stop { met: false }), "expected Stop(false), got {v2:?}");
    }

    // §8 item 20: headless path — mark_stopped_unmet sets outcome.
    #[test]
    fn mark_stopped_unmet_sets_outcome() {
        let mut sup = GoalSupervisor::new(
            Box::new(AlwaysFailsJudge),
            "goal",
            budget_cont(10),
            Some(GoalRetryPolicy::for_tests()),
        );
        sup.mark_stopped_unmet();
        assert_eq!(sup.status().outcome, GoalOutcome::Unmet);
        assert!(!sup.status().is_successful());
    }

    // When judge says DONE the outcome is Met and the loop can stop.
    #[tokio::test]
    async fn judge_done_returns_stop_met() {
        let mut sup = GoalSupervisor::new(
            Box::new(ScriptedJudge::new(vec!["DONE"])),
            "write a test",
            budget_cont(5),
            Some(GoalRetryPolicy::for_tests()),
        );
        let v = sup.evaluate("tests written", CancellationToken::new()).await;
        assert!(matches!(v, GoalVerdict::Stop { met: true }), "expected Stop(true), got {v:?}");
        assert_eq!(sup.status().outcome, GoalOutcome::Met);
        assert!(sup.status().is_successful());
    }

    #[test]
    fn last_assistant_text_finds_most_recent_turn() {
        use coda_llm::Content;
        let history = vec![
            Message::user("hello"),
            Message::new(Role::Assistant, vec![Content::Text("first".into())]),
            Message::user("more"),
            Message::new(Role::Assistant, vec![Content::Text("second".into())]),
        ];
        assert_eq!(last_assistant_text(&history), "second");
    }

    #[test]
    fn last_assistant_text_empty_when_no_assistant_turn() {
        let history = vec![Message::user("hello")];
        assert_eq!(last_assistant_text(&history), "");
    }
}
