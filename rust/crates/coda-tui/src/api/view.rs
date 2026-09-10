//! The event fence: what this client has already seen, and what it may apply.
//!
//! The engine numbers every event it produces with an envelope `seq` that is
//! gapless within one `engineInstanceId`, and a `session/getState` snapshot
//! declares the `cursor` it is exact at: every event with `seq <= cursor` is
//! already reflected in it, and no event with `seq > cursor` is
//! (`docs/superpowers/plans/2026-09-08-serve-api-implementation.md` §2.5).
//!
//! [`ServeView`] is the client half of that contract, and nothing else. It
//! owns no UI state: it decides, for one inbound frame, whether the frame is
//! new, already reflected in the snapshot we hold, held pending an in-flight
//! resync, or evidence that something was missed. The reducer stays the single
//! place UI state changes; this stays the single place "have we seen it?" is
//! answered.
//!
//! Three properties this exists to make impossible:
//!
//! - **Double-applying.** Buffering events across a `session/getState` and
//!   then applying all of them would replay whatever the snapshot already
//!   contains. The cursor is the exact discriminator, so the buffer is
//!   filtered by it rather than by a heuristic like "drop the first few".
//! - **Silent divergence.** A missing `seq` means an event genuinely never
//!   arrived; the honest response is to re-snapshot, not to carry on with a
//!   conversation that quietly lost a tool result.
//! - **Cross-process confusion.** A restarted or replaced engine mints a new
//!   `engineInstanceId`. Its cursors start over, so applying them against the
//!   previous process's fence would look like a catastrophic gap (or, worse,
//!   like duplicates). A new instance resets the fence outright.
//!
//! No polling anywhere: the streaming path is events. A snapshot is fetched on
//! connect, on an actual gap, and after an engine-owned reset — never per
//! token.

use coda_proto::messages::{ClientCapabilities, InitializeResult};
use coda_proto::state::{
    ActiveConfig, ActivityPhase, EngineLifecycle, PendingRequestDto, PendingRequestKind,
    StateSnapshot, SteeringQueueState,
};
use serde_json::Value;

/// The capabilities this client negotiates.
///
/// `stateEvents` enables state notifications. `richHistory` is a reserved
/// reader hint, not a switch controlling `session/getHistory`; the engine's
/// `history.rich` capability reports that API's availability.
pub fn client_capabilities() -> ClientCapabilities {
    ClientCapabilities {
        state_events: Some(true),
        rich_history: Some(true),
        max_event_payload_bytes: None,
    }
}

/// One inbound notification, with its envelope metadata read off.
#[derive(Debug, Clone)]
pub struct Frame {
    pub method: String,
    pub params: Value,
    /// `-1` when the engine did not stamp one (a legacy build).
    pub seq: i64,
}

impl Frame {
    pub fn new(method: impl Into<String>, params: Option<Value>) -> Self {
        let params = params.unwrap_or(Value::Null);
        let seq = params.get("seq").and_then(Value::as_i64).unwrap_or(-1);
        Self { method: method.into(), params, seq }
    }

    fn instance(&self) -> Option<&str> {
        self.params.get("engineInstanceId").and_then(Value::as_str)
    }
}

/// How many inbound frames may be held while a `session/getState` is in
/// flight.
///
/// Matches the engine's own event ring (`limits.ringEnvelopes`): once more
/// than a ring's worth has piled up behind a resync that is not completing,
/// the engine can no longer serve the missing range anyway, so holding more
/// buys nothing and costs memory without end. Overflow is reported rather
/// than silently trimmed — the conversation has to be re-read either way, and
/// a client that quietly dropped events would show a short transcript as if
/// it were whole.
pub const MAX_BUFFERED_FRAMES: usize = 2048;

/// What the fence says about one frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Reception {
    /// New, in order: apply it.
    Apply,
    /// Already reflected in the snapshot we hold. Applying it would double
    /// the content it carries.
    Duplicate,
    /// Held until the in-flight `session/getState` returns.
    Buffered,
    /// Events were genuinely missed. The caller must re-snapshot; the frame
    /// is buffered so nothing after the gap is lost either.
    Gap,
    /// This client's own hold buffer is full, so the frame was discarded.
    ///
    /// Distinct from [`Reception::Gap`] on purpose: the events did arrive,
    /// and saying they never did would misdescribe where the fault is. The
    /// recovery is the same — re-read state and conversation — but the
    /// message must not blame the engine for the client's bound.
    Overflowed,
    /// A different engine process. Everything local is stale.
    InstanceChanged,
    /// No envelope to fence on (a legacy engine, or `initialize` has not
    /// reported an instance yet). Applied unfenced, exactly as before this
    /// contract existed.
    ///
    /// The known limit of that compatibility: an unstamped frame has no
    /// position, so it cannot be held across a resync, cannot be recognised
    /// as already covered by a `session/getHistory` read, and cannot be seen
    /// to be missing. Against such an engine a rebuild that overlaps live
    /// streaming can still show a delta twice. There is no safe fix on this
    /// side — inventing a position would be a guess about ordering — and the
    /// contract's own answer is the `seq` these engines do not send.
    Unfenced,
}

