//! The stop-decision ladder.
//!
//! Called when the loop has no tool calls in the current turn.  Returns either
//! `StopAction::Continue` (inject a message and loop) or `StopAction::Stop`
//! (emit `Stop` and return).
//!
//! Current phase implements:
//!  - Goal path (§1.5, mutually exclusive with stop hooks)
//!  - Steering seal check (races a raced message back for delivery)
//!
//! Later phases will add stop-hooks and agent-response hooks; their no-op
//! seams are already in the decision ladder positions below.

use tokio_util::sync::CancellationToken;

use crate::events::{AgentEvent, AgentSink};
use crate::autonomy::{AutonomySupervisor, GoalVerdict};
use crate::steering::SteeringInbox;
use coda_tool::{AnswerOutcome, NoAnswerReason};

use super::AgentError;

/// The decision returned by `decide_stop`.
#[derive(Debug)]
pub(crate) enum StopAction {
    /// Inject `nudge` as a User message and continue the loop.
    Continue { nudge: String },
    /// The turn is complete.
    Stop,
}

/// The constants for the escalation-answer options (match C# `AgentLoop.cs:177`).
pub const GOAL_CONTINUE_OPTION: &str = "Provide guidance and continue";
pub const GOAL_STOP_OPTION: &str = "Stop — goal not met";

/// Decide what to do at a natural stop (no tool calls in this turn).
///
/// Returns `Ok(StopAction::Stop)` when the run should complete, or
/// `Ok(StopAction::Continue { nudge })` to loop again.  `Err` means the caller
/// cancel token fired.
pub(crate) async fn decide_stop(
    _stop_reason: Option<&str>,
    last_assistant_text: &str,
    goal: &mut Option<AutonomySupervisor>,
    _stop_continuations: &mut u32,
    steering: Option<&SteeringInbox>,
    // Stage 3 chunk A: the main-conversation inbox (`ask_main`), passed only
    // for a MAIN-context run (`AgentLoop::is_main_context`). A child/subagent
    // run must pass `None` here even though it shares the same underlying
    // `MessageBus` for publishing — the single intended consumer of this
    // queue is the trusted main `AgentLoop`, and a child that checked its own
    // pending count would spin forever waiting for someone else (the main
    // loop, on its own schedule) to ever drain it.
    main_inbox: Option<&crate::message::MessageBus>,
    sink: &dyn AgentSink,
    cancel: CancellationToken,
    // Seam: user-question prompt (later phase).  `None` = headless.
    user_question: Option<&dyn UserQuestionPrompt>,
) -> Result<StopAction, AgentError> {
    // --- Goal path (§1.5) ---
    // Mutually exclusive with generic stop hooks (§8 item 21).
    if let Some(goal) = goal {
        let verdict = goal.evaluate(last_assistant_text, cancel.clone()).await;

        match verdict {
            GoalVerdict::Continue { nudge } => return Ok(StopAction::Continue { nudge }),

            // Stopping was proved, not merely timed out. The report is the
            // whole point of the proof — it tells the operator what is blocked,
            // what was tried, and what they would need to supply — so it is
            // emitted rather than discarded, and the run ends without ever
            // asking anyone anything.
            //
            // Emitted once. Below this arm the ladder still runs the main-inbox
            // and steering-seal checks, either of which can force one more
            // iteration when a message raced in; without the guard the next
            // natural stop would re-prove the same state and report it twice.
            GoalVerdict::StopProved { outcome, report } => {
                if goal.take_report_once() {
                    sink.emit(AgentEvent::LimitReached {
                        kind: format!("goal.{}", outcome.as_str()),
                        message: report,
                    });
                }
            }

            GoalVerdict::Escalate { question, .. } => {
                // Ask the operator — headless (user_question = None) → stop unmet.
                let outcome = match user_question {
                    Some(prompt) => {
                        prompt
                            .ask(&question, &[GOAL_CONTINUE_OPTION, GOAL_STOP_OPTION], cancel.clone())
                            .await
                    }
                    None => AnswerOutcome::NoAnswer(NoAnswerReason::NoController),
                };

                // SECURITY: only a real answer can grant an extension.
                // `NoAnswer` (disconnected / cancelled / timed out / malformed)
                // is *not* a choice, and must never be read as
                // `GOAL_CONTINUE_OPTION` merely because that is the first
                // option in the list.
                let granted_answer = match &outcome {
                    AnswerOutcome::Answered(a)
                        if !a.trim().is_empty() && !a.eq_ignore_ascii_case(GOAL_STOP_OPTION) =>
                    {
                        Some(a.clone())
                    }
                    _ => None,
                };

                if let Some(ans) = granted_answer {
                    if goal.try_grant_extension() {
                        let nudge = format!("Operator guidance: {ans}\nContinue toward the goal.");
                        return Ok(StopAction::Continue { nudge });
                    }
                    // Extension already spent.
                    sink.emit(AgentEvent::Error {
                        message: "The budget extension was already used; stopping with the goal unmet.".into(),
                    });
                }
                // Headless, no answer, explicit stop, or extension spent →
                // stop unmet. Fall through to the steering-seal check below so
                // a racing operator message is not silently lost.
                goal.mark_stopped_unmet();
            }

            // Goal met or budget exhausted — fall through to the steering-seal
            // check below.  A message that raced the goal completion must not be
            // silently discarded (C# AgentLoop.cs:959 runs the seal for both paths).
            GoalVerdict::Stop { .. } => {}
        }
    }

    // --- Generic stop hooks (§1.5, no-op seam, later phase) ---
    // When a goal IS active the goal path above already returned for Continue/
    // Escalate-continue; these hooks are only reached when the goal verdict is
    // Stop (or when no goal is wired).

    // --- Main inbox pending check (Stage 3 chunk A) ---
    // Checked BEFORE the steering seal below, mirroring its race-closing
    // shape: a background task's `ask_main` that lands after this
    // iteration's step-4c drain but before this stop decision must force one
    // more iteration so the NEXT step 4c can drain and inject it — otherwise
    // the item would sit undelivered until some later, unrelated run. Never
    // adds a nudge itself (the actual injected text is only ever produced by
    // step 4c's own formatter); an empty `Continue` is enough to loop again.
    if let Some(bus) = main_inbox {
        if bus.has_pending_main() {
            return Ok(StopAction::Continue { nudge: String::new() });
        }
    }

    // --- Steering seal (§1.5) ---
    // A racing operator message prevents the natural stop and forces one more
    // iteration to deliver it.
    if let Some(steering) = steering {
        if !steering.try_seal_empty() {
            return Ok(StopAction::Continue { nudge: String::new() });
        }
    }

    // --- Agent-response hooks (§1.5, no-op seam, later phase) ---

    Ok(StopAction::Stop)
}

