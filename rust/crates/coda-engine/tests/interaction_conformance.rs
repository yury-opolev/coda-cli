//! Real out-of-process conformance for Stage D's interaction surface:
//! reverse-request discovery, out-of-band resolution, and — the property this
//! stage exists for — **an unanswered question never becomes an answer**.
//!
//! Everything here drives the actual compiled `coda-engine` binary over real
//! stdio, against a real streaming SSE provider fixture that records every
//! request it serves. There is no `PATH` lookup, no silent skip, no real
//! provider and no real credential.
//!
//! The scripted turn is the exact one the plan requires: a genuine
//! `ask_user_question` **ToolUse** with options `["Delete", "Keep"]`.

mod support;

use std::time::Duration;

use coda_client::{Connection, Engine, Inbound};
use coda_proto::messages::{method, ClientCapabilities, InitializeParams, PromptParams};
use serde_json::json;
use support::*;

const QUESTION: &str = "Delete the production database?";
const DELETE: &str = "Delete";
const KEEP: &str = "Keep";

/// The one scripted turn: the model asks the operator to choose.
fn ask_delete_or_keep() -> String {
    tool_use_turn(
        "toolu_q1",
        "ask_user_question",
        json!({ "question": QUESTION, "options": [DELETE, KEEP] }),
    )
}

async fn start_engine(
    sandbox: &Sandbox,
    provider: &ScriptedProvider,
) -> (Engine, tokio::sync::mpsc::UnboundedReceiver<Inbound>, String) {
    let command = base_engine_command(&coda_engine_exe(), sandbox)
        .arg("--no-mcp")
        .arg("--api-key")
        .arg("fake-test-key")
        .arg("--endpoint")
        .arg(&provider.endpoint);
    let (engine, inbound) = Engine::spawn(command).expect("engine spawns");

    let init_params = InitializeParams::new("interaction-conformance")
        .with_client_capabilities(ClientCapabilities {
            state_events: Some(true),
            ..Default::default()
        });
    let init = tokio::time::timeout(
        Duration::from_secs(20),
        engine
            .connection()
            .request(method::INITIALIZE, Some(serde_json::to_value(init_params).unwrap())),
    )
    .await
    .expect("initialize must not hang")
    .expect("initialize succeeds hermetically");

    let instance = init["engineInstanceId"].as_str().expect("engineInstanceId").to_string();
    assert_eq!(
        init["capabilities"]["requests.discovery"]["supported"], true,
        "the registry is wired, so the capability must be advertised"
    );
    assert_eq!(init["capabilities"]["requests.outOfBandResolve"]["supported"], true);
    assert_eq!(init["capabilities"]["state.awaitingUserInput"]["supported"], true);
    (engine, inbound, instance)
}

/// Drains inbound until the engine issues the reverse `request/question`,
/// returning its JSON-RPC id. Never a sleep: the arrival of the request *is*
/// the barrier.
/// Drains inbound until the engine issues the reverse `request/question`,
/// returning its responder and params. Never a sleep: the arrival of the
/// request *is* the barrier.
async fn await_question_request(
    inbound: &mut tokio::sync::mpsc::UnboundedReceiver<Inbound>,
) -> (coda_client::Responder, serde_json::Value) {
    loop {
        match tokio::time::timeout(Duration::from_secs(30), inbound.recv())
            .await
            .expect("the engine must issue request/question")
            .expect("inbound must not close")
        {
            Inbound::Request { method, params, responder } => {
                assert_eq!(method, "request/question", "the only reverse request in this turn");
                return (responder, params.unwrap_or(serde_json::Value::Null));
            }
            Inbound::Notification { method, params } => {
                support::assert_state_event_payload(&method, &params.unwrap_or(serde_json::Value::Null));
                continue;
            }
        }
    }
}

async fn pending(connection: &Connection) -> Vec<serde_json::Value> {
    connection
        .request(method::GET_PENDING_REQUESTS, Some(json!({})))
        .await
        .expect("session/getPendingRequests must not error")["requests"]
        .as_array()
        .cloned()
        .unwrap_or_default()
}

async fn get_state(connection: &Connection) -> serde_json::Value {
    connection.request(method::GET_STATE, Some(json!({}))).await.expect("getState")
}

// ─────────────────────────────────────────────────────────────────────────────
// The core safety property
// ─────────────────────────────────────────────────────────────────────────────

