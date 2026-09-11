//! What happens to a follow-up the operator typed while the model was
//! replying, once the engine says it reached the model.
//!
//! The engine reports a delivery twice, by two independent routes: the
//! `steering` projection inside `session/getState`, and the legacy
//! `event/steeringDelivered` notification. The state moves first and the
//! notification follows, so **either** can arrive first at the client. Both
//! orders have to end with the message visible exactly once — never nought
//! times, never twice, and never sitting in the recovery list as though it
//! had not been sent.

use coda_proto::events::Event;
use coda_proto::history::{GetHistoryResult, HistoryBlock, HistoryEntry};
use coda_proto::messages::CONTRACT_VERSION;
use coda_proto::state::*;
use coda_tui::coverage::HistoryCoverage;
use coda_tui::state::{UiEvent, UiState};
use coda_tui::transcript::Block;

const FOLLOW_UP: &str = "operator correction";

fn config() -> ActiveConfig {
    ActiveConfig {
        provider_id: None,
        model: "fixture-model".into(),
        effort: None,
        effort_is_auto: true,
        permission_mode: "default".into(),
        system_prompt_source: "default".into(),
    }
}

fn text_entry(index: i64, role: &str, body: &str) -> HistoryEntry {
    HistoryEntry::new(index, role, vec![HistoryBlock::Text {
        text: body.into(),
        omitted_reason: None,
        full_length: None,
    }])
}

fn delivered(message_id: &str) -> SteeringOutcomeDto {
    SteeringOutcomeDto {
        message_id: message_id.into(),
        outcome: SteeringOutcomeKind::Delivered,
        at: "2026-01-01T00:00:01Z".into(),
        turn_id: Some("turn".into()),
    }
}

/// A snapshot of a turn that is still running, with `steering` exactly as the
/// engine publishes it.
fn snapshot(steering: SteeringQueueState, live: Vec<HistoryEntry>) -> StateSnapshot {
    StateSnapshot {
        contract_version: CONTRACT_VERSION.into(),
        engine_instance_id: "engine".into(),
        session_id: "session".into(),
        workspace_path: ".".into(),
        cursor: 20,
        history_epoch: 0,
        history_length: 0,
        lifecycle: EngineLifecycle::Busy,
        initialized: true,
        last_turn_outcome: None,
        turn: Some(TurnState {
            turn_id: "turn".into(),
            started_at: "2026-01-01T00:00:00Z".into(),
            elapsed_ms: Some(1_000),
            phase: ActivityPhase::Responding,
            phase_since: "2026-01-01T00:00:00Z".into(),
            phase_elapsed_ms: Some(1_000),
            model_request: None,
            batches: Vec::new(),
            live_entries: live,
            live_truncated: false,
            live_omitted_bytes: 0,
            active_config: config(),
            concurrent: ConcurrentCounters::default(),
        }),
        steering,
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

fn delivery_snapshot() -> StateSnapshot {
    snapshot(
        SteeringQueueState { outcomes: vec![delivered("queued-1")], ..Default::default() },
        vec![text_entry(0, "user", "start"), text_entry(1, "assistant", "answer")],
    )
}

/// A state mid-turn: a prompt sent, a reply streaming, a follow-up queued.
fn mid_turn() -> UiState {
    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "start".into() });
    state.apply(UiEvent::Engine(Event::AssistantText { delta: "answer".into() }));
    state.apply(UiEvent::Queued { text: FOLLOW_UP.into(), id: Some("queued-1".into()) });
    state
}

fn visible(state: &UiState, body: &str) -> usize {
    state
        .transcript
        .blocks()
        .iter()
        .filter(|block| matches!(block, Block::User { text, .. } if text == body))
        .count()
}

fn assistant_blocks(state: &UiState) -> Vec<String> {
    state
        .transcript
        .blocks()
        .iter()
        .filter_map(|block| match block {
            Block::Assistant { text, .. } => Some(text.clone()),
            _ => None,
        })
        .collect()
}

fn notices(state: &UiState) -> Vec<String> {
    state
        .transcript
        .blocks()
        .iter()
        .filter_map(|block| match block {
            Block::Notice { text, .. } => Some(text.clone()),
            _ => None,
        })
        .collect()
}

