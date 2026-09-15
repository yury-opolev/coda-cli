//! Goal run verdicts, outcomes and status snapshot.

use std::time::Duration;

/// Decision returned by `AutonomySupervisor::evaluate` at every natural stop.
///
/// The `Escalate` variant imposes a caller contract (§4.4): the caller **MUST**
/// invoke exactly one of `TryGrantExtension` (then continue) or
/// `MarkStoppedUnmet` (then stop) before calling `evaluate` again, or the
/// exhausted budget will re-escalate indefinitely.  This is encoded in the
/// supervisor's state machine, not just a doc comment.
#[derive(Debug, Clone, PartialEq)]
pub enum GoalVerdict {
    /// Goal not yet met; inject `nudge` as a user message and keep looping.
    Continue { nudge: String },
    /// The run should end.  `met = true` when the judge confirmed completion;
    /// `met = false` when the budget was exhausted after the extension was spent.
    Stop { met: bool },
    /// The run should end because stopping was *proved*, not because a budget
    /// ran out. Carries the outcome the proof established.
    StopProved { outcome: GoalOutcome, report: String },
    /// Budget exhausted and extension unused: ask the operator `question`.
    /// The caller resolves by granting an extension (continue) or marking
    /// the goal unmet (stop).
    Escalate { question: String, remaining: Option<String> },
}

/// Terminal outcome of an autonomous goal run.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GoalOutcome {
    /// No goal was active for this run.
    None,
    /// The judge verified the goal as fully complete.
    Met,
    /// The budget (time or turns, including the one extension) was exhausted
    /// before the goal was confirmed complete.
    Unmet,
    /// Every remaining piece of work is parked behind a recorded blocker.
    ///
    /// The provable form of "impossible to proceed": the report names each
    /// blocker, what was tried, and what a human would need to supply.
    GenuinelyBlocked,
    /// The run was looping with nothing parked and nothing moving.
    ///
    /// Not blocked on anyone — it had simply stopped getting anywhere. This is
    /// what guarantees termination when the operator set no budget.
    Stalled,
}

impl GoalOutcome {
    pub fn as_str(self) -> &'static str {
        match self {
            GoalOutcome::None => "none",
            GoalOutcome::Met => "met",
            GoalOutcome::Unmet => "unmet",
            GoalOutcome::GenuinelyBlocked => "genuinelyBlocked",
            GoalOutcome::Stalled => "stalled",
        }
    }
}

/// Snapshot of goal run metrics, surfaced to callers at run end.
#[derive(Debug, Clone)]
pub struct GoalStatus {
    pub outcome: GoalOutcome,
    /// The "what remains" text from the last CONTINUE response, if any.
    pub remaining: Option<String>,
    /// How many times the supervisor nudged the loop to continue.
    pub continuations: u32,
    pub elapsed: Duration,
    pub escalated: bool,
    pub extension_used: bool,
}

impl GoalStatus {
    /// The "no goal active" sentinel.
    pub fn none() -> Self {
        Self {
            outcome: GoalOutcome::None,
            remaining: None,
            continuations: 0,
            elapsed: Duration::ZERO,
            escalated: false,
            extension_used: false,
        }
    }

    /// True when the goal was not active or was verified complete (i.e. not Unmet).
    pub fn is_successful(&self) -> bool {
        !matches!(
            self.outcome,
            GoalOutcome::Unmet | GoalOutcome::GenuinelyBlocked | GoalOutcome::Stalled
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn none_status_is_successful() {
        assert!(GoalStatus::none().is_successful());
    }

    #[test]
    fn met_outcome_is_successful() {
        let s = GoalStatus { outcome: GoalOutcome::Met, ..GoalStatus::none() };
        assert!(s.is_successful());
    }

    #[test]
    fn unmet_outcome_is_not_successful() {
        let s = GoalStatus { outcome: GoalOutcome::Unmet, ..GoalStatus::none() };
        assert!(!s.is_successful());
    }

    /// The two proved terminal states are honest failures: the goal was not
    /// met, and a caller must not read them as success.
    #[test]
    fn the_proved_terminal_outcomes_are_not_successful() {
        for outcome in [GoalOutcome::GenuinelyBlocked, GoalOutcome::Stalled] {
            let s = GoalStatus { outcome, ..GoalStatus::none() };
            assert!(!s.is_successful(), "{outcome:?} is not success");
        }
    }

    #[test]
    fn every_outcome_has_a_stable_wire_name() {
        for (outcome, name) in [
            (GoalOutcome::None, "none"),
            (GoalOutcome::Met, "met"),
            (GoalOutcome::Unmet, "unmet"),
            (GoalOutcome::GenuinelyBlocked, "genuinelyBlocked"),
            (GoalOutcome::Stalled, "stalled"),
        ] {
            assert_eq!(outcome.as_str(), name);
        }
    }
}