/// SECURITY: when the controller **cancels** the question, the engine must
/// not fabricate an answer and must not issue a follow-up model request.
///
/// The provider script contains exactly one turn. If the engine were to
/// continue on `"User answered: Delete"`, a second request would arrive and
/// `request_count()` would be 2.
#[tokio::test]
async fn a_cancelled_question_selects_no_answer_and_drives_no_follow_up_model_request() {
    let sandbox = Sandbox::new();
    let provider = scripted_provider(vec![ask_delete_or_keep()]).await;
    let (engine, mut inbound, instance) = start_engine(&sandbox, &provider).await;
    let connection = engine.connection();

    let prompt = connection
        .send_request(method::PROMPT, Some(serde_json::to_value(PromptParams::text("tidy up")).unwrap()))
        .expect("prompt queues");

    // Held for the whole test: a live responder is a controller that has not
    // replied. Dropping it would send a cancellation of its own, which would
    // resolve the request through the *raw* path and defeat the point.
    let (_responder, params) = await_question_request(&mut inbound).await;
    assert_eq!(params["question"], QUESTION);
    assert_eq!(params["options"][0], DELETE, "Delete really is the first option");

    // ── Discovery: the request is visible before it is answered ──────────
    let outstanding = pending(&connection).await;
    assert_eq!(outstanding.len(), 1, "the pending request must be discoverable");
    let handle = outstanding[0]["requestId"].as_str().expect("requestId").to_string();
    assert!(handle.starts_with(&format!("req-{instance}-")), "handles bind to the engine instance");
    assert_eq!(outstanding[0]["kind"], "question");
    assert_eq!(outstanding[0]["failClosedDefault"], "noAnswer");
    let attributed_turn =
        outstanding[0]["turnId"].as_str().expect("attributed to the running turn").to_string();

    // ── The public phase says the engine is waiting on a human ───────────
    let waiting = get_state(&connection).await;
    assert_eq!(waiting["lifecycle"], "busy");
    // One turn identity, not two. The claim guard mints the `turnId` once and
    // it is what the diagnostics context, the state snapshot and the
    // execution scope that attributes reverse requests all carry — a request
    // stamped with a *different* id would mean something re-minted one.
    assert_eq!(
        waiting["turn"]["turnId"], attributed_turn,
        "the request must carry the running turn's own id, not an independently minted one: {}",
        waiting["turn"]
    );
    assert_eq!(
        waiting["turn"]["phase"], "awaitingUserInput",
        "a client must be able to see that the engine is blocked on the operator"
    );
    assert_eq!(waiting["requests"].as_array().map(Vec::len), Some(1));

    // ── Out-of-band cancel: applies the fail-closed default ──────────────
    let cancelled = connection
        .request(method::CANCEL_REQUEST, Some(json!({ "requestId": handle, "reason": "operator closed the prompt" })))
        .await
        .expect("cancelRequest must succeed");
    assert_eq!(cancelled["ok"], true);
    assert_eq!(cancelled["appliedDefault"], "noAnswer", "never `Delete`, never an empty answer");
    assert_eq!(cancelled["outcome"], "noAnswer.declined");

    let result = tokio::time::timeout(Duration::from_secs(30), prompt)
        .await
        .expect("the turn must not hang on an unanswered question")
        .expect("prompt channel must not be dropped")
        .expect("the RPC itself answers");

    // ── The whole point ──────────────────────────────────────────────────
    assert_eq!(
        provider.request_count(),
        1,
        "SECURITY: no follow-up model request may run on a fabricated answer"
    );
    assert_eq!(result["ok"], false, "the turn reports a typed failure, not success");

    // The recorded conversation must not claim the operator chose anything.
    let history = connection
        .request(method::GET_HISTORY, Some(json!({ "limit": 100 })))
        .await
        .expect("getHistory");
    let transcript = serde_json::to_string(&history).expect("serialisable");
    assert!(
        !transcript.contains("User answered"),
        "SECURITY: a cancelled question must never be recorded as an answer: {transcript}"
    );
    assert!(
        transcript.contains("not answered"),
        "the transcript must say plainly that no answer arrived: {transcript}"
    );

    // The engine is available again, and nothing is left pending.
    assert!(pending(&connection).await.is_empty());
    let idle = get_state(&connection).await;
    assert_eq!(idle["lifecycle"], "ready");
    assert!(idle["requests"].as_array().map(Vec::is_empty).unwrap_or(false));
    assert!(
        idle["lastTurnOutcome"]["error"]["category"]
            .as_str()
            .is_some_and(|c| c.starts_with("agent.aborted.question.noAnswer")),
        "the terminal outcome names the typed abort: {}",
        idle["lastTurnOutcome"]
    );
}

