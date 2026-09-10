//! Real out-of-process conformance test for the Slice 0 / Stage C state,
//! event and queue vertical slice (`docs/superpowers/plans/
//! 2026-09-08-serve-api-implementation.md`).
//!
//! Drives the actual compiled `coda-engine` binary (`CARGO_BIN_EXE_coda-engine`,
//! available directly because this test lives in `coda-engine`'s own
//! integration-test target — see `hermetic.rs`'s module doc for why a
//! bin-only package's own crate is where its `CARGO_BIN_EXE_*` is set) over
//! real stdio, against a genuine **streaming** SSE fixture (a raw
//! `TcpListener`, not the one-shot non-streaming JSON responder used
//! elsewhere) that pauses mid-turn behind an explicit handshake barrier
//! (a `oneshot` channel signalled only after the test has observed real
//! `reasoning`-phase evidence) — never an arbitrary `sleep`.
//!
//! Hermetic: temp `CODA_HOME`/cwd, `--no-mcp`, no real credential or
//! provider request — the fixture is a local loopback listener reached
//! through `--api-key`/`--endpoint`.
//!
//! Proves, on the real wire:
//! - `initialize` negotiates `clientCapabilities.stateEvents` and returns
//!   `contractVersion`/`engineInstanceId`/`eventCursor`/`capabilities`.
//! - `session/getState` mid-turn reports `turn.phase == "reasoning"` only
//!   after a real `ThinkingDelta` was observed — never inferred from
//!   silence — and its `cursor` exactly equals the highest `seq` already
//!   delivered live at that instant (the state/event atomicity contract).
//! - `session/getEvents` replays the **whole** turn with a gapless seq
//!   range, `truncated:false`, and reconstructs the exact assistant text —
//!   proving no event is lost or duplicated across a simulated resync.
//! - A pre-existing legacy event (`event/assistantText`) gains only
//!   additive `seq`/`engineInstanceId` fields; its original field
//!   (`delta`) is untouched — the semantic (not byte-identical) legacy
//!   compatibility contract.

mod support;

use std::time::Duration;

use coda_client::{Connection, Engine, Inbound};
use coda_proto::messages::{method, ClientCapabilities, InitializeParams, PromptParams};
use support::{base_engine_command, coda_engine_exe, read_one_request, sse_event, Sandbox};
use tokio::io::AsyncWriteExt;
use tokio::net::TcpListener;
use tokio::sync::oneshot;

/// A real streaming SSE fixture: writes `prelude` immediately on connect,
/// then blocks until the returned `oneshot::Sender` is signalled, then
/// writes `tail` and closes. This is the explicit handshake barrier — no
/// `tokio::time::sleep` anywhere in this file.
async fn sse_fixture_with_barrier(prelude: String, tail: String) -> (String, oneshot::Sender<()>) {
    let (resume_tx, resume_rx) = oneshot::channel::<()>();
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();

    tokio::spawn(async move {
        let Ok((mut socket, _)) = listener.accept().await else { return };
        read_one_request(&mut socket).await;
        let header = "HTTP/1.1 200 OK\r\ncontent-type: text/event-stream\r\nconnection: close\r\n\r\n";
        socket.write_all(header.as_bytes()).await.expect("write headers");
        socket.write_all(prelude.as_bytes()).await.expect("write prelude");
        socket.flush().await.ok();
        // Explicit handshake barrier: proceeds only once the test has
        // observed real mid-stream evidence and deliberately releases it.
        let _ = resume_rx.await;
        socket.write_all(tail.as_bytes()).await.expect("write tail");
        let _ = socket.shutdown().await;
    });

    (format!("http://127.0.0.1:{port}"), resume_tx)
}

const THINKING_TEXT: &str = "let me work that out";
const ANSWER_TEXT: &str = "4";

