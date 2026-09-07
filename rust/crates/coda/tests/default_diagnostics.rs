//! Real-executable regression: an ordinary launch — with **no** logging
//! flags at all — must leave a bounded, privacy-safe, session-correlated
//! operational diagnostic log, on both a direct `coda serve` engine and a
//! headless `coda run` frontend with its spawned child engine.
//!
//! Uses the compiled `coda` binary (`CARGO_BIN_EXE_coda`) directly, a
//! temporary `CODA_HOME`/working directory, a cleared environment, and a
//! local fake Anthropic Messages endpoint reached through the existing
//! `--api-key`/`--endpoint` seam (`coda serve`) or its `CODA_SERVE_API_KEY`/
//! `CODA_SERVE_ENDPOINT` environment equivalents (`coda run`, which has no
//! such flags of its own) — no Copilot token exchange or real credential is
//! ever needed.

use std::path::{Path, PathBuf};
use std::time::Duration;

use coda_client::{Engine, EngineCommand};
use coda_proto::messages::{method, InitializeParams, PromptParams};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

/// Environment variables that must never leak from the developer's/CI's real
/// shell into a hermetic test process — inherited credentials or prior
/// diagnostic correlation would silently make the test non-hermetic (or, for
/// a credential, talk to a real provider).
///
/// Deliberately excludes `CODA_DIAG_RUN_ID`/`CODA_DIAG_DIR`/
/// `CODA_DIAG_VERBOSITY`: `EngineCommand::spawn` applies every `.env()`
/// addition before any `.env_remove()`, so removing a key here would always
/// win over a later, deliberate `.env(...)` addition (e.g. from
/// `coda_tui::diagnostics::forward_env`) — exactly backwards from what a
/// correlation test needs. None of these three are ever set on the outer
/// test process itself, so hermeticity does not depend on removing them.
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

/// A one-shot-per-connection fake Anthropic `/v1/messages` endpoint that
/// always replies with a fixed HTTP status/body/headers, so a real host's
/// HTTP-attempt and turn-failure diagnostics have something genuine to react
/// to. Runs until the test process exits.
async fn fake_anthropic_endpoint(status: u16, request_id: &'static str, body: &'static str) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();

    tokio::spawn(async move {
        loop {
            let Ok((mut socket, _)) = listener.accept().await else {
                return;
            };
            tokio::spawn(async move {
                let mut request = Vec::new();
                loop {
                    let mut chunk = [0u8; 8192];
                    let count = socket.read(&mut chunk).await.unwrap();
                    if count == 0 { return; }
                    request.extend_from_slice(&chunk[..count]);
                    assert!(request.len() <= 2 * 1024 * 1024, "fixture request too large");
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
                    "HTTP/1.1 {status} {reason}\r\n\
                     content-type: application/json\r\n\
                     content-length: {}\r\n\
                     x-request-id: {request_id}\r\n\
                     connection: close\r\n\r\n{body}",
                    body.len()
                );
                let _ = socket.write_all(response.as_bytes()).await;
                let _ = socket.shutdown().await;
            });
        }
    });

    format!("http://127.0.0.1:{port}")
}

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

    fn diagnostics_dir(&self) -> PathBuf {
        self.home.path().join(".coda").join("logs").join("diagnostics")
    }
}

/// Every recognized `.jsonl` diagnostic file's parsed lines, keyed by path.
fn read_all_diagnostic_files(dir: &Path) -> Vec<(PathBuf, Vec<serde_json::Value>)> {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return Vec::new();
    };
    entries
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().and_then(|e| e.to_str()) == Some("jsonl"))
        .map(|path| {
            let content = std::fs::read_to_string(&path).expect("diagnostic file is readable");
            let lines = content
                .lines()
                .filter(|l| !l.is_empty())
                .map(|l| serde_json::from_str(l).expect("every diagnostic line is valid JSON"))
                .collect();
            (path, lines)
        })
        .collect()
}

fn base_engine_command(exe: &Path, sandbox: &Sandbox) -> EngineCommand {
    let mut command = EngineCommand::new(exe.as_os_str()).working_dir(sandbox.cwd.path());
    command = command.env("CODA_HOME", sandbox.home.path());
    for var in SENSITIVE_ENV_VARS {
        command = command.env_remove(*var);
    }
    command
}