impl Reception {
    /// Whether the reducer should see this frame now.
    pub fn applies_now(self) -> bool {
        matches!(self, Reception::Apply | Reception::Unfenced)
    }
}

/// What a `session/getState` left for the caller to do.
#[derive(Debug, Default)]
pub struct Replay {
    /// Buffered frames that are new relative to the snapshot, in seq order.
    pub frames: Vec<Frame>,
    /// `(from, to)` bounds of a hole *inside* the buffered run: no event
    /// numbered between them ever arrived, so the frames from `to` onwards
    /// are still held and the conversation must be re-read rather than
    /// spliced. `None` when the buffer was contiguous.
    pub gap: Option<(i64, i64)>,
}

/// The client-side event fence.
#[derive(Debug)]
pub struct ServeView {
    instance: Option<String>,
    /// Highest seq already reflected in what the UI shows. `-1` = unknown.
    cursor: i64,
    /// Highest seq whose *conversation content* is already on screen because
    /// a `session/getHistory` read that covered it was applied.
    ///
    /// Deliberately not the same number as `cursor`. A history read is exact
    /// at its own cursor and the events up to it are already inside what it
    /// returned, so replaying their content would double it. Advancing
    /// `cursor` instead would also swallow the *state* frames in that range —
    /// a config change, a steering update, a request appearing — none of
    /// which a history read carries and all of which must still be applied.
    content_cursor: i64,
    history_epoch: i64,
    history_length: i64,
    /// Set while a `session/getState` is in flight: frames are held rather
    /// than applied, because the snapshot that is about to arrive may already
    /// contain them.
    resyncing: bool,
    buffered: Vec<Frame>,
    /// Set when a gap or a reset means the snapshot we hold is no longer a
    /// valid base. Cleared by [`Self::apply_snapshot`].
    needs_snapshot: bool,
    /// How many times the fence has been rebuilt from scratch. Diagnostics
    /// and tests only — a client that quietly resets in a loop is broken, and
    /// this makes that visible instead of merely slow.
    resets: u64,
    /// How many times the hold buffer overflowed and was released.
    dropped: u64,
    /// The largest history page this engine will serve, as it advertises it.
    /// `0` = not yet known.
    max_history_page: i64,
}

impl Default for ServeView {
    fn default() -> Self {
        Self::new()
    }
}

impl ServeView {
    pub fn new() -> Self {
        Self {
            instance: None,
            cursor: -1,
            content_cursor: -1,
            history_epoch: 0,
            history_length: 0,
            resyncing: false,
            buffered: Vec::new(),
            needs_snapshot: false,
            resets: 0,
            dropped: 0,
            max_history_page: 0,
        }
    }

    pub fn engine_instance_id(&self) -> Option<&str> {
        self.instance.as_deref()
    }
    pub fn cursor(&self) -> i64 {
        self.cursor
    }
    pub fn history_epoch(&self) -> i64 {
        self.history_epoch
    }
    pub fn history_length(&self) -> i64 {
        self.history_length
    }
    pub fn needs_snapshot(&self) -> bool {
        self.needs_snapshot
    }
    pub fn is_resyncing(&self) -> bool {
        self.resyncing
    }
    /// How many frames are held pending an in-flight snapshot.
    ///
    /// Bounded by [`MAX_BUFFERED_FRAMES`]; readable so a test can assert the
    /// bound rather than trusting it.
    pub fn buffered(&self) -> usize {
        self.buffered.len()
    }
    pub fn resets(&self) -> u64 {
        self.resets
    }
    /// How many times this client discarded a full hold buffer. Diagnostics
    /// and tests: a client that overflows repeatedly is failing to resync.
    pub fn dropped(&self) -> u64 {
        self.dropped
    }
    /// Whether the engine stamps envelopes at all — i.e. whether anything
    /// here is load-bearing, or this is a legacy connection.
    pub fn is_fenced(&self) -> bool {
        self.instance.is_some()
    }

    /// The largest page this engine will serve, or `None` until a snapshot
    /// has said. Never guessed: asking for more than the engine allows is
    /// clamped silently, and a client that assumed its own number would
    /// compute a window the engine does not serve.
    pub fn max_history_page(&self) -> Option<i64> {
        (self.max_history_page > 0).then_some(self.max_history_page)
    }

    /// Records that a `session/getHistory` read exact at `cursor` is now on
    /// screen, so the content of the events up to it is not applied again.
    pub fn note_history_read(&mut self, cursor: i64) {
        self.content_cursor = self.content_cursor.max(cursor);
    }

