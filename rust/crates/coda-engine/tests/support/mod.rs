//! Shared out-of-process conformance support: a real streaming SSE provider
//! fixture and a hermetic engine sandbox.
//!
//! This is deliberately a **real `TcpListener` speaking SSE**, not a one-shot
//! JSON responder pretending to be a stream. Phase transitions, mid-turn
//! snapshots and tool-use turns are only meaningful against a provider that
//! genuinely streams, and a one-shot responder would let a broken
//! implementation pass by accident.
//!
//! Every fixture serves a *scripted sequence* of turns and records how many
//! provider requests actually arrived — which is what lets a test assert the
//! strongest property in this stage: **no follow-up model request was made**.

#![allow(dead_code)]

use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use coda_client::EngineCommand;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

pub fn coda_engine_exe() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_coda-engine"))
}

/// The real publisher must emit the same payloads used to derive its schemas.
pub fn assert_state_event_payload(method: &str, params: &serde_json::Value) {
    use coda_proto::events::event_method;
    use coda_proto::state_events as wire;

    macro_rules! decode {
        ($payload:ty) => {{
            let event: wire::Sequenced<$payload> = serde_json::from_value(params.clone())
                .unwrap_or_else(|error| panic!("{method} violates its shared DTO: {error}"));
            assert!(event.seq >= 0, "{method}");
            assert!(!event.engine_instance_id.is_empty(), "{method}");
        }};
    }

    match method {
        event_method::ACTIVITY => decode!(wire::ActivityEvent),
        event_method::LIFECYCLE => decode!(wire::LifecycleEvent),
        event_method::STEERING_QUEUE => decode!(wire::SteeringQueueEvent),
        event_method::CONFIG_CHANGED => decode!(wire::ConfigChangedEvent),
        event_method::SESSION_CHANGED => decode!(wire::SessionChangedEvent),
        event_method::TURN_ENDED => decode!(wire::TurnEndedEvent),
        event_method::REQUEST_PENDING => decode!(wire::RequestPendingEvent),
        event_method::REQUEST_RESOLVED => decode!(wire::RequestResolvedEvent),
        event_method::EVENTS_DROPPED => decode!(wire::EventsDroppedEvent),
        _ => {}
    }
}

/// Environment that must not leak into a hermetic engine child.
pub const SENSITIVE_ENV_VARS: &[&str] = &[
    "ANTHROPIC_API_KEY",
    "GITHUB_TOKEN",
    "GH_TOKEN",
    "CODA_SERVE_API_KEY",
    "CODA_SERVE_ENDPOINT",
    "CODA_SERVE_PROVIDER",
    "CODA_ENGINE",
    "CODA_LOG",
];

pub struct Sandbox {
    pub home: tempfile::TempDir,
    pub cwd: tempfile::TempDir,
}

impl Sandbox {
    pub fn new() -> Self {
        Self {
            home: tempfile::tempdir().expect("temp CODA_HOME"),
            cwd: tempfile::tempdir().expect("temp cwd"),
        }
    }
}

pub fn base_engine_command(exe: &std::path::Path, sandbox: &Sandbox) -> EngineCommand {
    let mut command = EngineCommand::new(exe.as_os_str()).working_dir(sandbox.cwd.path());
    command = command.env("CODA_HOME", sandbox.home.path());
    for var in SENSITIVE_ENV_VARS {
        command = command.env_remove(*var);
    }
    for (key, _) in std::env::vars_os() {
        let name = key.to_string_lossy();
        if name.starts_with("CODA_SERVE_") || name.starts_with("CODA_DIAG_") {
            command = command.env_remove(key);
        }
    }
    command
}

// ─────────────────────────────────────────────────────────────────────────────
// SSE scripting
// ─────────────────────────────────────────────────────────────────────────────

pub fn sse_event(name: &str, data: serde_json::Value) -> String {
    format!("event: {name}\ndata: {data}\n\n")
}

pub fn message_start() -> String {
    sse_event(
        "message_start",
        serde_json::json!({ "type": "message_start", "message": { "usage": { "input_tokens": 10 } } }),
    )
}

pub fn text_turn(text: &str) -> String {
    message_start()
        + &sse_event(
            "content_block_start",
            serde_json::json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "text", "text": "" } }),
        )
        + &sse_event(
            "content_block_delta",
            serde_json::json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "text_delta", "text": text } }),
        )
        + &sse_event("content_block_stop", serde_json::json!({ "type": "content_block_stop", "index": 0 }))
        + &sse_event(
            "message_delta",
            serde_json::json!({ "type": "message_delta", "delta": { "stop_reason": "end_turn" }, "usage": { "output_tokens": 3 } }),
        )
        + &sse_event("message_stop", serde_json::json!({ "type": "message_stop" }))
}