/// A `session/getHistory` answer for this session, exact at a committed
/// length of `length`, as the engine builds it.
fn read(length: i64) -> GetHistoryResult {
    GetHistoryResult {
        session_id: "session".into(),
        engine_instance_id: "engine".into(),
        is_live_session: true,
        history_epoch: Some(0),
        cursor: Some(20),
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

/// The committed conversation as a rebuild hands it back: an ordinary user
/// prompt with **no** queue id, because committed history carries none.
fn committed_conversation() -> Vec<Block> {
    vec![
        Block::User {
            text: "start".into(),
            timestamp: "09:00".into(),
            pending: false,
            queue_id: None,
        },
        Block::Assistant { text: "answer".into(), complete: true },
        Block::User {
            text: FOLLOW_UP.into(),
            timestamp: "09:01".into(),
            pending: false,
            queue_id: None,
        },
        Block::Assistant { text: "done".into(), complete: true },
    ]
}

/// A snapshot of an engine whose turn is over, with `steering` as the engine
/// publishes it and a committed fence of `history_length`.
fn settled_snapshot(steering: SteeringQueueState, history_length: i64) -> StateSnapshot {
    let mut snapshot = snapshot(steering, Vec::new());
    snapshot.turn = None;
    snapshot.lifecycle = EngineLifecycle::Ready;
    snapshot.history_length = history_length;
    snapshot
}

/// A `delivered` outcome belonging to a named turn.
fn delivered_in(message_id: &str, turn_id: &str) -> SteeringOutcomeDto {
    SteeringOutcomeDto { turn_id: Some(turn_id.into()), ..delivered(message_id) }
}

#[test]
fn delivered_follow_up_remains_visible_when_snapshot_beats_delivery_event() {
    let mut state = mid_turn();
    state.apply(UiEvent::Snapshot(Box::new(delivery_snapshot())));
    state.apply(UiEvent::Engine(Event::SteeringDelivered {
        message_ids: vec!["queued-1".into()],
    }));
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    assert_eq!(visible(&state, FOLLOW_UP), 1, "a delivered follow-up vanished from the chat");
    assert!(state.queued.is_empty());
    assert!(state.unsent.is_empty(), "a delivered message must not be offered for resend");
}

#[test]
fn the_delivery_event_arriving_first_still_shows_the_message_exactly_once() {
    let mut state = mid_turn();
    state.apply(UiEvent::Engine(Event::SteeringDelivered {
        message_ids: vec!["queued-1".into()],
    }));
    state.apply(UiEvent::Snapshot(Box::new(delivery_snapshot())));
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    assert_eq!(visible(&state, FOLLOW_UP), 1, "the two delivery reports doubled the message");
    assert!(state.queued.is_empty());
    assert!(state.unsent.is_empty());
}

#[test]
fn repeated_reports_of_the_same_delivery_add_nothing() {
    let mut state = mid_turn();
    state.apply(UiEvent::Snapshot(Box::new(delivery_snapshot())));
    state.apply(UiEvent::Snapshot(Box::new(delivery_snapshot())));
    state.apply(UiEvent::Engine(Event::SteeringDelivered {
        message_ids: vec!["queued-1".into()],
    }));
    state.apply(UiEvent::Engine(Event::SteeringDelivered {
        message_ids: vec!["queued-1".into()],
    }));
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    assert_eq!(visible(&state, FOLLOW_UP), 1);
}

#[test]
fn two_identical_follow_ups_are_two_messages() {
    // Saying the same thing twice is a thing people do, and the second one is
    // not a duplicate to be swallowed. Only the queue id decides identity.
    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "start".into() });
    state.apply(UiEvent::Engine(Event::AssistantText { delta: "answer".into() }));
    state.apply(UiEvent::Queued { text: FOLLOW_UP.into(), id: Some("queued-1".into()) });
    state.apply(UiEvent::Queued { text: FOLLOW_UP.into(), id: Some("queued-2".into()) });

    let steering = SteeringQueueState {
        outcomes: vec![delivered("queued-1"), delivered("queued-2")],
        ..Default::default()
    };
    state.apply(UiEvent::Snapshot(Box::new(snapshot(steering, Vec::new()))));
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    assert_eq!(visible(&state, FOLLOW_UP), 2, "identical text is not the same message");
}

#[test]
fn a_delivery_mid_reply_never_splits_the_reply() {
    let mut state = mid_turn();
    state.apply(UiEvent::Snapshot(Box::new(delivery_snapshot())));
    // The reply continues after the delivery was reported.
    state.apply(UiEvent::Engine(Event::AssistantText { delta: " continues".into() }));
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    assert_eq!(
        assistant_blocks(&state),
        vec!["answer continues".to_string()],
        "the delivered message must wait for a block boundary, not cut the reply in two"
    );
    assert_eq!(visible(&state, FOLLOW_UP), 1);
}

#[test]
fn the_local_copy_is_shown_even_when_the_engines_preview_was_truncated() {
    // The queue's own `text` is capped on the wire. The client has the whole
    // message, and handing back the short version would mangle what the
    // operator actually sent.
    let long = format!("{FOLLOW_UP} {}", "and more ".repeat(16));
    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "start".into() });
    state.apply(UiEvent::Queued { text: long.clone(), id: Some("queued-1".into()) });

    let steering = SteeringQueueState {
        pending: vec![SteeringPendingDto {
            message_id: "queued-1".into(),
            enqueued_at: "2026-01-01T00:00:00Z".into(),
            text: FOLLOW_UP.into(),
            text_length: long.len() as i64,
            text_truncated: true,
        }],
        pending_count: 1,
        ..Default::default()
    };
    state.apply(UiEvent::Snapshot(Box::new(snapshot(steering, Vec::new()))));

    let steering =
        SteeringQueueState { outcomes: vec![delivered("queued-1")], ..Default::default() };
    state.apply(UiEvent::Snapshot(Box::new(snapshot(steering, Vec::new()))));
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    assert_eq!(visible(&state, &long), 1, "the whole message must be shown, not the preview");
}

#[test]
fn a_delivery_belonging_to_a_conversation_already_rebuilt_is_not_shown_twice() {
    // The engine's live projection carries the queue id of a delivered
    // steering message, so a conversation re-read from the engine and the
    // copy this client is still holding are recognisably the same message.
    let mut state = mid_turn();
    state.apply(UiEvent::Rehydrated {
        blocks: vec![
            Block::User {
                text: "start".into(),
                timestamp: String::new(),
                pending: false,
                queue_id: None,
            },
            Block::Assistant { text: "answer".into(), complete: true },
            Block::User {
                text: FOLLOW_UP.into(),
                timestamp: String::new(),
                pending: false,
                queue_id: Some("queued-1".into()),
            },
        ],
        notices: Vec::new(),
        coverage: None,
    });
    state.apply(UiEvent::Snapshot(Box::new(delivery_snapshot())));
    state.apply(UiEvent::Engine(Event::SteeringDelivered {
        message_ids: vec!["queued-1".into()],
    }));
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    assert_eq!(
        visible(&state, FOLLOW_UP),
        1,
        "the rebuilt conversation already contained it; it must not be appended again"
    );
    assert!(state.queued.is_empty());
    assert!(state.unsent.is_empty());
}