    /// Whether this frame's *content* is already on screen from a history
    /// read. Its metadata is not covered: history carries none.
    pub fn content_is_reflected(&self, seq: i64) -> bool {
        seq >= 0 && seq <= self.content_cursor
    }

    /// Seeds the fence from the handshake.
    ///
    /// `eventCursor` is the authoritative start cursor, valid at the instant
    /// `initialize` returned. Taking it here rather than assuming `0` is what
    /// makes a resumed session's already-burned seqs not look like a gap.
    pub fn on_initialize(&mut self, result: &InitializeResult) {
        let instance = result.engine_instance_id.clone();
        if instance.is_none() {
            // Legacy engine: no fence to keep. Everything applies unfenced.
            *self = Self::new();
            return;
        }
        let changed = self.instance.is_some() && self.instance != instance;
        if changed {
            self.resets += 1;
        }
        self.instance = instance;
        self.cursor = result.event_cursor.unwrap_or(-1);
        self.content_cursor = -1;
        self.history_epoch = 0;
        self.history_length = 0;
        self.buffered.clear();
        self.resyncing = false;
        // A handshake tells us the cursor, never the conversation: the first
        // snapshot/history read is what fills the transcript.
        self.needs_snapshot = true;
    }

    /// Begins a resync: frames arriving from now until [`Self::apply_snapshot`]
    /// are held rather than applied.
    pub fn begin_resync(&mut self) {
        self.resyncing = true;
    }

    /// The in-flight `session/getState` failed.
    ///
    /// The gate is released, because holding it is only justified while a
    /// snapshot is actually coming: a failed read that left it set would
    /// buffer every later frame for the rest of the session, freezing the
    /// screen mid-turn and growing the buffer without end.
    ///
    /// The cursor we already hold is still valid — nothing was applied that
    /// the failed read would have changed — so the held frames that continue
    /// from it are handed back to be applied, exactly as a snapshot would
    /// hand them back. A hole is still a hole: replay stops at it, the rest
    /// stays held, and the caller is told, so nothing is drawn as a complete
    /// conversation that is missing its middle.
    ///
    /// `needs_snapshot` stays set: the state read is still owed. The *rate*
    /// at which it is retried is the caller's business, not the fence's.
    pub fn abort_resync(&mut self) -> Replay {
        self.resyncing = false;
        self.needs_snapshot = true;
        self.drain_buffer()
    }

    /// Folds a snapshot in, returning the buffered frames that are **not**
    /// already reflected in it, in seq order.
    ///
    /// A frame with `seq <= snapshot.cursor` is dropped: the snapshot already
    /// contains whatever it carried. Everything above the cursor is handed
    /// back to be applied in order, so the reconnect protocol
    /// (buffer -> snapshot -> discard -> apply) is exactly what happens.
    ///
    /// The buffer itself can have a hole in it — the frames between two held
    /// frames may never have arrived — and that hole is *not* stitched over.
    /// Replay stops at it, the rest of the buffer is kept for the snapshot
    /// that will close it, and the gap is reported so the caller can rebuild
    /// the conversation instead of showing one with its middle missing.
    pub fn apply_snapshot(&mut self, snapshot: &StateSnapshot) -> Replay {
        if self.instance.as_deref() != Some(snapshot.engine_instance_id.as_str()) {
            if self.instance.is_some() {
                self.resets += 1;
            }
            self.instance = Some(snapshot.engine_instance_id.clone());
            self.buffered.clear();
            // Another process's seqs are its own, so nothing this client has
            // on screen can be said to cover them.
            self.content_cursor = -1;
        }
        self.max_history_page = snapshot.limits.max_history_page;
        self.cursor = snapshot.cursor;
        self.history_epoch = snapshot.history_epoch;
        self.history_length = snapshot.history_length;
        self.resyncing = false;
        self.needs_snapshot = false;

        self.drain_buffer()
    }

    /// Releases the held frames that continue from the cursor, in seq order.
    ///
    /// Shared by [`Self::apply_snapshot`] and [`Self::abort_resync`] so the
    /// two cannot disagree about what "already covered" and "across a hole"
    /// mean. Frames at or below the cursor are dropped as already reflected;
    /// replay stops at the first hole, which re-arms the gate and is reported
    /// to the caller.
    fn drain_buffer(&mut self) -> Replay {
        let mut held = std::mem::take(&mut self.buffered);
        held.sort_by_key(|frame| frame.seq);
        let mut replay = Replay::default();
        let mut iter = held.into_iter();
        for frame in iter.by_ref() {
            if frame.seq >= 0 && frame.seq <= self.cursor {
                continue;
            }
            if frame.seq > self.cursor + 1 && self.cursor >= 0 {
                // A hole between what the cursor covers and this frame. Hold
                // it (and the rest) for the next snapshot rather than
                // applying it across the missing events.
                replay.gap = Some((self.cursor, frame.seq));
                self.needs_snapshot = true;
                self.resyncing = true;
                self.buffered.push(frame);
                break;
            }
            if frame.seq >= 0 {
                self.cursor = frame.seq;
            }
            replay.frames.push(frame);
        }
        self.buffered.extend(iter);
        replay
    }