fn fixture_streams() -> (String, String) {
    let prelude = sse_event("message_start", serde_json::json!({ "type": "message_start", "message": { "usage": { "input_tokens": 10 } } }))
        + &sse_event("content_block_start", serde_json::json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "thinking", "thinking": "" } }))
        + &sse_event("content_block_delta", serde_json::json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "thinking_delta", "thinking": THINKING_TEXT } }));

    let tail = sse_event("content_block_stop", serde_json::json!({ "type": "content_block_stop", "index": 0 }))
        + &sse_event("content_block_start", serde_json::json!({ "type": "content_block_start", "index": 1, "content_block": { "type": "text", "text": "" } }))
        + &sse_event("content_block_delta", serde_json::json!({ "type": "content_block_delta", "index": 1, "delta": { "type": "text_delta", "text": ANSWER_TEXT } }))
        + &sse_event("content_block_stop", serde_json::json!({ "type": "content_block_stop", "index": 1 }))
        + &sse_event("message_delta", serde_json::json!({ "type": "message_delta", "delta": { "stop_reason": "end_turn" }, "usage": { "output_tokens": 3 } }))
        + &sse_event("message_stop", serde_json::json!({ "type": "message_stop" }));

    (prelude, tail)
}

/// One live notification, decoded down to what this test needs.
struct Live {
    method: String,
    seq: i64,
    params: serde_json::Value,
}

async fn recv_live(inbound: &mut tokio::sync::mpsc::UnboundedReceiver<Inbound>) -> Live {
    loop {
        match inbound.recv().await.expect("inbound stream must not close mid-turn") {
            Inbound::Notification { method, params } => {
                let params = params.unwrap_or(serde_json::Value::Null);
                support::assert_state_event_payload(&method, &params);
                let seq = params.get("seq").and_then(|v| v.as_i64()).unwrap_or(-1);
                assert!(seq >= 0, "every event (legacy or new) must carry an envelope seq: {method}");
                return Live { method, seq, params };
            }
            // No permission/question/plan-approval is expected in this
            // text-only, tool-free turn.
            Inbound::Request { method, .. } => {
                panic!("unexpected reverse request {method} in a tool-free turn");
            }
        }
    }
}

async fn get_state(connection: &Connection) -> serde_json::Value {
    connection
        .request(method::GET_STATE, Some(serde_json::json!({})))
        .await
        .expect("session/getState must not error")
}

/// The concatenation of `delta` across every live notification of `method`
/// whose envelope seq is at or below `cursor` — i.e. exactly what the
/// snapshot at that cursor claims to already reflect.
fn fold_delta(live: &[Live], method: &str, cursor: i64) -> String {
    live.iter()
        .filter(|e| e.method == method && e.seq <= cursor)
        .filter_map(|e| e.params["delta"].as_str())
        .collect()
}

fn live_blocks<'a>(state: &'a serde_json::Value, kind: &str) -> Vec<&'a serde_json::Value> {
    state["turn"]["liveEntries"]
        .as_array()
        .map(|entries| {
            entries
                .iter()
                .filter(|e| e["role"] == "assistant")
                .filter_map(|e| e["blocks"].as_array())
                .flatten()
                .filter(|b| b["kind"] == kind)
                .collect()
        })
        .unwrap_or_default()
}

fn live_assistant_text(state: &serde_json::Value) -> String {
    live_blocks(state, "text").iter().filter_map(|b| b["text"].as_str()).collect()
}

fn live_reasoning(state: &serde_json::Value) -> String {
    live_blocks(state, "reasoningSummary").iter().filter_map(|b| b["text"].as_str()).collect()
}