/// SECURITY: a *malformed* reply to `request/question` — a well-formed
/// JSON-RPC response with no usable answer — must take the same path.
#[tokio::test]
async fn a_malformed_reply_selects_no_answer_and_drives_no_follow_up_model_request() {
    let sandbox = Sandbox::new();
    let provider = scripted_provider(vec![ask_delete_or_keep()]).await;
    let (engine, mut inbound, _instance) = start_engine(&sandbox, &provider).await;
    let connection = engine.connection();

    let prompt = connection
        .send_request(method::PROMPT, Some(serde_json::to_value(PromptParams::text("tidy up")).unwrap()))
        .expect("prompt queues");

    let (responder, _params) = await_question_request(&mut inbound).await;
    // A reply with no `answer` field at all.
    responder.respond(json!({ "acknowledged": true }));

    let result = tokio::time::timeout(Duration::from_secs(30), prompt)
        .await
        .expect("must not hang")
        .expect("channel")
        .expect("rpc answers");

    assert_eq!(
        provider.request_count(),
        1,
        "SECURITY: a malformed reply must not become an answer the loop continues on"
    );
    assert_eq!(result["ok"], false);
    let state = get_state(&connection).await;
    assert!(
        state["lastTurnOutcome"]["error"]["category"]
            .as_str()
            .is_some_and(|c| c.contains("malformed")),
        "the typed reason must survive to the outcome: {}",
        state["lastTurnOutcome"]
    );
}

/// A *real* answer still works end to end: the tool result carries it and the
/// loop continues normally. Without this, the tests above could be satisfied
/// by an engine that simply never answers anything.
#[tokio::test]
async fn a_real_answer_is_delivered_and_the_loop_continues() {
    let sandbox = Sandbox::new();
    let provider =
        scripted_provider(vec![ask_delete_or_keep(), text_turn("Understood — keeping it.")]).await;
    let (engine, mut inbound, _instance) = start_engine(&sandbox, &provider).await;
    let connection = engine.connection();

    let prompt = connection
        .send_request(method::PROMPT, Some(serde_json::to_value(PromptParams::text("tidy up")).unwrap()))
        .expect("prompt queues");

    let (responder, _params) = await_question_request(&mut inbound).await;
    responder.respond(json!({ "answer": KEEP }));

    let result = tokio::time::timeout(Duration::from_secs(30), prompt)
        .await
        .expect("must not hang")
        .expect("channel")
        .expect("rpc answers");

    assert_eq!(result["ok"], true, "a real answer must let the run complete: {result}");
    assert_eq!(
        provider.request_count(),
        2,
        "a real answer *does* drive the follow-up request the fault path must not"
    );
    let follow_up = &provider.requests()[1];
    assert!(
        follow_up.contains("User answered: Keep"),
        "the operator's actual choice reaches the model: {follow_up}"
    );
    assert!(
        !follow_up.contains("User answered: Delete"),
        "and only their actual choice does: {follow_up}"
    );
}

