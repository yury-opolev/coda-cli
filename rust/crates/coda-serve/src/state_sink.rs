//! Bridges the agent event stream to `EngineState` transitions and to the
//! wire, as **one atomic step per event** (Stage C correction C1).
//!
//! # Why this owns publication
//!
//! `StateSink` used to mutate `EngineState` (which published its gated state
//! events while holding STATE) and *then* forward the event to `ServeSink`,
//! which published the legacy `event/*` frame afterwards, outside the state
//! lock. That window is observable: a `session/getState` landing inside it
//! returned a snapshot whose `cursor` was `N` while its `liveEntries` already
//! contained the text of the event published at `N + 1`. A client following
//! the documented reconnect protocol — snapshot at `C`, then apply everything
//! with `seq > C` — appended that text a second time.
//!
//! So every `AgentEvent` is now applied in a single
//! [`EngineState::transact`]: the view transition and **all** publications it
//! implies — the legacy wire frame and any gated state events — are assigned
//! their seqs and handed to the bus while STATE is held. `cursor` therefore
//! means exactly what the contract says: every event with `seq <= cursor` is
//! reflected in this snapshot, and no event with `seq > cursor` is.
//!
//! Because the transition functions run *inside* the state lock, they are
//! plain `StateInner` methods (`crate::state`) rather than the public,
//! self-locking `EngineState` methods — calling those here would re-enter the
//! same non-reentrant mutex.

use std::sync::Arc;

use coda_agent::events::{to_proto_event, AgentEvent, AgentSink};
use coda_proto::events::ToolCallStatus;
use coda_proto::state::ToolCallStateStatus;
use serde_json::Value;

use crate::state::{EngineState, PendingEvent};

fn map_status(status: ToolCallStatus) -> ToolCallStateStatus {
    match status {
        ToolCallStatus::Pending | ToolCallStatus::AwaitingApproval => {
            ToolCallStateStatus::AwaitingPermission
        }
        ToolCallStatus::Running => ToolCallStateStatus::Running,
        ToolCallStatus::Succeeded => ToolCallStateStatus::Completed,
        ToolCallStatus::Failed => ToolCallStateStatus::Failed,
        ToolCallStatus::Cancelled => ToolCallStateStatus::Cancelled,
        ToolCallStatus::Skipped => ToolCallStateStatus::Skipped,
    }
}

/// Methods whose payload gains the canonical `turnId` correlation id.
///
/// `tools.stableIds` promises a client can correlate a tool call end to end;
/// without the owning turn on the payload that promise was only half true.
/// The value comes from the state's own turn table, inside the same
/// transaction, and is purely additive: `rootTurnId` / `activityId` /
/// `sourceId` / `callId` / `batchId` keep exactly the values they have today.
fn wants_turn_id(method: &str) -> bool {
    use coda_proto::events::event_method as m;
    matches!(method, m::TOOL_CALL | m::TOOL_PROGRESS | m::TOOL_RESULT | m::TURN_COMPLETE)
}

/// Observes the same `AgentEvent` stream the wire sees, mirrors the relevant
/// transitions into `EngineState`, and publishes the legacy notification —
/// all in one transaction.
pub struct StateSink {
    state: Arc<EngineState>,
}

impl StateSink {
    pub fn new(state: Arc<EngineState>) -> Self {
        Self { state }
    }
}

