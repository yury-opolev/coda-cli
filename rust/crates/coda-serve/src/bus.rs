//! `EventBus` — the single ordering/publication point for outbound
//! notifications (§2.1, §2.5, §3 of the serve API implementation plan).
//!
//! Every event the engine produces — legacy `event/*` notifications routed
//! through `ServeSink`, and new gated state/queue/lifecycle notifications
//! synthesised directly by `EngineState` — passes through
//! [`EventBus::publish`]. It:
//!
//! 1. Assigns a gapless, monotonic `seq`, unique within this
//!    `engineInstanceId`, injected into the notification's `params` object as
//!    envelope metadata (never part of any typed `Event` payload).
//! 2. Stores the **whole encoded frame** in a bounded ring (never a
//!    truncated payload), evicting oldest-whole-envelopes under size
//!    pressure and raising `oldest_available_cursor`.
//! 3. Writes the frame to the single outbound channel — unless the
//!    notification method is one of the *new* gated methods and this
//!    connection never negotiated `stateEvents` (§2.1: a seq is still burned
//!    and the frame is still ringed either way, so cursors never depend on
//!    negotiation).
//!
//! There is exactly one `EventBus` per process (one stdio connection), so
//! capability negotiation is a single flag rather than a per-client table —
//! consistent with `session.multiClientAttach: {supported:false}`.

use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;

use coda_proto::events::{EventEnvelope, GetEventsResult};
use coda_proto::{Notification, encode_frame};
use serde_json::Value;
use tokio::sync::mpsc;

use crate::dispatch::RpcError;

/// Default bounded-ring limits (§4 Slice 0; §7 open item A — first numbers,
/// advertised in `StateSnapshot.limits` so a client always knows the real
/// bound rather than assuming one).
pub const DEFAULT_RING_ENVELOPES: usize = 2048;
pub const DEFAULT_RING_BYTES: usize = 4 * 1024 * 1024;

struct StoredEnvelope {
    seq: i64,
    method: String,
    /// The whole encoded wire frame — replayed verbatim, never truncated.
    /// This is the **only** payload the ring retains (I3): the decoded
    /// `params` are re-parsed from this frame on replay, so the advertised
    /// byte bound covers everything the ring actually keeps alive rather
    /// than a fraction of it.
    frame: Vec<u8>,
}

impl StoredEnvelope {
    /// Every byte this entry keeps alive.
    fn retained_bytes(&self) -> usize {
        self.frame.len() + self.method.len()
    }

    /// Re-parses the stored frame back into its `params` object. A frame the
    /// bus itself encoded always round-trips; a failure here is reported as
    /// an explicit empty object rather than a silent partial payload.
    fn params(&self) -> Value {
        let text = match std::str::from_utf8(&self.frame) {
            Ok(t) => t,
            Err(_) => return Value::Object(serde_json::Map::new()),
        };
        let body = text.find("\r\n\r\n").map(|i| i + 4).unwrap_or(0);
        serde_json::from_str::<Value>(&text[body..])
            .ok()
            .and_then(|mut v| v.get_mut("params").map(Value::take))
            .unwrap_or_else(|| Value::Object(serde_json::Map::new()))
    }
}

struct BusInner {
    next_seq: i64,
    ring: VecDeque<StoredEnvelope>,
    ring_bytes: usize,
    /// Monotonically raised whenever eviction drops the true oldest cursor;
    /// `0` means nothing has ever been evicted.
    evicted_up_to: i64,
}

/// The single ordering/publication point for outbound event notifications.
pub struct EventBus {
    outgoing: mpsc::UnboundedSender<Vec<u8>>,
    engine_instance_id: String,
    /// Set once `initialize` negotiates `clientCapabilities.stateEvents`.
    state_events_enabled: AtomicBool,
    inner: Mutex<BusInner>,
    ring_envelopes_limit: usize,
    ring_bytes_limit: usize,
    /// Test-only: `(seq, delay)` — pause for `delay` immediately before the
    /// frame for `seq` is handed to the outbound channel. Used to prove that
    /// assignment, ringing and the hand-off are one indivisible step: with
    /// the pause inside the critical section a racing publisher cannot
    /// overtake, without it the wire order inverts every time.
    #[cfg(test)]
    pre_send_delay: Mutex<Option<(i64, std::time::Duration)>>,
}