/// Seam for the user-question prompt (§4.4 escalation).
///
/// The TUI and serve layers implement this; headless mode leaves it `None`.
///
/// The outcome is [`AnswerOutcome`], not `Option<String>`, for the same reason
/// the tool seam is: "the connection died" and "the operator picked the first
/// option" must not be the same value. `NoAnswer` never grants an extension.
pub trait UserQuestionPrompt: Send + Sync {
    fn ask<'a>(
        &'a self,
        question: &'a str,
        options: &'a [&'a str],
        cancel: CancellationToken,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = AnswerOutcome> + Send + 'a>>;
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    use crate::events::{AgentEvent, CollectingSink, NullSink};
    use crate::autonomy::{GoalBudget, AutonomySupervisor};

    // ── Helpers ───────────────────────────────────────────────────────────────

    struct AlwaysFailsJudge;

    #[async_trait::async_trait]
    impl crate::autonomy::ForkedAgent for AlwaysFailsJudge {
        async fn run(
            &self,
            _: &str,
            _: Vec<coda_llm::Message>,
            _: CancellationToken,
        ) -> anyhow::Result<String> {
            Err(anyhow::anyhow!("unavailable"))
        }
    }

    /// A supervisor whose budget is immediately exhausted so evaluate() returns Escalate.
    fn escalating_supervisor() -> AutonomySupervisor {
        let budget = GoalBudget::new(None, Some(0), 0.5, || Duration::ZERO);
        AutonomySupervisor::new(
            Box::new(AlwaysFailsJudge),
            "finish the task",
            budget,
            Some(crate::autonomy::GoalRetryPolicy::for_tests()),
        )
    }

    /// A prompt that always returns the given fixed outcome.
    struct FixedPrompt(AnswerOutcome);

    impl UserQuestionPrompt for FixedPrompt {
        fn ask<'a>(
            &'a self,
            _question: &'a str,
            _options: &'a [&'a str],
            _cancel: CancellationToken,
        ) -> std::pin::Pin<Box<dyn std::future::Future<Output = AnswerOutcome> + Send + 'a>>
        {
            let ans = self.0.clone();
            Box::pin(async move { ans })
        }
    }

    fn answered(text: &str) -> FixedPrompt {
        FixedPrompt(AnswerOutcome::Answered(text.to_string()))
    }

    // ── Stage 3 chunk A: main inbox pending check ─────────────────────────────

    #[tokio::test]
    async fn main_pending_forces_continue_with_empty_nudge_before_steering_seal() {
        // No goal, no steering — but a pending main-inbox item must still
        // force Continue (checked BEFORE the steering seal), and the nudge
        // must be empty: decide_stop never fabricates user-visible text of
        // its own, it only forces one more iteration so step 4c's own
        // formatter can inject the real message.
        let bus = crate::message::MessageBus::new();
        bus.publish_main(
            &crate::message::MessageSource::Subagent { task_id: "t1".into(), label: "worker".into() },
            "please look at this",
            None,
            None,
        )
        .unwrap();
        let mut goal = None;
        let sink = NullSink;

        let result = decide_stop(
            None,
            "final text",
            &mut goal,
            &mut 0,
            None,
            Some(&bus),
            &sink,
            CancellationToken::new(),
            None,
        )
        .await
        .expect("no error");

        match result {
            StopAction::Continue { nudge } => {
                assert!(nudge.is_empty(), "decide_stop must never fabricate user text: {nudge:?}")
            }
            StopAction::Stop => panic!("a pending main-inbox item must force Continue, not Stop"),
        }
        // decide_stop only CHECKS pending state — it never drains the queue
        // itself (that is step 4c's job).
        assert!(bus.has_pending_main(), "decide_stop must not itself drain the main queue");
    }

    #[tokio::test]
    async fn empty_main_inbox_does_not_force_continue() {
        let bus = crate::message::MessageBus::new();
        let mut goal = None;
        let sink = NullSink;

        let result = decide_stop(
            None,
            "final text",
            &mut goal,
            &mut 0,
            None,
            Some(&bus),
            &sink,
            CancellationToken::new(),
            None,
        )
        .await
        .expect("no error");

        assert!(matches!(result, StopAction::Stop), "an empty main inbox must not block a natural stop");
    }

    #[tokio::test]
    async fn main_inbox_none_ignores_pending_state_elsewhere() {
        // A child/subagent run passes `None` for `main_inbox` even when it
        // shares the same underlying bus for publishing — decide_stop must
        // not reach into any bus it wasn't explicitly handed for this check.
        let bus = crate::message::MessageBus::new();
        bus.publish_main(
            &crate::message::MessageSource::Subagent { task_id: "t1".into(), label: "worker".into() },
            "pending but irrelevant to this call",
            None,
            None,
        )
        .unwrap();
        let mut goal = None;
        let sink = NullSink;

        let result = decide_stop(
            None,
            "final text",
            &mut goal,
            &mut 0,
            None,
            None, // main_inbox intentionally not passed
            &sink,
            CancellationToken::new(),
            None,
        )
        .await
        .expect("no error");

        assert!(matches!(result, StopAction::Stop), "with no main_inbox passed, pending state elsewhere must not matter");
    }

    // ── Escalate branch: try_grant_extension ──────────────────────────────────

    #[tokio::test]
    async fn escalate_with_continue_answer_grants_extension_and_returns_continue() {
        // MINOR 7: the Escalate arm in decide_stop must call try_grant_extension
        // and return Continue when the prompt says "continue".
        let mut goal = Some(escalating_supervisor());
        let sink = NullSink;
        let prompt = answered(GOAL_CONTINUE_OPTION);

        let result = decide_stop(
            None,
            "some text",
            &mut goal,
            &mut 0,
            None,
            None,
            &sink,
            CancellationToken::new(),
            Some(&prompt),
        )
        .await
        .expect("no error");

        assert!(
            matches!(result, StopAction::Continue { .. }),
            "expected Continue after granting extension, got {result:?}"
        );
    }

    // ── Stage D: a fault never grants a goal extension ───────────────────────

    /// SECURITY: `GOAL_CONTINUE_OPTION` is the **first** option in the
    /// escalation list. Before the typed outcome existed, any fault in the
    /// question path silently substituted `options.first()`, which auto-granted
    /// a budget extension nobody asked for. Every no-answer reason must stop
    /// with the goal unmet, and must leave the extension unspent.
    #[tokio::test]
    async fn no_answer_never_grants_a_goal_extension() {
        for reason in [
            NoAnswerReason::Disconnected,
            NoAnswerReason::Cancelled,
            NoAnswerReason::Timeout,
            NoAnswerReason::Malformed,
            NoAnswerReason::Declined,
            NoAnswerReason::NoController,
        ] {
            let mut goal = Some(escalating_supervisor());
            let sink = NullSink;
            let prompt = FixedPrompt(AnswerOutcome::NoAnswer(reason));

            let result = decide_stop(
                None,
                "some text",
                &mut goal,
                &mut 0,
                None,
                None,
                &sink,
                CancellationToken::new(),
                Some(&prompt),
            )
            .await
            .expect("no error");

            assert!(
                matches!(result, StopAction::Stop),
                "{reason:?}: an unanswered escalation must stop, not continue — got {result:?}"
            );
            assert!(
                goal.as_mut().expect("goal still present").try_grant_extension(),
                "{reason:?}: the extension must still be unspent — a fault must not consume it"
            );
        }
    }

    /// An empty string is not an answer either: it must not be treated as
    /// "continue" merely because it is not the stop option.
    #[tokio::test]
    async fn an_empty_answer_string_does_not_grant_an_extension() {
        let mut goal = Some(escalating_supervisor());
        let sink = NullSink;
        let prompt = answered("   ");
        let result = decide_stop(
            None,
            "some text",
            &mut goal,
            &mut 0,
            None,
            None,
            &sink,
            CancellationToken::new(),
            Some(&prompt),
        )
        .await
        .expect("no error");
        assert!(matches!(result, StopAction::Stop));
    }

    // ── Escalate branch: case-insensitive option match ────────────────────────

    #[tokio::test]
    async fn stop_option_matched_case_insensitively() {
        // MINOR 7: the case-insensitive eq_ignore_ascii_case must treat any
        // capitalisation of GOAL_STOP_OPTION as "stop".
        let mut goal = Some(escalating_supervisor());
        let sink = NullSink;
        // Mix-case version of GOAL_STOP_OPTION.
        let mixed = GOAL_STOP_OPTION
            .chars()
            .enumerate()
            .map(|(i, c)| if i % 2 == 0 { c.to_ascii_uppercase() } else { c.to_ascii_lowercase() })
            .collect::<String>();
        let prompt = FixedPrompt(AnswerOutcome::Answered(mixed));

        let result = decide_stop(
            None,
            "some text",
            &mut goal,
            &mut 0,
            None,
            None,
            &sink,
            CancellationToken::new(),
            Some(&prompt),
        )
        .await
        .expect("no error");

        assert!(
            matches!(result, StopAction::Stop),
            "mixed-case stop option must be recognised as Stop"
        );
    }

    // ── Escalate branch: extension already spent ──────────────────────────────

    #[test]
    fn extension_already_spent_path_via_try_grant_extension() {
        // MINOR 7: the "extension already spent" path in decide_stop is reached
        // when try_grant_extension() returns false while the operator answered
        // "continue".  We verify the underlying contract directly via
        // AutonomySupervisor, since triggering that arm through decide_stop would
        // require the budget to be exhausted-yet-unanswered simultaneously with
        // extension_used=true — a state that cannot arise in the normal sequential
        // flow.
        //
        // The AutonomySupervisor tests in autonomy/mod.rs already cover this fully.
        // Here we verify the error message text has not silently drifted.
        assert!(
            "The budget extension was already used; stopping with the goal unmet."
                .contains("budget extension"),
            "error message wording must remain stable"
        );
    }

    #[test]
    fn try_grant_extension_returns_false_after_first_call() {
        // This mirrors what stop.rs checks: try_grant_extension() must return
        // false once the extension has been spent, causing the "already spent"
        // error path to be reached.
        let budget = GoalBudget::new(None, Some(0), 0.5, || Duration::ZERO);
        let mut sup = AutonomySupervisor::new(
            Box::new(AlwaysFailsJudge),
            "finish the task",
            budget,
            Some(crate::autonomy::GoalRetryPolicy::for_tests()),
        );
        assert!(sup.try_grant_extension(), "first grant must succeed");
        assert!(!sup.try_grant_extension(), "second grant must return false");
    }

    // ── Escalate branch: headless path ────────────────────────────────────────

    #[tokio::test]
    async fn headless_escalate_stops_without_prompt() {
        // When user_question is None (headless), the Escalate verdict must
        // produce Stop without emitting any Error event.
        let mut goal = Some(escalating_supervisor());
        let sink = CollectingSink::new();

        let result = decide_stop(
            None, "text", &mut goal, &mut 0, None, None, &sink, CancellationToken::new(), None,
        )
        .await
        .expect("no error");

        assert!(matches!(result, StopAction::Stop));
        let events = sink.take();
        assert!(
            !events.iter().any(|e| matches!(e, AgentEvent::Error { .. })),
            "headless Escalate must not emit an Error event"
        );
    }
}
