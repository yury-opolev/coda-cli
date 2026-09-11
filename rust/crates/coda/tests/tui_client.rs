//! The TUI as an ordinary public-API client, against the real engine.
//!
//! Every test here drives the **compiled `coda serve` binary** over real
//! stdio, through the same `coda_tui::api` code the shipped front-end runs.
//! Nothing is re-implemented for the test: the bootstrap under test is the
//! bootstrap `coda` executes, and the reconciliation under test is the
//! reducer the terminal draws from. A test against a parallel implementation
//! would prove only that the test agrees with itself.
//!
//! `CARGO_BIN_EXE_coda` is required — no PATH lookup and no silent skip.
//! Hermetic throughout: temporary `CODA_HOME` and working directory,
//! `--no-mcp`, and a fake credential pointing at a loopback SSE fixture. No
//! real profile, provider or key is touched.

mod support;

use std::time::Duration;

use coda_boot::SessionIntent;
use coda_client::{Connection, Engine, Inbound, Responder};
use coda_proto::messages::{method, PromptParams};
use coda_proto::RequestId;
use coda_proto::state::EngineLifecycle;
use coda_tui::api::{self, boot::BootError};
use coda_tui::state::{Activity, PendingPrompt, UiEvent, UiState};
use coda_tui::transcript::Block;
use serde_json::json;
use support::{barrier_provider, scripted_provider, serve_command, sse_event, text_turn, Sandbox};

const TIMEOUT: Duration = Duration::from_secs(45);

/// Boots the engine the way the front-end does, returning its pieces.
async fn boot(
    sandbox: &Sandbox,
    endpoint: &str,
    intent: &SessionIntent,
) -> Result<coda_tui::api::boot::Booted, BootError> {
    api::boot::boot(serve_command(sandbox, endpoint), intent, "coda-tui").await
}

/// Runs one complete turn, draining the inbound stream until it ends.
async fn run_turn(
    connection: &Connection,
    inbound: &mut tokio::sync::mpsc::UnboundedReceiver<Inbound>,
    text: &str,
) {
    let params = serde_json::to_value(PromptParams::text(text)).expect("prompt params");
    let prompt = connection.send_request(method::PROMPT, Some(params)).expect("prompt sent");
    let drain = async {
        loop {
            match inbound.recv().await {
                Some(Inbound::Notification { .. }) => {}
                Some(Inbound::Request { responder, .. }) => {
                    responder.fail(coda_proto::error_codes::METHOD_NOT_FOUND, "not in this test")
                }
                None => break,
            }
        }
    };
    tokio::select! {
        _ = prompt => {}
        _ = drain => {}
        _ = tokio::time::sleep(TIMEOUT) => panic!("the turn did not finish within {TIMEOUT:?}"),
    }
}

// ---------------------------------------------------------------------------
// Bootstrap: public methods only
// ---------------------------------------------------------------------------

/// A JSON-RPC server that records every method a client calls.
///
/// It answers the real bootstrap round-trips, so the *actual* sequence
/// `coda_tui::api::boot` runs is what gets recorded — not a description of it.
struct RecordingEngine {
    calls: std::sync::Arc<std::sync::Mutex<Vec<String>>>,
}

impl RecordingEngine {
    fn methods(&self) -> Vec<String> {
        self.calls.lock().expect("calls poisoned").clone()
    }
}

/// Wires a client `Connection` to an in-process recording server.
fn recording_engine(sessions: Vec<serde_json::Value>) -> (Connection, RecordingEngine) {
    let (client_side, server_side) = tokio::io::duplex(64 * 1024);
    let (client_read, client_write) = tokio::io::split(client_side);
    let (connection, mut inbound, _tasks) = coda_client::connect(client_read, client_write);
    // Nothing here sends notifications; draining keeps the channel alive.
    tokio::spawn(async move { while inbound.recv().await.is_some() {} });

    let calls = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
    let recorder = std::sync::Arc::clone(&calls);

    tokio::spawn(async move {
        use coda_proto::{encode_frame, FrameDecoder};
        use tokio::io::{AsyncReadExt, AsyncWriteExt};

        let (mut read, mut write) = tokio::io::split(server_side);
        let mut decoder = FrameDecoder::new();
        let mut buffer = [0u8; 8192];
        loop {
            let count = match read.read(&mut buffer).await {
                Ok(0) | Err(_) => return,
                Ok(count) => count,
            };
            decoder.feed(&buffer[..count]);
            while let Ok(Some(frame)) = decoder.next_frame() {
                let request: serde_json::Value = match serde_json::from_slice(&frame) {
                    Ok(value) => value,
                    Err(_) => continue,
                };
                let Some(method) = request["method"].as_str() else { continue };
                recorder.lock().expect("calls poisoned").push(method.to_string());
                let result = match method {
                    "session/listSessions" => {
                        json!({ "sessions": sessions, "totalKnown": sessions.len(), "truncated": false })
                    }
                    "initialize" => json!({
                        "protocolVersion": "1",
                        "sessionId": request["params"]["sessionId"].as_str().unwrap_or("fresh"),
                        "serverInfo": "recording",
                        "contractVersion": coda_proto::messages::CONTRACT_VERSION,
                        "engineInstanceId": "rec-1",
                        "eventCursor": 0,
                    }),
                    "session/fork" => json!({ "ok": true, "newSessionId": "forked-1" }),
                    _ => json!({}),
                };
                let response = json!({ "jsonrpc": "2.0", "id": request["id"], "result": result });
                let bytes = serde_json::to_vec(&response).expect("response");
                if write.write_all(&encode_frame(&bytes)).await.is_err() {
                    return;
                }
            }
        }
    });

    (connection, RecordingEngine { calls })
}

fn saved(id: &str, created: &str) -> serde_json::Value {
    json!({
        "sessionId": id,
        "createdUtc": created,
        "messageCount": 4,
        "preview": "hello",
        "previewTruncated": false,
        "isCurrent": false,
    })
}