#[test]
fn a_turn_that_ends_before_the_delivery_notice_does_not_strand_a_delivered_message() {
    // The turn's completion and the delivery notification race on the wire.
    // Losing that race must not leave a message the model received in the
    // recovery list, described as never sent.
    let mut state = mid_turn();
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });
    assert_eq!(state.unsent.len(), 1, "with no news, an unsent message is kept recoverable");

    state.apply(UiEvent::Engine(Event::SteeringDelivered {
        message_ids: vec!["queued-1".into()],
    }));

    assert_eq!(visible(&state, FOLLOW_UP), 1, "the delivered message must still be shown");
    assert!(state.unsent.is_empty(), "it reached the model; it is not a draft to resend");
}

#[test]
fn a_late_snapshot_rescues_a_message_the_turn_end_had_already_stranded() {
    let mut state = mid_turn();
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });
    assert_eq!(state.unsent.len(), 1);

    let mut late = delivery_snapshot();
    late.turn = None;
    late.lifecycle = EngineLifecycle::Ready;
    state.apply(UiEvent::Snapshot(Box::new(late)));

    assert_eq!(visible(&state, FOLLOW_UP), 1);
    assert!(state.unsent.is_empty(), "a delivered message is never offered for resend");
    assert!(state.queued.is_empty());
}

#[test]
fn a_message_the_engine_says_was_not_delivered_stays_recoverable() {
    let mut state = mid_turn();
    let steering = SteeringQueueState {
        outcomes: vec![SteeringOutcomeDto {
            message_id: "queued-1".into(),
            outcome: SteeringOutcomeKind::CancelledTurnEnded,
            at: "2026-01-01T00:00:01Z".into(),
            turn_id: Some("turn".into()),
        }],
        ..Default::default()
    };
    state.apply(UiEvent::Snapshot(Box::new(snapshot(steering, Vec::new()))));

    assert_eq!(visible(&state, FOLLOW_UP), 0, "it never reached the model; it is not in the chat");
    assert_eq!(state.unsent.len(), 1, "and it is recoverable");
}

// ---------------------------------------------------------------------------
// The conversation the client rebuilt as evidence
//
// Both delivery reports can be lost at once: a gap in the event stream takes
// the notification, and the state read that would have carried the outcome is
// refused. What the client does manage to read is the conversation — which by
// then contains the delivered message as an ordinary committed prompt, with
// no queue id on it, because only the *live* projection carries one. The
// outcome ring is retained, so the next snapshot still reports the delivery,
// and acting on that alone appends a second copy of a message already on
// screen. Identity cannot settle it; what the read itself proves can.
// ---------------------------------------------------------------------------

/// The client holding a receipt for a message it never put on screen, with
/// the conversation rebuilt from the engine in front of it.
fn stranded_after_rebuild(coverage: Option<HistoryCoverage>) -> UiState {
    let mut state = mid_turn();
    // The turn ends with no delivery news at all, so the follow-up is kept
    // recoverable rather than silently dropped.
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });
    assert_eq!(state.unsent.len(), 1);
    state.apply(UiEvent::Rehydrated {
        blocks: committed_conversation(),
        notices: Vec::new(),
        coverage,
    });
    state
}

#[test]
fn a_retained_outcome_adds_nothing_when_the_conversation_read_already_covers_it() {
    let mut state = stranded_after_rebuild(Some(HistoryCoverage::of_read(&read(4))));

    let steering =
        SteeringQueueState { outcomes: vec![delivered_in("queued-1", "turn")], ..Default::default() };
    state.apply(UiEvent::Snapshot(Box::new(settled_snapshot(steering, 4))));

    assert_eq!(
        visible(&state, FOLLOW_UP),
        1,
        "the read covered the committed prefix; a second copy was appended anyway"
    );
    assert!(state.unsent.is_empty(), "a delivered message is never offered for resend");
    assert!(state.queued.is_empty());
    assert!(
        !notices(&state).iter().any(|n| n.contains("did reach the model")),
        "nothing was placed late, so nothing may claim it was: {:?}",
        notices(&state)
    );
}

#[test]
fn a_newer_cursor_alone_does_not_reopen_a_covered_delivery() {
    // Metadata keeps flowing after a read — a request appearing, a phase
    // change — so the snapshot's cursor is past the read's while the
    // conversation itself has not moved at all. Comparing cursors would call
    // that "new content" and append the message a second time.
    let mut state = stranded_after_rebuild(Some(HistoryCoverage::of_read(&read(4))));

    let steering =
        SteeringQueueState { outcomes: vec![delivered_in("queued-1", "turn")], ..Default::default() };
    let mut later = settled_snapshot(steering, 4);
    later.cursor = 97;

    state.apply(UiEvent::Snapshot(Box::new(later)));

    assert_eq!(visible(&state, FOLLOW_UP), 1, "a cursor that moved is not history that grew");
    assert!(state.unsent.is_empty());
}

