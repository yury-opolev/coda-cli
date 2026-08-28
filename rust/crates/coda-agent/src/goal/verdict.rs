//! Goal run verdicts, outcomes and status snapshot.

use std::time::Duration;

/// Decision returned by `GoalSupervisor::evaluate` at every natural stop.
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
        self.outcome != GoalOutcome::Unmet
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
}