impl AgentSink for StateSink {
    fn emit(&self, event: AgentEvent) {
        // The legacy wire publication for this event, if it has one. Built
        // before the lock is taken; injected into the transaction below so it
        // is numbered and sent together with the state transition.
        let wire = to_proto_event(&event).and_then(|proto| proto.to_notification());

        self.state.transact(move |s| {
            let mut events: Vec<PendingEvent> = Vec::new();

            // The legacy frame carries the data; publish it first so a
            // snapshot at the resulting cursor already reflects it.
            if let Some((method, mut params)) = wire {
                if wants_turn_id(&method) {
                    if let (Some(turn_id), Value::Object(map)) = (s.current_turn_id(), &mut params) {
                        let turn_id = turn_id.to_string();
                        map.entry("turnId").or_insert_with(|| Value::String(turn_id));
                    }
                }
                events.push((method, params, false));
            }

            match &event {
                AgentEvent::ModelRequestStarted { request_id } => {
                    events.extend(s.model_request_started(request_id.clone()));
                }
                // Silence after ModelRequestEnded must never be read as
                // `reasoning`/`responding` — the phase only changes on the
                // next *observed* Thinking/AssistantText/ToolCall event.
                AgentEvent::ModelRequestEnded { .. } => {}
                // C5: `ThinkingStarted` arrives here as an empty delta. It is
                // real evidence that reasoning began — for an
                // encrypted-reasoning provider it is the only such evidence —
                // so it is passed through rather than filtered out. The
                // accumulator ignores empty text, so nothing is invented.
                AgentEvent::Thinking { delta } => {
                    events.extend(s.observed_thinking_delta(delta));
                }
                AgentEvent::ThinkingComplete { .. } => {
                    events.extend(s.thinking_complete());
                }
                AgentEvent::AssistantText { delta } => {
                    events.extend(s.observed_text_delta(delta));
                }
                AgentEvent::ToolBatchStarted { batch_id, call_ids } => {
                    events.extend(s.tool_batch_started(batch_id.clone(), call_ids.clone()));
                }
                AgentEvent::ToolBatchEnded { batch_id } => {
                    events.extend(s.tool_batch_ended(batch_id));
                }
                AgentEvent::ToolCall { tool_name, input_json, correlation } => {
                    let call_id = correlation.source_id.clone().unwrap_or_default();
                    let batch_id = correlation.activity_id.clone().unwrap_or_default();
                    events.extend(s.tool_call_started(&call_id, &batch_id, tool_name, input_json));
                }
                AgentEvent::ToolResult { content, is_error, status, correlation, .. } => {
                    let call_id = correlation.source_id.clone().unwrap_or_default();
                    let batch_id = correlation.activity_id.clone().unwrap_or_default();
                    events.extend(s.tool_call_finished(
                        &call_id,
                        &batch_id,
                        map_status(*status),
                        *is_error,
                        content,
                    ));
                }
                // Real usage, observed from the stream's terminal `Done`
                // event. Without this the snapshot reported `usage: unknown`
                // for the whole life of the process.
                AgentEvent::Usage { usage } => {
                    events.extend(
                        s.usage_updated(usage.input_tokens as i64, usage.output_tokens as i64),
                    );
                }
                _ => {}
            }
            events
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::bus::EventBus;
    use coda_proto::state::{ActiveConfig, ActivityPhase, Limits};
    use std::collections::HashMap;
    use std::sync::atomic::{AtomicBool, Ordering};

    fn active_config() -> ActiveConfig {
        ActiveConfig {
            provider_id: Some("anthropic".into()),
            model: "m".into(),
            effort: None,
            effort_is_auto: true,
            permission_mode: "default".into(),
            system_prompt_source: "default".into(),
        }
    }

    fn limits() -> Limits {
        Limits {
            ring_envelopes: 2048,
            ring_bytes: 4 * 1024 * 1024,
            live_bytes_cap: 256 * 1024,
            outcomes_retained: 200,
            history_block_bytes_cap: 64 * 1024,
            max_history_page: 500,
            max_session_page: 200,
        }
    }

    struct Harness {
        state: Arc<EngineState>,
        sink: Arc<StateSink>,
        rx: tokio::sync::mpsc::UnboundedReceiver<Vec<u8>>,
    }

    fn harness() -> Harness {
        let (tx, rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        bus.enable_state_events();
        let state = Arc::new(EngineState::new(bus, "s1", "/work", HashMap::new(), active_config()));
        let sink = Arc::new(StateSink::new(Arc::clone(&state)));
        Harness { state, sink, rx }
    }

    fn decode(frame: &[u8]) -> serde_json::Value {
        let text = std::str::from_utf8(frame).unwrap();
        let body = text.find("\r\n\r\n").map(|i| i + 4).unwrap_or(0);
        serde_json::from_str(&text[body..]).unwrap()
    }

    /// `(seq, method, params)` for every frame written to the connection.
    fn drain(
        rx: &mut tokio::sync::mpsc::UnboundedReceiver<Vec<u8>>,
    ) -> Vec<(i64, String, serde_json::Value)> {
        let mut out = Vec::new();
        while let Ok(frame) = rx.try_recv() {
            let msg = decode(&frame);
            out.push((
                msg["params"]["seq"].as_i64().unwrap(),
                msg["method"].as_str().unwrap().to_string(),
                msg["params"].clone(),
            ));
        }
        out
    }

    #[test]
    fn a_legacy_event_is_still_published_unchanged_apart_from_envelope_metadata() {
        let mut h = harness();
        h.sink.emit(AgentEvent::AssistantText { delta: "hello".into() });
        let frames = drain(&mut h.rx);
        let text = frames.iter().find(|(_, m, _)| m == "event/assistantText").expect("frame");
        assert_eq!(text.2["delta"], "hello");
        assert_eq!(text.0, 1);
        assert_eq!(text.2["engineInstanceId"], "engine-1");
    }

    #[test]
    fn events_with_no_wire_representation_still_drive_the_phase_machine() {
        let mut h = harness();
        h.state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        h.sink.emit(AgentEvent::ModelRequestStarted { request_id: "r1".into() });
        let snap = h.state.project("v1", limits());
        assert_eq!(snap.turn.unwrap().phase, ActivityPhase::WaitingForModel);
        let frames = drain(&mut h.rx);
        assert!(
            !frames.iter().any(|(_, m, _)| m.contains("modelRequest")),
            "ModelRequestStarted has no wire event; it must not invent one"
        );
    }

    // ── C5: ThinkingStarted (empty delta) must reach the phase machine ────
    #[test]
    fn an_empty_thinking_delta_from_thinking_started_enters_reasoning() {
        let mut h = harness();
        h.state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        h.sink.emit(AgentEvent::ModelRequestStarted { request_id: "r1".into() });
        h.sink.emit(AgentEvent::Thinking { delta: String::new() });
        let snap = h.state.project("v1", limits());
        assert_eq!(
            snap.turn.unwrap().phase,
            ActivityPhase::Reasoning,
            "a bodyless ThinkingStarted is the only reasoning signal an encrypted-reasoning provider gives"
        );
        let frames = drain(&mut h.rx);
        assert!(
            frames.iter().any(|(_, m, p)| m == "event/thinking" && p["delta"] == ""),
            "the bodyless wire event must still be published for the UI block"
        );
    }

    // ── Usage is wired end to end ────────────────────────────────────────
    #[test]
    fn an_observed_usage_event_updates_the_snapshot_and_the_wire_together() {
        let mut h = harness();
        h.sink.emit(AgentEvent::Usage {
            usage: coda_llm::Usage { input_tokens: 1200, output_tokens: 300, ..coda_llm::Usage::ZERO },
        });
        let snap = h.state.project("v1", limits());
        let last = snap.usage.last_response.expect("usage must no longer be permanently unknown");
        assert_eq!((last.input_tokens, last.output_tokens), (1200, 300));
        let frames = drain(&mut h.rx);
        assert!(frames.iter().any(|(_, m, p)| m == "event/usage" && p["inputTokens"] == 1200));
    }

    // ── Canonical turnId on tool payloads, legacy aliases untouched ───────
    #[test]
    fn tool_events_carry_the_canonical_turn_id_without_changing_any_legacy_alias() {
        let mut h = harness();
        h.state.begin_turn("turn-42", "go", active_config(), ActivityPhase::Preparing);
        let correlation = coda_llm::Correlation {
            root_turn_id: Some("root-1".into()),
            activity_id: Some("batch-1".into()),
            source_id: Some("toolu_abc".into()),
        };
        h.sink.emit(AgentEvent::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation,
        });
        let frames = drain(&mut h.rx);
        let (_, _, params) =
            frames.iter().find(|(_, m, _)| m == "event/toolCall").expect("toolCall frame");
        assert_eq!(params["turnId"], "turn-42", "the canonical turn id must be on the payload");
        assert_eq!(params["rootTurnId"], "root-1", "legacy alias values must be unchanged");
        assert_eq!(params["activityId"], "batch-1");
        assert_eq!(params["sourceId"], "toolu_abc");
        assert_eq!(params["callId"], "toolu_abc");
        assert_eq!(params["batchId"], "batch-1");
    }

    #[test]
    fn tool_events_outside_a_turn_carry_no_invented_turn_id() {
        let mut h = harness();
        h.sink.emit(AgentEvent::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: Default::default(),
        });
        let frames = drain(&mut h.rx);
        let (_, _, params) = frames.iter().find(|(_, m, _)| m == "event/toolCall").unwrap();
        assert!(params.get("turnId").is_none(), "an unknown turn id must be absent, never invented");
    }

    // ── C1: the snapshot/replay contract, under real contention ───────────
    //
    // The documented reconnect protocol is: take a snapshot at cursor `C`,
    // then apply every event with `seq > C`. That is only sound if the live
    // text in a snapshot is *exactly* the fold of the `event/assistantText`
    // payloads with `seq <= C` — no more (or the client duplicates text when
    // it applies the tail) and no less (or the client loses text it will
    // never be sent again).
    //
    // Two writer threads stream deltas through the sink while a reader
    // repeatedly snapshots. Every observation is checked against the frames
    // the bus actually put on the connection.
    #[test]
    fn a_snapshots_live_text_is_exactly_the_fold_of_the_events_its_cursor_covers() {
        let h = harness();
        let mut rx = h.rx;
        h.state.begin_turn("t1", "", active_config(), ActivityPhase::Preparing);

        let stop = Arc::new(AtomicBool::new(false));
        let mut writers = Vec::new();
        for w in 0..2 {
            let sink = Arc::clone(&h.sink);
            writers.push(std::thread::spawn(move || {
                for i in 0..600 {
                    sink.emit(AgentEvent::AssistantText { delta: format!("<{w}.{i}>") });
                }
            }));
        }

        let reader_state = Arc::clone(&h.state);
        let reader_stop = Arc::clone(&stop);
        let reader = std::thread::spawn(move || {
            let mut observations = Vec::new();
            while !reader_stop.load(Ordering::SeqCst) {
                let snap = reader_state.project("v1", limits());
                observations.push((snap.cursor, crate::state::tests::live_text(&snap)));
            }
            observations
        });

        for w in writers {
            w.join().unwrap();
        }
        stop.store(true, Ordering::SeqCst);
        let observations = reader.join().unwrap();

        // Ground truth: the deltas as the bus numbered and sent them.
        let frames = drain(&mut rx);
        let mut deltas: Vec<(i64, String)> = frames
            .iter()
            .filter(|(_, m, _)| m == "event/assistantText")
            .map(|(seq, _, p)| (*seq, p["delta"].as_str().unwrap().to_string()))
            .collect();
        deltas.sort_by_key(|(seq, _)| *seq);
        assert_eq!(deltas.len(), 1200, "every delta must reach the connection exactly once");

        let ground_truth: String = deltas.iter().map(|(_, d)| d.as_str()).collect();
        let mut checked = 0;
        for (cursor, live) in &observations {
            let expected: String =
                deltas.iter().filter(|(seq, _)| seq <= cursor).map(|(_, d)| d.as_str()).collect();
            assert_eq!(
                live, &expected,
                "a snapshot at cursor {cursor} must contain exactly the deltas its cursor covers"
            );
            // Applying the tail must reconstruct the conversation exactly.
            let tail: String =
                deltas.iter().filter(|(seq, _)| seq > cursor).map(|(_, d)| d.as_str()).collect();
            assert_eq!(
                format!("{live}{tail}"),
                ground_truth,
                "snapshot at {cursor} plus everything after it must equal the whole stream"
            );
            checked += 1;
        }
        assert!(checked > 0, "the reader must have observed at least one snapshot");

        let final_snap = h.state.project("v1", limits());
        assert_eq!(crate::state::tests::live_text(&final_snap), ground_truth);
    }

    // ── C4: subagent/background work never reaches this sink ──────────────
    //
    // The parent verified the real wiring: `TaskTool`, the scheduled runtime
    // and the hook runner all hand a `NullSink` to the subagent factory, so a
    // child's stream never reaches the foreground `StateSink`. This pins that
    // invariant at the only place it could break — if a child stream ever did
    // arrive here it would silently rewrite the foreground phase.
    #[test]
    fn a_child_stream_emitted_into_its_own_null_sink_cannot_touch_foreground_state() {
        let h = harness();
        h.state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        h.sink.emit(AgentEvent::AssistantText { delta: "foreground".into() });

        // Exactly what a subagent gets today: its own sink, not this one.
        let child: Arc<dyn AgentSink> = Arc::new(coda_agent::events::NullSink);
        child.emit(AgentEvent::ModelRequestStarted { request_id: "child".into() });
        child.emit(AgentEvent::Thinking { delta: "child reasoning".into() });
        child.emit(AgentEvent::AssistantText { delta: "child text".into() });

        let snap = h.state.project("v1", limits());
        let turn = snap.turn.unwrap();
        assert_eq!(
            turn.phase,
            ActivityPhase::Responding,
            "the foreground phase must reflect only foreground events"
        );
        assert!(turn.model_request.is_none(), "a child's model request is not the foreground's");
        let live = serde_json::to_string(&turn.live_entries).unwrap();
        assert!(live.contains("foreground"));
        assert!(!live.contains("child"), "no child content may leak into the foreground projection");
        assert!(
            turn.concurrent.background_tasks.is_none(),
            "counts this engine cannot observe stay unknown rather than a false zero"
        );
    }
}
