//! Real stdin-EOF conformance: a controller that **disappears** mid-question
//! must never become an answer.
//!
//! This is the fault the Stage D notes previously called impossible to
//! reproduce out of process. It is not: the obstacle was only `coda-client`'s
//! `Responder`, which necessarily holds the write channel open. Driving the
//! compiled `coda-engine` binary through a raw `tokio::process::Command` with
//! piped stdio, using the *public* framing primitives (`encode_frame` /
//! `FrameDecoder`) instead of the client crate, makes a genuine half-close
//! trivial: drop the child's `stdin` and the engine's read loop observes a
//! real `Ok(0)`.
//!
//! What that proves, and nothing else does:
//!
//! - `read_loop` reaches `fail_all_pending()` on EOF (it runs synchronously
//!   before the transport returns), so the outstanding `request/question`
//!   takes its fail-closed default;
//! - the model loop is **not** continued on a fabricated answer — the scripted
//!   provider serves exactly one turn and records every request it was asked
//!   for, so a second one would be visible;
//! - the process then exits on its own, bounded, rather than hanging on a
//!   human who is no longer there.
//!
//! The environment is the same hermetic sandbox the other conformance suites
//! use: a temp `CODA_HOME`, a temp working directory, no MCP, no `PATH`
//! lookup, no real credential and no real provider.

mod support;

use std::process::Stdio;
use std::time::Duration;

use coda_proto::{encode_frame, FrameDecoder};
use serde_json::{json, Value};
use support::*;
use tokio::io::{AsyncReadExt, AsyncWriteExt};

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

/// The same hermetic child the `coda-client` suites get, built as a raw
/// `tokio::process::Command` so the test owns the pipes directly.
fn raw_engine_command(
    sandbox: &Sandbox,
    provider: &ScriptedProvider,
) -> tokio::process::Command {
    let mut command = tokio::process::Command::new(coda_engine_exe());
    command
        .current_dir(sandbox.cwd.path())
        .env("CODA_HOME", sandbox.home.path())
        .arg("--no-mcp")
        .arg("--api-key")
        .arg("fake-test-key")
        .arg("--endpoint")
        .arg(&provider.endpoint)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .kill_on_drop(true);
    for var in SENSITIVE_ENV_VARS {
        command.env_remove(var);
    }
    for (key, _) in std::env::vars_os() {
        let name = key.to_string_lossy();
        if name.starts_with("CODA_SERVE_") || name.starts_with("CODA_DIAG_") {
            command.env_remove(key);
        }
    }
    command
}

fn frame(id: Option<i64>, method: &str, params: Value) -> Vec<u8> {
    let body = match id {
        Some(id) => json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params }),
        None => json!({ "jsonrpc": "2.0", "method": method, "params": params }),
    };
    encode_frame(&serde_json::to_vec(&body).expect("serialisable"))
}

/// Reads framed messages off the child's stdout until `predicate` matches,
/// returning the matching message plus everything seen along the way.
async fn read_until(
    stdout: &mut tokio::process::ChildStdout,
    decoder: &mut FrameDecoder,
    seen: &mut Vec<Value>,
    predicate: impl Fn(&Value) -> bool,
) -> Value {
    let mut buf = vec![0u8; 16 * 1024];
    loop {
        // Anything already buffered first: one read can carry many frames.
        while let Some(bytes) = decoder.next_frame().expect("the engine must stay framed") {
            let message: Value = serde_json::from_slice(&bytes).expect("valid JSON-RPC");
            seen.push(message.clone());
            if predicate(&message) {
                return message;
            }
        }
        let read = tokio::time::timeout(Duration::from_secs(30), stdout.read(&mut buf))
            .await
            .expect("the engine must not go silent")
            .expect("stdout must be readable");
        assert_ne!(read, 0, "the engine closed stdout before the expected message arrived");
        decoder.feed(&buf[..read]);
    }
}

/// Drains whatever the engine still manages to write after the half-close,
/// until stdout closes. Bounded, and never required to contain anything.
async fn drain_remaining(
    stdout: &mut tokio::process::ChildStdout,
    decoder: &mut FrameDecoder,
    seen: &mut Vec<Value>,
) {
    let mut buf = vec![0u8; 16 * 1024];
    loop {
        while let Ok(Some(bytes)) = decoder.next_frame() {
            if let Ok(message) = serde_json::from_slice::<Value>(&bytes) {
                seen.push(message);
            }
        }
        match tokio::time::timeout(Duration::from_secs(10), stdout.read(&mut buf)).await {
            Ok(Ok(0)) | Ok(Err(_)) | Err(_) => return,
            Ok(Ok(n)) => decoder.feed(&buf[..n]),
        }
    }
}