    /// Classifies one inbound notification, taking custody of it when it must
    /// be held or replayed.
    pub fn receive(&mut self, frame: Frame) -> Reception {
        // A frame from a different process invalidates everything: its
        // cursors are its own, and mixing the two streams would either look
        // like a huge gap or silently duplicate content.
        if let (Some(known), Some(seen)) = (self.instance.as_deref(), frame.instance()) {
            if known != seen {
                self.instance = Some(seen.to_string());
                self.cursor = -1;
                self.content_cursor = -1;
                self.history_epoch = 0;
                self.history_length = 0;
                self.buffered.clear();
                self.needs_snapshot = true;
                self.resyncing = true;
                self.resets += 1;
                self.buffered.push(frame);
                return Reception::InstanceChanged;
            }
        }

        if !self.is_fenced() || frame.seq < 0 {
            // Nothing to fence on. This is the pre-contract behaviour and it
            // is still correct for a legacy engine; it just proves nothing.
            return Reception::Unfenced;
        }

        if self.resyncing {
            return self.hold(frame);
        }

        if frame.seq <= self.cursor {
            return Reception::Duplicate;
        }

        if self.cursor >= 0 && frame.seq > self.cursor + 1 {
            // A real gap. Hold this frame (and everything after it) so the
            // snapshot can discard what it covers and the rest still lands.
            self.needs_snapshot = true;
            self.resyncing = true;
            return match self.hold(frame) {
                Reception::Buffered => Reception::Gap,
                other => other,
            };
        }

        self.cursor = frame.seq;
        Reception::Apply
    }

    /// Takes custody of a frame, within the bound.
    ///
    /// At the bound the buffer is emptied rather than trimmed: the frames it
    /// held are no longer a contiguous run that can be spliced onto anything,
    /// so keeping some of them would only make a partial conversation look
    /// whole. The cursor is deliberately left where it is, so the next
    /// snapshot still knows exactly what this client has applied.
    fn hold(&mut self, frame: Frame) -> Reception {
        if self.buffered.len() >= MAX_BUFFERED_FRAMES {
            self.buffered.clear();
            self.needs_snapshot = true;
            self.dropped += 1;
            return Reception::Overflowed;
        }
        self.buffered.push(frame);
        Reception::Buffered
    }

    /// Records that the conversation itself was replaced (fork / rewind /
    /// compact / resume). Returns `true` when the epoch really moved, which
    /// is the client's signal to throw its transcript away and rehydrate.
    pub fn on_history_reset(&mut self, epoch: i64, history_length: i64) -> bool {
        if epoch == self.history_epoch {
            self.history_length = history_length;
            return false;
        }
        self.history_epoch = epoch;
        self.history_length = history_length;
        self.needs_snapshot = true;
        true
    }
}

// ---------------------------------------------------------------------------
// State frames
// ---------------------------------------------------------------------------

/// A `stateEvents` frame, decoded.
///
/// Only the frames this client acts on are modelled. Anything else stays an
/// ordinary unknown notification: inventing a variant for a method we ignore
/// would claim a behaviour that does not exist.
#[derive(Debug, Clone)]
pub enum StateFrame {
    Activity { turn_id: String, phase: ActivityPhase },
    Lifecycle { lifecycle: EngineLifecycle, initialized: bool },
    TurnEnded { turn_id: String, history_epoch: i64, history_length: i64 },
    ConfigChanged { active: Option<ActiveConfig>, next: Option<ActiveConfig> },
    Steering(SteeringQueueState),
    SessionChanged { reason: String, session_id: String, history_epoch: i64, history_length: i64 },
    /// A decision the engine is waiting on, with the outstanding list as it
    /// stood when the frame was emitted.
    ///
    /// `at` is the frame's own `seq` — the engine time this list describes.
    /// It is what makes the list usable for *retirement* and not only for
    /// discovery: a request announced at a later seq than a list was taken
    /// cannot be described by it, so its absence says nothing. `None` is a
    /// legacy engine that stamps no sequence, and nothing is retired on a
    /// guess.
    RequestPending {
        request: Box<PendingRequestDto>,
        requests: Vec<PendingRequestDto>,
        at: Option<i64>,
    },
    /// A request reached a terminal outcome, with the engine's own verdict.
    ///
    /// `kind` and `outcome` are what the engine published
    /// (`allowed`/`denied`/`approved`/`rejected`/`answered`/`noAnswer.<reason>`).
    /// Both are optional because an engine that predates them says only *that*
    /// the request ended — and "we do not know how" is the truth in that case.
    /// It must never be flattened into a decision this client made up.
    RequestResolved {
        request_id: String,
        kind: Option<PendingRequestKind>,
        outcome: Option<String>,
        requests: Vec<PendingRequestDto>,
    },
    EventsDropped { from_cursor: i64, to_cursor: i64 },
}