#[test]
fn a_committed_prefix_that_has_grown_since_the_read_is_not_claimed_as_covered() {
    // The entries the read never saw are exactly the ones in question, so the
    // message is shown rather than suppressed on a claim that does not hold.
    let mut state = stranded_after_rebuild(Some(HistoryCoverage::of_read(&read(4))));

    let steering =
        SteeringQueueState { outcomes: vec![delivered_in("queued-1", "turn")], ..Default::default() };
    state.apply(UiEvent::Snapshot(Box::new(settled_snapshot(steering, 9))));

    assert_eq!(visible(&state, FOLLOW_UP), 2, "a message that might not be on screen must be shown");
    assert!(state.unsent.is_empty(), "it reached the model; it is not a draft to resend");
}

#[test]
fn one_identical_message_already_committed_and_one_just_delivered_are_two_messages() {
    // Two follow-ups with the same words: the first committed in the turn
    // that has ended, the second delivered into the turn running now. A
    // client that deduplicated by text would swallow the live one; identity
    // is the queue id, and coverage is about *which* committed prefix was
    // read, never about what the words are.
    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "start".into() });
    state.apply(UiEvent::Engine(Event::AssistantText { delta: "answer".into() }));
    state.apply(UiEvent::Queued { text: FOLLOW_UP.into(), id: Some("queued-1".into()) });
    state.apply(UiEvent::Queued { text: FOLLOW_UP.into(), id: Some("queued-2".into()) });
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });
    state.apply(UiEvent::Rehydrated {
        blocks: committed_conversation(),
        notices: Vec::new(),
        coverage: Some(HistoryCoverage::of_read(&read(4))),
    });

    let steering = SteeringQueueState {
        outcomes: vec![delivered_in("queued-1", "turn"), delivered_in("queued-2", "turn-2")],
        ..Default::default()
    };
    let mut running = snapshot(steering, Vec::new());
    running.history_length = 4;
    if let Some(turn) = running.turn.as_mut() {
        turn.turn_id = "turn-2".into();
    }
    state.apply(UiEvent::Snapshot(Box::new(running)));

    assert_eq!(
        visible(&state, FOLLOW_UP),
        2,
        "the committed one and the live one are two messages: {:?}",
        state.transcript.blocks()
    );
    assert!(state.unsent.is_empty());
    assert!(state.queued.is_empty());
}

#[test]
fn a_delivery_into_the_running_turn_is_never_covered_by_an_older_committed_read() {
    // The committed length has not changed and the read covers all of it —
    // and none of that says anything about a turn that is still running,
    // whose content is not committed yet. Suppressing here would lose a
    // message the model has already been given.
    let mut state = mid_turn();
    state.apply(UiEvent::Rehydrated {
        blocks: vec![
            Block::User {
                text: "start".into(),
                timestamp: "09:00".into(),
                pending: false,
                queue_id: None,
            },
            Block::Assistant { text: "answer".into(), complete: true },
        ],
        notices: Vec::new(),
        coverage: Some(HistoryCoverage::of_read(&read(2))),
    });

    let steering =
        SteeringQueueState { outcomes: vec![delivered_in("queued-1", "turn")], ..Default::default() };
    let mut running = snapshot(steering, Vec::new());
    running.history_length = 2;
    state.apply(UiEvent::Snapshot(Box::new(running)));
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    assert_eq!(visible(&state, FOLLOW_UP), 1, "a live delivery was suppressed as though committed");
    assert!(state.unsent.is_empty());
}

#[test]
fn an_unsequenced_delivery_is_never_claimed_as_covered() {
    // An engine that does not say which turn a delivery belonged to cannot be
    // placed against a turn boundary. Single-flight makes "it must be the one
    // that just ended" tempting, and a client that guesses it suppresses a
    // message nothing will ever show again.
    let mut state = stranded_after_rebuild(Some(HistoryCoverage::of_read(&read(4))));

    let steering = SteeringQueueState {
        outcomes: vec![SteeringOutcomeDto { turn_id: None, ..delivered("queued-1") }],
        ..Default::default()
    };
    state.apply(UiEvent::Snapshot(Box::new(settled_snapshot(steering, 4))));

    assert_eq!(visible(&state, FOLLOW_UP), 2, "an unprovable claim must not suppress a message");
    assert!(state.unsent.is_empty(), "it reached the model; it is not a draft to resend");
}

#[test]
fn another_process_or_epochs_conversation_never_settles_this_ones_receipt() {
    // A read is evidence about the conversation it came from. Borrowing it
    // across a replaced engine, a different session or a compaction would
    // clear a receipt on the strength of somebody else's history.
    let mut stale = read(4);
    stale.engine_instance_id = "another-engine".into();
    let mut state = stranded_after_rebuild(Some(HistoryCoverage::of_read(&stale)));
    let steering =
        SteeringQueueState { outcomes: vec![delivered_in("queued-1", "turn")], ..Default::default() };
    state.apply(UiEvent::Snapshot(Box::new(settled_snapshot(steering.clone(), 4))));
    assert_eq!(visible(&state, FOLLOW_UP), 2, "another process's read cleared this receipt");

    let mut stale = read(4);
    stale.history_epoch = Some(7);
    let mut state = stranded_after_rebuild(Some(HistoryCoverage::of_read(&stale)));
    state.apply(UiEvent::Snapshot(Box::new(settled_snapshot(steering.clone(), 4))));
    assert_eq!(visible(&state, FOLLOW_UP), 2, "a compacted conversation is not this one");

    let mut stale = read(4);
    stale.session_id = "another-session".into();
    let mut state = stranded_after_rebuild(Some(HistoryCoverage::of_read(&stale)));
    state.apply(UiEvent::Snapshot(Box::new(settled_snapshot(steering.clone(), 4))));
    assert_eq!(visible(&state, FOLLOW_UP), 2, "another conversation is not this one");
}