/// Launches the real engine directly (`coda serve`), with **no** `--log-file`
/// or `--diagnostic-verbosity` flags, against a fake local Anthropic endpoint
/// reached through `--api-key`/`--endpoint`, and returns once a full
/// initialize + one failing prompt turn has round-tripped.
async fn run_one_serve_turn(status: u16, request_id: &'static str, body: &'static str) -> (Sandbox, serde_json::Value) {
    let sandbox = Sandbox::new();
    let endpoint = fake_anthropic_endpoint(status, request_id, body).await;
    let exe = PathBuf::from(env!("CARGO_BIN_EXE_coda"));

    let command = base_engine_command(&exe, &sandbox)
        .arg("serve")
        .arg("--no-mcp")
        .arg("--api-key")
        .arg("fake-test-key")
        .arg("--endpoint")
        .arg(&endpoint);

    let (engine, mut inbound) = Engine::spawn(command).expect("engine spawns");
    let connection = engine.connection();

    let init_val = serde_json::to_value(InitializeParams::new("default-diagnostics-test")).unwrap();
    tokio::time::timeout(
        Duration::from_secs(15),
        connection.request(method::INITIALIZE, Some(init_val)),
    )
    .await
    .expect("initialize must not hang")
    .expect("the handshake succeeds even with no real credentials configured yet");
    eprintln!("[test] initialize completed");

    let prompt_val = serde_json::to_value(PromptParams::text("what is 2+2?")).unwrap();
    let pending = connection
        .send_request(method::PROMPT, Some(prompt_val))
        .expect("prompt request queues");

    // Drain notifications until the turn ends; a fake fixture never streams
    // real tokens, so only the terminal response matters here.
    let drain = async {
        loop {
            match inbound.recv().await {
                Some(_) => continue,
                None => break,
            }
        }
    };
    let response = tokio::time::timeout(Duration::from_secs(30), async {
        tokio::select! {
            r = pending => r.ok().and_then(|r| r.ok()).unwrap_or_default(),
            _ = drain => serde_json::Value::Null,
        }
    })
    .await
    .expect("the prompt turn must not hang");
    eprintln!("[test] prompt completed: {response:?}");

    // `Engine::shutdown` closes the child's stdin by dropping its *own*
    // internal connection/sender; a second clone held here would keep the
    // writer task (and therefore the child's stdin) alive, so the engine
    // would sit blocked reading for the whole grace period before being
    // force-killed. Drop it explicitly first.
    drop(connection);
    drop(inbound);

    let shutdown_started = std::time::Instant::now();
    engine
        .shutdown(Duration::from_secs(20))
        .await
        .expect("graceful shutdown");
    eprintln!("[test] engine shutdown took {:?}", shutdown_started.elapsed());

    (sandbox, response)
}

#[tokio::test]
async fn default_launch_creates_a_diagnostic_log_with_no_flags_at_all() {
    // A successful-looking (200, empty SSE) response is enough to prove file
    // creation and lifecycle recording; the failure-path assertions live in
    // the dedicated privacy/correlation test below.
    let (sandbox, _response) = run_one_serve_turn(200, "req-ok-1", "").await;

    let files = read_all_diagnostic_files(&sandbox.diagnostics_dir());
    assert_eq!(files.len(), 1, "coda serve owns exactly one diagnostic file for its process");
    let (_path, lines) = &files[0];

    assert!(lines.iter().any(|l| l["kind"] == "process_start"), "{lines:?}");
    assert!(lines.iter().any(|l| l["kind"] == "session_initialized"), "{lines:?}");
    assert!(lines.iter().any(|l| l["kind"] == "process_end"), "{lines:?}");
}

#[tokio::test]
async fn a_provider_failure_records_a_safe_turn_with_full_correlation_and_http_status() {
    let body = r#"{"type":"error","error":{"type":"invalid_request_error","param":"input[22].summary","message":"Missing required parameter: 'input[22].summary'. contact us at leaked-secret@example.com"}}"#;
    let (sandbox, response) = run_one_serve_turn(400, "req-fixture-77", body).await;

    assert_eq!(response["ok"], false, "the turn must genuinely fail: {response:?}");

    let files = read_all_diagnostic_files(&sandbox.diagnostics_dir());
    assert_eq!(files.len(), 1);
    let (path, lines) = &files[0];

    let turn_start = lines.iter().find(|l| l["kind"] == "turn_start").expect("turn_start recorded");
    let session_id = turn_start["session_id"].as_str().expect("session id present").to_owned();
    let turn_id = turn_start["turn_id"].as_str().expect("turn id present").to_owned();
    assert!(!session_id.is_empty() && !turn_id.is_empty());

    let http_result = lines
        .iter()
        .find(|l| l["kind"] == "http_result" && l["status"] == 400)
        .expect("an http_result with status 400 was recorded");
    assert_eq!(http_result["session_id"], session_id);
    assert_eq!(http_result["turn_id"], turn_id);
    assert!(http_result["request_id"].as_str().is_some(), "each attempt gets an internal request id");
    assert_eq!(
        http_result["provider_request_id"], "req-fixture-77",
        "the provider's own x-request-id header, captured before the body was consumed"
    );

    let turn_failed = lines.iter().find(|l| l["kind"] == "turn_failed").expect("turn_failed recorded");
    assert_eq!(turn_failed["session_id"], session_id);
    assert_eq!(turn_failed["turn_id"], turn_id);
    assert_eq!(turn_failed["status"], 400);
    assert_eq!(turn_failed["category"], "client_error");

    // Privacy, at whatever the default verbosity is: no prompt, no raw
    // message text, and definitely no embedded "secret".
    let content = std::fs::read_to_string(path).unwrap();
    assert!(!content.contains("leaked-secret"));
    assert!(!content.contains("Missing required parameter"));
    assert!(!content.contains("what is 2+2"));
}