impl EventBus {
    pub fn new(outgoing: mpsc::UnboundedSender<Vec<u8>>, engine_instance_id: impl Into<String>) -> Self {
        Self::with_limits(outgoing, engine_instance_id, DEFAULT_RING_ENVELOPES, DEFAULT_RING_BYTES)
    }

    pub fn with_limits(
        outgoing: mpsc::UnboundedSender<Vec<u8>>,
        engine_instance_id: impl Into<String>,
        ring_envelopes_limit: usize,
        ring_bytes_limit: usize,
    ) -> Self {
        Self {
            outgoing,
            engine_instance_id: engine_instance_id.into(),
            state_events_enabled: AtomicBool::new(false),
            inner: Mutex::new(BusInner {
                next_seq: 1,
                ring: VecDeque::new(),
                ring_bytes: 0,
                evicted_up_to: 0,
            }),
            ring_envelopes_limit,
            ring_bytes_limit,
            #[cfg(test)]
            pre_send_delay: Mutex::new(None),
        }
    }

    /// Test-only: pause immediately before the frame for `seq` is written to
    /// the connection (see the field docs on `pre_send_delay`).
    #[cfg(test)]
    pub(crate) fn delay_send_of(&self, seq: i64, duration: std::time::Duration) {
        *self.pre_send_delay.lock().unwrap() = Some((seq, duration));
    }

    pub fn engine_instance_id(&self) -> &str {
        &self.engine_instance_id
    }

    pub fn ring_envelopes_limit(&self) -> i64 {
        self.ring_envelopes_limit as i64
    }

    pub fn ring_bytes_limit(&self) -> i64 {
        self.ring_bytes_limit as i64
    }

    /// Called once, from `initialize`, when the client negotiated
    /// `stateEvents`. Idempotent; never unset for the life of the process.
    pub fn enable_state_events(&self) {
        self.state_events_enabled.store(true, Ordering::SeqCst);
    }

    pub fn state_events_enabled(&self) -> bool {
        self.state_events_enabled.load(Ordering::SeqCst)
    }

    /// The last seq assigned, or `0` before any event has been published
    /// (the baseline cursor — deliberately not a fixed "hello at 0": there is
    /// no event *at* seq 0, seq 0 just means "nothing yet").
    pub fn cursor(&self) -> i64 {
        self.inner.lock().unwrap_or_else(|p| p.into_inner()).next_seq - 1
    }

    /// Publishes one notification. `gated` is `true` for the new
    /// state/queue/lifecycle methods (§2.4): a seq is burned and the frame is
    /// always ringed, but the frame is only written to the connection when
    /// `stateEvents` was negotiated. Returns the assigned seq.
    pub fn publish(&self, method: &str, params: Value, gated: bool) -> i64 {
        self.publish_inner(method, Some(params), gated)
    }

    /// Test hook for the (currently unreachable, but defended) path where a
    /// notification cannot be encoded: `None` params stands in for a failing
    /// `serde_json::to_vec`.
    #[cfg(test)]
    pub(crate) fn publish_forcing_encode_failure(&self, method: &str, gated: bool) -> i64 {
        self.publish_inner(method, None, gated)
    }

    /// Every byte the ring is actually holding right now. Used by tests to
    /// prove the advertised byte bound covers real retained memory rather
    /// than only part of each stored envelope.
    #[cfg(test)]
    pub(crate) fn retained_true_bytes(&self) -> usize {
        let inner = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        inner.ring.iter().map(StoredEnvelope::retained_bytes).sum()
    }