#[tokio::test]
async fn continuing_resolves_the_session_over_the_public_api_and_reads_no_files() {
    // The whole point of the de-privatisation: `--continue` used to read
    // `.coda/sessions` directly. The captured call list is what proves it
    // does not any more, and that an external orchestrator could do exactly
    // the same thing.
    let (connection, engine) = recording_engine(vec![
        saved("older", "2026-09-01T10:00:00+00:00"),
        saved("newest", "2026-09-08T09:00:00+00:00"),
    ]);

    let opened =
        api::boot::resolve_and_initialize(&connection, &SessionIntent::Latest, "coda-tui")
            .await
            .expect("continue resolves");

    assert_eq!(
        engine.methods(),
        ["session/listSessions", "initialize"],
        "only public, documented methods — and no transcript read"
    );
    assert_eq!(opened.session_id, "newest", "the most recent session was opened");
}

#[tokio::test]
async fn an_explicit_resume_id_goes_straight_to_the_handshake() {
    // The engine validates the id during `initialize` and answers with a
    // typed "no such session", so listing first would be a round-trip that
    // adds nothing.
    let (connection, engine) = recording_engine(vec![saved("abc", "2026-09-01T10:00:00+00:00")]);
    let opened = api::boot::resolve_and_initialize(
        &connection,
        &SessionIntent::Resume("abc".into()),
        "coda-tui",
    )
    .await
    .expect("resume resolves");

    assert_eq!(engine.methods(), ["initialize"]);
    assert_eq!(opened.session_id, "abc");
}

#[tokio::test]
async fn forking_asks_the_engine_to_copy_the_session_rather_than_copying_files() {
    let (connection, engine) = recording_engine(vec![saved("src", "2026-09-01T10:00:00+00:00")]);
    let opened =
        api::boot::resolve_and_initialize(&connection, &SessionIntent::Fork(None), "coda-tui")
            .await
            .expect("fork resolves");

    assert_eq!(engine.methods(), ["session/listSessions", "initialize", "session/fork"]);
    assert_eq!(opened.session_id, "forked-1");
    assert_eq!(opened.forked_from.as_deref(), Some("src"));
}

#[tokio::test]
async fn an_empty_workspace_starts_fresh_on_continue_but_refuses_to_fork_nothing() {
    let (connection, _) = recording_engine(Vec::new());
    let opened =
        api::boot::resolve_and_initialize(&connection, &SessionIntent::Latest, "coda-tui")
            .await
            .expect("continue in an empty directory starts a session");
    assert!(
        opened.notices.iter().any(|n| n.contains("starting a new one")),
        "the substitution must be stated, not silent: {:?}",
        opened.notices
    );

    let (connection, _) = recording_engine(Vec::new());
    let error =
        api::boot::resolve_and_initialize(&connection, &SessionIntent::Fork(None), "coda-tui")
            .await
            .expect_err("forking nothing is an error, as it always was");
    assert!(matches!(error, BootError::NoSessions), "{error}");

    let (connection, _) = recording_engine(Vec::new());
    let error = api::boot::resolve_and_initialize(
        &connection,
        &SessionIntent::Fork(Some("nope".into())),
        "coda-tui",
    )
    .await
    .expect_err("forking an unknown id is an error");
    assert!(matches!(error, BootError::NotFound(ref id) if id == "nope"), "{error}");
}

// ---------------------------------------------------------------------------
// Against the real engine
// ---------------------------------------------------------------------------

#[tokio::test]
async fn the_handshake_negotiates_the_state_contract_and_seeds_the_fence() {
    let provider = scripted_provider(vec![text_turn("hello")]).await;
    let sandbox = Sandbox::new();
    let booted = boot(&sandbox, &provider.endpoint, &SessionIntent::New)
        .await
        .expect("the engine starts and initialises");

    let initialize = &booted.initialize;
    assert_eq!(
        initialize.contract_version.as_deref(),
        Some(coda_proto::messages::CONTRACT_VERSION),
        "the engine must report the contract this client negotiated"
    );
    let instance = initialize.engine_instance_id.clone().expect("an engine instance id");
    assert!(!instance.is_empty());
    assert!(initialize.event_cursor.is_some(), "the authoritative start cursor is required");

    let mut view = api::ServeView::new();
    view.on_initialize(initialize);
    assert!(view.is_fenced());
    assert!(view.needs_snapshot(), "a handshake gives a cursor, never the conversation");

    let snapshot = api::get_state(&booted.connection).await.expect("session/getState");
    assert_eq!(snapshot.engine_instance_id, instance);
    assert_eq!(snapshot.lifecycle, EngineLifecycle::Ready);
    view.apply_snapshot(&snapshot);
    assert!(!view.needs_snapshot());

    let _ = booted.engine.shutdown(Duration::from_secs(5)).await;
}

#[tokio::test]
async fn a_resumed_session_renders_the_old_conversation_not_a_count() {
    // The failure this exists to prevent: a resume that reports "restored 4
    // messages" above an empty screen.
    let provider = scripted_provider(vec![text_turn("the answer is 4")]).await;
    let sandbox = Sandbox::new();

    // First run: produce a real saved session by having the engine write it.
    let first = boot(&sandbox, &provider.endpoint, &SessionIntent::New)
        .await
        .expect("first engine starts");
    let mut inbound = first.inbound;
    run_turn(&first.connection, &mut inbound, "what is two plus two").await;
    let _ = first.engine.shutdown(Duration::from_secs(5)).await;

    // Second run: continue, and rebuild the transcript from the engine.
    let second = boot(&sandbox, &provider.endpoint, &SessionIntent::Latest)
        .await
        .expect("second engine continues");
    let history = api::get_history(&second.connection, &api::HistoryWindow::default())
        .await
        .expect("session/getHistory");
    assert!(history.entries.len() >= 2, "the saved conversation must come back: {history:?}");

    let hydrated = api::history::hydrate(&history.entries, &[], false);
    let mut state = UiState::new();
    state.apply(UiEvent::Rehydrated {
        blocks: hydrated.blocks,
        notices: hydrated.notices,
        // What this very read proves about the committed prefix, exactly as
        // the client records it in production.
        coverage: Some(coda_tui::coverage::HistoryCoverage::of_read(&history)),
    });

    let user: Vec<&str> = state
        .transcript
        .blocks()
        .iter()
        .filter_map(|b| match b {
            Block::User { text, .. } => Some(text.as_str()),
            _ => None,
        })
        .collect();
    let assistant: Vec<&str> = state
        .transcript
        .blocks()
        .iter()
        .filter_map(|b| match b {
            Block::Assistant { text, .. } => Some(text.as_str()),
            _ => None,
        })
        .collect();

    assert!(
        user.iter().any(|t| t.contains("two plus two")),
        "the operator's own prompt must be replayed: {user:?}"
    );
    assert!(
        assistant.iter().any(|t| t.contains("the answer is 4")),
        "the assistant's reply must be replayed: {assistant:?}"
    );
    // Nothing invented: a stored message carries no wall clock.
    for block in state.transcript.blocks() {
        if let Block::User { timestamp, .. } = block {
            assert!(timestamp.is_empty(), "a fabricated timestamp reached a replayed message");
        }
    }

    let _ = second.engine.shutdown(Duration::from_secs(5)).await;
}