/// Decodes a `stateEvents` frame, or `None` if this is not one.
///
/// A malformed payload for a method we do know is `None` too: acting on half
/// a frame would be worse than treating it as noise and letting the next
/// snapshot correct us.
pub fn parse_state_frame(method: &str, params: &Value) -> Option<StateFrame> {
    use coda_proto::events::event_method as m;
    let str_field = |key: &str| params.get(key).and_then(Value::as_str).map(str::to_string);
    let i64_field = |key: &str| params.get(key).and_then(Value::as_i64);

    match method {
        m::ACTIVITY => Some(StateFrame::Activity {
            turn_id: str_field("turnId")?,
            phase: serde_json::from_value(params.get("phase")?.clone()).ok()?,
        }),
        m::LIFECYCLE => Some(StateFrame::Lifecycle {
            lifecycle: serde_json::from_value(params.get("lifecycle")?.clone()).ok()?,
            initialized: params.get("initialized").and_then(Value::as_bool).unwrap_or(false),
        }),
        m::TURN_ENDED => Some(StateFrame::TurnEnded {
            turn_id: str_field("turnId")?,
            history_epoch: i64_field("historyEpoch").unwrap_or(0),
            history_length: i64_field("historyLength").unwrap_or(0),
        }),
        m::CONFIG_CHANGED => Some(StateFrame::ConfigChanged {
            active: params.get("active").and_then(|v| serde_json::from_value(v.clone()).ok()),
            next: params.get("next").and_then(|v| serde_json::from_value(v.clone()).ok()),
        }),
        m::STEERING_QUEUE => {
            Some(StateFrame::Steering(serde_json::from_value(params.clone()).ok()?))
        }
        m::SESSION_CHANGED => Some(StateFrame::SessionChanged {
            reason: str_field("reason").unwrap_or_default(),
            session_id: str_field("sessionId")?,
            history_epoch: i64_field("historyEpoch").unwrap_or(0),
            history_length: i64_field("historyLength").unwrap_or(0),
        }),
        m::REQUEST_PENDING => Some(StateFrame::RequestPending {
            request: Box::new(serde_json::from_value(params.get("request")?.clone()).ok()?),
            requests: params
                .get("requests")
                .and_then(|v| serde_json::from_value(v.clone()).ok())
                .unwrap_or_default(),
            // The same envelope field the fence reads, for the same reason:
            // it is the engine's own ordering, and this list is only
            // authoritative about what came before it.
            at: i64_field("seq").filter(|seq| *seq >= 0),
        }),
        m::REQUEST_RESOLVED => Some(StateFrame::RequestResolved {
            request_id: str_field("requestId")?,
            kind: params.get("kind").and_then(|v| serde_json::from_value(v.clone()).ok()),
            outcome: str_field("outcome"),
            requests: params
                .get("requests")
                .and_then(|v| serde_json::from_value(v.clone()).ok())
                .unwrap_or_default(),
        }),
        m::EVENTS_DROPPED => Some(StateFrame::EventsDropped {
            from_cursor: i64_field("fromCursor").unwrap_or(-1),
            to_cursor: i64_field("toCursor").unwrap_or(-1),
        }),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_proto::messages::CONTRACT_VERSION;
    use coda_proto::state::{EffectiveConfig, Limits, ToolsState, UsageState};
    use serde_json::json;

    fn initialize(instance: &str, cursor: i64) -> InitializeResult {
        InitializeResult {
            protocol_version: "1".into(),
            session_id: "s1".into(),
            server_info: "coda".into(),
            telemetry_log_path: None,
            contract_version: Some(CONTRACT_VERSION.into()),
            engine_instance_id: Some(instance.into()),
            event_cursor: Some(cursor),
            capabilities: None,
        }
    }

    pub(crate) fn snapshot(instance: &str, cursor: i64) -> StateSnapshot {
        StateSnapshot {
            contract_version: CONTRACT_VERSION.into(),
            engine_instance_id: instance.into(),
            session_id: "s1".into(),
            workspace_path: "/w".into(),
            cursor,
            history_epoch: 0,
            history_length: 0,
            lifecycle: EngineLifecycle::Ready,
            initialized: true,
            last_turn_outcome: None,
            turn: None,
            steering: SteeringQueueState::default(),
            tools: ToolsState::default(),
            requests: Vec::new(),
            config: EffectiveConfig {
                active: None,
                next: ActiveConfig {
                    provider_id: None,
                    model: "m".into(),
                    effort: None,
                    effort_is_auto: true,
                    permission_mode: "default".into(),
                    system_prompt_source: "default".into(),
                },
                differing: Vec::new(),
            },
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

    fn text_frame(instance: &str, seq: i64, delta: &str) -> Frame {
        Frame::new(
            "event/assistantText",
            Some(json!({ "delta": delta, "seq": seq, "engineInstanceId": instance })),
        )
    }

    #[test]
    fn a_legacy_engine_without_an_instance_applies_everything_unfenced() {
        // No envelope means no fence. The old behaviour is still correct;
        // this must not start dropping frames because they carry no seq.
        let mut view = ServeView::new();
        let legacy = InitializeResult {
            protocol_version: "1".into(),
            session_id: "s".into(),
            server_info: "coda".into(),
            ..Default::default()
        };
        view.on_initialize(&legacy);
        assert!(!view.is_fenced());
        let frame = Frame::new("event/assistantText", Some(json!({ "delta": "hi" })));
        assert_eq!(view.receive(frame), Reception::Unfenced);
        assert!(!view.needs_snapshot(), "a legacy engine has no snapshot to want");
    }

    #[test]
    fn in_order_frames_advance_the_cursor() {
        let mut view = ServeView::new();
        view.on_initialize(&initialize("e1", 4));
        for seq in 5..=8 {
            assert_eq!(view.receive(text_frame("e1", seq, "x")), Reception::Apply);
        }
        assert_eq!(view.cursor(), 8);
    }

    #[test]
    fn a_frame_the_snapshot_already_covers_is_not_applied_twice() {
        // The whole point of the cursor: buffering across a getState and then
        // applying everything would repeat whatever the snapshot contains.
        let mut view = ServeView::new();
        view.on_initialize(&initialize("e1", 0));
        view.begin_resync();
        assert_eq!(view.receive(text_frame("e1", 1, "a")), Reception::Buffered);
        assert_eq!(view.receive(text_frame("e1", 2, "b")), Reception::Buffered);
        assert_eq!(view.receive(text_frame("e1", 3, "c")), Reception::Buffered);

        // The snapshot is exact at 2, so it already contains "a" and "b".
        let replay = view.apply_snapshot(&snapshot("e1", 2));
        let deltas: Vec<&str> = replay.frames.iter().filter_map(|f| f.params["delta"].as_str()).collect();
        assert_eq!(deltas, ["c"], "only events above the cursor may be replayed");
        assert_eq!(view.cursor(), 3);
        assert!(!view.is_resyncing());
    }

    #[test]
    fn buffered_frames_are_replayed_in_seq_order_however_they_arrived() {
        let mut view = ServeView::new();
        view.on_initialize(&initialize("e1", 0));
        view.begin_resync();
        for seq in [3, 1, 2] {
            view.receive(text_frame("e1", seq, &seq.to_string()));
        }
        let replay = view.apply_snapshot(&snapshot("e1", 0));
        let order: Vec<i64> = replay.frames.iter().map(|f| f.seq).collect();
        assert_eq!(order, [1, 2, 3]);
    }

    #[test]
    fn a_duplicate_frame_after_a_snapshot_is_refused() {
        let mut view = ServeView::new();
        view.on_initialize(&initialize("e1", 0));
        view.apply_snapshot(&snapshot("e1", 7));
        assert_eq!(view.receive(text_frame("e1", 7, "old")), Reception::Duplicate);
        assert_eq!(view.receive(text_frame("e1", 3, "older")), Reception::Duplicate);
        assert_eq!(view.receive(text_frame("e1", 8, "new")), Reception::Apply);
    }

    #[test]
    fn a_real_gap_asks_for_a_snapshot_and_keeps_what_came_after_it() {
        // Losing an event must not be papered over: the frames after the gap
        // are held so that re-snapshotting loses nothing either.
        let mut view = ServeView::new();
        view.on_initialize(&initialize("e1", 0));
        assert_eq!(view.receive(text_frame("e1", 1, "a")), Reception::Apply);
        assert_eq!(view.receive(text_frame("e1", 5, "e")), Reception::Gap);
        assert!(view.needs_snapshot());
        assert!(view.is_resyncing());
        // Everything after the gap keeps being held, not dropped.
        assert_eq!(view.receive(text_frame("e1", 6, "f")), Reception::Buffered);

        let replay = view.apply_snapshot(&snapshot("e1", 5));
        let deltas: Vec<&str> = replay.frames.iter().filter_map(|f| f.params["delta"].as_str()).collect();
        assert_eq!(deltas, ["f"], "the post-gap frame survived the resync");
        assert!(!view.needs_snapshot());
    }

    #[test]
    fn a_gap_inside_the_buffered_run_is_reported_rather_than_stitched_over() {
        // Buffered frames can have a hole in them: the events between two
        // held frames may never have arrived. Applying the later one on top
        // of the snapshot's cursor would splice a conversation that is
        // missing its middle, and would say nothing about it.
        let mut view = ServeView::new();
        view.on_initialize(&initialize("e1", 0));
        view.begin_resync();
        view.receive(text_frame("e1", 1, "a"));
        view.receive(text_frame("e1", 2, "b"));
        // 3..=8 never arrived.
        view.receive(text_frame("e1", 9, "i"));

        let replay = view.apply_snapshot(&snapshot("e1", 2));
        assert_eq!(replay.gap, Some((2, 9)), "the hole in the buffer was not noticed");
        assert!(replay.frames.is_empty(), "nothing may be applied across the hole");
        assert!(view.needs_snapshot(), "a further snapshot is the only way to close it");

        // The unapplied frame is still held, so the snapshot that closes the
        // gap decides whether it is new.
        let second = view.apply_snapshot(&snapshot("e1", 9));
        assert_eq!(second.gap, None);
        assert!(second.frames.is_empty(), "the newer snapshot already covers it");
    }

    #[test]
    fn an_aborted_resync_releases_the_gate_and_replays_what_follows_the_cursor() {
        // The snapshot read failed. Leaving the gate held would buffer every
        // later frame for the rest of the session: the screen would freeze
        // mid-turn and the buffer would grow without end. The cursor we still
        // hold is valid, so the frames that continue from it are applied.
        let mut view = ServeView::new();
        view.on_initialize(&initialize("e1", 0));
        view.begin_resync();
        assert_eq!(view.receive(text_frame("e1", 1, "a")), Reception::Buffered);
        assert_eq!(view.receive(text_frame("e1", 2, "b")), Reception::Buffered);

        let replay = view.abort_resync();
        let deltas: Vec<&str> =
            replay.frames.iter().filter_map(|f| f.params["delta"].as_str()).collect();
        assert_eq!(deltas, ["a", "b"], "held frames that continue the stream must be released");
        assert_eq!(replay.gap, None);
        assert_eq!(view.cursor(), 2, "the cursor follows what was actually applied");
        assert!(!view.is_resyncing(), "the gate must not survive a failed read");
        assert!(view.needs_snapshot(), "the snapshot is still owed");
        assert_eq!(view.buffered(), 0);

        // Streaming continues normally rather than piling up.
        assert_eq!(view.receive(text_frame("e1", 3, "c")), Reception::Apply);
    }

    #[test]
    fn an_aborted_resync_still_refuses_to_splice_across_a_hole() {
        // Releasing the gate must not become a licence to show a conversation
        // with its middle missing as though it were complete.
        let mut view = ServeView::new();
        view.on_initialize(&initialize("e1", 0));
        view.begin_resync();
        view.receive(text_frame("e1", 1, "a"));
        // 2..=8 never arrived.
        view.receive(text_frame("e1", 9, "i"));

        let replay = view.abort_resync();
        let deltas: Vec<&str> =
            replay.frames.iter().filter_map(|f| f.params["delta"].as_str()).collect();
        assert_eq!(deltas, ["a"], "only the contiguous run may be applied");
        assert_eq!(replay.gap, Some((1, 9)));
        assert_eq!(view.cursor(), 1);
        assert!(view.is_resyncing(), "the post-hole frames stay held for the next snapshot");
        assert_eq!(view.buffered(), 1);
    }

    #[test]
    fn the_held_buffer_is_bounded_and_says_so_instead_of_growing_for_ever() {
        // A resync that never completes must cost a bounded amount of memory.
        // Overflow is reported, not hidden: the conversation has to be
        // re-read, exactly as for events that never arrived.
        let mut view = ServeView::new();
        view.on_initialize(&initialize("e1", 0));
        view.begin_resync();
        for seq in 1..=(MAX_BUFFERED_FRAMES as i64) {
            assert_eq!(view.receive(text_frame("e1", seq, "x")), Reception::Buffered);
        }
        assert_eq!(view.buffered(), MAX_BUFFERED_FRAMES);

        let overflow = view.receive(text_frame("e1", MAX_BUFFERED_FRAMES as i64 + 1, "x"));
        assert_eq!(overflow, Reception::Overflowed, "the buffer must not grow past its bound");
        assert!(view.buffered() <= MAX_BUFFERED_FRAMES);
        assert!(view.needs_snapshot(), "the only honest recovery is a re-read");

        // And it stays bounded however long the failure lasts.
        for seq in 0..(MAX_BUFFERED_FRAMES as i64 * 2) {
            view.receive(text_frame("e1", MAX_BUFFERED_FRAMES as i64 + 2 + seq, "x"));
            assert!(view.buffered() <= MAX_BUFFERED_FRAMES);
        }
    }

    #[test]
    fn a_new_engine_instance_resets_the_fence_instead_of_reading_a_gap() {
        // A restarted engine's seqs start over. Treating them against the old
        // cursor would either look like a catastrophic gap or silently drop
        // the whole new conversation as duplicates.
        let mut view = ServeView::new();
        view.on_initialize(&initialize("e1", 0));
        view.receive(text_frame("e1", 40, "x"));
        assert_eq!(view.cursor(), 0, "seq 40 after cursor 0 is a gap, not an advance");

        let reception = view.receive(text_frame("e2", 1, "fresh"));
        assert_eq!(reception, Reception::InstanceChanged);
        assert_eq!(view.engine_instance_id(), Some("e2"));
        assert!(view.needs_snapshot());
        assert_eq!(view.resets(), 1);

        let replay = view.apply_snapshot(&snapshot("e2", 0));
        let deltas: Vec<&str> = replay.frames.iter().filter_map(|f| f.params["delta"].as_str()).collect();
        assert_eq!(deltas, ["fresh"], "the new instance's first frame is not lost");
    }

    #[test]
    fn a_history_reset_is_reported_once_per_epoch() {
        let mut view = ServeView::new();
        view.on_initialize(&initialize("e1", 0));
        view.apply_snapshot(&snapshot("e1", 0));
        assert!(view.on_history_reset(1, 12), "a new epoch must be reported");
        assert!(!view.on_history_reset(1, 14), "the same epoch is not a second reset");
        assert_eq!(view.history_length(), 14);
        assert!(view.needs_snapshot());
    }

    #[test]
    fn a_gap_is_never_declared_before_the_first_cursor_is_known() {
        // Before any snapshot or handshake cursor, "seq 900" is simply where
        // the engine is, not evidence that 899 events were lost.
        let mut view = ServeView::new();
        let mut result = initialize("e1", 0);
        result.event_cursor = None;
        view.on_initialize(&result);
        assert_eq!(view.receive(text_frame("e1", 900, "x")), Reception::Apply);
        assert_eq!(view.cursor(), 900);
    }

    // -- State frame decoding --------------------------------------------

    #[test]
    fn state_frames_decode_to_what_the_engine_publishes() {
        let activity = parse_state_frame(
            "event/activity",
            &json!({ "turnId": "t1", "phase": "runningTools", "phaseSince": "now" }),
        )
        .expect("activity decodes");
        assert!(matches!(
            activity,
            StateFrame::Activity { phase: ActivityPhase::RunningTools, .. }
        ));

        let lifecycle = parse_state_frame(
            "event/lifecycle",
            &json!({ "lifecycle": "busy", "initialized": true }),
        )
        .expect("lifecycle decodes");
        assert!(matches!(
            lifecycle,
            StateFrame::Lifecycle { lifecycle: EngineLifecycle::Busy, initialized: true }
        ));

        let changed = parse_state_frame(
            "event/sessionChanged",
            &json!({ "reason": "fork", "sessionId": "s2", "historyEpoch": 3, "historyLength": 9 }),
        )
        .expect("sessionChanged decodes");
        match changed {
            StateFrame::SessionChanged { reason, session_id, history_epoch, history_length } => {
                assert_eq!((reason.as_str(), session_id.as_str()), ("fork", "s2"));
                assert_eq!((history_epoch, history_length), (3, 9));
            }
            other => panic!("expected a session change, got {other:?}"),
        }
    }

    #[test]
    fn a_resolved_request_carries_the_engines_own_verdict() {
        // The engine publishes `kind` and `outcome` on `event/requestResolved`.
        // A client that dropped them had to invent one, and the invention was
        // always a denial — so an approval granted on another client showed up
        // here as a refusal.
        let resolved = parse_state_frame(
            "event/requestResolved",
            &json!({
                "requestId": "req-e1-1",
                "kind": "permission",
                "outcome": "allowed",
                "requests": [],
            }),
        )
        .expect("requestResolved decodes");
        match resolved {
            StateFrame::RequestResolved { request_id, kind, outcome, .. } => {
                assert_eq!(request_id, "req-e1-1");
                assert_eq!(kind, Some(PendingRequestKind::Permission));
                assert_eq!(outcome.as_deref(), Some("allowed"));
            }
            other => panic!("expected a resolution, got {other:?}"),
        }
    }

    #[test]
    fn a_resolution_without_a_verdict_stays_unknown_rather_than_guessing() {
        // A legacy engine publishes only the id. "Unknown" is the truth; a
        // fabricated denial is not.
        let resolved =
            parse_state_frame("event/requestResolved", &json!({ "requestId": "req-e1-1" }))
                .expect("requestResolved decodes");
        match resolved {
            StateFrame::RequestResolved { kind, outcome, .. } => {
                assert_eq!(kind, None);
                assert_eq!(outcome, None);
            }
            other => panic!("expected a resolution, got {other:?}"),
        }
    }

    #[test]
    fn an_ordinary_event_is_not_mistaken_for_a_state_frame() {
        assert!(parse_state_frame("event/assistantText", &json!({ "delta": "hi" })).is_none());
        // A known method with a missing required field is noise, not half a
        // transition: the next snapshot corrects us.
        assert!(parse_state_frame("event/activity", &json!({ "turnId": "t1" })).is_none());
    }
}
