//! Plan approval without an operator.
//!
//! `exit_plan_mode` asks a human to approve a plan and waits — with no timeout,
//! because a slow human should not be cancelled. Under a goal that wait is
//! indefinite, and it is the third blocking seam: the design named the question
//! prompt and the permission prompt but missed this one, so a goal run that
//! finished researching and tried to present its plan would hang there forever.
//!
//! It is most acute under `plan` permission mode, which the design explicitly
//! supports as an autonomy envelope. Under that mode the agent researches
//! read-only and then the natural terminal action is exactly this one.
//!
//! The resolution mirrors the permission seam: decide from the operator's own
//! mode rather than asking. If the mode would let the plan be carried out, the
//! plan is approved and work continues. If it would not, approval is refused
//! and the reason is parked as a capability blocker, so the run reports what it
//! would have needed instead of stalling.

use std::sync::Arc;

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use coda_tool::PlanApprover;

use crate::permission::mode_state::SharedModeState;
use crate::permission::PermissionMode;

use super::ledger::{AssumptionLedger, BlockerKind};

/// Approves plans from policy while a goal is active.
pub struct AutonomousPlanApprover {
    mode: SharedModeState,
    ledger: Arc<AssumptionLedger>,
}

impl AutonomousPlanApprover {
    /// Build an approver.
    ///
    /// `ledger` must be the supervisor's own handle, for the same reason the
    /// other seams require it: a separate one would accept every write and
    /// surface none of them.
    pub fn new(mode: SharedModeState, ledger: Arc<AssumptionLedger>) -> Self {
        Self { mode, ledger }
    }
}

#[async_trait]
impl PlanApprover for AutonomousPlanApprover {
    async fn approve(&self, plan: &str, _cancel: CancellationToken) -> bool {
        // Read live, so a mid-run mode change is honoured here exactly as it is
        // at the permission seam.
        let mode = self.mode.get();
        match mode {
            // The mode permits changes, so carrying out the plan is within what
            // the operator already allowed.
            PermissionMode::BypassPermissions | PermissionMode::AcceptEdits => true,

            // `default` asks before each mutating action anyway, and those
            // requests are themselves resolved from policy. Approving the plan
            // does not widen anything: each step still faces the envelope.
            PermissionMode::Default => true,

            // `plan` mode forbids every mutating action, so approving a plan
            // would promise work the run cannot legally do. Refuse, and record
            // why, so the report names the mode that would have been needed.
            PermissionMode::Plan => {
                let tried = vec![format!(
                    "prepared a plan ({} characters) and sought approval to carry it out",
                    plan.len()
                )];
                let _ = self.ledger.park_blocker(
                    BlockerKind::InsufficientPermission,
                    &tried,
                    "permission mode acceptEdits or higher to carry out a plan",
                    None,
                );
                false
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::permission::mode_state::PermissionModeState;

    fn approver(mode: PermissionMode) -> (AutonomousPlanApprover, Arc<AssumptionLedger>) {
        let ledger = Arc::new(AssumptionLedger::new());
        let state = Arc::new(PermissionModeState::new(mode));
        (AutonomousPlanApprover::new(state, Arc::clone(&ledger)), ledger)
    }

    /// The whole point: whatever the answer, it arrives immediately.
    #[tokio::test]
    async fn every_mode_answers_without_waiting() {
        for mode in [
            PermissionMode::Default,
            PermissionMode::AcceptEdits,
            PermissionMode::Plan,
            PermissionMode::BypassPermissions,
        ] {
            let (a, _) = approver(mode);
            let answered = tokio::time::timeout(
                std::time::Duration::from_secs(5),
                a.approve("the plan", CancellationToken::new()),
            )
            .await;
            assert!(answered.is_ok(), "{mode:?} must not block");
        }
    }

    #[tokio::test]
    async fn a_mode_that_permits_changes_approves_the_plan() {
        for mode in [
            PermissionMode::BypassPermissions,
            PermissionMode::AcceptEdits,
            PermissionMode::Default,
        ] {
            let (a, ledger) = approver(mode);
            assert!(a.approve("the plan", CancellationToken::new()).await, "{mode:?}");
            assert!(
                !ledger.has_parked_blockers(),
                "{mode:?}: an approved plan blocks nothing"
            );
        }
    }

    /// Plan mode forbids every mutating action, so approving would promise work
    /// the run cannot legally do.
    #[tokio::test]
    async fn plan_mode_refuses_and_records_what_was_needed() {
        let (a, ledger) = approver(PermissionMode::Plan);
        assert!(!a.approve("the plan", CancellationToken::new()).await);

        let snapshot = ledger.snapshot();
        assert!(snapshot.has_parked_blockers());
        assert_eq!(
            snapshot.goal_level_blockers(),
            1,
            "a plan-mode refusal blocks the goal, not one named todo"
        );
    }

    /// A refusal must satisfy the exhaustion rule like any other park, or the
    /// blocker would be silently dropped and the run could not prove it stopped.
    #[tokio::test]
    async fn a_refusal_records_what_was_attempted() {
        let (a, ledger) = approver(PermissionMode::Plan);
        a.approve("the plan", CancellationToken::new()).await;

        let recorded = ledger.entries().into_iter().any(|e| match e {
            super::super::LedgerEntry::ParkedBlocker { tried, .. } => {
                tried.iter().any(|t| t.contains("prepared a plan"))
            }
            _ => false,
        });
        assert!(recorded, "the attempt must be recorded, not assumed");
    }

    #[tokio::test]
    async fn a_mid_run_mode_change_is_observed_by_the_next_plan() {
        let ledger = Arc::new(AssumptionLedger::new());
        let state = Arc::new(PermissionModeState::new(PermissionMode::Plan));
        let a = AutonomousPlanApprover::new(Arc::clone(&state), ledger);

        assert!(!a.approve("p", CancellationToken::new()).await);
        state.set(PermissionMode::AcceptEdits);
        assert!(a.approve("p", CancellationToken::new()).await, "the change must take effect");
    }
}
