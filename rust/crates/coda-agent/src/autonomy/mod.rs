//! Autonomy supervisor: the "keep going until done" lever for a goal run.
//!
//! The supervisor is consulted at every natural stop while a goal is active.
//! It owns the completion judge (with retry / backoff), the budget, and the
//! escalation state machine.  Failures of the completion judge fail **open**
//! (return `Continue`) because the budget guarantees eventual termination.
//!
//! This module is the home for the wider autonomy machinery described in
//! `docs/superpowers/specs/2026-09-15-really-autonomous-agents-design.md`:
//! the assumption ledger, stuck detection, the proxy answerer, the
//! permission resolver and the recovery guard land here alongside
//! `completion`.
//!
//! Not thread-safe; owned and mutated exclusively by the single agent loop.

use std::sync::Arc;

use coda_llm::{Content, Message, Role};
use tokio_util::sync::CancellationToken;

pub mod answerer;
pub mod budget;
pub mod completion;
pub mod ledger;
pub mod permission;
pub mod plan;
pub mod recovery;
pub mod retry;
pub mod stuck;
pub mod termination;
pub mod verdict;

pub use answerer::{ProxyAnswer, ProxyAnswerer};
pub use budget::GoalBudget;
pub use ledger::{
    AssumptionLedger, BlockerKind, Confidence, LedgerEntry, LedgerError, LedgerSnapshot,
    WorkItemRef,
};
pub use permission::PermissionResolver;
pub use plan::AutonomousPlanApprover;
pub use recovery::{RecoveryExecutor, RecoveryGuard, RecoveryKind};
pub use retry::GoalRetryPolicy;
pub use stuck::{StuckDetector, StuckObservation, StuckPattern};
pub use termination::{
    ProgressSnapshot, ProgressTracker, TerminationInputs, TerminationProof, NO_PROGRESS_WINDOW,
};
pub use verdict::{GoalOutcome, GoalStatus, GoalVerdict};
use completion::SYSTEM_PROMPT;

/// An isolated forked-agent call used by the completion judge.
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

/// What the mid-turn gate decided. See [`AutonomySupervisor::check_mid_turn`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MidTurn {
    /// Nothing to do; carry on with the next model call.
    Continue,
    /// Inject this correction before the next model call.
    Nudge(String),
    /// End the run now, with a proved outcome and an operator-facing report.
    Stop { outcome: GoalOutcome, report: String },
}

/// Autonomous goal supervisor.
///
/// Call [`AutonomySupervisor::evaluate`] at every natural stop.  When the verdict
/// is [`GoalVerdict::Escalate`], the caller MUST resolve it with exactly one of:
/// - [`AutonomySupervisor::try_grant_extension`] (extend the budget; continue), or
/// - [`AutonomySupervisor::mark_stopped_unmet`] (accept failure; stop).
///
/// ## What is shared and what is not
///
/// The budget and the completion outcome are driven only by the agent loop, at
/// a natural stop, and stay plain fields behind the loop's `&mut`.
///
/// The ledger and the stuck detector are written from tool threads while the
/// loop reads them, so each is separately `Arc`-shared with its own interior
/// lock. Sharing the pieces rather than the whole supervisor keeps the seams
/// honest about what they touch — the question seam can reach the ledger and
/// nothing else — and avoids one coarse lock serialising unrelated work.
pub struct AutonomySupervisor {
    judge: Box<dyn ForkedAgent>,
    goal: String,
    budget: GoalBudget,
    retry: GoalRetryPolicy,
    outcome: GoalOutcome,
    last_remaining: Option<String>,
    /// `true` after the first `Escalate` verdict is returned, so callers
    /// (tests and the serve layer) can observe the escalation lifecycle.
    escalated: bool,
    /// Shared with the question and permission seams.
    ledger: Arc<AssumptionLedger>,
    /// Shared with the loop's per-iteration observation.
    stuck: Arc<StuckDetector>,
    /// Tracks whether the run is still getting anywhere.
    progress: ProgressTracker,
    /// Work items not yet done, refreshed by the loop each turn.
    open_work_items: Vec<String>,
    /// Work items finished, as a monotonically comparable count.
    completed_work_items: usize,
    /// Successful file mutations, as a monotonically comparable count.
    files_changed: u64,
}