#[test]
fn a_conversation_read_that_failed_proves_nothing() {
    // The rebuild never happened, so there is no coverage to claim and the
    // message is shown rather than quietly dropped.
    let mut state = mid_turn();
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    let steering =
        SteeringQueueState { outcomes: vec![delivered_in("queued-1", "turn")], ..Default::default() };
    state.apply(UiEvent::Snapshot(Box::new(settled_snapshot(steering, 4))));

    assert_eq!(visible(&state, FOLLOW_UP), 1, "an unread conversation is not a covered one");
    assert!(state.unsent.is_empty());
}

#[test]
fn a_rebuild_of_unknown_provenance_withdraws_the_previous_claim() {
    // A rebuild that cannot vouch for itself replaces the last claim with
    // nothing, rather than leaving it to describe a conversation it no longer
    // matches.
    let mut state = stranded_after_rebuild(Some(HistoryCoverage::of_read(&read(4))));
    state.apply(UiEvent::Rehydrated {
        blocks: committed_conversation(),
        notices: Vec::new(),
        coverage: None,
    });

    let steering =
        SteeringQueueState { outcomes: vec![delivered_in("queued-1", "turn")], ..Default::default() };
    state.apply(UiEvent::Snapshot(Box::new(settled_snapshot(steering, 4))));

    assert_eq!(visible(&state, FOLLOW_UP), 2, "a claim outlived the read that made it");
}

// ---------------------------------------------------------------------------
// Saying where a late message actually belongs
//
// A delivery confirmed after its turn ended can only be appended at the
// bottom of the transcript, which is not where it was said. Position is a
// claim about chronology, so the claim has to be corrected in words rather
// than left to the layout — and the message keeps the time it was sent, not
// the time the confirmation happened to arrive.
// ---------------------------------------------------------------------------

/// A clock that moves, so "when it was sent" and "now" are distinguishable.
fn ticking() -> String {
    use std::sync::atomic::{AtomicUsize, Ordering};
    static TICK: AtomicUsize = AtomicUsize::new(0);
    format!("t{:03}", TICK.fetch_add(1, Ordering::SeqCst))
}

#[test]
fn a_previous_turns_late_confirmation_is_placed_honestly_rather_than_silently() {
    let mut state = UiState::with_clock(ticking);
    state.apply(UiEvent::Submitted { text: "start".into() });
    state.apply(UiEvent::Engine(Event::AssistantText { delta: "answer".into() }));
    state.apply(UiEvent::Queued { text: FOLLOW_UP.into(), id: Some("queued-1".into()) });
    let sent_at = state.queued[0].queued_at.clone();
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });
    assert_eq!(state.unsent.len(), 1, "with no news, an unsent message is kept recoverable");

    // A whole turn later, the engine's retained outcome finally arrives.
    state.apply(UiEvent::Submitted { text: "second prompt".into() });
    let steering =
        SteeringQueueState { outcomes: vec![delivered_in("queued-1", "turn")], ..Default::default() };
    let mut running = snapshot(steering, Vec::new());
    if let Some(turn) = running.turn.as_mut() {
        turn.turn_id = "turn-2".into();
    }
    state.apply(UiEvent::Snapshot(Box::new(running)));

    assert_eq!(visible(&state, FOLLOW_UP), 1, "the delivered message must be shown");
    assert!(state.unsent.is_empty(), "it reached the model; it is not a draft to resend");

    let (text, timestamp) = state
        .transcript
        .blocks()
        .iter()
        .rev()
        .find_map(|block| match block {
            Block::User { text, timestamp, .. } => Some((text.clone(), timestamp.clone())),
            _ => None,
        })
        .expect("a user block");
    assert_eq!(text, FOLLOW_UP, "it lands at the tail, after a later prompt");
    assert_eq!(
        timestamp, sent_at,
        "a message placed late must keep the time it was sent, not be restamped as new"
    );

    let said = notices(&state);
    let corrected = said
        .iter()
        .position(|n| {
            n.contains("did reach the model after all")
                && n.contains("rather than in the place it was said")
                && n.contains("not sent again")
        })
        .unwrap_or_else(|| {
            panic!("the position claims a chronology that is false, and nothing corrects it: {said:?}")
        });
    let claimed_unsent = said
        .iter()
        .position(|n| n.contains("was not sent"))
        .expect("the turn end said it had not been sent");
    assert!(
        claimed_unsent < corrected,
        "the correction must follow the claim it withdraws, not precede it: {said:?}"
    );
}

#[test]
fn a_delivery_into_the_turn_that_is_running_claims_nothing_about_being_late() {
    // The ordinary case must stay quiet: this message really is at the end of
    // the conversation, so there is nothing to correct.
    let mut state = mid_turn();
    state.apply(UiEvent::Snapshot(Box::new(delivery_snapshot())));
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    assert_eq!(visible(&state, FOLLOW_UP), 1);
    let said = notices(&state);
    assert!(
        !said.iter().any(|n| n.contains("rather than in the place it was said")),
        "a message in its right place must not be announced as displaced: {said:?}"
    );
}

// ---------------------------------------------------------------------------
// The engine's view of a running turn is byte-budgeted
// ---------------------------------------------------------------------------