    fn publish_inner(&self, method: &str, params: Option<Value>, gated: bool) -> i64 {
        let mut inner = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        let seq = inner.next_seq;
        inner.next_seq += 1;

        let mut params = params;
        if let Some(Value::Object(map)) = &mut params {
            map.insert("seq".to_string(), Value::from(seq));
            map.insert("engineInstanceId".to_string(), Value::from(self.engine_instance_id.clone()));
        }

        let encoded = params
            .as_ref()
            .and_then(|p| serde_json::to_vec(&Notification::new(method, Some(p.clone()))).ok())
            .map(|bytes| encode_frame(&bytes));

        // I7: the seq is already burned and must never be handed out twice.
        // A frame that cannot be encoded becomes an explicit `eventsDropped`
        // marker occupying exactly that seq, so the ring stays gapless and a
        // client learns that something was lost instead of silently seeing a
        // hole (or, worse, a second event reusing the number).
        let (method, frame, gated) = match encoded {
            Some(frame) => (method.to_string(), frame, gated),
            None => {
                tracing::error!(seq, method, "failed to serialise bus notification");
                let marker = serde_json::json!(coda_proto::state_events::Sequenced {
                    seq,
                    engine_instance_id: self.engine_instance_id.clone(),
                    payload: coda_proto::state_events::EventsDroppedEvent {
                        from_cursor: seq,
                        to_cursor: seq,
                        reason: "serializationFailed".into(),
                    },
                });
                let method = coda_proto::events::event_method::EVENTS_DROPPED.to_string();
                let bytes = serde_json::to_vec(&Notification::new(&method, Some(marker)))
                    .expect("the dropped-event marker is a fixed, always-encodable shape");
                (method, encode_frame(&bytes), true)
            }
        };

        inner.ring.push_back(StoredEnvelope { seq, method, frame: frame.clone() });
        inner.ring_bytes += inner.ring.back().map(StoredEnvelope::retained_bytes).unwrap_or(0);

        while inner.ring.len() > self.ring_envelopes_limit || inner.ring_bytes > self.ring_bytes_limit {
            if let Some(evicted) = inner.ring.pop_front() {
                inner.ring_bytes -= evicted.retained_bytes();
                inner.evicted_up_to = evicted.seq;
            } else {
                break;
            }
        }

        // C2: assignment, ringing and the hand-off to the connection are one
        // indivisible step. Dropping BUS before `send` let a later seq reach
        // the wire first, so a client applying frames in arrival order saw
        // events out of sequence. `UnboundedSender::send` never blocks, so
        // holding the lock across it costs nothing and no `.await` happens
        // inside a `std::sync::Mutex` critical section.
        #[cfg(test)]
        {
            let delay = *self.pre_send_delay.lock().unwrap();
            if let Some((target, duration)) = delay {
                if target == seq {
                    std::thread::sleep(duration);
                }
            }
        }
        if !gated || self.state_events_enabled.load(Ordering::SeqCst) {
            let _ = self.outgoing.send(frame);
        }
        drop(inner);
        seq
    }