#[tokio::test]
async fn real_headless_frontend_and_child_leave_correlated_failure_logs() {
    let sandbox = Sandbox::new();
    let body = r#"{"error":{"param":"input[22].summary","message":"SENSITIVE_RESPONSE_BODY"}}"#;
    let endpoint = fake_anthropic_endpoint(400, "req-real-headless", body).await;
    let output = run_headless_process(&sandbox, Some(&endpoint)).await;
    assert!(!output.status.success());
    let result: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap();
    assert_eq!(result["ok"], false);
    let files = read_all_diagnostic_files(&sandbox.diagnostics_dir());
    let parent = files.iter().find(|(_, records)| records.iter().any(|r| r["role"] == "run")).unwrap();
    let child = files.iter().find(|(_, records)| records.iter().any(|r| r["role"] == "serve")).unwrap();
    assert_eq!(parent.1[0]["run_id"], child.1[0]["run_id"]);
    assert!(parent.1.iter().any(|r| r["kind"] == "engine_log_path"));
    assert!(parent.1.iter().any(|r| r["kind"] == "process_end"));
    assert!(child.1.iter().any(|r| r["kind"] == "http_result" && r["status"] == 400
        && r["provider_request_id"] == "req-real-headless"));
    assert!(child.1.iter().any(|r| r["kind"] == "turn_failed" && r["session_id"].is_string()
        && r["turn_id"].is_string()));
    for (path, _) in files {
        let text = std::fs::read_to_string(path).unwrap();
        for secret in ["SENSITIVE_RESPONSE_BODY", "PRIVATE_DIAGNOSTIC_PROMPT", "fake-test-key"] {
            assert!(!text.contains(secret));
        }
    }
}

#[tokio::test]
async fn real_headless_rpc_rejection_exits_and_records_preflight_failure() {
    let sandbox = Sandbox::new();
    // A malformed isolated routing file fails before credential lookup, so
    // this fixture cannot accidentally access an OS credential store.
    std::fs::create_dir_all(sandbox.home.path().join(".coda")).unwrap();
    std::fs::write(sandbox.home.path().join(".coda").join("settings.json"), "{invalid").unwrap();
    let output = run_headless_process(&sandbox, None).await;
    assert!(!output.status.success());
    let result: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap();
    assert_eq!(result["ok"], false);
    assert!(result["error"].is_string(), "an RPC rejection must not become an empty failure");
    let files = read_all_diagnostic_files(&sandbox.diagnostics_dir());
    assert!(files.iter().any(|(_, records)| records.iter().any(|r| r["kind"] == "startup_failure")));
    assert!(files.iter().any(|(_, records)| records.iter().any(|r|
        r["kind"] == "turn_failed" && r["session_id"].is_string() && r["turn_id"].is_string())));
}

async fn run_headless_process(sandbox: &Sandbox, endpoint: Option<&str>) -> std::process::Output {
    let mut command = tokio::process::Command::new(env!("CARGO_BIN_EXE_coda"));
    command.env_clear().current_dir(sandbox.cwd.path()).kill_on_drop(true)
        .env("CODA_HOME", sandbox.home.path())
        .env("CODA_SERVE_DISABLE_MCP", "1")
        .args(["run", "--json", "--prompt", "PRIVATE_DIAGNOSTIC_PROMPT", "--system-prompt", "Do not use tools."]);
    for name in ["SystemRoot", "PATH", "TEMP", "TMP"] {
        if let Some(value) = std::env::var_os(name) { command.env(name, value); }
    }
    if let Some(endpoint) = endpoint {
        command.env("CODA_SERVE_API_KEY", "fake-test-key").env("CODA_SERVE_ENDPOINT", endpoint);
    }
    tokio::time::timeout(Duration::from_secs(15), command.output())
        .await.expect("headless must finish even when prompt returns an RPC error")
        .expect("headless process starts")
}

