//! What a successful conversation read proves about messages this client is
//! still holding a receipt for.
//!
//! The problem this exists for: the engine reports a steering delivery twice
//! (the `steering` projection in `session/getState`, and the legacy
//! `event/steeringDelivered` notification) and **both** reports can be lost —
//! a dropped connection during a turn loses the notification, and the first
//! snapshot this client manages to read can already be *after* the turn
//! ended. What survives is the retained `delivered` outcome, which keeps
//! being republished in every snapshot for as long as the outcome ring holds
//! it. Acting on that outcome alone means appending the message again,
//! underneath a conversation that was rebuilt from `session/getHistory` and
//! already contains it — committed history carries no queue id, so the two
//! copies cannot be recognised as one by identity.
//!
//! The way out is not to guess by text (two follow-ups with the same words
//! are two follow-ups) but to ask a question this client can actually answer:
//! *does the conversation I read already cover the committed prefix this
//! snapshot is describing?* That is decidable from the read's own fences.
//!
//! The proof it rests on, in `coda-serve`:
//!
//! - `host.rs` writes `*committed = history` (the whole agent history,
//!   including any steering text delivered into the turn) and calls
//!   `EngineState::end_turn` with `historyLength = committed.len()` **inside
//!   one transaction, under the history lock**, and that same transaction
//!   sets `turn = None`. So a snapshot that reports a turn as no longer
//!   running reports a `historyLength` whose committed prefix already
//!   contains that turn's delivered steering text.
//! - `session/getHistory` answers under the same lock and reports the
//!   `historyLength` its own read was exact at, so a read is a claim about a
//!   specific committed prefix rather than about "now".
//! - the epoch, the engine instance and the session id are all carried by
//!   both the read and the snapshot, so a compaction, a fork, a rewind or a
//!   different process invalidates the claim instead of silently borrowing
//!   another conversation's evidence.
//!
//! What it deliberately cannot prove is stated as `false` rather than
//! guessed: a delivery into the turn that is *still running* is not in any
//! committed prefix, and an outcome with no `turnId` (a legacy, unsequenced
//! engine) cannot be placed against a turn boundary at all.

use coda_proto::history::GetHistoryResult;
use coda_proto::state::{StateSnapshot, SteeringOutcomeDto};

/// A `session/getHistory` answer that was actually applied to the screen.
///
/// Built from the response itself — never from the view's current mutable
/// state — so what it claims is what *that* read returned, even if a newer
/// snapshot has since moved the numbers on.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HistoryCoverage {
    /// The process that answered. Another process's committed prefix is not
    /// evidence about this one.
    engine_instance_id: String,
    session_id: String,
    /// `None` for a saved transcript, which has no epoch: unfenced, so it
    /// proves nothing about a live conversation.
    history_epoch: Option<i64>,
    /// The committed/live fence this read was exact at.
    committed_length: i64,
    /// Whether the engine's projection of the *running* turn had to drop
    /// content to stay inside its byte budget. When it did, a message this
    /// client knows was delivered can be missing from the rebuilt
    /// conversation without the conversation being wrong.
    live_truncated: bool,
}

impl HistoryCoverage {
    /// Records what a read returned, as the answer itself reported it.
    pub fn of_read(result: &GetHistoryResult) -> Self {
        Self {
            engine_instance_id: result.engine_instance_id.clone(),
            session_id: result.session_id.clone(),
            // A saved transcript is not the live conversation, and its
            // absence of an epoch is not an epoch of `0`.
            history_epoch: result.is_live_session.then_some(result.history_epoch).flatten(),
            committed_length: result.history_length,
            live_truncated: result.live_truncated.unwrap_or(false),
        }
    }

    /// Whether the engine had to shorten the running turn's projection in the
    /// read this coverage came from.
    pub fn live_was_truncated(&self) -> bool {
        self.live_truncated
    }