#[test]
fn a_delivered_message_the_live_view_had_no_room_for_survives_a_rebuild() {
    // `coda-serve`'s live projection charges every entry against a byte cap,
    // and a steering message that arrives with nothing left is dropped from
    // it altogether — `push_user_block` returns without pushing when the
    // remaining room is zero. The read announces `liveTruncated`, so a
    // conversation rebuilt from it can legitimately not contain a message
    // this client knows was delivered. Clearing the local copy on that
    // rebuild destroys the only full copy of what the operator typed.
    let long = format!("{FOLLOW_UP} {}", "and more ".repeat(32));
    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "start".into() });
    state.apply(UiEvent::Engine(Event::AssistantText { delta: "answer".into() }));
    state.apply(UiEvent::Queued { text: long.clone(), id: Some("queued-1".into()) });
    // Reported delivered while the reply is still streaming, so the message
    // waits behind the open block rather than splitting it.
    state.apply(UiEvent::Snapshot(Box::new(delivery_snapshot())));
    assert_eq!(visible(&state, &long), 0, "it is parked behind the open reply");

    // The conversation is re-read, and the engine's live view had no room.
    let mut budgeted = read(2);
    budgeted.live_truncated = Some(true);
    state.apply(UiEvent::Rehydrated {
        blocks: vec![
            Block::User {
                text: "start".into(),
                timestamp: "09:00".into(),
                pending: false,
                queue_id: None,
            },
            Block::Assistant { text: "answer".into(), complete: false },
        ],
        notices: vec!["The running turn's view is shortened.".into()],
        coverage: Some(HistoryCoverage::of_read(&budgeted)),
    });

    assert!(state.unsent.is_empty(), "it was delivered; it is not an unsent draft");
    assert_eq!(state.delivered_local.len(), 1, "the operator's text was thrown away");
    assert_eq!(
        state.delivered_local[0].text, long,
        "the whole message must be kept, not the engine's shortened preview"
    );
    let said = notices(&state);
    assert!(
        said.iter().any(|n| n.contains("ran out of room")
            && n.contains("nothing was sent again")
            && n.contains("recoverable with Up")),
        "the operator must be told where their text went: {said:?}"
    );
    assert_eq!(
        state.recall_unsent().as_deref(),
        Some(long.as_str()),
        "and it must actually be recoverable"
    );
}

#[test]
fn a_rebuild_that_shows_the_delivered_message_keeps_no_second_copy_of_it() {
    // The mirror image: nothing was truncated, the rebuild carries the queue
    // id, so the parked copy is redundant and is dropped rather than kept as
    // a phantom recoverable draft.
    let mut state = mid_turn();
    state.apply(UiEvent::Snapshot(Box::new(delivery_snapshot())));
    state.apply(UiEvent::Rehydrated {
        blocks: vec![
            Block::User {
                text: "start".into(),
                timestamp: "09:00".into(),
                pending: false,
                queue_id: None,
            },
            Block::Assistant { text: "answer".into(), complete: false },
            Block::User {
                text: FOLLOW_UP.into(),
                timestamp: "09:01".into(),
                pending: false,
                queue_id: Some("queued-1".into()),
            },
        ],
        notices: Vec::new(),
        coverage: Some(HistoryCoverage::of_read(&read(2))),
    });
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    assert_eq!(visible(&state, FOLLOW_UP), 1);
    assert!(state.delivered_local.is_empty(), "a message on screen needs no recovery copy");
    assert!(state.unsent.is_empty());
}

#[test]
fn a_tail_page_still_covers_the_committed_prefix_it_announced_as_partly_unloaded() {
    // The window this client asks for is the newest page, and when earlier
    // entries are left out it says so ("showing the most recent N of M").
    // Those entries are not missing — they are announced. A delivered message
    // that belongs among them is part of the conversation either way, and
    // appending it at the *bottom* would place it after everything that came
    // later, which is the one thing the announcement does not license.
    let mut state = stranded_after_rebuild(Some(HistoryCoverage::of_read(&{
        let mut paged = read(250);
        paged.entries = Vec::new();
        paged.next_index = 250;
        paged.total_known = 250;
        paged.truncated = false;
        paged
    })));

    let steering =
        SteeringQueueState { outcomes: vec![delivered_in("queued-1", "turn")], ..Default::default() };
    state.apply(UiEvent::Snapshot(Box::new(settled_snapshot(steering, 250))));

    assert_eq!(
        visible(&state, FOLLOW_UP),
        1,
        "an announced tail page is not permission to re-append an older message"
    );
    assert!(state.unsent.is_empty(), "it reached the model; it is not a draft to resend");
}

// ---------------------------------------------------------------------------
// A whole queue, not a single message
//
// An operator who is typing while the model works does not queue one
// follow-up; they queue a dozen, some of them the same words twice. Every one
// of them reaches the model, so every one of them belongs in the conversation
// exactly once — none swallowed, none doubled, and none quietly moved to the
// recovery list as though it had never been sent.
// ---------------------------------------------------------------------------

const BATCH: usize = 16;
const REPEATS: usize = 4;

fn batch_ids() -> Vec<String> {
    (0..BATCH)
        .map(|i| format!("m{i:02}"))
        .chain((0..REPEATS).map(|i| format!("same-{i}")))
        .collect()
}

fn batch_text(id: &str) -> String {
    if id.starts_with("same-") {
        FOLLOW_UP.to_string()
    } else {
        format!("follow-up {id}")
    }
}

fn pending_dto(id: &str) -> SteeringPendingDto {
    SteeringPendingDto {
        message_id: id.into(),
        enqueued_at: "2026-01-01T00:00:00Z".into(),
        text: batch_text(id),
        text_length: batch_text(id).len() as i64,
        text_truncated: false,
    }
}