// ─────────────────────────────────────────────────────────────────────────────
// Out-of-band resolution: exactly once, validated before consumption
// ─────────────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn out_of_band_resolution_is_exactly_once_and_kind_checked() {
    let sandbox = Sandbox::new();
    let provider =
        scripted_provider(vec![ask_delete_or_keep(), text_turn("Deleted nothing.")]).await;
    let (engine, mut inbound, instance) = start_engine(&sandbox, &provider).await;
    let connection = engine.connection();

    let prompt = connection
        .send_request(method::PROMPT, Some(serde_json::to_value(PromptParams::text("tidy up")).unwrap()))
        .expect("prompt queues");

    let (_responder, _params) = await_question_request(&mut inbound).await;
    let outstanding = pending(&connection).await;
    let handle = outstanding[0]["requestId"].as_str().expect("requestId").to_string();

    // ── Wrong kind: refused, and the request stays outstanding ───────────
    let wrong_kind = connection
        .request(
            method::RESOLVE_REQUEST,
            Some(json!({ "requestId": handle, "outcome": { "allow": true } })),
        )
        .await;
    assert!(wrong_kind.is_err(), "a permission outcome must not resolve a question");
    assert_eq!(pending(&connection).await.len(), 1, "a refused payload must not consume the entry");

    // ── Explicitly declared wrong kind: same ─────────────────────────────
    assert!(
        connection
            .request(
                method::RESOLVE_REQUEST,
                Some(json!({ "requestId": handle, "outcome": { "kind": "permission", "allow": true } })),
            )
            .await
            .is_err(),
        "a declared kind mismatch is refused"
    );
    assert_eq!(pending(&connection).await.len(), 1);

    // ── Malformed: refused, still outstanding ────────────────────────────
    assert!(
        connection
            .request(method::RESOLVE_REQUEST, Some(json!({ "requestId": handle, "outcome": { "answer": "" } })))
            .await
            .is_err(),
        "an empty answer is not a decision"
    );
    assert_eq!(pending(&connection).await.len(), 1);

    // ── A handle from a different engine instance: refused ───────────────
    let stale = handle.replace(&instance, "00000000-0000-4000-8000-000000000000");
    assert_ne!(stale, handle, "the substitution must actually change the handle");
    assert!(
        connection
            .request(method::RESOLVE_REQUEST, Some(json!({ "requestId": stale, "outcome": { "answer": KEEP } })))
            .await
            .is_err(),
        "a handle minted by another engine process must never resolve this one"
    );
    assert_eq!(pending(&connection).await.len(), 1);

    // ── Unknown handle: refused ──────────────────────────────────────────
    assert!(
        connection
            .request(
                method::RESOLVE_REQUEST,
                Some(json!({ "requestId": format!("req-{instance}-9999"), "outcome": { "answer": KEEP } })),
            )
            .await
            .is_err()
    );

    // ── The one valid resolution wins ────────────────────────────────────
    let resolved = connection
        .request(method::RESOLVE_REQUEST, Some(json!({ "requestId": handle, "outcome": { "answer": KEEP } })))
        .await
        .expect("a well-formed, right-kind outcome resolves");
    assert_eq!(resolved["state"], "resolved");
    assert_eq!(resolved["outcome"], "answered");

    // ── A duplicate cannot resolve a second time ─────────────────────────
    assert!(
        connection
            .request(method::RESOLVE_REQUEST, Some(json!({ "requestId": handle, "outcome": { "answer": DELETE } })))
            .await
            .is_err(),
        "SECURITY: a replayed resolution must not overwrite the answer already delivered"
    );
    assert!(
        connection
            .request(method::CANCEL_REQUEST, Some(json!({ "requestId": handle })))
            .await
            .is_err(),
        "and cancelling an already-resolved request is refused too"
    );

    let result = tokio::time::timeout(Duration::from_secs(30), prompt)
        .await
        .expect("must not hang")
        .expect("channel")
        .expect("rpc answers");
    assert_eq!(result["ok"], true);
    assert!(
        provider.requests()[1].contains("User answered: Keep"),
        "the *first* resolution is the one that reached the model"
    );
    assert!(!provider.requests()[1].contains("User answered: Delete"));
}

/// SECURITY: the signal a controller sends when the operator **deliberately**
/// dismisses a question — a JSON-RPC error reply with `REQUEST_CANCELLED` —
/// must be classified as `declined` end to end, and must not fabricate an
/// answer or drive a follow-up model request.
///
/// This is the exact shape the TUI produces on Esc. It is deliberately kept
/// distinct from `malformed` (a controller that tried to answer and produced
/// nothing usable, e.g. `{"answer": ""}`) and from `disconnected` (no
/// controller at all): all three are fail-closed, but only `declined` says a
/// human made a decision, and that is what a client renders.
#[tokio::test]
async fn a_deliberately_cancelled_question_is_declined_end_to_end() {
    let sandbox = Sandbox::new();
    let provider = scripted_provider(vec![ask_delete_or_keep()]).await;
    let (engine, mut inbound, _instance) = start_engine(&sandbox, &provider).await;
    let connection = engine.connection();

    let prompt = connection
        .send_request(method::PROMPT, Some(serde_json::to_value(PromptParams::text("tidy up")).unwrap()))
        .expect("prompt queues");

    let (responder, _params) = await_question_request(&mut inbound).await;
    // Exactly what the TUI does when the operator presses Esc: an error
    // reply, never `{"answer": ""}`.
    responder.fail(coda_proto::jsonrpc::error_codes::REQUEST_CANCELLED, "request cancelled");

    let result = tokio::time::timeout(Duration::from_secs(30), prompt)
        .await
        .expect("must not hang")
        .expect("channel")
        .expect("rpc answers");

    assert_eq!(
        provider.request_count(),
        1,
        "SECURITY: a dismissed question must never continue the model loop"
    );
    assert_eq!(result["ok"], false);

    let state = get_state(&connection).await;
    let category = state["lastTurnOutcome"]["error"]["category"]
        .as_str()
        .unwrap_or_default()
        .to_string();
    assert_eq!(
        category, "agent.aborted.question.noAnswer.declined",
        "a deliberate cancellation must reach the client as `declined`, not `malformed` or \
         `disconnected`: {}",
        state["lastTurnOutcome"]
    );
    assert!(pending(&connection).await.is_empty(), "and nothing is left pending");
}