#[tokio::test]
async fn a_mid_turn_snapshot_seeds_the_clock_from_a_genuinely_non_zero_server_time() {
    // The engine's `elapsedMs` is measured by *its* monotonic clock. A client
    // that reset to zero, or subtracted a remote wall clock from a local one,
    // would show a duration that jumps. The barrier holds a real turn open so
    // the number is genuinely non-zero rather than a default.
    let prelude = support::message_start()
        + &sse_event(
            "content_block_start",
            json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "text", "text": "" } }),
        )
        + &sse_event(
            "content_block_delta",
            json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "text_delta", "text": "wor" } }),
        );
    let tail = sse_event("content_block_stop", json!({ "type": "content_block_stop", "index": 0 }))
        + &sse_event(
            "message_delta",
            json!({ "type": "message_delta", "delta": { "stop_reason": "end_turn" }, "usage": { "output_tokens": 3 } }),
        )
        + &sse_event("message_stop", json!({ "type": "message_stop" }));

    let (endpoint, release) = barrier_provider(prelude, tail).await;
    let sandbox = Sandbox::new();
    let booted = boot(&sandbox, &endpoint, &SessionIntent::New).await.expect("engine starts");
    let connection = booted.connection.clone();
    let mut inbound = booted.inbound;

    let params = serde_json::to_value(PromptParams::text("say something")).expect("params");
    let prompt = connection.send_request(method::PROMPT, Some(params)).expect("prompt sent");

    // Wait for real evidence the turn is under way, then let it sit.
    let mut saw_text = false;
    let deadline = tokio::time::Instant::now() + TIMEOUT;
    while !saw_text {
        let message = tokio::time::timeout_at(deadline, inbound.recv())
            .await
            .expect("the engine must stream before the barrier")
            .expect("stream open");
        if let Inbound::Notification { method, .. } = message {
            saw_text = method == "event/assistantText";
        }
    }
    tokio::time::sleep(Duration::from_millis(400)).await;

    let snapshot = api::get_state(&connection).await.expect("mid-turn getState");
    assert_eq!(snapshot.lifecycle, EngineLifecycle::Busy);
    let turn = snapshot.turn.clone().expect("a running turn");
    let elapsed = turn.elapsed_ms.expect("the engine reports its own elapsed time");
    assert!(elapsed >= 300, "the server clock must be genuinely running, got {elapsed}ms");

    let now = std::time::Instant::now();
    let mut state = UiState::new();
    state.reconcile(&snapshot, now);
    let progress = state.turn_progress.as_ref().expect("a rehydrated clock");
    assert_eq!(progress.elapsed_ms(now), elapsed, "the client adopted the engine's duration");
    assert_eq!(
        progress.elapsed_ms(now + Duration::from_secs(3)),
        elapsed + 3_000,
        "and kept it running locally"
    );
    assert!(state.is_busy());

    let _ = release.send(());
    let _ = tokio::time::timeout(TIMEOUT, prompt).await;
    let _ = booted.engine.shutdown(Duration::from_secs(5)).await;
}

#[tokio::test]
async fn the_queue_is_reconciled_from_the_engine_and_the_local_draft_survives() {
    // Steering is engine-owned: what is still pending, and what was
    // delivered, is the engine's answer, not a local guess. The user's own
    // text must survive the reconciliation intact.
    let prelude = support::message_start()
        + &sse_event(
            "content_block_start",
            json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "text", "text": "" } }),
        )
        + &sse_event(
            "content_block_delta",
            json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "text_delta", "text": "thinking" } }),
        );
    let tail = sse_event("content_block_stop", json!({ "type": "content_block_stop", "index": 0 }))
        + &sse_event(
            "message_delta",
            json!({ "type": "message_delta", "delta": { "stop_reason": "end_turn" }, "usage": { "output_tokens": 3 } }),
        )
        + &sse_event("message_stop", json!({ "type": "message_stop" }));

    let (endpoint, release) = barrier_provider(prelude, tail).await;
    let sandbox = Sandbox::new();
    let booted = boot(&sandbox, &endpoint, &SessionIntent::New).await.expect("engine starts");
    let connection = booted.connection.clone();
    let mut inbound = booted.inbound;

    let params = serde_json::to_value(PromptParams::text("start")).expect("params");
    let prompt = connection.send_request(method::PROMPT, Some(params)).expect("prompt sent");

    let deadline = tokio::time::Instant::now() + TIMEOUT;
    loop {
        let message = tokio::time::timeout_at(deadline, inbound.recv())
            .await
            .expect("the engine must stream")
            .expect("stream open");
        if matches!(&message, Inbound::Notification { method, .. } if method == "event/assistantText")
        {
            break;
        }
    }

    let steered = connection
        .request(method::STEER, Some(json!({ "text": "  also check the tests  " })))
        .await
        .expect("session/steer");
    assert_eq!(steered["ok"], true, "{steered}");
    let message_id = steered["messageId"].as_str().expect("a message id").to_string();

    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "start".into() });
    state.apply(UiEvent::Queued {
        text: "  also check the tests  ".into(),
        id: Some(message_id.clone()),
    });

    let snapshot = api::get_state(&connection).await.expect("getState");
    assert_eq!(snapshot.steering.pending.len(), 1, "the engine owns the queue");
    assert_eq!(snapshot.steering.pending[0].message_id, message_id);
    assert_eq!(
        snapshot.steering.pending[0].text, "  also check the tests  ",
        "the engine retains the full original text, whitespace included"
    );

    let now = std::time::Instant::now();
    state.reconcile(&snapshot, now);
    assert_eq!(state.queued.len(), 1);
    assert_eq!(
        state.queued[0].text, "  also check the tests  ",
        "reconciliation must not degrade the draft to a preview"
    );

    // Recall is the authoritative withdraw path, and it returns the same
    // full text — which is what makes recall-into-composer lossless.
    let recalled = connection.request(method::RECALL_STEERING, None).await.expect("recall");
    let messages = recalled["messages"].as_array().expect("recalled messages");
    assert_eq!(messages.len(), 1);
    assert_eq!(messages[0]["text"], "  also check the tests  ");

    let after = api::get_state(&connection).await.expect("getState after recall");
    assert!(after.steering.pending.is_empty(), "the recall emptied the engine's queue");
    state.reconcile(&after, now);
    assert!(state.queued.is_empty(), "and the client converged on that");

    let _ = release.send(());
    let _ = tokio::time::timeout(TIMEOUT, prompt).await;
    let _ = booted.engine.shutdown(Duration::from_secs(5)).await;
}