#[tokio::test]
async fn a_frontend_and_its_spawned_engine_correlate_under_one_run_id() {
    // Drives the real production pieces — `coda_client::Engine::spawn`,
    // `coda_tui::diagnostics::forward_env`, and a genuinely spawned `coda
    // serve` child — exactly as `coda run`'s `main.rs` does, but orchestrated
    // from the test itself rather than through the compiled `coda run`
    // subcommand.
    //
    // This library-level companion isolates forwarding behavior; the real
    // headless executable and early RPC rejection are exercised above.
    let body = r#"{"error":{"param":"input[0].summary","message":"Missing required parameter: 'input[0].summary'."}}"#;
    let endpoint = fake_anthropic_endpoint(400, "req-headless-1", body).await;
    let sandbox = Sandbox::new();
    let exe = PathBuf::from(env!("CARGO_BIN_EXE_coda"));

    // Simulates the frontend's own root context, exactly as `coda`'s
    // `main.rs` builds one via `coda_tui::diagnostics::init`.
    let parent_logger = coda_diagnostics::Logger::open(
        coda_diagnostics::Options {
            directory: sandbox.diagnostics_dir(),
            file: None,
            role: coda_diagnostics::ProcessRole::Run,
            version: "test".into(),
            verbosity: coda_diagnostics::Verbosity::Normal,
        },
        coda_diagnostics::Limits::default(),
    )
    .expect("parent logger opens");
    let parent_ctx =
        coda_diagnostics::DiagnosticContext::root(std::sync::Arc::new(parent_logger), "shared-run-id-123");
    parent_ctx.record(coda_diagnostics::Event::ProcessStart);

    let command = base_engine_command(&exe, &sandbox)
        .arg("serve")
        .arg("--no-mcp")
        .arg("--api-key")
        .arg("fake-test-key")
        .arg("--endpoint")
        .arg(&endpoint);
    // The exact production call: forwards run id/directory/verbosity so the
    // child's own default logger lands in the same directory under the same
    // run id, never reusing the parent's file.
    let command = coda_tui::diagnostics::forward_env(command, &parent_ctx, &sandbox.diagnostics_dir());

    let (engine, mut inbound) = Engine::spawn(command).expect("engine spawns");
    parent_ctx.record(coda_diagnostics::Event::EngineStart { pid: None });
    let connection = engine.connection();

    let init_val = serde_json::to_value(InitializeParams::new("frontend-correlation-test")).unwrap();
    tokio::time::timeout(
        Duration::from_secs(15),
        connection.request(method::INITIALIZE, Some(init_val)),
    )
    .await
    .expect("initialize must not hang")
    .expect("handshake succeeds");

    let prompt_val = serde_json::to_value(PromptParams::text("what is 2+2?")).unwrap();
    let pending = connection
        .send_request(method::PROMPT, Some(prompt_val))
        .expect("prompt request queues");
    let drain = async {
        loop {
            if inbound.recv().await.is_none() {
                break;
            }
        }
    };
    let response = tokio::time::timeout(Duration::from_secs(30), async {
        tokio::select! {
            r = pending => r.ok().and_then(|r| r.ok()).unwrap_or_default(),
            _ = drain => serde_json::Value::Null,
        }
    })
    .await
    .expect("the prompt turn must not hang");
    assert_eq!(response["ok"], false, "the turn must genuinely fail: {response:?}");

    drop(connection);
    drop(inbound);
    engine.shutdown(Duration::from_secs(5)).await.expect("graceful shutdown");
    parent_ctx.record(coda_diagnostics::Event::EngineEnd { exit_code: Some(0) });
    parent_ctx.record(coda_diagnostics::Event::ProcessEnd { exit_code: 1 });

    let files = read_all_diagnostic_files(&sandbox.diagnostics_dir());
    assert_eq!(files.len(), 2, "the frontend and its engine child each own a distinct file: {files:?}");

    let run_ids: std::collections::HashSet<_> = files
        .iter()
        .filter_map(|(_, lines)| lines.first().and_then(|l| l["run_id"].as_str()))
        .collect();
    assert_eq!(run_ids, std::collections::HashSet::from(["shared-run-id-123"]), "{files:?}");

    let roles: std::collections::HashSet<_> = files
        .iter()
        .flat_map(|(_, lines)| lines.iter().filter_map(|l| l["role"].as_str()))
        .collect();
    assert!(roles.contains("run"), "{roles:?}");
    assert!(roles.contains("serve"), "{roles:?}");

    let parent_lines = files
        .iter()
        .find(|(_, lines)| lines.iter().any(|l| l["role"] == "run"))
        .map(|(_, lines)| lines)
        .expect("a parent (run) file exists");
    assert!(parent_lines.iter().any(|l| l["kind"] == "engine_start"), "{parent_lines:?}");
    assert!(
        parent_lines.iter().any(|l| l["kind"] == "process_end"),
        "process-end must be recorded explicitly before the headless `process::exit`: {parent_lines:?}"
    );

    let child_lines = files
        .iter()
        .find(|(_, lines)| lines.iter().any(|l| l["role"] == "serve"))
        .map(|(_, lines)| lines)
        .expect("a child (serve) file exists");
    assert!(child_lines.iter().any(|l| l["kind"] == "turn_failed"), "{child_lines:?}");
}