/// The engine's queue state part-way through a turn: `still_pending` is what
/// it is still holding, `done` everything it has already delivered.
fn queue_state(still_pending: &[String], done: &[String]) -> SteeringQueueState {
    SteeringQueueState {
        pending_count: still_pending.len() as i64,
        pending: still_pending.iter().map(|id| pending_dto(id)).collect(),
        outcomes: done.iter().map(|id| delivered_in(id, "turn")).collect(),
        outcomes_truncated: false,
        retained_outcomes: 64,
    }
}

fn user_texts(state: &UiState) -> Vec<String> {
    state
        .transcript
        .blocks()
        .iter()
        .filter_map(|block| match block {
            Block::User { text, .. } => Some(text.clone()),
            _ => None,
        })
        .collect()
}

#[test]
fn a_whole_queue_of_follow_ups_survives_being_delivered_in_waves() {
    let ids = batch_ids();
    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "start".into() });
    state.apply(UiEvent::Engine(Event::AssistantText { delta: "answer".into() }));
    for id in &ids {
        state.apply(UiEvent::Queued { text: batch_text(id), id: Some(id.clone()) });
    }
    assert_eq!(state.queued.len(), ids.len(), "the engine acknowledged every one of them");

    // Three waves, each with the two delivery reports racing a different way
    // round — which is exactly what happens on a real connection, where the
    // engine's state moves before its notification does but the notification
    // can still arrive first.
    let waves: [(usize, usize); 3] = [(0, 5), (5, 10), (10, ids.len())];
    for (wave, (from, to)) in waves.into_iter().enumerate() {
        let delivered_now: Vec<String> = ids[from..to].to_vec();
        let done: Vec<String> = ids[..to].to_vec();
        let still_pending: Vec<String> = ids[to..].to_vec();
        let event = UiEvent::Engine(Event::SteeringDelivered {
            message_ids: delivered_now.clone(),
        });
        let snapshot =
            UiEvent::Snapshot(Box::new(snapshot(queue_state(&still_pending, &done), Vec::new())));
        match wave {
            // The notification wins the race.
            0 => {
                state.apply(event);
                state.apply(snapshot);
            }
            // The state read wins it.
            1 => {
                state.apply(snapshot);
                state.apply(event);
            }
            // The notification is lost outright: only the retained outcome
            // in the snapshot ever says these reached the model.
            _ => state.apply(snapshot),
        }
        // The reply keeps streaming between waves, so every delivery lands
        // behind an open block and has to wait for a boundary.
        state.apply(UiEvent::Engine(Event::AssistantText { delta: " more".into() }));
    }

    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });

    let shown = user_texts(&state);
    for id in &ids {
        let text = batch_text(id);
        let count = shown.iter().filter(|t| **t == text).count();
        let expected = if id.starts_with("same-") { REPEATS } else { 1 };
        assert_eq!(
            count, expected,
            "{id} ({text:?}) appears {count} times, expected {expected}: {shown:?}"
        );
    }
    assert_eq!(
        shown.len(),
        1 + ids.len(),
        "the prompt plus every follow-up, once each: {shown:?}"
    );
    assert!(state.queued.is_empty(), "the engine delivered all of them");
    assert!(
        state.unsent.is_empty(),
        "delivered messages must never be offered for resend: {:?}",
        state.unsent
    );
    assert!(
        assistant_blocks(&state).iter().all(|text| !text.is_empty()),
        "a delivery must never split a reply into empty halves: {:?}",
        assistant_blocks(&state)
    );
}

#[test]
fn a_committed_queue_is_not_re_appended_when_the_conversation_is_rebuilt() {
    // The same batch, now through the part that actually lost them: the turn
    // ends, the conversation is re-read (committed entries carry no queue
    // ids), and the engine keeps republishing the retained `delivered`
    // outcomes for all twenty of them.
    let ids = batch_ids();
    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "start".into() });
    for id in &ids {
        state.apply(UiEvent::Queued { text: batch_text(id), id: Some(id.clone()) });
    }
    // The turn ends before any delivery news at all: every one of them is
    // stranded in the recovery list.
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });
    assert_eq!(state.unsent.len(), ids.len());

    // The conversation comes back from the engine with all of them committed.
    let mut blocks = vec![Block::User {
        text: "start".into(),
        timestamp: "09:00".into(),
        pending: false,
        queue_id: None,
    }];
    for id in &ids {
        blocks.push(Block::User {
            text: batch_text(id),
            timestamp: "09:01".into(),
            pending: false,
            queue_id: None,
        });
    }
    blocks.push(Block::Assistant { text: "answer".into(), complete: true });
    let committed = 1 + ids.len() as i64 + 1;
    state.apply(UiEvent::Rehydrated {
        blocks,
        notices: Vec::new(),
        coverage: Some(HistoryCoverage::of_read(&read(committed))),
    });

    // And the snapshot still reports every delivery, because the outcome ring
    // is retained.
    let steering = queue_state(&[], &ids);
    state.apply(UiEvent::Snapshot(Box::new(settled_snapshot(steering, committed))));

    let shown = user_texts(&state);
    for id in &ids {
        let text = batch_text(id);
        let count = shown.iter().filter(|t| **t == text).count();
        let expected = if id.starts_with("same-") { REPEATS } else { 1 };
        assert_eq!(count, expected, "{id} appears {count} times, expected {expected}: {shown:?}");
    }
    assert_eq!(shown.len(), 1 + ids.len(), "nothing was appended a second time: {shown:?}");
    assert!(state.unsent.is_empty(), "none of them may be offered for resend");
    assert!(state.queued.is_empty());
}

