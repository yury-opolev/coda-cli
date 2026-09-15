//! The termination proof: deciding, defensibly, that a run should stop.
//!
//! A goal promises "keep working until this is done". Honouring that means the
//! run may not stop merely because something looked hard — but it also must
//! not run forever, and with budgets defaulting to 240h and settable to `none`
//! the budget cannot be what stops it.
//!
//! So stopping has to be *earned*. This module turns "I think we're stuck" into
//! a claim that can be checked: a run stops early only when the ledger and the
//! stuck detector together establish that no further progress is possible.
//!
//! ## The two proofs
//!
//! **Genuinely blocked** — every remaining piece of work is parked behind a
//! recorded blocker, and nothing has moved for several turns. The operator gets
//! a list of what is blocked, what was tried, and what they would need to
//! supply. This is the state the design exists to make *derivable* rather than
//! a matter of opinion.
//!
//! **Stalled** — the agent is looping and nothing is parked. Nothing is blocked
//! in the sense of needing a human; the run has simply stopped getting
//! anywhere. This is the backstop that guarantees termination when no budget
//! would.
//!
//! The two are mutually exclusive by construction, and that matters more than
//! it looks: an earlier iteration let a permission denial park a blocker that
//! no work item could ever match, which satisfied neither proof and left a run
//! that could reach no terminal state at all.

use super::ledger::LedgerSnapshot;

/// Consecutive continuations without a progress signal before either proof may
/// be considered.
///
/// Three, matching the stuck detector's own monologue and error thresholds, so
/// the two mechanisms judge a run on the same timescale rather than one
/// pre-empting the other.
pub const NO_PROGRESS_WINDOW: u32 = 3;

/// The measurable facts about a run at one moment.
///
/// Compared against the previous turn's copy to decide whether anything
/// actually happened. Counters rather than events, so a turn that both
/// completes a todo and reverts one is correctly seen as movement.
///
/// Every field here must be a fact about **work done**, never about what the
/// agent or a judge *said*. The completion judge's "what remains" prose was
/// once part of this comparison, and because that prose is regenerated from
/// the agent's varying output it differed almost every turn — so the quiet
/// counter reset continually and neither termination proof could ever fire. A
/// progress signal that a flailing agent can produce by flailing is not a
/// progress signal.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ProgressSnapshot {
    /// Work items marked done.
    pub completed_work_items: usize,
    /// Successful file mutations.
    pub files_changed: u64,
    /// **Distinct** entries in the assumption ledger.
    ///
    /// Distinct, not total: an agent that retries the same denied action every
    /// turn appends an identical `Denial` every turn, and counting those would
    /// make hitting the same wall repeatedly read as forward motion.
    pub distinct_ledger_entries: usize,
}

/// Counts how long a run has gone without moving.
#[derive(Debug, Default)]
pub struct ProgressTracker {
    previous: Option<ProgressSnapshot>,
    without_progress: u32,
}

impl ProgressTracker {
    pub fn new() -> Self {
        Self::default()
    }

    /// Record the current state and report whether anything moved.
    ///
    /// The first observation is always progress: there is nothing to compare
    /// against, and treating an unknown as a stall would let a run be declared
    /// finished before it had done anything.
    pub fn observe(&mut self, snapshot: ProgressSnapshot) -> bool {
        let moved = match &self.previous {
            None => true,
            Some(previous) => previous != &snapshot,
        };
        if moved {
            self.without_progress = 0;
        } else {
            self.without_progress = self.without_progress.saturating_add(1);
        }
        self.previous = Some(snapshot);
        moved
    }

    /// Consecutive observations with nothing to show for them.
    pub fn without_progress(&self) -> u32 {
        self.without_progress
    }

    /// Whether the run has been still long enough for a proof to be considered.
    pub fn is_quiet(&self) -> bool {
        self.without_progress >= NO_PROGRESS_WINDOW
    }

    /// Forget the history. Used when the operator speaks, since a new
    /// instruction makes everything before it a poor guide to whether the run
    /// is getting anywhere now.
    pub fn reset(&mut self) {
        self.previous = None;
        self.without_progress = 0;
    }
}

/// What the evidence supports.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TerminationProof {
    /// Every remaining item is parked behind a recorded blocker.
    GenuinelyBlocked,
    /// Looping with nothing parked and nothing moving.
    Stalled,
    /// Neither proof holds — keep working.
    KeepGoing,
}

/// Everything the proof needs, gathered at one moment.
#[derive(Debug, Clone, Copy)]
pub struct TerminationInputs<'a> {
    /// Work items not yet done, from the todo list.
    pub open_work_items: &'a [String],
    /// A single consistent view of the ledger.
    pub ledger: &'a LedgerSnapshot,
    /// Whether the stuck detector has seen a loop survive its nudge.
    pub stuck: bool,
    /// Consecutive turns without a progress signal.
    pub without_progress: u32,
}

