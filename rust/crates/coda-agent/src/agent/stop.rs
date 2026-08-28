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
use crate::goal::{GoalSupervisor, GoalVerdict};
use crate::steering::SteeringInbox;

use super::AgentError;

/// The decision returned by `decide_stop`.
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
    goal: &mut Option<GoalSupervisor>,
    _stop_continuations: &mut u32,
    steering: Option<&SteeringInbox>,
    sink: &dyn AgentSink,
    cancel: CancellationToken,
    // Seam: user-question prompt (later phase).  `None` = headless.
    user_question: Option<&dyn UserQuestionPrompt>,
) -> Result<StopAction, AgentError> {
    // --- Goal path (§1.5) ---
    // Mutually exclusive with generic stop hooks (§8 item 21).
    if let Some(goal) = goal {
        let verdict = goal.evaluate(last_assistant_text, cancel.clone()).await;

        return match verdict {
            GoalVerdict::Continue { nudge } => Ok(StopAction::Continue { nudge }),

            GoalVerdict::Escalate { question, .. } => {
                // Ask the operator — headless (user_question = None) → stop unmet.
                let answer = match user_question {
                    Some(prompt) => {
                        prompt
                            .ask(&question, &[GOAL_CONTINUE_OPTION, GOAL_STOP_OPTION], cancel.clone())
                            .await
                    }
                    None => None,
                };

                let wants_continue = answer
                    .as_deref()
                    .map(|a| !a.trim().is_empty() && !a.eq_ignore_ascii_case(GOAL_STOP_OPTION))
                    .unwrap_or(false);

                if wants_continue {
                    let ans = answer.unwrap();
                    if goal.try_grant_extension() {
                        let nudge = format!("Operator guidance: {ans}\nContinue toward the goal.");
                        return Ok(StopAction::Continue { nudge });
                    }
                    // Extension already spent.
                    sink.emit(AgentEvent::Error {
                        message: "The budget extension was already used; stopping with the goal unmet.".into(),
                    });
                }
                // Headless, explicit stop, or extension spent → stop unmet.
                goal.mark_stopped_unmet();
                Ok(StopAction::Stop)
            }

            GoalVerdict::Stop { .. } => Ok(StopAction::Stop),
        };
    }

    // --- Generic stop hooks (§1.5, no-op seam, later phase) ---
    // When a goal IS active the goal path above already returned, so these
    // hooks are only consulted when no goal is wired (§8 item 21).

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
/// A later phase will provide a concrete implementation.
pub trait UserQuestionPrompt: Send + Sync {
    fn ask<'a>(
        &'a self,
        question: &'a str,
        options: &'a [&'a str],
        cancel: CancellationToken,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Option<String>> + Send + 'a>>;
}
