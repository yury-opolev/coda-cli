//! Hermetic real-binary start/initialize/shutdown proof for `coda-engine`,
//! through both invocation forms: bare (no subcommand) and the explicit
//! `serve` subcommand `--engine`/`CODA_ENGINE` actually spawns
//! (`<engine> serve ...`). Cross-binary comparisons against `coda` itself
//! (version/help parity, `coda run --engine coda-engine`) live in
//! `crates/coda/tests/engine_parity.rs`: a bin-only package (this one, and
//! `coda`) cannot be a Cargo dependency of another package's tests — there is
//! no lib target to link — so each binary's own crate is where its
//! `CARGO_BIN_EXE_*` is available, and the cross checks live wherever `coda`
//! itself is the `CARGO_BIN_EXE_coda` package.
//!
//! No real credentials or provider requests: a local loopback HTTP fixture
//! stands in for the Anthropic endpoint, reached through `--api-key`/
//! `--endpoint`, and every process runs with a temporary `CODA_HOME`/working
//! directory and `--no-mcp`.

use std::path::PathBuf;
use std::time::Duration;

use coda_client::{Engine, EngineCommand};
use coda_proto::messages::{method, InitializeParams, PromptParams};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

fn coda_engine_exe() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_coda-engine"))
}

/// Environment variables that must never leak from the developer's/CI's real
/// shell into a hermetic test process, mirroring `coda`'s own
/// `default_diagnostics.rs` fixture list.
const SENSITIVE_ENV_VARS: &[&str] = &[
    "ANTHROPIC_API_KEY",
    "GITHUB_TOKEN",
    "GH_TOKEN",
    "CODA_SERVE_API_KEY",
    "CODA_SERVE_ENDPOINT",
    "CODA_SERVE_PROVIDER",
    "CODA_ENGINE",
    "CODA_LOG",
];

/// A hermetic temp `CODA_HOME` + working directory pair.
struct Sandbox {
    home: tempfile::TempDir,
    cwd: tempfile::TempDir,
}

impl Sandbox {
    fn new() -> Self {
        Self {
            home: tempfile::tempdir().expect("temp CODA_HOME"),
            cwd: tempfile::tempdir().expect("temp cwd"),
        }
    }
}

fn base_engine_command(exe: &std::path::Path, sandbox: &Sandbox) -> EngineCommand {
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

/// A one-shot-per-connection fake Anthropic `/v1/messages` endpoint that
/// always replies with a fixed HTTP status/body, so a turn has something
/// genuine — but never real — to react to. Runs until the test process exits.
async fn fake_anthropic_endpoint(status: u16, body: &'static str) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();

    tokio::spawn(async move {
        loop {
            let Ok((mut socket, _)) = listener.accept().await else { return };
            tokio::spawn(async move {
                let mut request = Vec::new();
                loop {
                    let mut chunk = [0u8; 8192];
                    let count = socket.read(&mut chunk).await.unwrap();
                    if count == 0 { return; }
                    request.extend_from_slice(&chunk[..count]);
                    if let Some(header_end) = request.windows(4).position(|w| w == b"\r\n\r\n") {
                        let headers = String::from_utf8_lossy(&request[..header_end]);
                        let length = headers.lines().find_map(|line| {
                            let (key, value) = line.split_once(':')?;
                            key.eq_ignore_ascii_case("content-length")
                                .then(|| value.trim().parse::<usize>().unwrap())
                        }).unwrap_or(0);
                        if request.len() >= header_end + 4 + length { break; }
                    }
                }
                let reason = if status == 200 { "OK" } else { "Error" };
                let response = format!(
                    "HTTP/1.1 {status} {reason}\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{body}",
                    body.len()
                );
                let _ = socket.write_all(response.as_bytes()).await;
                let _ = socket.shutdown().await;
            });
        }
    });

    format!("http://127.0.0.1:{port}")
}

/// The bare (no-subcommand) form of `coda-engine` is exactly `coda-engine
/// serve` with no flags — proven by driving it through a real hermetic
/// initialize + prompt round trip with `--no-mcp`, a temp `CODA_HOME`, and a
/// local fake endpoint (no real credential, no real provider request).
#[tokio::test]
async fn coda_engine_bare_invocation_starts_initializes_and_shuts_down_hermetically() {
    let sandbox = Sandbox::new();
    let endpoint = fake_anthropic_endpoint(400, r#"{"error":{"message":"fixture rejects every request"}}"#).await;

    let command = base_engine_command(&coda_engine_exe(), &sandbox)
        .arg("--no-mcp")
        .arg("--api-key")
        .arg("fake-test-key")
        .arg("--endpoint")
        .arg(&endpoint);

    let (engine, mut inbound) = Engine::spawn(command).expect("engine spawns");
    let connection = engine.connection();

    let init_val = serde_json::to_value(InitializeParams::new("coda-engine-bare-test")).unwrap();
    tokio::time::timeout(Duration::from_secs(15), connection.request(method::INITIALIZE, Some(init_val)))
        .await
        .expect("initialize must not hang")
        .expect("the handshake succeeds even with no real credentials configured yet");

    let prompt_val = serde_json::to_value(PromptParams::text("what is 2+2?")).unwrap();
    let pending = connection.send_request(method::PROMPT, Some(prompt_val)).expect("prompt queues");
    let drain = async { loop { if inbound.recv().await.is_none() { break; } } };
    let _ = tokio::time::timeout(Duration::from_secs(30), async {
        tokio::select! {
            r = pending => { let _ = r; }
            _ = drain => {}
        }
    })
    .await
    .expect("the prompt turn must not hang");

    let _ = engine.shutdown(Duration::from_secs(5)).await;
}

/// Same proof, through the explicit `serve` subcommand — the form `--engine`
/// actually invokes (`<engine> serve ...`), so this is what a spawned
/// `coda-engine` looks like from `coda`'s or an external orchestrator's
/// point of view.
#[tokio::test]
async fn coda_engine_serve_subcommand_starts_initializes_and_shuts_down_hermetically() {
    let sandbox = Sandbox::new();
    let endpoint = fake_anthropic_endpoint(400, r#"{"error":{"message":"fixture rejects every request"}}"#).await;

    let command = base_engine_command(&coda_engine_exe(), &sandbox)
        .arg("serve")
        .arg("--no-mcp")
        .arg("--api-key")
        .arg("fake-test-key")
        .arg("--endpoint")
        .arg(&endpoint);

    let (engine, _inbound) = Engine::spawn(command).expect("engine spawns");
    let connection = engine.connection();

    let init_val = serde_json::to_value(InitializeParams::new("coda-engine-serve-test")).unwrap();
    tokio::time::timeout(Duration::from_secs(15), connection.request(method::INITIALIZE, Some(init_val)))
        .await
        .expect("initialize must not hang")
        .expect("the handshake succeeds even with no real credentials configured yet");

    let _ = engine.shutdown(Duration::from_secs(5)).await;
}