/// A turn whose only content is one tool call.
pub fn tool_use_turn(call_id: &str, tool_name: &str, input: serde_json::Value) -> String {
    let partial = input.to_string();
    message_start()
        + &sse_event(
            "content_block_start",
            serde_json::json!({
                "type": "content_block_start", "index": 0,
                "content_block": { "type": "tool_use", "id": call_id, "name": tool_name }
            }),
        )
        + &sse_event(
            "content_block_delta",
            serde_json::json!({
                "type": "content_block_delta", "index": 0,
                "delta": { "type": "input_json_delta", "partial_json": partial }
            }),
        )
        + &sse_event("content_block_stop", serde_json::json!({ "type": "content_block_stop", "index": 0 }))
        + &sse_event(
            "message_delta",
            serde_json::json!({ "type": "message_delta", "delta": { "stop_reason": "tool_use" }, "usage": { "output_tokens": 5 } }),
        )
        + &sse_event("message_stop", serde_json::json!({ "type": "message_stop" }))
}

/// Reads one full HTTP request (headers + declared body) off `socket`.
///
/// Returns the request body, so a test can assert on what the engine actually
/// asked the provider — the only way to prove a *specific* follow-up request
/// did or did not happen.
pub async fn read_one_request(socket: &mut tokio::net::TcpStream) -> String {
    let mut request = Vec::new();
    loop {
        let mut chunk = [0u8; 8192];
        let count = match socket.read(&mut chunk).await {
            Ok(c) => c,
            Err(_) => return String::new(),
        };
        if count == 0 {
            return String::new();
        }
        request.extend_from_slice(&chunk[..count]);
        if let Some(header_end) = request.windows(4).position(|w| w == b"\r\n\r\n") {
            let headers = String::from_utf8_lossy(&request[..header_end]);
            let length = headers
                .lines()
                .find_map(|line| {
                    let (key, value) = line.split_once(':')?;
                    key.eq_ignore_ascii_case("content-length")
                        .then(|| value.trim().parse::<usize>().unwrap_or(0))
                })
                .unwrap_or(0);
            if request.len() >= header_end + 4 + length {
                return String::from_utf8_lossy(&request[header_end + 4..]).into_owned();
            }
        }
    }
}

/// A scripted streaming provider that records every request it served.
pub struct ScriptedProvider {
    pub endpoint: String,
    requests: Arc<Mutex<Vec<String>>>,
}

impl ScriptedProvider {
    /// The bodies of every provider request served so far.
    ///
    /// `len()` is the count a test asserts on to prove that an unanswered
    /// question produced **no** follow-up model request.
    pub fn requests(&self) -> Vec<String> {
        self.requests.lock().expect("requests poisoned").clone()
    }

    pub fn request_count(&self) -> usize {
        self.requests.lock().expect("requests poisoned").len()
    }
}

/// Serves `turns` in order, one per incoming connection, recording each
/// request body. A connection beyond the script gets a `429` rather than a
/// hang, so an unexpected extra request fails loudly instead of timing out.
pub async fn scripted_provider(turns: Vec<String>) -> ScriptedProvider {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();
    let requests = Arc::new(Mutex::new(Vec::new()));
    let recorder = Arc::clone(&requests);

    tokio::spawn(async move {
        let mut remaining = turns.into_iter();
        loop {
            let Ok((mut socket, _)) = listener.accept().await else { return };
            let body = read_one_request(&mut socket).await;
            recorder.lock().expect("requests poisoned").push(body);
            match remaining.next() {
                Some(turn) => {
                    let header = "HTTP/1.1 200 OK\r\ncontent-type: text/event-stream\r\nconnection: close\r\n\r\n";
                    let _ = socket.write_all(header.as_bytes()).await;
                    let _ = socket.write_all(turn.as_bytes()).await;
                    let _ = socket.flush().await;
                }
                None => {
                    let body = "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"script exhausted\"}}";
                    let response = format!(
                        "HTTP/1.1 400 Bad Request\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{body}",
                        body.len()
                    );
                    let _ = socket.write_all(response.as_bytes()).await;
                }
            }
            let _ = socket.shutdown().await;
        }
    });

    ScriptedProvider { endpoint: format!("http://127.0.0.1:{port}"), requests }
}
