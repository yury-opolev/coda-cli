//! Hermetic fixtures for driving the real `coda serve` binary from a test.
//!
//! Deliberately a **real streaming SSE provider** on a loopback `TcpListener`,
//! not a one-shot JSON responder: phase transitions, mid-turn snapshots and
//! the turn clock are only meaningful against something that genuinely
//! streams, and a one-shot responder would let a broken client pass by
//! accident.
//!
//! Every process here runs with a temporary `CODA_HOME` and working
//! directory, `--no-mcp`, and a fake credential pointing at the loopback
//! fixture. No real profile, no real provider, no real key.

#![allow(dead_code)]

use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use coda_client::EngineCommand;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

/// The `coda` binary this test run built. Required — never a PATH lookup and
/// never a silent skip: an absent binary is a broken test run, not a reason to
/// report success.
pub fn coda_exe() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_coda"))
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

/// `coda serve` in a sandbox, wired to a loopback provider fixture.
pub fn serve_command(sandbox: &Sandbox, endpoint: &str) -> EngineCommand {
    let mut command = EngineCommand::new(coda_exe().as_os_str())
        .arg("serve")
        .arg("--no-mcp")
        .arg("--api-key")
        .arg("sk-fixture-not-a-real-key")
        .arg("--endpoint")
        .arg(endpoint)
        .working_dir(sandbox.cwd.path());
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

// ---------------------------------------------------------------------------
// SSE scripting
// ---------------------------------------------------------------------------

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

/// Reads one full HTTP request (headers + declared body) off `socket`.
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
    pub fn requests(&self) -> Vec<String> {
        self.requests.lock().expect("requests poisoned").clone()
    }
    pub fn request_count(&self) -> usize {
        self.requests.lock().expect("requests poisoned").len()
    }
}

/// Serves `turns` in order, one per incoming connection.
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

/// A provider that streams a prelude, waits on an explicit barrier, then
/// finishes. No `sleep` anywhere: the test decides when the turn may end.
pub async fn barrier_provider(
    prelude: String,
    tail: String,
) -> (String, tokio::sync::oneshot::Sender<()>) {
    let (resume_tx, resume_rx) = tokio::sync::oneshot::channel::<()>();
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();

    tokio::spawn(async move {
        let Ok((mut socket, _)) = listener.accept().await else { return };
        read_one_request(&mut socket).await;
        let header = "HTTP/1.1 200 OK\r\ncontent-type: text/event-stream\r\nconnection: close\r\n\r\n";
        let _ = socket.write_all(header.as_bytes()).await;
        let _ = socket.write_all(prelude.as_bytes()).await;
        let _ = socket.flush().await;
        let _ = resume_rx.await;
        let _ = socket.write_all(tail.as_bytes()).await;
        let _ = socket.shutdown().await;
    });

    (format!("http://127.0.0.1:{port}"), resume_tx)
}