#[test]
fn a_queue_the_engine_stopped_explaining_is_kept_recoverable_rather_than_claimed_either_way() {
    // The outcome ring is bounded (`retainedOutcomes`), so a long queue can
    // outrun it: the engine stops listing the message as pending and no
    // longer says what became of it. That is genuine doubt, and the only
    // honest answer is to keep the text — never to claim it was delivered
    // (which would put words in the conversation the model may never have
    // seen) and never to send it again.
    let ids = batch_ids();
    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "start".into() });
    for id in &ids {
        state.apply(UiEvent::Queued { text: batch_text(id), id: Some(id.clone()) });
    }

    // The engine holds none of them any more and reports outcomes for only
    // the last five; the rest fell out of the ring.
    let explained: Vec<String> = ids[ids.len() - 5..].to_vec();
    let mut steering = queue_state(&[], &explained);
    steering.outcomes_truncated = true;
    state.apply(UiEvent::Snapshot(Box::new(snapshot(steering, Vec::new()))));

    let shown = user_texts(&state);
    for id in &explained {
        assert!(
            shown.contains(&batch_text(id)),
            "an explained delivery must be in the conversation: {id}"
        );
    }
    assert_eq!(
        state.unsent.len(),
        ids.len() - explained.len(),
        "every unexplained message must be kept recoverable: {:?}",
        state.unsent
    );
    for message in &state.unsent {
        let id = message.id.as_deref().expect("an acknowledged message keeps its id");
        assert_eq!(message.text, batch_text(id), "the operator's own text must be kept intact");
        assert!(!shown.contains(&message.text), "an unexplained message must not be claimed as said");
    }
    let said = notices(&state);
    assert!(
        said.iter().any(|n| n.contains("did not report what happened")
            && n.contains("nothing was resent")),
        "the doubt must be stated without claiming either way: {said:?}"
    );
}

// ---------------------------------------------------------------------------
// The boundary a parked delivery is actually waiting for
//
// A delivery reported while a block is still open waits behind it, so it
// cannot split a reply in two. The block it is waiting for can end in two
// quite different ways: the transcript closes it (a new block starting, the
// turn ending), or the engine closes it *in place* — `assistantTextComplete`
// and `thinkingComplete` mark the tail finished exactly where it stands.
// Only the first used to release the queue, so an operator who queued a
// dozen follow-ups watched the pending list empty with nothing appearing in
// the conversation until some later, unrelated boundary happened along.
// ---------------------------------------------------------------------------

#[test]
fn a_delivery_lands_as_soon_as_the_reply_it_waited_for_is_finished() {
    let mut state = mid_turn();
    state.apply(UiEvent::Snapshot(Box::new(delivery_snapshot())));
    assert_eq!(visible(&state, FOLLOW_UP), 0, "it waits rather than splitting the reply");

    state.apply(UiEvent::Engine(Event::AssistantTextComplete));

    assert_eq!(
        visible(&state, FOLLOW_UP),
        1,
        "the reply is finished, so the message it interrupted must be on screen: {:?}",
        state.transcript.blocks()
    );
    assert_eq!(assistant_blocks(&state), vec!["answer".to_string()], "and the reply is in one piece");
}

#[test]
fn a_delivery_parked_behind_a_reasoning_burst_lands_when_the_burst_ends() {
    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "start".into() });
    state.apply(UiEvent::Engine(Event::Thinking { delta: "weighing it up".into() }));
    state.apply(UiEvent::Queued { text: FOLLOW_UP.into(), id: Some("queued-1".into()) });
    state.apply(UiEvent::Snapshot(Box::new(delivery_snapshot())));
    assert_eq!(visible(&state, FOLLOW_UP), 0, "it waits rather than cutting the burst in half");

    state.apply(UiEvent::Engine(Event::ThinkingComplete {
        elapsed_ms: 1_200,
        thinking_tokens: Some(40),
    }));

    assert_eq!(visible(&state, FOLLOW_UP), 1, "the burst is over; the message must be on screen");
}

#[test]
fn a_whole_queue_delivered_mid_reply_appears_the_moment_the_reply_ends() {
    // The batch version of the same boundary, which is where it was actually
    // noticed: many messages, one reply, and nothing on screen until long
    // after the engine had delivered every one of them.
    let ids = batch_ids();
    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "start".into() });
    state.apply(UiEvent::Engine(Event::AssistantText { delta: "answer".into() }));
    for id in &ids {
        state.apply(UiEvent::Queued { text: batch_text(id), id: Some(id.clone()) });
    }
    state.apply(UiEvent::Engine(Event::SteeringDelivered { message_ids: ids.clone() }));
    assert!(state.queued.is_empty(), "the engine delivered all of them");

    state.apply(UiEvent::Engine(Event::AssistantTextComplete));

    let shown = user_texts(&state);
    for id in &ids {
        let text = batch_text(id);
        let count = shown.iter().filter(|t| **t == text).count();
        let expected = if id.starts_with("same-") { REPEATS } else { 1 };
        assert_eq!(count, expected, "{id} appears {count} times, expected {expected}: {shown:?}");
    }
    assert_eq!(shown.len(), 1 + ids.len(), "every queued message, once each: {shown:?}");
    assert_eq!(
        assistant_blocks(&state),
        vec!["answer".to_string()],
        "and the reply they interrupted stayed in one piece"
    );
    assert!(state.unsent.is_empty(), "none of them may be offered for resend");
}