#[tokio::test]
async fn the_engine_holds_the_slot_after_a_turn_ends_and_the_client_does_not_claim_ready() {
    // `event/turnComplete` reports the last output; the single-flight slot is
    // released separately. A client that said "ready" in between invited a
    // prompt the engine would refuse as busy.
    let provider = scripted_provider(vec![text_turn("done")]).await;
    let sandbox = Sandbox::new();
    let booted = boot(&sandbox, &provider.endpoint, &SessionIntent::New)
        .await
        .expect("engine starts");
    let mut inbound = booted.inbound;

    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "go".into() });

    let params = serde_json::to_value(PromptParams::text("go")).expect("params");
    let prompt = booted.connection.send_request(method::PROMPT, Some(params)).expect("sent");

    let mut lifecycles = Vec::new();
    let mut saw_busy_phase = false;
    let mut seen_turn_complete = false;
    let deadline = tokio::time::Instant::now() + TIMEOUT;
    loop {
        let Ok(Some(message)) = tokio::time::timeout_at(deadline, inbound.recv()).await else {
            break;
        };
        let Inbound::Notification { method, params } = message else { continue };
        let params = params.unwrap_or(serde_json::Value::Null);
        if let Some(frame) = api::view::parse_state_frame(&method, &params) {
            // The same routing `App::dispatch_frame` performs: state frames
            // carry metadata, never content, so they are applied alongside
            // the legacy content stream rather than instead of it.
            match frame {
                api::StateFrame::Lifecycle { lifecycle, .. } => {
                    lifecycles.push(lifecycle);
                    state.apply(UiEvent::CoreLifecycle(lifecycle));
                    if seen_turn_complete && lifecycle == EngineLifecycle::Ready {
                        break;
                    }
                }
                api::StateFrame::Activity { phase, .. } => {
                    saw_busy_phase = true;
                    state.apply(UiEvent::CoreActivity(phase))
                }
                _ => {}
            }
            continue;
        }
        if method == "event/turnComplete" {
            seen_turn_complete = true;
            state.apply(UiEvent::Engine(coda_proto::Event::parse(&method, Some(&params))));
            assert_eq!(
                state.activity,
                Activity::Working,
                "the client claimed ready while the engine still held the slot"
            );
        }
    }

    assert!(seen_turn_complete, "the turn never completed");
    // The two edges of the single-flight slot are published differently, and
    // this is the fact a client has to be built on: the engine announces the
    // *taken* edge as a turn phase (`event/activity`, emitted only while a
    // turn is open) and the *released* edge as `event/lifecycle: ready`. A
    // client that waited for a `lifecycle: busy` frame would wait forever and
    // fall back to believing `event/turnComplete`.
    assert!(saw_busy_phase, "the engine published no turn phase, so the slot looked free");
    assert_eq!(
        lifecycles.last(),
        Some(&EngineLifecycle::Ready),
        "the release edge must arrive after the turn completes: {lifecycles:?}"
    );
    assert_eq!(state.activity, Activity::Ready, "and the client converged once released");

    let _ = tokio::time::timeout(TIMEOUT, prompt).await;
    let _ = booted.engine.shutdown(Duration::from_secs(5)).await;
}

// ---------------------------------------------------------------------------
// Pending interactions
// ---------------------------------------------------------------------------

fn responder(id: i64) -> (Responder, tokio::sync::mpsc::UnboundedReceiver<Vec<u8>>) {
    let (tx, rx) = tokio::sync::mpsc::unbounded_channel();
    (Responder::new(RequestId::Number(id), tx), rx)
}

fn answered(rx: &mut tokio::sync::mpsc::UnboundedReceiver<Vec<u8>>) -> Option<serde_json::Value> {
    let frame = rx.try_recv().ok()?;
    let mut decoder = coda_proto::FrameDecoder::new();
    decoder.feed(&frame);
    let bytes = decoder.next_frame().ok()??;
    serde_json::from_slice(&bytes).ok()
}

fn pending_dto(handle: &str, tool: &str) -> coda_proto::state::PendingRequestDto {
    coda_proto::state::PendingRequestDto {
        request_id: handle.into(),
        kind: coda_proto::state::PendingRequestKind::Permission,
        issued_at: "now".into(),
        turn_id: None,
        call_id: None,
        display: json!({ "toolName": tool, "inputPreview": "x" }),
        fail_closed_default: "deny".into(),
    }
}