    /// Whether the conversation this read rebuilt still accounts for every
    /// committed entry `snapshot` counts.
    ///
    /// `>=` rather than `==`: a read taken *after* the snapshot covers strictly
    /// more, and nothing this client shows is stale in that direction. Growth
    /// the other way — a snapshot that counts more than the read saw — is
    /// exactly the case where the claim must fail, because the entries the
    /// read never saw are the ones in question.
    ///
    /// An empty instance or session id means an engine that does not identify
    /// itself; it is refused rather than matched against another blank.
    pub fn covers_committed(&self, snapshot: &StateSnapshot) -> bool {
        !self.engine_instance_id.is_empty()
            && self.engine_instance_id == snapshot.engine_instance_id
            && !self.session_id.is_empty()
            && self.session_id == snapshot.session_id
            && self.history_epoch == Some(snapshot.history_epoch)
            && self.committed_length >= snapshot.history_length
    }

    /// Whether a `delivered` outcome is already part of the conversation this
    /// read rebuilt, so re-appending the local copy would double it.
    ///
    /// Two conditions, both required: the read covers the committed prefix
    /// the snapshot describes, and the delivery belongs to a turn that is
    /// over — because only a finished turn's content has been committed.
    pub fn covers_delivery(&self, snapshot: &StateSnapshot, outcome: &SteeringOutcomeDto) -> bool {
        self.covers_committed(snapshot) && delivery_turn_is_finished(snapshot, outcome)
    }
}