    /// `session/getEvents`: replay from the ring, unfiltered by gating (a
    /// client explicit enough to call this wants the full stored stream).
    pub fn get_events(
        &self,
        expected_instance_id: &str,
        after_cursor: i64,
        limit: usize,
    ) -> Result<GetEventsResult, RpcError> {
        if expected_instance_id != self.engine_instance_id {
            return Err(RpcError {
                code: crate::dispatch::error_code::INSTANCE_CHANGED,
                message: format!(
                    "engine instance changed: expected {expected_instance_id}, current {}",
                    self.engine_instance_id
                ),
            });
        }

        let inner = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        // I3: with an empty ring there is no oldest *retained* seq. Reporting
        // the last evicted one would advertise a seq the ring cannot serve
        // (exactly what happens when a single oversized event evicts itself).
        // `next_seq` is the honest answer: nothing at or below it is held,
        // and that is the first seq that could still be retained.
        let oldest_available_cursor =
            inner.ring.front().map(|e| e.seq).unwrap_or(inner.next_seq);
        // `truncated`: the next seq the caller expects (`after_cursor + 1`)
        // is older than the oldest envelope the ring still holds, and
        // something has actually been published in that range.
        let truncated = inner.evicted_up_to > 0 && after_cursor < inner.evicted_up_to;

        let events: Vec<EventEnvelope> = inner
            .ring
            .iter()
            .filter(|e| e.seq > after_cursor)
            .take(if limit == 0 { usize::MAX } else { limit })
            .map(|e| EventEnvelope {
                seq: e.seq,
                engine_instance_id: self.engine_instance_id.clone(),
                method: e.method.clone(),
                params: e.params(),
            })
            .collect();

        let next_cursor = events.last().map(|e| e.seq).unwrap_or_else(|| after_cursor.max(inner.next_seq - 1));

        Ok(GetEventsResult {
            engine_instance_id: self.engine_instance_id.clone(),
            events,
            next_cursor,
            truncated,
            oldest_available_cursor,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn decode_notification(bytes: &[u8]) -> Value {
        let s = std::str::from_utf8(bytes).unwrap();
        let body_start = s.find("\r\n\r\n").map(|i| i + 4).unwrap_or(0);
        serde_json::from_str(&s[body_start..]).unwrap()
    }

    #[test]
    fn cursor_starts_at_zero_with_no_fixed_hello() {
        let (tx, _rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        assert_eq!(bus.cursor(), 0);
    }

    #[test]
    fn seq_is_gapless_and_monotonic_across_publishes() {
        let (tx, mut rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        let s1 = bus.publish("event/a", json!({}), false);
        let s2 = bus.publish("event/b", json!({}), false);
        let s3 = bus.publish("event/c", json!({}), false);
        assert_eq!((s1, s2, s3), (1, 2, 3));
        assert_eq!(bus.cursor(), 3);

        for expected_seq in [1, 2, 3] {
            let frame = rx.try_recv().unwrap();
            let msg = decode_notification(&frame);
            assert_eq!(msg["params"]["seq"], expected_seq);
            assert_eq!(msg["params"]["engineInstanceId"], "engine-1");
        }
    }

    #[test]
    fn a_gated_event_is_numbered_and_ringed_but_not_sent_when_not_negotiated() {
        let (tx, mut rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        let seq = bus.publish("event/activity", json!({"phase": "reasoning"}), true);
        assert_eq!(seq, 1);
        assert!(rx.try_recv().is_err(), "gated event must not be written to the connection");

        // But it is still stored: getEvents (which is not gated) returns it.
        let result = bus.get_events("engine-1", 0, 10).unwrap();
        assert_eq!(result.events.len(), 1);
        assert_eq!(result.events[0].seq, 1);
        assert_eq!(result.events[0].method, "event/activity");
    }

    #[test]
    fn a_gated_event_is_sent_once_state_events_are_enabled() {
        let (tx, mut rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        bus.enable_state_events();
        bus.publish("event/activity", json!({}), true);
        assert!(rx.try_recv().is_ok());
    }

    #[test]
    fn legacy_events_are_never_gated_regardless_of_negotiation() {
        let (tx, mut rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        assert!(!bus.state_events_enabled());
        bus.publish("event/assistantText", json!({"delta": "hi"}), false);
        assert!(rx.try_recv().is_ok(), "legacy events must be sent even when stateEvents was never negotiated");
    }

    #[test]
    fn get_events_requires_matching_engine_instance_id() {
        let (tx, _rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        bus.publish("event/a", json!({}), false);
        let err = bus.get_events("some-other-instance", 0, 10).unwrap_err();
        assert_eq!(err.code, crate::dispatch::error_code::INSTANCE_CHANGED);
    }

    #[test]
    fn get_events_returns_only_events_after_the_given_cursor() {
        let (tx, _rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        bus.publish("event/a", json!({}), false);
        bus.publish("event/b", json!({}), false);
        bus.publish("event/c", json!({}), false);
        let result = bus.get_events("engine-1", 1, 10).unwrap();
        assert_eq!(result.events.len(), 2);
        assert_eq!(result.events[0].seq, 2);
        assert_eq!(result.events[1].seq, 3);
        assert_eq!(result.next_cursor, 3);
        assert!(!result.truncated);
    }

    #[test]
    fn get_events_respects_the_limit_and_reports_the_partial_next_cursor() {
        let (tx, _rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        for _ in 0..5 {
            bus.publish("event/a", json!({}), false);
        }
        let result = bus.get_events("engine-1", 0, 2).unwrap();
        assert_eq!(result.events.len(), 2);
        assert_eq!(result.next_cursor, 2);
    }

    #[test]
    fn ring_eviction_by_envelope_count_raises_the_oldest_available_cursor_and_never_truncates_a_payload() {
        let (tx, _rx) = mpsc::unbounded_channel();
        let bus = EventBus::with_limits(tx, "engine-1", 3, DEFAULT_RING_BYTES);
        for i in 0..5 {
            bus.publish("event/a", json!({"i": i}), false);
        }
        // Only the last 3 whole envelopes remain.
        let result = bus.get_events("engine-1", 0, 100).unwrap();
        assert_eq!(result.events.len(), 3);
        assert_eq!(result.events[0].seq, 3);
        assert_eq!(result.oldest_available_cursor, 3);
        // A caller asking from before the eviction point learns explicitly.
        let stale = bus.get_events("engine-1", 0, 100).unwrap();
        assert!(stale.truncated, "a cursor before the oldest available seq must be reported truncated");
        // No payload was ever shortened — every remaining envelope is whole.
        for e in &result.events {
            assert_eq!(e.params["i"], e.seq - 1);
        }
    }

    #[test]
    fn ring_eviction_by_byte_size_evicts_whole_envelopes() {
        let (tx, _rx) = mpsc::unbounded_channel();
        // Each envelope frame is a few hundred bytes; force eviction quickly.
        let bus = EventBus::with_limits(tx, "engine-1", DEFAULT_RING_ENVELOPES, 200);
        for i in 0..20 {
            bus.publish("event/a", json!({"i": i, "pad": "x".repeat(20)}), false);
        }
        let result = bus.get_events("engine-1", 0, 1000).unwrap();
        assert!(result.events.len() < 20, "byte pressure must evict old envelopes");
        assert!(result.oldest_available_cursor > 1);
        assert!(result.truncated);
    }

    // ── C2: wire order must never invert under concurrent publishers ──────
    //
    // Deterministic, not probabilistic: the bus is told to pause just before
    // the frame for seq 1 reaches the connection. If assignment/ringing and
    // the hand-off are not one indivisible step, a second publisher overtakes
    // during that pause and the connection sees seq 2 before seq 1.
    #[test]
    fn a_publisher_paused_before_the_hand_off_cannot_be_overtaken_on_the_wire() {
        let (tx, mut rx) = mpsc::unbounded_channel();
        let bus = std::sync::Arc::new(EventBus::new(tx, "engine-1"));
        bus.delay_send_of(1, std::time::Duration::from_millis(250));

        let first = {
            let bus = std::sync::Arc::clone(&bus);
            std::thread::spawn(move || bus.publish("event/assistantText", json!({ "delta": "one" }), false))
        };
        // Give the first publisher time to reach the pause.
        std::thread::sleep(std::time::Duration::from_millis(60));
        let second = {
            let bus = std::sync::Arc::clone(&bus);
            std::thread::spawn(move || bus.publish("event/assistantText", json!({ "delta": "two" }), false))
        };
        assert_eq!(first.join().unwrap(), 1);
        assert_eq!(second.join().unwrap(), 2);

        let mut delivered = Vec::new();
        while let Ok(frame) = rx.try_recv() {
            delivered.push(decode_notification(&frame)["params"]["seq"].as_i64().unwrap());
        }
        assert_eq!(
            delivered,
            vec![1, 2],
            "frames must reach the connection in seq order; a later seq overtook an earlier one"
        );
    }

    #[test]
    fn concurrent_publishers_write_frames_to_the_connection_in_strictly_increasing_seq_order() {
        for _ in 0..8 {
            let (tx, mut rx) = mpsc::unbounded_channel();
            let bus = std::sync::Arc::new(EventBus::new(tx, "engine-1"));
            let barrier = std::sync::Arc::new(std::sync::Barrier::new(4));
            let mut handles = Vec::new();
            for t in 0..4 {
                let bus = std::sync::Arc::clone(&bus);
                let barrier = std::sync::Arc::clone(&barrier);
                handles.push(std::thread::spawn(move || {
                    barrier.wait();
                    for i in 0..200 {
                        bus.publish("event/assistantText", json!({ "delta": format!("{t}-{i}") }), false);
                    }
                }));
            }
            for h in handles {
                h.join().unwrap();
            }

            let mut previous = 0i64;
            let mut seen = 0;
            while let Ok(frame) = rx.try_recv() {
                let seq = decode_notification(&frame)["params"]["seq"].as_i64().unwrap();
                assert!(
                    seq > previous,
                    "outgoing frames must reach the connection in seq order: {seq} followed {previous}"
                );
                previous = seq;
                seen += 1;
            }
            assert_eq!(seen, 800, "every published frame must reach the connection exactly once");
        }
    }

    // ── I3: the ring must bound what it actually retains ─────────────────
    #[test]
    fn the_ring_bounds_every_byte_it_actually_retains_not_only_the_encoded_frame() {
        let (tx, _rx) = mpsc::unbounded_channel();
        let limit = 4096;
        let bus = EventBus::with_limits(tx, "engine-1", DEFAULT_RING_ENVELOPES, limit);
        for i in 0..200 {
            bus.publish("event/assistantText", json!({ "i": i, "delta": "x".repeat(120) }), false);
        }
        assert!(
            bus.retained_true_bytes() <= limit,
            "the ring retained {} bytes against an advertised {limit}-byte bound",
            bus.retained_true_bytes()
        );
    }

    // ── I3: an event too large for the ring must not be reported retained ─
    #[test]
    fn an_event_evicted_by_its_own_size_is_never_reported_as_available() {
        let (tx, _rx) = mpsc::unbounded_channel();
        let bus = EventBus::with_limits(tx, "engine-1", DEFAULT_RING_ENVELOPES, 64);
        let seq = bus.publish("event/assistantText", json!({ "delta": "y".repeat(4096) }), false);
        let result = bus.get_events("engine-1", 0, 100).unwrap();
        assert!(result.events.is_empty(), "the oversized envelope cannot be retained");
        assert_ne!(
            result.oldest_available_cursor, seq,
            "an evicted seq must never be advertised as the oldest available one"
        );
        assert!(
            result.oldest_available_cursor > seq,
            "with an empty ring the oldest available cursor is the next seq that could still be retained"
        );
        assert!(result.truncated, "a caller from 0 must be told the range is gone");
    }

    // ── I7: a burned seq is never reused, and the gap is explicit ────────
    #[test]
    fn a_seq_is_never_reused_when_a_frame_cannot_be_encoded() {
        let (tx, mut rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        bus.enable_state_events();
        let first = bus.publish("event/a", json!({}), false);
        let failed = bus.publish_forcing_encode_failure("event/b", false);
        let third = bus.publish("event/c", json!({}), false);
        assert_eq!((first, failed, third), (1, 2, 3), "a failed encode must still burn its seq");

        let result = bus.get_events("engine-1", 0, 10).unwrap();
        let seqs: Vec<i64> = result.events.iter().map(|e| e.seq).collect();
        assert_eq!(seqs, vec![1, 2, 3], "the ring stays gapless: the failure is ringed as an explicit gap marker");
        assert_eq!(result.events[1].method, coda_proto::events::event_method::EVENTS_DROPPED);
        assert_eq!(result.events[1].params["fromCursor"], 2);
        assert_eq!(result.events[1].params["toCursor"], 2);

        let mut delivered = Vec::new();
        while let Ok(frame) = rx.try_recv() {
            delivered.push(decode_notification(&frame)["params"]["seq"].as_i64().unwrap());
        }
        assert_eq!(delivered, vec![1, 2, 3], "the connection sees the gap marker, never a reused seq");
    }

    #[test]
    fn an_empty_ring_reports_the_next_seq_as_its_oldest_available_cursor() {
        let (tx, _rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        let result = bus.get_events("engine-1", 0, 10).unwrap();
        assert_eq!(result.oldest_available_cursor, 1);
        assert!(!result.truncated, "nothing has been published, so nothing was lost");
    }

    #[test]
    fn a_replacement_loss_marker_still_requires_state_event_negotiation() {
        let (tx, mut rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        bus.publish_inner("event/assistantText", None, false);
        assert!(rx.try_recv().is_err(), "a legacy client must not receive a gated method");
        let replay = bus.get_events("engine-1", 0, 10).unwrap();
        assert_eq!(replay.events.len(), 1, "the marker must still occupy its replay sequence");
        assert_eq!(replay.events[0].method, coda_proto::events::event_method::EVENTS_DROPPED);
        assert_eq!(replay.events[0].seq, 1);
    }

    #[test]
    fn a_client_at_the_current_cursor_sees_no_gap_and_is_not_truncated() {
        let (tx, _rx) = mpsc::unbounded_channel();
        let bus = EventBus::new(tx, "engine-1");
        bus.publish("event/a", json!({}), false);
        bus.publish("event/b", json!({}), false);
        let result = bus.get_events("engine-1", 2, 10).unwrap();
        assert!(result.events.is_empty());
        assert!(!result.truncated);
        assert_eq!(result.next_cursor, 2);
    }
}