#[tokio::test]
async fn a_request_described_by_both_paths_is_one_decision_and_the_responder_survives() {
    // The raw round-trip and `session/getPendingRequests` can describe the
    // same request. Two modals is the visible bug; dropping the responder is
    // the invisible one — a dropped responder *declines*.
    let mut pending = api::requests::PendingInteractions::new();
    // Discovered out of band, so there is no engine sequence on it; the
    // handle is what identifies the decision either way.
    pending.on_discovered(&pending_dto("req-e1-1", "edit"), Some("e1".into()), None);
    let (responder, mut rx) = responder(1);
    let opened = pending.on_server_request(
        Some("req-e1-1".into()),
        Some("e1".into()),
        PendingPrompt::Permission { tool: "edit".into(), preview: "x".into() },
        responder,
    );

    assert!(!opened, "the raw frame described a decision already on screen");
    assert_eq!(pending.len(), 1);
    assert!(answered(&mut rx).is_none(), "the responder must still be waiting");
}

#[tokio::test]
async fn concurrent_requests_queue_and_answering_resolves_exactly_the_selected_one() {
    let mut pending = api::requests::PendingInteractions::new();
    let (foreground, mut foreground_rx) = responder(1);
    let (background, mut background_rx) = responder(2);
    pending.on_server_request(
        Some("req-e1-1".into()),
        Some("e1".into()),
        PendingPrompt::Permission { tool: "edit".into(), preview: "a".into() },
        foreground,
    );
    pending.on_server_request(
        Some("req-e1-2".into()),
        Some("e1".into()),
        PendingPrompt::Permission { tool: "run_command".into(), preview: "b".into() },
        background,
    );

    assert_eq!(pending.current().expect("current").key(), "req-e1-1");
    match pending.take_current(Some("e1")) {
        api::requests::Delivery::Raw(responder) => responder.respond(json!({ "allow": true })),
        _ => panic!("the raw round-trip is the exactly-once path"),
    }
    assert_eq!(answered(&mut foreground_rx).expect("answered")["result"]["allow"], true);
    assert!(answered(&mut background_rx).is_none(), "the queued decision was untouched");
    assert_eq!(pending.current().expect("next").key(), "req-e1-2");
}

#[tokio::test]
async fn a_declined_question_reaches_the_engine_as_a_decline_not_an_empty_answer() {
    // Against the real engine: cancelling a question must produce a typed
    // decline. An empty string would be indistinguishable from the operator
    // choosing an empty answer, which is exactly what the contract forbids.
    let provider = scripted_provider(vec![text_turn("unused")]).await;
    let sandbox = Sandbox::new();
    let booted = boot(&sandbox, &provider.endpoint, &SessionIntent::New)
        .await
        .expect("engine starts");

    // The engine refuses an empty answer out of band, which is the property
    // the client's decline path depends on: it must cancel, not resolve.
    let error = api::resolve_request(&booted.connection, "req-nope-1", json!({ "answer": "" }))
        .await
        .expect_err("an empty answer is not a decision");
    match error {
        coda_client::ClientError::Rpc(rpc) => {
            assert!(rpc.code != 0, "a typed error, not a silent success: {rpc:?}");
        }
        other => panic!("expected a typed RPC error, got {other}"),
    }

    let _ = booted.engine.shutdown(Duration::from_secs(5)).await;
}

// ---------------------------------------------------------------------------
// Local vs API-only maintenance
// ---------------------------------------------------------------------------

#[tokio::test]
async fn the_local_maintenance_gate_is_the_clients_own_and_no_engine_answer_can_open_it() {
    use coda_tui::local::AccessMode;

    // The engine's own describe/list surfaces are read-only by construction:
    // there is no key in either that could switch a client into local-write
    // mode, and the gate does not consult them at all.
    let provider = scripted_provider(vec![text_turn("unused")]).await;
    let sandbox = Sandbox::new();
    let booted = boot(&sandbox, &provider.endpoint, &SessionIntent::New)
        .await
        .expect("engine starts");

    let described = api::config_describe(&booted.connection).await.expect("config/describe");
    let mcp = api::mcp_list(&booted.connection).await.expect("mcp/list");

    let engine_answer = format!("{described:?} {mcp:?}");
    assert!(
        !engine_answer.contains("isLocal"),
        "the engine must not claim to know whether its client is local"
    );

    // Whatever the engine said, an API-only client refuses local maintenance.
    let api_only = AccessMode::for_launch(true);
    assert!(!api_only.allows_local_maintenance());
    let refusal = coda_tui::local::unsupported_remotely("MCP server configuration");
    assert!(refusal.contains("engine host"), "{refusal}");

    // And the shipping default — this process started its own core — keeps
    // every editor working exactly as before.
    assert!(AccessMode::for_launch(false).allows_local_maintenance());

    // `mcp/list` is the display path in both modes, and carries no secrets.
    for server in &mcp.servers {
        assert!(
            !engine_answer.contains("coda-secret:"),
            "a secret reference target reached the wire for {}",
            server.name
        );
    }

    let _ = booted.engine.shutdown(Duration::from_secs(5)).await;
}

#[tokio::test]
async fn output_styles_come_from_the_engines_catalogue_rather_than_a_linked_enum() {
    let provider = scripted_provider(vec![text_turn("unused")]).await;
    let sandbox = Sandbox::new();
    let booted = boot(&sandbox, &provider.endpoint, &SessionIntent::New)
        .await
        .expect("engine starts");

    let described = api::config_describe(&booted.connection).await.expect("config/describe");
    let styles = api::allowed_values(&described, "outputStyle")
        .expect("the engine publishes its output styles");
    assert!(!styles.is_empty(), "an empty catalogue would leave the client guessing");
    assert!(
        styles.iter().any(|s| s.value == "default"),
        "the built-in default must be listed: {styles:?}"
    );

    let _ = booted.engine.shutdown(Duration::from_secs(5)).await;
}

/// Keeps the unused-import checker honest about the engine handle type.
#[allow(dead_code)]
fn engine_type_is_used(_: Option<Engine>) {}

// ---------------------------------------------------------------------------
// The front-ends' own launch contract
// ---------------------------------------------------------------------------