#[tokio::test]
async fn state_event_queue_vertical_slice_conformance() {
    let sandbox = Sandbox::new();
    let (prelude, tail) = fixture_streams();
    let (endpoint, resume) = sse_fixture_with_barrier(prelude, tail).await;

    let command = base_engine_command(&coda_engine_exe(), &sandbox)
        .arg("--no-mcp")
        .arg("--api-key")
        .arg("fake-test-key")
        .arg("--endpoint")
        .arg(&endpoint);

    let (engine, mut inbound) = Engine::spawn(command).expect("engine spawns");
    let connection = engine.connection();

    // ── initialize negotiates stateEvents and returns the Slice 0 fields ──
    let init_params = InitializeParams::new("state-conformance-test")
        .with_client_capabilities(ClientCapabilities { state_events: Some(true), ..Default::default() });
    let init_value = tokio::time::timeout(
        Duration::from_secs(15),
        connection.request(method::INITIALIZE, Some(serde_json::to_value(init_params).unwrap())),
    )
    .await
    .expect("initialize must not hang")
    .expect("initialize succeeds hermetically");

    let engine_instance_id = init_value["engineInstanceId"].as_str().expect("engineInstanceId present").to_string();
    assert!(!engine_instance_id.is_empty());
    assert_eq!(init_value["contractVersion"], coda_proto::messages::CONTRACT_VERSION);
    // The reported cursor is exactly what the engine has published so far —
    // and seq 0 is "nothing yet", never a fixed hello event at 0.
    let event_cursor = init_value["eventCursor"].as_i64().expect("eventCursor present");
    let baseline = connection
        .request(
            method::GET_EVENTS,
            Some(serde_json::json!({ "engineInstanceId": engine_instance_id, "afterCursor": 0, "limit": 1000 })),
        )
        .await
        .expect("session/getEvents must not error");
    let baseline_seqs: Vec<i64> =
        baseline["events"].as_array().unwrap().iter().map(|e| e["seq"].as_i64().unwrap()).collect();
    assert!(baseline_seqs.iter().all(|s| *s >= 1), "no event may occupy seq 0");
    assert_eq!(
        event_cursor,
        baseline_seqs.last().copied().unwrap_or(0),
        "eventCursor must be exactly the last seq initialize produced"
    );
    assert!(
        baseline["events"].as_array().unwrap().iter().all(|e| e["method"] != "event/engineHello"),
        "there is no fixed hello-at-0 event"
    );
    assert_eq!(
        init_value["capabilities"]["state.snapshot"]["supported"],
        true,
        "only actually-implemented capabilities may be advertised supported"
    );
    // `history.rich` was the placeholder here until Stage D shipped it. The
    // invariant is unchanged and still pinned — an unimplemented capability
    // is explicit `supported:false` with a reason — so it now points at one
    // the engine genuinely does not implement.
    assert_eq!(
        init_value["capabilities"]["state.sectionFilter"]["supported"],
        false,
        "unimplemented capabilities must be explicit supported:false with a reason"
    );
    assert!(init_value["capabilities"]["state.sectionFilter"]["reason"].is_string());
    assert_eq!(
        init_value["capabilities"]["history.rich"]["supported"],
        true,
        "session/getHistory is wired and tested, so it must be advertised"
    );

    // ── initial getState: idle, no turn yet ──────────────────────────────
    let idle_state = get_state(&connection).await;
    assert_eq!(idle_state["lifecycle"], "ready");
    assert!(idle_state["turn"].is_null());
    assert_eq!(idle_state["engineInstanceId"], engine_instance_id);

    // ── kick off the turn ─────────────────────────────────────────────────
    let prompt_value = serde_json::to_value(PromptParams::text("what is 2+2?")).unwrap();
    let pending_prompt =
        connection.send_request(method::PROMPT, Some(prompt_value)).expect("prompt request queues");

    // Drain live notifications until real ThinkingDelta evidence arrives —
    // never inferred, never a sleep.
    let mut live: Vec<Live> = Vec::new();
    loop {
        let ev = recv_live(&mut inbound).await;
        let is_real_thinking_delta = ev.method == "event/thinking" && ev.params["delta"].as_str().is_some_and(|d| !d.is_empty());
        live.push(ev);
        if is_real_thinking_delta {
            break;
        }
    }
    let cursor_at_reasoning = live.last().unwrap().seq;

    // ── mid-turn snapshot: reasoning phase, atomic with the live cursor ──
    let mid_state = get_state(&connection).await;
    assert_eq!(mid_state["lifecycle"], "busy");
    let turn = mid_state["turn"].as_object().expect("turn must be present mid-stream");
    assert_eq!(turn["phase"], "reasoning", "phase must reflect the real ThinkingDelta just observed, not effort/silence");
    assert!(turn["turnId"].as_str().is_some_and(|s| !s.is_empty()));
    // The running turn must name the provider it is actually using. This was
    // a placeholder `null` until the client was resolved, so a client that
    // snapshotted early saw "unknown" for something the engine plainly knew.
    let turn_provider = turn["activeConfig"]["providerId"]
        .as_str()
        .expect("a running turn must name its provider, not report a placeholder unknown")
        .to_string();
    assert!(!turn_provider.is_empty());
    assert_eq!(
        mid_state["config"]["next"]["providerId"], turn_provider,
        "and `config.next` must report the same wired provider, not omit it as unknown"
    );
    // Turn duration is measured by the engine, not inferred from a UTC string
    // by a client whose clock it does not share.
    let turn_elapsed = turn["elapsedMs"]
        .as_i64()
        .expect("a running turn must publish the server's own monotonic duration");
    assert!(turn_elapsed >= 0);
    assert!(
        turn["phaseElapsedMs"].as_i64().is_some_and(|p| p >= 0),
        "and the phase clock, so a per-phase timer needs no client-side arithmetic"
    );
    assert!(
        turn["startedAt"].as_str().is_some_and(|s| !s.is_empty()),
        "the UTC start stays — elapsedMs is additional, not a replacement"
    );
    assert_eq!(
        mid_state["cursor"], cursor_at_reasoning,
        "snapshot cursor must exactly equal the highest seq already delivered live at this instant"
    );

    let reinitialize = tokio::time::timeout(
        Duration::from_secs(5),
        connection.request(method::INITIALIZE, Some(serde_json::json!({
            "protocolVersion": "1",
            "sessionId": "must-not-replace-a-running-session",
        }))),
    ).await.expect("concurrent initialize must fail promptly").unwrap_err();
    match reinitialize {
        coda_client::ClientError::Rpc(error) => {
            assert!(error.message.contains("busy"), "{error:?}");
        }
        error => panic!("expected an explicit busy RPC error, got {error:?}"),
    }
    let after_refusal = get_state(&connection).await;
    assert_eq!(after_refusal["cursor"], mid_state["cursor"]);
    assert_eq!(after_refusal["sessionId"], mid_state["sessionId"]);
    assert_eq!(after_refusal["turn"]["turnId"], mid_state["turn"]["turnId"]);
    assert_eq!(after_refusal["lifecycle"], "busy");

    // The live projection must be exactly the fold of the events its own
    // cursor covers — the state/replay property, asserted against the real
    // wire rather than restated as `cursor >= 0`.
    let mid_cursor = mid_state["cursor"].as_i64().unwrap();
    assert_eq!(
        live_reasoning(&mid_state),
        fold_delta(&live, "event/thinking", mid_cursor),
        "the reasoning in a snapshot must be exactly the Thinking deltas at or below its cursor"
    );
    assert_eq!(
        live_assistant_text(&mid_state),
        fold_delta(&live, "event/assistantText", mid_cursor),
        "the assistant text in a snapshot must be exactly the deltas at or below its cursor"
    );

    // I1: the in-flight turn is never counted as committed history, so a
    // client concatenating `historyLength` with `liveEntries` cannot double
    // count at any instant.
    assert_eq!(
        mid_state["historyLength"], 0,
        "nothing is committed while the turn is in flight"
    );
    assert_eq!(mid_state["tools"]["truncated"], false);
    assert!(mid_state["tools"]["retained"].as_i64().is_some_and(|r| r > 0));

    // The `sections` filter is not implemented: it is refused, not ignored.
    let filtered = connection
        .request(method::GET_STATE, Some(serde_json::json!({ "sections": ["tools"] })))
        .await;
    assert!(
        filtered.is_err(),
        "an unimplemented section filter must be rejected, never silently answered with the whole snapshot"
    );
    assert_eq!(
        init_value["capabilities"]["state.sectionFilter"]["supported"],
        false,
        "and the catalog must say so"
    );

    // ── release the barrier: fixture emits the rest of the turn ──────────
    resume.send(()).expect("fixture is still waiting on the barrier");

    // Drain to the very end of the turn. `event/lifecycle {ready}` is the
    // last frame a turn produces: it is published when the single-flight slot
    // is actually released, after `event/turnComplete` and the gated
    // `event/turnEnded` (I1/I4).
    loop {
        let ev = recv_live(&mut inbound).await;
        let done = ev.method == "event/lifecycle" && ev.params["lifecycle"] == "ready";
        live.push(ev);
        if done {
            break;
        }
    }
    let final_cursor = live.last().unwrap().seq;
    assert!(final_cursor > cursor_at_reasoning, "the turn must have produced further events after the barrier");
    assert!(
        live.iter().any(|e| e.method == "event/turnComplete"),
        "the legacy turnComplete frame must still be published"
    );

    let prompt_result = tokio::time::timeout(Duration::from_secs(30), pending_prompt)
        .await
        .expect("prompt must not hang")
        .expect("prompt channel must not be dropped")
        .expect("prompt must succeed");
    assert_eq!(prompt_result["ok"], true);

    // ── replay via session/getEvents: whole turn, gapless, no loss ───────
    let replay = connection
        .request(
            method::GET_EVENTS,
            Some(serde_json::json!({ "engineInstanceId": engine_instance_id, "afterCursor": 0, "limit": 1000 })),
        )
        .await
        .expect("session/getEvents must not error");

    assert_eq!(replay["truncated"], false);
    assert_eq!(replay["oldestAvailableCursor"], 1);
    let replayed = replay["events"].as_array().expect("events array");
    assert_eq!(replayed.len(), live.len(), "replay must return exactly the events actually delivered live — no loss, no duplication");

    let seqs: Vec<i64> = replayed.iter().map(|e| e["seq"].as_i64().unwrap()).collect();
    let mut sorted = seqs.clone();
    sorted.sort_unstable();
    assert_eq!(seqs, sorted, "replay must be in seq order");
    for w in sorted.windows(2) {
        assert_eq!(w[1], w[0] + 1, "seq must be gapless across the whole replayed range");
    }
    assert_eq!(replay["nextCursor"], final_cursor);

    // Reconstruct assistant text purely from the replay — proves whole-frame
    // fidelity independent of the live drain.
    let reconstructed: String = replayed
        .iter()
        .filter(|e| e["method"] == "event/assistantText")
        .filter_map(|e| e["params"]["delta"].as_str())
        .collect();
    assert_eq!(reconstructed, ANSWER_TEXT);

    // Legacy compatibility: `event/assistantText` still carries `delta`
    // (original field, unchanged meaning), additively gaining `seq` and
    // `engineInstanceId`.
    let assistant_text_frame = replayed
        .iter()
        .find(|e| e["method"] == "event/assistantText")
        .expect("assistantText event must be present in the replay");
    assert_eq!(assistant_text_frame["params"]["delta"], ANSWER_TEXT);
    assert!(assistant_text_frame["params"]["seq"].is_i64());
    assert_eq!(assistant_text_frame["params"]["engineInstanceId"], engine_instance_id);

    // ── final getState: turn cleared, outcome recorded, no duplication ───
    let final_state = get_state(&connection).await;
    assert!(final_state["turn"].is_null(), "an ended turn must not leave a stale liveEntries view");
    assert_eq!(final_state["lastTurnOutcome"]["stopReason"], "end_turn");
    assert_eq!(final_state["lastTurnOutcome"]["interrupted"], false);

    // I1: `ready` and "a prompt is accepted" agree. The slot is released, so
    // the engine must both say `ready` and actually accept the next prompt.
    assert_eq!(final_state["lifecycle"], "ready");
    let second_prompt = tokio::time::timeout(
        Duration::from_secs(30),
        connection
            .request(method::PROMPT, Some(serde_json::to_value(PromptParams::text("again")).unwrap())),
    )
    .await
    .expect("a second prompt must not hang");
    match &second_prompt {
        // The fixture's single connection is spent, so this turn fails at the
        // provider — but it was *accepted*, which is what `ready` promised.
        Ok(value) => assert!(value.get("ok").is_some()),
        Err(e) => assert!(
            !e.to_string().contains("busy"),
            "a snapshot that says `ready` must never be followed by a busy refusal: {e}"
        ),
    }

    // I4: every state mutation a client must converge on was published.
    let ended_event = live
        .iter()
        .find(|e| e.method == "event/turnEnded")
        .expect("a state client must learn the turn ended without polling");
    assert_eq!(ended_event.params["historyLength"], 2, "the fence travels with the event");
    assert_eq!(ended_event.params["interrupted"], false);
    assert!(ended_event.params["finalizedCallIds"].is_array());
    assert!(
        live.iter().any(|e| e.method == "event/lifecycle" && e.params["lifecycle"] == "ready"),
        "becoming available again must be published"
    );
    // I4: a client must never be left holding a placeholder `activeConfig` it
    // has to discover was wrong. That is satisfied either by the placeholder
    // being accurate from the instant the turn opened — which the mid-turn
    // snapshot above proves for `providerId`, the field that used to be the
    // placeholder — or by publishing the correction. What is not allowed is
    // silently replacing it.
    assert!(
        !turn_provider.is_empty() || live.iter().any(|e| e.method == "event/configChanged"),
        "the running turn's config must be accurate on arrival or corrected out loud"
    );
    for event in live.iter().filter(|e| e.method == "event/configChanged") {
        assert!(
            event.params["next"]["providerId"].as_str().is_some_and(|p| !p.is_empty()),
            "a published config must carry the same wired provider a snapshot does, or a \
             converging client loses it: {}",
            event.params
        );
    }

    // I1: the fence moved and the live view cleared in the same step, so at
    // no observable instant is the turn present in both.
    assert_eq!(
        final_state["historyLength"], 2,
        "the committed prompt and reply are counted exactly once, only after the live view was cleared"
    );

    // C1/C6: `event/turnComplete` was published inside the same transaction
    // that cleared the turn, so the final snapshot's cursor already covers
    // it. A client that snapshots here and applies everything after the
    // cursor receives nothing it has already accounted for.
    let turn_complete_seq = live
        .iter()
        .find(|e| e.method == "event/turnComplete")
        .map(|e| e.seq)
        .expect("turnComplete must have been delivered");
    assert!(
        final_state["cursor"].as_i64().unwrap() >= turn_complete_seq,
        "a snapshot showing the turn as over must already cover the event that announced it"
    );

    // Usage is real: it used to be permanently `unknown` because the
    // observation was never wired.
    assert_eq!(
        final_state["usage"]["lastResponse"]["inputTokens"], 10,
        "usage observed on the wire must reach the snapshot"
    );
    assert_eq!(final_state["usage"]["lastResponse"]["outputTokens"], 3);
    assert_eq!(final_state["usage"]["session"]["inputTokens"], 10);

    // The whole replayed stream reconstructs the turn exactly: committed
    // history length + the (now empty) live view, with the assistant text
    // recoverable from the events alone.
    assert_eq!(
        replayed
            .iter()
            .filter(|e| e["method"] == "event/thinking")
            .filter_map(|e| e["params"]["delta"].as_str())
            .collect::<String>(),
        THINKING_TEXT,
        "reasoning must be recoverable from the replay exactly once"
    );

    drop(connection);
    drop(inbound);
    let _ = engine.shutdown(Duration::from_secs(10)).await;
}