impl AutonomySupervisor {
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
            ledger: Arc::new(AssumptionLedger::new()),
            stuck: Arc::new(StuckDetector::new()),
            progress: ProgressTracker::new(),
            open_work_items: Vec::new(),
            completed_work_items: 0,
            files_changed: 0,
        }
    }

    /// The goal being pursued.
    pub fn goal(&self) -> &str {
        &self.goal
    }

    /// The assumption ledger, for wiring into the question and permission seams.
    pub fn ledger(&self) -> Arc<AssumptionLedger> {
        Arc::clone(&self.ledger)
    }

    /// The stuck detector, for the loop to feed each iteration.
    pub fn stuck(&self) -> Arc<StuckDetector> {
        Arc::clone(&self.stuck)
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
    /// The ladder, in order:
    /// 1. The completion judge says the goal is met — stop, met.
    /// 2. The termination proof establishes that nothing further is possible —
    ///    stop with the proved outcome and a report.
    /// 3. The budget is exhausted — escalate once, then stop unmet.
    /// 4. Otherwise keep working.
    ///
    /// The proof sits *above* the budget deliberately. A run that is genuinely
    /// blocked should say so while it still has budget left, rather than
    /// burning hours to reach the same conclusion by timing out.
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

        let user_msg = completion::build_user_message(&self.goal, recent_assistant_text);
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
                self.continue_or_prove(nudge_unavailable)
            }
            Ok((true, Some(response))) => {
                if completion::is_complete(&response) {
                    self.outcome = GoalOutcome::Met;
                    return GoalVerdict::Stop { met: true };
                }

                self.last_remaining = Some(completion::remaining(&response));
                self.budget.record_continuation();
                let nudge = format!(
                    "The goal is not yet complete. Still remaining: {}\n\
                     Keep working toward the goal, then stop only when it is fully done:\n{}",
                    self.last_remaining.as_deref().unwrap_or("unspecified"),
                    self.goal
                );
                self.continue_or_prove(nudge)
            }
            Ok((true, None)) => {
                // Shouldn't happen (true + None) but fail-open.
                self.budget.record_continuation();
                self.continue_or_prove(nudge_unavailable)
            }
        }
    }

    /// Record this turn's progress, then either continue or stop on a proof.
    ///
    /// The nudge is threaded through rather than built here so the caller's
    /// reason for continuing — judge unavailable, or work still remaining —
    /// survives into the message the agent actually sees.
    fn continue_or_prove(&mut self, nudge: String) -> GoalVerdict {
        let ledger = self.ledger.snapshot();
        self.progress.observe(ProgressSnapshot {
            completed_work_items: self.completed_work_items,
            files_changed: self.files_changed,
            distinct_ledger_entries: ledger.distinct_entries(),
        });

        let proof = termination::prove(TerminationInputs {
            open_work_items: &self.open_work_items,
            ledger: &ledger,
            stuck: self.stuck.is_stuck(),
            without_progress: self.progress.without_progress(),
        });

        match proof {
            TerminationProof::GenuinelyBlocked => {
                self.outcome = GoalOutcome::GenuinelyBlocked;
                GoalVerdict::StopProved {
                    outcome: GoalOutcome::GenuinelyBlocked,
                    report: self.build_report(),
                }
            }
            TerminationProof::Stalled => {
                self.outcome = GoalOutcome::Stalled;
                GoalVerdict::StopProved {
                    outcome: GoalOutcome::Stalled,
                    report: self.build_report(),
                }
            }
            // Before continuing, hand the agent the stuck detector's one-shot
            // correction if it has one: a run that can fix itself should be
            // given the chance before any of this matters.
            TerminationProof::KeepGoing => match self.stuck.take_nudge() {
                Some(correction) => GoalVerdict::Continue {
                    nudge: format!("{correction}\n\n{nudge}"),
                },
                None => GoalVerdict::Continue { nudge },
            },
        }
    }

    /// Enforcement that must run on **every** iteration, not only at a natural
    /// stop.
    ///
    /// The stop ladder is reached only when a turn calls no tools. An agent
    /// that calls a tool every turn — `cargo test` failing, then `cargo test`
    /// again, forever — would otherwise never be checked at all: not the
    /// budget, not the stuck detector, not the corrective nudge. That is the
    /// commonest shape of a runaway and precisely what the stuck heuristics
    /// were ported to catch, so the check cannot live only on the path that
    /// shape never takes.
    ///
    /// Deliberately does **not** call the completion judge. Asking "is the goal
    /// done?" mid-turn would cost an LLM round trip per tool batch and cannot
    /// be answered honestly while work is still in flight.
    pub fn check_mid_turn(&mut self) -> MidTurn {
        if self.budget.is_exhausted() && self.budget.extension_used() {
            self.outcome = GoalOutcome::Unmet;
            return MidTurn::Stop { outcome: GoalOutcome::Unmet, report: self.build_report() };
        }

        // `is_stuck` is true only once a loop has survived its own corrective
        // nudge, so reaching here means the agent was told and carried on.
        if self.stuck.is_stuck() {
            self.outcome = GoalOutcome::Stalled;
            return MidTurn::Stop { outcome: GoalOutcome::Stalled, report: self.build_report() };
        }

        match self.stuck.take_nudge() {
            Some(correction) => MidTurn::Nudge(correction),
            None => MidTurn::Continue,
        }
    }
    ///
    /// Called by the loop each turn. Kept as explicit inputs rather than having
    /// the supervisor reach into the todo store or the filesystem, so the
    /// progress rule stays a pure comparison that tests can drive directly.
    pub fn record_progress(
        &mut self,
        open_work_items: Vec<String>,
        completed_work_items: usize,
        files_changed: u64,
    ) {
        self.open_work_items = open_work_items;
        self.completed_work_items = completed_work_items;
        self.files_changed = files_changed;
    }

    /// The operator-facing account of why the run stopped.
    ///
    /// Read from the ledger rather than from the decision snapshot: this is
    /// prose for a human, so the freshest entries are more useful than perfect
    /// agreement with the moment the proof was taken.
    pub fn build_report(&self) -> String {
        let entries = self.ledger.entries();
        let mut report = String::new();

        let blockers: Vec<&LedgerEntry> = entries
            .iter()
            .filter(|e| matches!(e, LedgerEntry::ParkedBlocker { .. }))
            .collect();

        if blockers.is_empty() {
            report.push_str(
                "The run stopped making progress and no further action was available.\n",
            );
        } else {
            report.push_str("The run stopped because everything remaining is blocked.\n\n");
            for entry in blockers {
                if let LedgerEntry::ParkedBlocker { kind, tried, needs, blocks } = entry {
                    let what = blocks.as_ref().map(|b| b.display()).unwrap_or("the goal");
                    report.push_str(&format!("- {what} ({})\n", kind.as_str()));
                    report.push_str(&format!("  needs: {needs}\n"));
                    for attempt in tried {
                        report.push_str(&format!("  tried: {attempt}\n"));
                    }
                }
            }
        }

        let unsure = self.ledger.low_confidence_assumptions();
        if !unsure.is_empty() {
            report.push_str("\nDecisions made on your behalf that are worth checking:\n");
            for entry in unsure {
                if let LedgerEntry::Assumption { question, chosen, rationale, .. } = entry {
                    report.push_str(&format!("- {question} -> {chosen} ({rationale})\n"));
                }
            }
        }

        report
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
pub struct CompletionJudgePrompt;

impl CompletionJudgePrompt {
    pub fn is_complete(response: &str) -> bool {
        completion::is_complete(response)
    }
    pub fn remaining(response: &str) -> String {
        completion::remaining(response)
    }
    pub fn build_user_message(goal: &str, recent_output: &str) -> String {
        completion::build_user_message(goal, recent_output)
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
        GoalBudget::new(None, Some(max_cont), 0.5, || Duration::ZERO)
    }

    // §8 item 19: judge failure fails open → Continue, RecordContinuation.
    #[tokio::test]
    async fn judge_failure_fails_open() {
        let mut sup = AutonomySupervisor::new(
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
        let mut sup = AutonomySupervisor::new(
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
        let mut sup = AutonomySupervisor::new(
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
        let mut sup = AutonomySupervisor::new(
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
        let mut sup = AutonomySupervisor::new(
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

    // ── Shared components ────────────────────────────────────────────────────

    /// The seams write to the ledger through their own handles while the loop
    /// reads it. If the handles were copies rather than shares, every
    /// assumption recorded by a tool would be invisible to the report and to
    /// the termination proof.
    #[test]
    fn the_ledger_handed_to_a_seam_is_the_supervisors_own() {
        let sup = AutonomySupervisor::new(
            Box::new(AlwaysFailsJudge),
            "goal",
            budget_cont(10),
            Some(GoalRetryPolicy::for_tests()),
        );

        let seam_handle = sup.ledger();
        seam_handle.record_assumption(
            "asked by a tool",
            &["a".to_owned()],
            "a",
            "because",
            Confidence::High,
        );

        assert_eq!(sup.ledger().len(), 1, "the supervisor must see the seam's write");
    }

    #[test]
    fn the_stuck_detector_handed_to_the_loop_is_the_supervisors_own() {
        let sup = AutonomySupervisor::new(
            Box::new(AlwaysFailsJudge),
            "goal",
            budget_cont(10),
            Some(GoalRetryPolicy::for_tests()),
        );

        let loop_handle = sup.stuck();
        for _ in 0..4 {
            loop_handle.observe(StuckObservation::new("run_command", "{}", "E", true));
        }

        assert_eq!(
            sup.stuck().detect(),
            Some(StuckPattern::RepeatedActionError),
            "the supervisor must see what the loop observed"
        );
    }

    #[test]
    fn the_goal_text_is_readable_for_the_stand_in() {
        let sup = AutonomySupervisor::new(
            Box::new(AlwaysFailsJudge),
            "ship the feature",
            budget_cont(10),
            Some(GoalRetryPolicy::for_tests()),
        );
        assert_eq!(sup.goal(), "ship the feature");
    }

    // ── The termination proof, through the supervisor ────────────────────────

    /// A run with plenty of budget left, nothing parked and nothing looping
    /// must keep working however quiet it is. Quiet is not the same as done.
    #[tokio::test]
    async fn a_quiet_run_with_budget_left_keeps_working() {
        let mut sup = AutonomySupervisor::new(
            Box::new(ScriptedJudge::new(vec!["CONTINUE: more", "CONTINUE: more", "CONTINUE: more"])),
            "finish the work",
            budget_cont(100),
            Some(GoalRetryPolicy::for_tests()),
        );

        for _ in 0..3 {
            let verdict = sup.evaluate("still going", CancellationToken::new()).await;
            assert!(matches!(verdict, GoalVerdict::Continue { .. }), "{verdict:?}");
        }
    }

    /// The headline guarantee: a blocked run stops with a proof and a report,
    /// while it still has budget, and without asking anyone anything.
    #[tokio::test]
    async fn a_fully_blocked_run_stops_with_a_report_and_never_escalates() {
        let mut sup = AutonomySupervisor::new(
            Box::new(ScriptedJudge::new(vec![
                "CONTINUE: needs a key",
                "CONTINUE: needs a key",
                "CONTINUE: needs a key",
                "CONTINUE: needs a key",
            ])),
            "wire up billing",
            budget_cont(100),
            Some(GoalRetryPolicy::for_tests()),
        );

        sup.ledger()
            .park_blocker(
                crate::autonomy::BlockerKind::MissingCredential,
                &["looked for STRIPE_KEY in the environment".to_owned()],
                "STRIPE_KEY",
                None,
            )
            .expect("an attempt was recorded");

        // The ledger stops changing, so from here nothing moves.
        let mut last = GoalVerdict::Continue { nudge: String::new() };
        for _ in 0..4 {
            last = sup.evaluate("blocked", CancellationToken::new()).await;
        }

        match last {
            GoalVerdict::StopProved { outcome, report } => {
                assert_eq!(outcome, GoalOutcome::GenuinelyBlocked);
                assert!(report.contains("STRIPE_KEY"), "the report must name what is needed: {report}");
                assert!(report.contains("looked for"), "and what was tried: {report}");
            }
            other => panic!("a fully blocked run must stop with a proof, got {other:?}"),
        }
        assert_eq!(sup.status().outcome, GoalOutcome::GenuinelyBlocked);
        assert!(!sup.status().is_successful());
        assert!(!sup.status().escalated, "a proved stop must never escalate to a human");
    }

    /// The termination guarantee when the operator set no budget at all: a
    /// looping run still stops, via the stuck detector rather than a ceiling.
    #[tokio::test]
    async fn a_looping_run_with_an_unlimited_budget_still_terminates() {
        let judge = ScriptedJudge::new(vec![
            "CONTINUE: x", "CONTINUE: x", "CONTINUE: x", "CONTINUE: x", "CONTINUE: x",
        ]);
        let mut sup = AutonomySupervisor::new(
            Box::new(judge),
            "impossible task",
            // No ceiling on either dimension.
            GoalBudget::new(None, None, 0.5, || Duration::ZERO),
            Some(GoalRetryPolicy::for_tests()),
        );

        // The agent loops on one failing call until the detector gives up on it.
        let stuck = sup.stuck();
        for _ in 0..5 {
            stuck.observe(StuckObservation::new("run_command", "{}", "E", true));
        }
        stuck.take_nudge().expect("the loop is nudged once");
        stuck.observe(StuckObservation::new("run_command", "{}", "E", true));

        let mut verdicts = Vec::new();
        for _ in 0..5 {
            verdicts.push(sup.evaluate("looping", CancellationToken::new()).await);
        }

        let stopped = verdicts.iter().any(|v| {
            matches!(v, GoalVerdict::StopProved { outcome: GoalOutcome::Stalled, .. })
        });
        assert!(
            stopped,
            "an unlimited budget must not mean an unlimited run: {verdicts:?}"
        );
        assert_eq!(sup.status().outcome, GoalOutcome::Stalled);
    }

    /// A completed goal wins over every proof: finishing is not being stuck.
    #[tokio::test]
    async fn a_completed_goal_stops_as_met_even_while_quiet() {
        let mut sup = AutonomySupervisor::new(
            Box::new(ScriptedJudge::new(vec!["DONE"])),
            "the work",
            budget_cont(100),
            Some(GoalRetryPolicy::for_tests()),
        );
        sup.ledger()
            .park_blocker(
                crate::autonomy::BlockerKind::MissingAccess,
                &["tried".to_owned()],
                "access",
                None,
            )
            .unwrap();

        let verdict = sup.evaluate("all done", CancellationToken::new()).await;
        assert!(matches!(verdict, GoalVerdict::Stop { met: true }), "{verdict:?}");
        assert_eq!(sup.status().outcome, GoalOutcome::Met);
    }

    /// Recorded work that is neither finished nor blocked is work still to do.
    #[tokio::test]
    async fn unblocked_work_items_keep_the_run_going() {
        let mut sup = AutonomySupervisor::new(
            Box::new(ScriptedJudge::new(vec![
                "CONTINUE: x", "CONTINUE: x", "CONTINUE: x", "CONTINUE: x", "CONTINUE: x",
            ])),
            "the work",
            budget_cont(100),
            Some(GoalRetryPolicy::for_tests()),
        );
        sup.record_progress(vec!["unblocked item".to_owned()], 0, 0);
        sup.ledger()
            .park_blocker(
                crate::autonomy::BlockerKind::MissingAccess,
                &["tried".to_owned()],
                "access",
                Some("a different item"),
            )
            .unwrap();

        for _ in 0..5 {
            let verdict = sup.evaluate("working", CancellationToken::new()).await;
            assert!(
                matches!(verdict, GoalVerdict::Continue { .. }),
                "unblocked work must keep the run alive: {verdict:?}"
            );
        }
    }

    /// Real movement resets the quiet counter, so a slow but productive run is
    /// never mistaken for a stalled one.
    #[tokio::test]
    async fn movement_prevents_any_proof_from_firing() {
        let mut sup = AutonomySupervisor::new(
            Box::new(ScriptedJudge::new(vec![
                "CONTINUE: x", "CONTINUE: x", "CONTINUE: x", "CONTINUE: x", "CONTINUE: x",
            ])),
            "the work",
            budget_cont(100),
            Some(GoalRetryPolicy::for_tests()),
        );
        sup.ledger()
            .park_blocker(
                crate::autonomy::BlockerKind::MissingAccess,
                &["tried".to_owned()],
                "access",
                None,
            )
            .unwrap();

        for turn in 0..5 {
            sup.record_progress(Vec::new(), 0, turn + 1);
            let verdict = sup.evaluate("progressing", CancellationToken::new()).await;
            assert!(
                matches!(verdict, GoalVerdict::Continue { .. }),
                "a run that is changing files is making progress: {verdict:?}"
            );
        }
    }

    /// The stuck detector's correction must actually reach the agent, or it
    /// never gets the chance to fix itself before being judged.
    #[tokio::test]
    async fn a_pending_correction_is_delivered_with_the_nudge() {
        let mut sup = AutonomySupervisor::new(
            Box::new(ScriptedJudge::new(vec!["CONTINUE: keep going"])),
            "the work",
            budget_cont(100),
            Some(GoalRetryPolicy::for_tests()),
        );

        let stuck = sup.stuck();
        for _ in 0..4 {
            stuck.observe(StuckObservation::new("run_command", "{}", "E", true));
        }

        match sup.evaluate("looping", CancellationToken::new()).await {
            GoalVerdict::Continue { nudge } => {
                assert!(nudge.contains("run_command"), "the correction must be delivered: {nudge}");
                assert!(nudge.contains("keep going"), "and the judge's nudge kept: {nudge}");
            }
            other => panic!("expected a correction, got {other:?}"),
        }
    }

    /// REGRESSION: the completion judge's prose was once a progress signal.
    /// Because it is regenerated from the agent's varying output it differed
    /// nearly every turn, so the quiet counter reset continually and a blocked
    /// run never reached its proof. A judge that says something different every
    /// time must not keep a stalled run alive.
    #[tokio::test]
    async fn varying_judge_prose_does_not_keep_a_blocked_run_alive() {
        let mut sup = AutonomySupervisor::new(
            Box::new(ScriptedJudge::new(vec![
                "CONTINUE: still needs the key",
                "CONTINUE: the key is still missing",
                "CONTINUE: waiting on a credential",
                "CONTINUE: blocked on STRIPE_KEY",
                "CONTINUE: no key yet",
            ])),
            "wire up billing",
            GoalBudget::new(None, None, 0.5, || Duration::ZERO),
            Some(GoalRetryPolicy::for_tests()),
        );
        sup.ledger()
            .park_blocker(
                crate::autonomy::BlockerKind::MissingCredential,
                &["looked for STRIPE_KEY".to_owned()],
                "STRIPE_KEY",
                None,
            )
            .unwrap();

        let mut verdicts = Vec::new();
        for _ in 0..5 {
            verdicts.push(sup.evaluate("varied narration each turn", CancellationToken::new()).await);
        }

        assert!(
            verdicts.iter().any(|v| matches!(
                v,
                GoalVerdict::StopProved { outcome: GoalOutcome::GenuinelyBlocked, .. }
            )),
            "prose is not progress: {verdicts:?}"
        );
    }

    /// REGRESSION: repeatedly hitting the same wall appends an identical ledger
    /// entry each turn. Counting those as new would make failure read as
    /// forward motion and postpone termination forever.
    #[tokio::test]
    async fn repeating_an_identical_denial_is_not_progress() {
        let mut sup = AutonomySupervisor::new(
            Box::new(ScriptedJudge::new(vec![
                "CONTINUE: x", "CONTINUE: x", "CONTINUE: x", "CONTINUE: x", "CONTINUE: x",
            ])),
            "the work",
            GoalBudget::new(None, None, 0.5, || Duration::ZERO),
            Some(GoalRetryPolicy::for_tests()),
        );

        let stuck = sup.stuck();
        for _ in 0..5 {
            stuck.observe(StuckObservation::new("run_command", "{}", "denied", true));
        }
        stuck.take_nudge();
        stuck.observe(StuckObservation::new("run_command", "{}", "denied", true));

        let mut verdicts = Vec::new();
        for _ in 0..5 {
            // The agent retries the same forbidden action every turn.
            sup.ledger().record_denial("run_command", "plan", "bypassPermissions");
            verdicts.push(sup.evaluate("retrying", CancellationToken::new()).await);
        }

        assert!(
            verdicts.iter().any(|v| matches!(v, GoalVerdict::StopProved { .. })),
            "an identical repeated denial is not progress: {verdicts:?}"
        );
    }
}