/// Runs the real `coda` binary headlessly in a sandbox, returning its output.
///
/// Everything the engine child needs is passed as environment, exactly as a
/// user's shell would: the child inherits it, and no real profile, key or
/// provider is involved.
async fn run_headless(
    sandbox: &Sandbox,
    endpoint: &str,
    engine_model: &str,
    args: &[&str],
) -> std::process::Output {
    // Async on purpose: the provider fixture is a task on this very runtime,
    // so a blocking `wait` here would deadlock the listener it is waiting for.
    let mut command = tokio::process::Command::new(support::coda_exe());
    command.arg("run").args(args).arg("--cwd").arg(sandbox.cwd.path());
    // Scrub the ambient environment first, then set only what this fixture
    // needs — the scrub list deliberately includes the very variables the
    // sandbox uses, so the order matters.
    for var in support::SENSITIVE_ENV_VARS {
        command.env_remove(var);
    }
    command
        .env("CODA_HOME", sandbox.home.path())
        .env("CODA_SERVE_API_KEY", "sk-fixture-not-a-real-key")
        .env("CODA_SERVE_ENDPOINT", endpoint)
        .env("CODA_SERVE_MODEL", engine_model)
        .env("CODA_SERVE_DISABLE_MCP", "1")
        .env("CODA_DISABLE_PROJECT_MCP", "1");
    command.output().await.expect("the coda binary runs")
}