/// Decide whether the evidence supports stopping.
///
/// Both proofs require the run to have been quiet for [`NO_PROGRESS_WINDOW`]
/// turns. Without that a single unlucky turn — one parked blocker while other
/// work was still in flight — could end a run that was going perfectly well.
pub fn prove(inputs: TerminationInputs<'_>) -> TerminationProof {
    if inputs.without_progress < NO_PROGRESS_WINDOW {
        return TerminationProof::KeepGoing;
    }

    if inputs.ledger.has_parked_blockers() {
        // Something is parked. The question is whether *everything* is.
        let everything_parked = if inputs.open_work_items.is_empty() {
            // No todo list to reason about. A blocker recorded against the goal
            // as a whole is then the only evidence available, and it is enough:
            // a permission limit or a missing credential blocks the work
            // whether or not anyone wrote a todo for it.
            inputs.ledger.goal_level_blockers() > 0
        } else {
            inputs.ledger.all_parked(inputs.open_work_items.iter().map(String::as_str))
        };

        if everything_parked {
            return TerminationProof::GenuinelyBlocked;
        }

        // Parked work exists, but some of the remainder is not blocked. If the
        // agent is also looping, that remainder is not being advanced either,
        // and the run must still end.
        //
        // An earlier version returned `KeepGoing` here on the grounds that a
        // run with real blockers should report them rather than be dismissed as
        // merely looping. That reasoning confused the *label* with the
        // *decision*: it declined to call the state `Stalled` but then chose no
        // terminal state at all, so a single parked branch disabled termination
        // for the whole run — forever, once budgets can be `none`. The label is
        // a presentation question; terminating is not optional. The report
        // lists every parked blocker whichever proof fired, so nothing is lost.
        if inputs.stuck {
            return TerminationProof::Stalled;
        }

        return TerminationProof::KeepGoing;
    }

    // Nothing parked. Looping with nothing to show for it is a stall.
    if inputs.stuck {
        return TerminationProof::Stalled;
    }

    TerminationProof::KeepGoing
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::autonomy::ledger::{AssumptionLedger, BlockerKind};

    fn items(list: &[&str]) -> Vec<String> {
        list.iter().map(|s| (*s).to_owned()).collect()
    }

    fn ledger_with_item_parks(parked: &[&str]) -> AssumptionLedger {
        let ledger = AssumptionLedger::new();
        for item in parked {
            ledger
                .park_blocker(
                    BlockerKind::MissingCredential,
                    &items(&["checked the environment"]),
                    "a credential",
                    Some(item),
                )
                .expect("an attempt was recorded");
        }
        ledger
    }

    fn ledger_with_goal_level_park() -> AssumptionLedger {
        let ledger = AssumptionLedger::new();
        ledger
            .park_blocker(
                BlockerKind::InsufficientPermission,
                &items(&["attempted run_command under permission mode plan"]),
                "permission mode bypassPermissions or higher",
                None,
            )
            .expect("an attempt was recorded");
        ledger
    }

    fn inputs<'a>(
        open: &'a [String],
        ledger: &'a LedgerSnapshot,
        stuck: bool,
        without_progress: u32,
    ) -> TerminationInputs<'a> {
        TerminationInputs { open_work_items: open, ledger, stuck, without_progress }
    }

    // ── Progress tracking ────────────────────────────────────────────────────

    /// With nothing to compare against, the first turn cannot be a stall — a
    /// run must never be declared finished before it has done anything.
    #[test]
    fn the_first_observation_always_counts_as_progress() {
        let mut t = ProgressTracker::new();
        assert!(t.observe(ProgressSnapshot::default()));
        assert_eq!(t.without_progress(), 0);
        assert!(!t.is_quiet());
    }

    #[test]
    fn an_unchanged_snapshot_counts_as_no_progress() {
        let mut t = ProgressTracker::new();
        t.observe(ProgressSnapshot::default());
        assert!(!t.observe(ProgressSnapshot::default()));
        assert_eq!(t.without_progress(), 1);
    }

    #[test]
    fn each_kind_of_movement_counts_as_progress() {
        let base = ProgressSnapshot::default();
        let variants = [
            ProgressSnapshot { completed_work_items: 1, ..base.clone() },
            ProgressSnapshot { files_changed: 1, ..base.clone() },
            ProgressSnapshot { distinct_ledger_entries: 1, ..base.clone() },
        ];
        for moved in variants {
            let mut t = ProgressTracker::new();
            t.observe(base.clone());
            t.observe(base.clone());
            assert!(t.observe(moved.clone()), "{moved:?} is movement");
            assert_eq!(t.without_progress(), 0, "movement resets the count");
        }
    }

    /// REGRESSION: the completion judge's "what remains" prose was once part of
    /// this comparison. It is regenerated from the agent's varying output, so it
    /// differed nearly every turn, reset the quiet counter continually, and left
    /// both proofs unable to fire in any realistic run. A progress signal a
    /// flailing agent can produce by flailing is not a progress signal.
    #[test]
    fn a_run_that_only_produces_varying_prose_is_not_making_progress() {
        let mut t = ProgressTracker::new();
        // Whatever the judge or the agent says, nothing measurable moves.
        for _ in 0..(NO_PROGRESS_WINDOW + 2) {
            t.observe(ProgressSnapshot {
                completed_work_items: 0,
                files_changed: 0,
                distinct_ledger_entries: 0,
            });
        }
        assert!(t.is_quiet(), "talking is not progress");
    }

    #[test]
    fn the_run_is_quiet_only_after_the_full_window() {
        let mut t = ProgressTracker::new();
        t.observe(ProgressSnapshot::default());
        for _ in 0..(NO_PROGRESS_WINDOW - 1) {
            t.observe(ProgressSnapshot::default());
            assert!(!t.is_quiet());
        }
        t.observe(ProgressSnapshot::default());
        assert!(t.is_quiet());
    }

    #[test]
    fn a_new_instruction_clears_the_progress_history() {
        let mut t = ProgressTracker::new();
        for _ in 0..6 {
            t.observe(ProgressSnapshot::default());
        }
        assert!(t.is_quiet());

        t.reset();
        assert_eq!(t.without_progress(), 0);
        assert!(!t.is_quiet());
        assert!(t.observe(ProgressSnapshot::default()), "the next turn starts fresh");
    }

    /// A very long quiet run must not overflow the counter.
    #[test]
    fn the_quiet_counter_saturates() {
        let mut t = ProgressTracker::new();
        t.observe(ProgressSnapshot::default());
        t.without_progress = u32::MAX;
        t.observe(ProgressSnapshot::default());
        assert_eq!(t.without_progress(), u32::MAX);
    }

    // ── The proofs ───────────────────────────────────────────────────────────

    /// Nothing may be concluded from a single quiet turn: other work is often
    /// still in flight when the first blocker lands.
    #[test]
    fn a_run_that_has_not_been_quiet_long_enough_keeps_going() {
        let ledger = ledger_with_item_parks(&["one"]).snapshot();
        for quiet in 0..NO_PROGRESS_WINDOW {
            assert_eq!(
                prove(inputs(&items(&["one"]), &ledger, true, quiet)),
                TerminationProof::KeepGoing,
                "{quiet} quiet turns is not yet enough to stop"
            );
        }
    }

    #[test]
    fn every_open_item_parked_proves_genuinely_blocked() {
        let ledger = ledger_with_item_parks(&["one", "two"]).snapshot();
        assert_eq!(
            prove(inputs(&items(&["one", "two"]), &ledger, false, NO_PROGRESS_WINDOW)),
            TerminationProof::GenuinelyBlocked
        );
    }

    /// The proof is a subset check: one unblocked item is enough to keep going.
    #[test]
    fn one_unparked_item_is_enough_to_keep_going() {
        let ledger = ledger_with_item_parks(&["one"]).snapshot();
        assert_eq!(
            prove(inputs(&items(&["one", "two"]), &ledger, false, NO_PROGRESS_WINDOW)),
            TerminationProof::KeepGoing
        );
    }

    /// REGRESSION (hang): a parked blocker on one branch must not disable
    /// termination for the whole run. This case — something parked, something
    /// else unparked, and the agent looping — previously returned `KeepGoing`
    /// unconditionally and ran forever once budgets could be `none`. The label
    /// was the only thing in question; terminating never was.
    #[test]
    fn a_stuck_run_with_unfinished_unblocked_work_still_terminates() {
        let ledger = ledger_with_item_parks(&["one"]).snapshot();
        assert_eq!(
            prove(inputs(&items(&["one", "two"]), &ledger, true, NO_PROGRESS_WINDOW)),
            TerminationProof::Stalled,
            "a looping run must reach a terminal state even with a blocker recorded"
        );
    }

    /// The same shape, but the agent is merely slow rather than looping: that
    /// must still keep going, or a long build would kill a healthy run.
    #[test]
    fn a_slow_run_with_unfinished_unblocked_work_keeps_going() {
        let ledger = ledger_with_item_parks(&["one"]).snapshot();
        assert_eq!(
            prove(inputs(&items(&["one", "two"]), &ledger, false, NO_PROGRESS_WINDOW * 5)),
            TerminationProof::KeepGoing,
            "slow is not stuck"
        );
    }

    /// REGRESSION: a permission-denied run has a goal-level blocker and no
    /// todos. Requiring a named work item made it satisfy neither proof —
    /// `all_parked` is false with no items, and the stall path is disqualified
    /// because something *is* parked — so the run could reach no terminal state
    /// at all. Exactly the hang this design exists to remove.
    #[test]
    fn a_goal_level_blocker_with_no_todo_list_still_proves_genuinely_blocked() {
        let ledger = ledger_with_goal_level_park().snapshot();
        assert_eq!(
            prove(inputs(&[], &ledger, false, NO_PROGRESS_WINDOW)),
            TerminationProof::GenuinelyBlocked
        );
    }

    /// With a todo list present, a goal-level blocker alone does not excuse
    /// unfinished named work.
    #[test]
    fn a_goal_level_blocker_does_not_cover_named_work_items() {
        let ledger = ledger_with_goal_level_park().snapshot();
        assert_eq!(
            prove(inputs(&items(&["one"]), &ledger, false, NO_PROGRESS_WINDOW)),
            TerminationProof::KeepGoing
        );
    }

    #[test]
    fn looping_with_nothing_parked_is_a_stall() {
        let empty = AssumptionLedger::new().snapshot();
        assert_eq!(
            prove(inputs(&[], &empty, true, NO_PROGRESS_WINDOW)),
            TerminationProof::Stalled
        );
    }

    /// Quiet is not the same as stuck. A run that is simply slow — a long
    /// build, a long download — must not be killed for it.
    #[test]
    fn a_quiet_run_that_is_not_looping_keeps_going() {
        let empty = AssumptionLedger::new().snapshot();
        assert_eq!(
            prove(inputs(&[], &empty, false, NO_PROGRESS_WINDOW * 10)),
            TerminationProof::KeepGoing
        );
    }

    /// A run with recorded blockers should be *reported* as blocked wherever
    /// the evidence supports it, rather than dismissed as merely looping.
    #[test]
    fn a_fully_accounted_run_reports_blocked_rather_than_stalled() {
        let parked = ledger_with_item_parks(&["one"]).snapshot();
        for stuck in [true, false] {
            assert_eq!(
                prove(inputs(&items(&["one"]), &parked, stuck, NO_PROGRESS_WINDOW)),
                TerminationProof::GenuinelyBlocked,
                "everything is parked, so the run is blocked, not stalled"
            );
        }
    }

    /// THE GUARANTEE. Sweep the whole reachable input space: every quiet,
    /// looping run must reach a terminal state, whatever the mix of blockers
    /// and work items. This is what replaces the budget as the thing that
    /// stops a runaway, so it is asserted exhaustively rather than by example.
    #[test]
    fn every_quiet_looping_run_reaches_a_terminal_state() {
        let empty = AssumptionLedger::new().snapshot();
        let goal_level = ledger_with_goal_level_park().snapshot();
        let item_level = ledger_with_item_parks(&["one"]).snapshot();
        let mixed = {
            let l = ledger_with_item_parks(&["one"]);
            l.park_blocker(
                BlockerKind::InsufficientPermission,
                &items(&["attempted under plan"]),
                "a higher mode",
                None,
            )
            .unwrap();
            l.snapshot()
        };

        let ledgers = [
            ("empty", &empty),
            ("goal-level", &goal_level),
            ("item-level", &item_level),
            ("mixed", &mixed),
        ];
        let work_sets = [
            ("none", items(&[])),
            ("all parked", items(&["one"])),
            ("some unparked", items(&["one", "two"])),
        ];

        for (ledger_name, ledger) in ledgers {
            for (work_name, open) in &work_sets {
                let proof = prove(inputs(open, ledger, true, NO_PROGRESS_WINDOW));
                assert_ne!(
                    proof,
                    TerminationProof::KeepGoing,
                    "a quiet, looping run must terminate \
                     (ledger: {ledger_name}, work: {work_name})"
                );
            }
        }
    }

    /// The mirror of the guarantee: a run that is quiet but NOT looping is
    /// never killed unless everything is genuinely accounted for. Slowness must
    /// never be punished.
    #[test]
    fn a_quiet_but_productive_run_is_never_killed_for_being_slow() {
        let empty = AssumptionLedger::new().snapshot();
        let item_level = ledger_with_item_parks(&["one"]).snapshot();

        for (ledger, open) in [
            (&empty, items(&[])),
            (&empty, items(&["one"])),
            (&item_level, items(&["one", "two"])),
        ] {
            assert_eq!(
                prove(inputs(&open, ledger, false, NO_PROGRESS_WINDOW * 10)),
                TerminationProof::KeepGoing,
                "not looping and not fully blocked means there is still work to do"
            );
        }
    }
}