/// SECURITY: a genuine stdin EOF while a `request/question` is outstanding
/// must fail the request closed — never `User answered: Delete`, never an
/// empty success — and must not drive a follow-up model request.
#[tokio::test]
async fn a_real_stdin_eof_never_answers_an_outstanding_question() {
    let sandbox = Sandbox::new();
    // Exactly one scripted turn. A follow-up request would be request #2.
    let provider = scripted_provider(vec![ask_delete_or_keep()]).await;

    let mut child = raw_engine_command(&sandbox, &provider).spawn().expect("engine spawns");
    let mut stdin = child.stdin.take().expect("piped stdin");
    let mut stdout = child.stdout.take().expect("piped stdout");
    let mut decoder = FrameDecoder::new();
    let mut seen: Vec<Value> = Vec::new();

    // ── initialize ───────────────────────────────────────────────────────
    stdin
        .write_all(&frame(
            Some(1),
            "initialize",
            json!({
                "protocolVersion": "1",
                "clientInfo": "eof-conformance",
                "clientCapabilities": { "stateEvents": true }
            }),
        ))
        .await
        .expect("initialize is written");
    stdin.flush().await.expect("flush");
    let init = read_until(&mut stdout, &mut decoder, &mut seen, |m| m["id"] == json!(1)).await;
    assert!(init.get("error").is_none(), "initialize must succeed hermetically: {init}");

    // ── prompt: drives the scripted ask_user_question tool call ──────────
    stdin
        .write_all(&frame(Some(2), "session/prompt", json!({ "text": "tidy up" })))
        .await
        .expect("prompt is written");
    stdin.flush().await.expect("flush");

    // The arrival of the reverse request is the barrier — never a sleep.
    let question = read_until(&mut stdout, &mut decoder, &mut seen, |m| {
        m["method"] == json!("request/question")
    })
    .await;
    assert_eq!(question["params"]["question"], QUESTION);
    assert_eq!(question["params"]["options"][0], DELETE, "Delete really is the first option");
    assert_eq!(provider.request_count(), 1, "one turn has been served so far");

    // ── The fault: the controller disappears. Not a cancel, not an error
    //    reply — the pipe simply closes. ───────────────────────────────────
    drop(stdin);

    let status = tokio::time::timeout(Duration::from_secs(60), async {
        drain_remaining(&mut stdout, &mut decoder, &mut seen).await;
        child.wait().await
    })
    .await
    .expect("the engine must exit on EOF rather than waiting for a human who is gone")
    .expect("the child is waitable");
    assert!(status.success() || status.code().is_some(), "a bounded, ordinary exit: {status:?}");

    // ── The whole point ──────────────────────────────────────────────────
    assert_eq!(
        provider.request_count(),
        1,
        "SECURITY: a lost connection must never continue the model loop on a fabricated answer; \
         requests served: {:?}",
        provider.requests()
    );
    for body in provider.requests() {
        assert!(
            !body.contains("User answered"),
            "SECURITY: no provider request may claim the operator answered: {body}"
        );
    }

    // Nothing the engine wrote may look like an answer either.
    let transcript = serde_json::to_string(&seen).expect("serialisable");
    assert!(
        !transcript.contains("User answered"),
        "SECURITY: the engine must not report an answer nobody gave: {transcript}"
    );

    // `read_loop` reaches `fail_all_pending()` synchronously on EOF, before
    // the transport returns and the writer task is torn down, so in practice
    // the terminal outcome is published too. Whether that last frame wins the
    // flush against the writer's shutdown is a scheduling race, so it is not
    // *required* here — but it can only ever say one thing, and that is
    // asserted. The deterministic proof that the EOF branch really resolves
    // the request lives next to the code, in
    // `coda_serve::transport::tests::a_stdin_eof_fails_every_outstanding_reverse_request_closed`,
    // which drives the same `read_loop` against a closed pipe.
    for message in &seen {
        if message["method"] == json!("event/requestResolved") {
            assert_eq!(
                message["params"]["outcome"], "noAnswer.disconnected",
                "a lost connection is a typed no-answer — never a choice: {message}"
            );
            assert_eq!(message["params"]["kind"], "question");
            assert_eq!(
                message["params"]["requests"].as_array().map(Vec::len),
                Some(0),
                "and nothing is left outstanding"
            );
        }
    }
    assert!(
        seen.iter().any(|m| m["method"] == json!("event/requestPending")),
        "the request really did go outstanding before the disconnect: {:?}",
        seen.iter().filter_map(|m| m["method"].as_str()).collect::<Vec<_>>()
    );
}

/// The control: with the pipe intact, a real answer still completes the turn.
/// Without this, the test above would be satisfied by an engine that simply
/// never answers anything.
#[tokio::test]
async fn the_same_setup_still_completes_a_turn_when_the_controller_answers() {
    let sandbox = Sandbox::new();
    let provider =
        scripted_provider(vec![ask_delete_or_keep(), text_turn("Understood — keeping it.")]).await;

    let mut child = raw_engine_command(&sandbox, &provider).spawn().expect("engine spawns");
    let mut stdin = child.stdin.take().expect("piped stdin");
    let mut stdout = child.stdout.take().expect("piped stdout");
    let mut decoder = FrameDecoder::new();
    let mut seen: Vec<Value> = Vec::new();

    stdin
        .write_all(&frame(Some(1), "initialize", json!({ "protocolVersion": "1" })))
        .await
        .expect("write");
    stdin.flush().await.expect("flush");
    read_until(&mut stdout, &mut decoder, &mut seen, |m| m["id"] == json!(1)).await;

    stdin
        .write_all(&frame(Some(2), "session/prompt", json!({ "text": "tidy up" })))
        .await
        .expect("write");
    stdin.flush().await.expect("flush");

    let question = read_until(&mut stdout, &mut decoder, &mut seen, |m| {
        m["method"] == json!("request/question")
    })
    .await;
    let id = question["id"].clone();

    let reply = json!({ "jsonrpc": "2.0", "id": id, "result": { "answer": KEEP } });
    stdin
        .write_all(&encode_frame(&serde_json::to_vec(&reply).unwrap()))
        .await
        .expect("write");
    stdin.flush().await.expect("flush");

    let prompt_result =
        read_until(&mut stdout, &mut decoder, &mut seen, |m| m["id"] == json!(2)).await;
    assert_eq!(prompt_result["result"]["ok"], true, "a real answer completes the turn: {prompt_result}");
    assert_eq!(
        provider.request_count(),
        2,
        "a real answer *does* drive the follow-up request the EOF path must not"
    );
    assert!(
        provider.requests()[1].contains("User answered: Keep"),
        "the operator's actual choice reaches the model: {}",
        provider.requests()[1]
    );

    drop(stdin);
    let _ = tokio::time::timeout(Duration::from_secs(30), child.wait()).await;
}