#[tokio::test]
async fn a_provider_flag_leaves_the_model_to_the_engine_rather_than_a_saved_local_value() {
    // `--provider` selects an *account*, and the engine resolves the model for
    // whatever credential it actually connected — on its own host, from its
    // own settings. A front-end that resolved a model itself and pushed it
    // with `session/setModel` overrode an engine-owned decision with a value
    // read from this machine, which for a remote or custom engine is simply
    // somebody else's file.
    //
    // The discriminator is real: the engine was started with one model, this
    // machine's settings name a different one for the same provider, and the
    // provider fixture records which one the request actually carried.
    // Several scripted turns: a run may legitimately make more than one
    // provider request, and every one of them must carry the engine's model.
    let provider = scripted_provider(vec![
        text_turn("done"),
        text_turn("done"),
        text_turn("done"),
        text_turn("done"),
    ])
    .await;
    let sandbox = Sandbox::new();
    // `CODA_HOME` names the *home*; the profile lives in `<home>/.coda`.
    let profile = sandbox.home.path().join(".coda");
    std::fs::create_dir_all(&profile).expect("profile dir");
    std::fs::write(
        profile.join("settings.json"),
        json!({
            "defaultProvider": "anthropic",
            "modelByProvider": { "anthropic": "claude-3-5-haiku-latest" },
        })
        .to_string(),
    )
    .expect("seed the front-end's settings");

    let output = run_headless(
        &sandbox,
        &provider.endpoint,
        "claude-sonnet-4-5",
        &["-p", "say hello", "--provider", "anthropic"],
    )
    .await;
    assert!(
        output.status.success(),
        "the run failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );

    let requests: Vec<String> = provider
        .requests()
        .into_iter()
        .filter(|body| body.contains("\"model\""))
        .collect();
    assert!(!requests.is_empty(), "the engine never sent a model request to the provider");
    for body in &requests {
        // Only the model field is reported on failure: the whole request body
        // is the entire tool catalogue, and drowning the reason in it helps
        // nobody.
        let model = body.split('"').nth(3).unwrap_or("<none>");
        assert_eq!(
            model, "claude-sonnet-4-5",
            "the model the request carried was not the engine's own"
        );
        assert!(
            !body.contains("claude-3-5-haiku-latest"),
            "the front-end pushed a locally saved model ({model}) over the engine's"
        );
    }
}

#[test]
fn neither_front_end_resolves_an_engine_owned_model_itself() {
    // The guard for the paths a headless run cannot reach: the interactive
    // launch does the same thing, and there is no terminal in a test to drive
    // it. `resolve_for_provider` is the engine's own startup resolution; a
    // front-end calling it is by construction reading the wrong machine's
    // settings whenever the engine is not this process's child.
    let root = std::fs::read_to_string(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src/main.rs"),
    )
    .expect("read coda/src/main.rs");

    let offenders: Vec<(usize, &str)> = root
        .lines()
        .enumerate()
        .filter(|(_, line)| line.contains("settings::resolve_for_provider"))
        .map(|(index, line)| (index + 1, line.trim()))
        .collect();

    assert!(
        offenders.is_empty(),
        "the front-end resolves an engine-owned model itself:\n{offenders:?}\n\
         The engine resolves the model for the provider it connected; forward \
         the provider and let it answer."
    );
}

#[tokio::test]
async fn the_standalone_front_end_treats_a_named_engine_as_someone_elses_machine() {
    // The real launch path, not the helper it is built from: the standalone
    // binary asks the library to connect, and the library decides the
    // contract from the flags. Hard-coding `TrustedLocal` in that path let a
    // `--engine`/`CODA_ENGINE` session edit MCP, plugin and settings files the
    // engine it was talking to never reads.
    use coda_tui::cli::Cli;
    use coda_tui::local::AccessMode;
    use clap::Parser;

    let provider = scripted_provider(vec![text_turn("unused")]).await;
    let sandbox = Sandbox::new();
    let engine = support::coda_exe();
    let cli = Cli::try_parse_from([
        "coda-tui",
        "--engine",
        engine.to_str().expect("engine path"),
        "-C",
        sandbox.cwd.path().to_str().expect("cwd"),
    ])
    .expect("parse");
    assert_eq!(cli.access_mode(), AccessMode::ApiOnly, "the flag decides the contract");

    let (app, engine_process, _inbound) = coda_tui::startup::connect_from_cli(
        &cli,
        coda_render::Theme::default(),
        |command| {
            let mut command = command
                .arg("--no-mcp")
                .arg("--api-key")
                .arg("sk-fixture-not-a-real-key")
                .arg("--endpoint")
                .arg(&provider.endpoint)
                .env("CODA_HOME", sandbox.home.path());
            for var in support::SENSITIVE_ENV_VARS {
                command = command.env_remove(*var);
            }
            command
        },
    )
    .await
    .expect("the engine starts and the handshake completes");

    assert_eq!(
        app.access_mode(),
        AccessMode::ApiOnly,
        "the connected app claimed local trust for an engine it was pointed at"
    );
    assert!(!app.access_mode().allows_local_maintenance());

    let _ = engine_process.shutdown(Duration::from_secs(5)).await;
}

// ---------------------------------------------------------------------------
// The launch preflight: what happens before an engine is started at all
// ---------------------------------------------------------------------------
//
// These drive the seam both binaries run through — `preflight::decide` and
// `preflight::prepare_launch_with` — with the terminal supplied by the test
// instead of by the console, and with a counted credential port. The engine is
// spawned by the test *after* the preflight returns, exactly as `main` does,
// so "the wizard came before the child" is observable rather than asserted
// about a comment.

#[path = "support/copilot_fixture.rs"]
mod copilot_fixture;

use coda_auth::service::ProviderIdentity;
use coda_tui::local::auth::AuthPort;
use coda_tui::local::AccessMode;
use coda_tui::preflight::{
    self, Launch, LaunchIntent, Need, ScriptedInput, SetupOptions, SetupOutcome,
};

/// A launch that never names an engine of its own, and never exports one.
fn no_environment(_key: &str) -> Option<String> {
    None
}

#[tokio::test]
async fn an_api_only_launch_starts_its_engine_without_reading_this_machines_credentials() {
    // The engine is somebody else's: this machine's keychain is not the one it
    // uses, so the launch must not open it — not to decide, not to report, not
    // at all — and must go straight to the engine it was pointed at.
    let provider = scripted_provider(vec![text_turn("preflight kept out of the way")]).await;
    let sandbox = Sandbox::new();
    let command = serve_command(&sandbox, &provider.endpoint);

    let port = AuthPort::from_factory(|_| {
        Box::pin(async {
            panic!("an API-only launch opened this machine's credential store");
        })
    });
    let intent =
        LaunchIntent::from_launch(AccessMode::ApiOnly, Some("github-copilot"), &[], no_environment);

    let launch = preflight::prepare_launch_with(command, &intent, &port, |_need| async {
        panic!("an API-only launch offered a setup screen")
    })
    .await;

    let command = match launch {
        Launch::Proceed { command, notes, .. } => {
            assert!(notes.is_empty(), "a healthy remote start was not quiet: {notes:?}");
            command
        }
        Launch::Abandoned(message) => panic!("an API-only launch was abandoned: {message}"),
    };
    assert_eq!(port.opens(), 0, "the credential store was opened for a foreign engine");

    // And the engine it was pointed at still starts and serves a turn.
    let booted = api::boot::boot(command, &SessionIntent::New, "coda-tui")
        .await
        .expect("the engine starts");
    let mut inbound = booted.inbound;
    run_turn(&booted.connection, &mut inbound, "hello").await;
    assert_eq!(provider.request_count(), 1, "the engine never reached its provider");
    let _ = booted.engine.shutdown(Duration::from_secs(5)).await;
}

#[tokio::test]
async fn a_launch_whose_provider_has_no_credential_is_offered_setup_before_any_child_is_spawned() {
    // The failure this closes: the engine was spawned first, failed closed on
    // the provider it was told to use, and took the session with it — so the
    // screen that could have fixed it was never reachable.
    let sandbox = Sandbox::new();
    let marker = sandbox.cwd.path().join("engine-was-spawned.marker");
    let spawner = coda_client::EngineCommand::new("powershell.exe")
        .arg("-NoProfile")
        .arg("-NonInteractive")
        .arg("-Command")
        .arg(format!("New-Item -ItemType File -Force -Path '{}' | Out-Null", marker.display()))
        .working_dir(sandbox.cwd.path());

    let port = AuthPort::isolated(sandbox.home.path(), Vec::new());
    let intent = LaunchIntent::from_launch(
        AccessMode::TrustedLocal,
        Some("github-copilot"),
        &[],
        no_environment,
    );

    let offered = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
    let seen = std::sync::Arc::clone(&offered);
    let launch = preflight::prepare_launch_with(spawner.clone(), &intent, &port, |need| async move {
        assert_eq!(
            need,
            Need::SelectedMissing {
                identity: ProviderIdentity::GithubCopilot,
                required: true,
            }
        );
        seen.store(true, std::sync::atomic::Ordering::SeqCst);
        SetupOutcome::Cancelled
    })
    .await;

    assert!(offered.load(std::sync::atomic::Ordering::SeqCst), "no setup was offered");
    match launch {
        Launch::Abandoned(message) => {
            assert!(message.contains("Nothing was saved"), "{message}");
            assert!(message.contains("no engine was started"), "{message}");
        }
        Launch::Proceed { .. } => panic!("a cancelled setup started an engine anyway"),
    }
    assert!(
        !marker.exists(),
        "a child was spawned for a launch whose account was never connected"
    );

    // The seam's own condition: this launch *required* GitHub Copilot, so a
    // setup that comes back with a different account has not honoured it.
    // The account may well be connected — this function cannot un-connect one
    // — but no engine is started on it, and the message says exactly that
    // rather than pretending nothing happened.
    let mismatched = preflight::prepare_launch_with(spawner.clone(), &intent, &port, |_need| async {
        SetupOutcome::Connected {
            identity: ProviderIdentity::AnthropicApiKey,
            deployment: None,
            notes: vec!["connected".to_owned()],
        }
    })
    .await;
    match mismatched {
        Launch::Abandoned(message) => {
            assert!(message.contains("different provider"), "{message}");
            assert!(message.contains("no engine was started"), "{message}");
        }
        Launch::Proceed { command, .. } => panic!(
            "a setup that connected the wrong account was cleared to start an engine: {:?}",
            command.env
        ),
    }
    assert!(
        !marker.exists(),
        "a child was spawned for an account this launch never asked for"
    );

    // The other half of the same seam: a launch that named no account of its
    // own takes whatever the setup connected, and hands the launcher a
    // command it then spawns.
    let open_intent =
        LaunchIntent::from_launch(AccessMode::TrustedLocal, None, &[], no_environment);
    let connected = preflight::prepare_launch_with(spawner, &open_intent, &port, |_need| async {
        SetupOutcome::Connected {
            identity: ProviderIdentity::AnthropicApiKey,
            deployment: None,
            notes: vec!["connected".to_owned()],
        }
    })
    .await;
    let command = match connected {
        Launch::Proceed { command, .. } => command,
        Launch::Abandoned(message) => panic!("a completed setup started nothing: {message}"),
    };
    assert!(
        command.env.iter().any(|(key, value)| key == "CODA_SERVE_PROVIDER" && value == "anthropic"),
        "the rebuilt launch does not name the account the setup connected"
    );
    let (child, _inbound) = coda_client::Engine::spawn(command).expect("the child starts");
    for _ in 0..200 {
        if marker.exists() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    assert!(marker.exists(), "the launch never spawned the engine it was cleared to start");
    let _ = child.shutdown(Duration::from_secs(5)).await;
}

/// The whole launcher path for a first run that chooses GitHub Copilot: the
/// setup screen signs in against a loopback stand-in for GitHub, the launch it
/// rebuilds starts a **fresh managed engine**, and that engine answers the
/// public API using the credential the screen just stored.
///
/// Nothing here contacts GitHub, opens a browser or touches a real profile:
/// every endpoint is a socket this test owns, reached because the shipping
/// `GH_COPILOT_*` overrides point at it, and the wizard is driven with the
/// browser launcher explicitly off.
#[tokio::test(flavor = "multi_thread")]
async fn the_setup_screen_connects_copilot_and_the_launch_it_rebuilds_reaches_that_engine() {
    use copilot_fixture::{
        open_engine, run_turn as copilot_turn, session_models, DeviceOutcome, FakeCopilotHost,
        Tokens, ADVERTISED_MODELS, MESSAGES_MODEL,
    };
    use ratatui::backend::TestBackend;
    use ratatui::Terminal;

    let tokens = Tokens {
        github: "ghu_fixture-durable-tui".to_owned(),
        copilot: "fixture-copilot-token-tui".to_owned(),
        api_key: "sk-ant-fixture-tui-0123456789".to_owned(),
    };
    let host = FakeCopilotHost::start(tokens.clone(), DeviceOutcome::Authorize);
    let sandbox = copilot_fixture::Sandbox::new();

    // The wizard's profile is the one the engine will read: an isolated
    // profile rooted at the sandbox home is exactly what a child with
    // `CODA_HOME` pointing there resolves.
    let environment: Vec<(String, String)> =
        host.overrides().into_iter().map(|(key, value)| (key.to_owned(), value)).collect();
    let port = AuthPort::isolated(sandbox.home(), environment);

    let mut terminal = Terminal::new(TestBackend::new(110, 44)).expect("terminal");
    // The form opens on GitHub Copilot with "public github.com" selected;
    // Enter accepts it. The device grant is authorized by the fixture, so no
    // person and no browser is involved.
    let mut input = ScriptedInput::keys(&[crossterm::event::KeyCode::Enter]);
    let outcome = preflight::run_setup(
        &mut terminal,
        &mut input,
        &coda_render::Theme::default(),
        &port,
        &Need::SelectedMissing { identity: ProviderIdentity::GithubCopilot, required: true },
        &SetupOptions::without_browser(),
    )
    .await;

    match &outcome {
        SetupOutcome::Connected { identity, notes, .. } => {
            assert_eq!(*identity, ProviderIdentity::GithubCopilot);
            let said = notes.join("\n");
            assert!(said.contains("Connected account: GitHub Copilot"), "{said}");
            // Hosts, never URLs, and never a token.
            assert!(!said.contains(&tokens.github), "a durable token reached the report");
            assert!(!said.contains(&tokens.copilot), "a Copilot token reached the report");
        }
        other => panic!("the setup screen did not connect an account: {other:?}"),
    }
    assert!(
        sandbox.credential_path("github-copilot").exists(),
        "the credential is not where the engine reads it"
    );
    assert!(
        host.requests_for(copilot_fixture::DEVICE_CODE_PATH).len() >= 1,
        "the device flow never reached the host the screen disclosed:\n{}",
        host.wire_summary()
    );

    // The launch the preflight rebuilds, started for real. `--model` is
    // re-applied here for the same reason `coda` applies it after boot: the
    // rebuild drops the *previous* account's instructions, and the model this
    // test wants is its own.
    let command = copilot_fixture::serve_command(&support::coda_exe(), &sandbox, &host.overrides(), &[]);
    let command = preflight::apply(command, &outcome).arg("--model").arg(MESSAGES_MODEL);
    assert!(
        command
            .env
            .iter()
            .any(|(key, value)| key == "CODA_SERVE_PROVIDER" && value == "github-copilot"),
        "the rebuilt launch does not name the account that was connected"
    );

    let before = host.request_count();
    let (engine, mut inbound) = open_engine(command).await;
    let models = session_models(&engine.connection()).await;
    let ids: Vec<String> = models["models"]
        .as_array()
        .expect("models is an array")
        .iter()
        .map(|model| model["id"].as_str().expect("a model id").to_owned())
        .collect();
    assert_eq!(ids.len(), ADVERTISED_MODELS, "the engine did not read the fixture's catalogue: {ids:?}");

    copilot_turn(&engine.connection(), &mut inbound, "two plus two").await;

    let served = host.requests();
    let engine_requests = &served[before..];
    let turns: Vec<_> =
        engine_requests.iter().filter(|request| request.path == "/v1/messages").collect();
    assert_eq!(
        turns.len(),
        1,
        "the fresh engine did not run the turn at the host the account belongs to:\n{}",
        host.wire_summary()
    );
    assert_eq!(
        turns[0].authorization(),
        format!("Bearer {}", tokens.copilot),
        "the turn was not authenticated by the credential the setup screen stored"
    );

    let _ = engine.shutdown(Duration::from_secs(5)).await;
}