/// Whether the turn an outcome belongs to has finished, as of `snapshot`.
///
/// `None` — an engine that does not say which turn a delivery belonged to —
/// is *not* treated as finished. Single-flight would make that a tempting
/// guess, but a client that guesses here suppresses a message the model
/// received and nothing ever shows it again.
pub fn delivery_turn_is_finished(snapshot: &StateSnapshot, outcome: &SteeringOutcomeDto) -> bool {
    let Some(turn_id) = outcome.turn_id.as_deref() else {
        return false;
    };
    snapshot.turn.as_ref().is_none_or(|running| running.turn_id != turn_id)
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_proto::messages::CONTRACT_VERSION;
    use coda_proto::state::*;

    fn config() -> ActiveConfig {
        ActiveConfig {
            provider_id: None,
            model: "m".into(),
            effort: None,
            effort_is_auto: true,
            permission_mode: "default".into(),
            system_prompt_source: "default".into(),
        }
    }

    pub(crate) fn snapshot(history_length: i64) -> StateSnapshot {
        StateSnapshot {
            contract_version: CONTRACT_VERSION.into(),
            engine_instance_id: "e1".into(),
            session_id: "s1".into(),
            workspace_path: ".".into(),
            cursor: 10,
            history_epoch: 3,
            history_length,
            lifecycle: EngineLifecycle::Ready,
            initialized: true,
            last_turn_outcome: None,
            turn: None,
            steering: SteeringQueueState::default(),
            tools: ToolsState::default(),
            requests: Vec::new(),
            config: EffectiveConfig { next: config(), active: None, differing: Vec::new() },
            usage: UsageState::default(),
            limits: Limits {
                ring_envelopes: 2048,
                ring_bytes: 4 << 20,
                live_bytes_cap: 262_144,
                outcomes_retained: 64,
                history_block_bytes_cap: 65_536,
                max_history_page: 500,
                max_session_page: 200,
            },
            capabilities: Default::default(),
        }
    }

    fn running(snapshot: &mut StateSnapshot, turn_id: &str) {
        snapshot.turn = Some(TurnState {
            turn_id: turn_id.into(),
            started_at: "2026-01-01T00:00:00Z".into(),
            elapsed_ms: Some(1),
            phase: ActivityPhase::Responding,
            phase_since: "2026-01-01T00:00:00Z".into(),
            phase_elapsed_ms: Some(1),
            model_request: None,
            batches: Vec::new(),
            live_entries: Vec::new(),
            live_truncated: false,
            live_omitted_bytes: 0,
            active_config: config(),
            concurrent: ConcurrentCounters::default(),
        });
        snapshot.lifecycle = EngineLifecycle::Busy;
    }

    fn read(length: i64) -> GetHistoryResult {
        GetHistoryResult {
            session_id: "s1".into(),
            engine_instance_id: "e1".into(),
            is_live_session: true,
            history_epoch: Some(3),
            cursor: Some(10),
            history_length: length,
            entries: Vec::new(),
            next_index: length,
            total_known: length,
            truncated: false,
            live_entries: None,
            live_truncated: None,
            live_omitted_bytes: None,
        }
    }

    fn delivered(turn_id: Option<&str>) -> SteeringOutcomeDto {
        SteeringOutcomeDto {
            message_id: "m1".into(),
            outcome: SteeringOutcomeKind::Delivered,
            at: "2026-01-01T00:00:01Z".into(),
            turn_id: turn_id.map(str::to_string),
        }
    }

    #[test]
    fn a_read_that_saw_the_whole_committed_prefix_covers_a_finished_turns_delivery() {
        let coverage = HistoryCoverage::of_read(&read(6));
        assert!(coverage.covers_delivery(&snapshot(6), &delivered(Some("t1"))));
    }

    #[test]
    fn a_committed_prefix_that_grew_after_the_read_is_not_covered() {
        // The entries the read never saw are precisely the ones in question.
        let coverage = HistoryCoverage::of_read(&read(6));
        assert!(!coverage.covers_delivery(&snapshot(7), &delivered(Some("t1"))));
    }

    #[test]
    fn a_delivery_into_the_running_turn_is_never_covered_by_committed_history() {
        let coverage = HistoryCoverage::of_read(&read(6));
        let mut snapshot = snapshot(6);
        running(&mut snapshot, "t1");
        assert!(!coverage.covers_delivery(&snapshot, &delivered(Some("t1"))));
    }

    #[test]
    fn a_delivery_into_an_earlier_turn_is_covered_even_while_a_new_turn_runs() {
        let coverage = HistoryCoverage::of_read(&read(6));
        let mut snapshot = snapshot(6);
        running(&mut snapshot, "t2");
        assert!(coverage.covers_delivery(&snapshot, &delivered(Some("t1"))));
    }

    #[test]
    fn an_outcome_that_names_no_turn_is_never_claimed_as_covered() {
        let coverage = HistoryCoverage::of_read(&read(6));
        assert!(!coverage.covers_delivery(&snapshot(6), &delivered(None)));
    }

    #[test]
    fn another_process_or_conversation_is_not_evidence_about_this_one() {
        let mut other_instance = read(6);
        other_instance.engine_instance_id = "e2".into();
        assert!(!HistoryCoverage::of_read(&other_instance).covers_committed(&snapshot(6)));

        let mut other_session = read(6);
        other_session.session_id = "s2".into();
        assert!(!HistoryCoverage::of_read(&other_session).covers_committed(&snapshot(6)));

        let mut other_epoch = read(6);
        other_epoch.history_epoch = Some(4);
        assert!(!HistoryCoverage::of_read(&other_epoch).covers_committed(&snapshot(6)));
    }

    #[test]
    fn a_saved_transcript_has_no_epoch_and_proves_nothing_about_the_live_one() {
        let mut saved = read(6);
        saved.is_live_session = false;
        saved.history_epoch = None;
        assert!(!HistoryCoverage::of_read(&saved).covers_committed(&snapshot(6)));
    }

    #[test]
    fn an_engine_that_does_not_identify_itself_is_refused_rather_than_matched_blank() {
        let mut anonymous = read(6);
        anonymous.engine_instance_id = String::new();
        let mut snapshot = snapshot(6);
        snapshot.engine_instance_id = String::new();
        assert!(!HistoryCoverage::of_read(&anonymous).covers_committed(&snapshot));
    }
}