// ─────────────────────────────────────────────────────────────────────────────
// Abandoned request
// ─────────────────────────────────────────────────────────────────────────────

/// A controller that abandons the reverse request without answering — what
/// `coda-client` does when a UI surface is torn down mid-prompt — must fail
/// the request closed and drive no follow-up model request.
///
/// (The stdin-EOF variant of the same fault is covered for real, out of
/// process, in `tests/eof_conformance.rs`: a raw `tokio::process::Command`
/// with piped stdio and the public framing primitives can genuinely
/// half-close the engine's stdin, which a held `coda-client` `Responder`
/// cannot. The deterministic unit-level counterpart lives beside `read_loop`
/// in `coda-serve/src/transport.rs`.)
#[tokio::test]
async fn abandoning_the_reverse_request_never_produces_a_second_model_request() {
    let sandbox = Sandbox::new();
    let provider = scripted_provider(vec![ask_delete_or_keep()]).await;
    let (engine, mut inbound, _instance) = start_engine(&sandbox, &provider).await;
    let connection = engine.connection();

    let prompt = connection
        .send_request(method::PROMPT, Some(serde_json::to_value(PromptParams::text("tidy up")).unwrap()))
        .expect("prompt queues");
    let (responder, _params) = await_question_request(&mut inbound).await;
    assert_eq!(provider.request_count(), 1);
    assert_eq!(pending(&connection).await.len(), 1);

    // Abandon it. `Responder::drop` replies with a cancellation error, which
    // is a fault, not a choice.
    drop(responder);

    let result = tokio::time::timeout(Duration::from_secs(30), prompt)
        .await
        .expect("must not hang")
        .expect("channel")
        .expect("rpc answers");

    assert_eq!(
        provider.request_count(),
        1,
        "SECURITY: an abandoned question must never be read as an answer"
    );
    assert_eq!(result["ok"], false);
    assert!(pending(&connection).await.is_empty(), "and nothing is left pending");
}

// ─────────────────────────────────────────────────────────────────────────────
// Gate + discovery
// ─────────────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn state_dependent_interaction_methods_require_initialization() {
    let sandbox = Sandbox::new();
    let provider = scripted_provider(vec![]).await;
    let command = base_engine_command(&coda_engine_exe(), &sandbox)
        .arg("--no-mcp")
        .arg("--api-key")
        .arg("fake-test-key")
        .arg("--endpoint")
        .arg(&provider.endpoint);
    let (engine, _inbound) = Engine::spawn(command).expect("engine spawns");
    let connection = engine.connection();

    // Gated: state-dependent, and new in this contract.
    for (method_name, params) in [
        (method::GET_PENDING_REQUESTS, json!({})),
        (method::GET_HISTORY, json!({})),
        (method::RESOLVE_REQUEST, json!({ "requestId": "req-x-1", "outcome": {} })),
        (method::CANCEL_REQUEST, json!({ "requestId": "req-x-1" })),
        (method::CONFIG_SET, json!({ "key": "model", "value": "m" })),
    ] {
        let err = tokio::time::timeout(
            Duration::from_secs(15),
            connection.request(method_name, Some(params)),
        )
        .await
        .expect("must not hang")
        .expect_err("must be gated before initialize");
        assert!(
            err.to_string().contains("initialize"),
            "{method_name} must return a typed notInitialized: {err}"
        );
    }

    // Read-only discovery: valid before `initialize` by design, so a client
    // can choose a session (or inspect the engine) before starting one.
    for method_name in
        [method::LIST_SESSIONS, method::CONFIG_DESCRIBE, method::MCP_LIST, method::GET_STATE]
    {
        tokio::time::timeout(Duration::from_secs(15), connection.request(method_name, Some(json!({}))))
            .await
            .expect("must not hang")
            .unwrap_or_else(|e| panic!("{method_name} is read-only discovery and must work: {e}"));
    }
}
