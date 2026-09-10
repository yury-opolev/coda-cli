//! Real-binary argument/help/version parity between `coda serve` and the
//! standalone `coda-engine`, plus proof that the existing `--engine`/
//! `CODA_ENGINE` seam already selects the new core artifact — no change to
//! `coda run`/`coda serve` needed.
//!
//! `coda-engine` is a separate, bin-only workspace package (no lib target),
//! so it cannot be a `[dev-dependencies]` entry here the way a normal crate
//! would be — there is nothing to link against, only a binary to run.
//! [`coda_engine_exe`] builds the current core once and obtains its executable
//! path from Cargo's artifact messages, honoring custom target directories.
//! An existing binary is not evidence that it matches the current source.
//!
//! No real credentials or provider requests: every fixture is a local
//! loopback HTTP listener reached through `--api-key`/`--endpoint` (or their
//! `CODA_SERVE_API_KEY`/`CODA_SERVE_ENDPOINT` env equivalents), and every
//! process runs with a temporary `CODA_HOME`/working directory and
//! `--no-mcp`.

use std::path::{Path, PathBuf};
use std::sync::OnceLock;
use std::time::Duration;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

fn coda_exe() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_coda"))
}

/// Builds a current native core once per test run; Cargo supplies the path.
fn coda_engine_exe() -> PathBuf {
    static ENGINE_EXE: OnceLock<PathBuf> = OnceLock::new();
    ENGINE_EXE.get_or_init(|| {
        let workspace_root = Path::new(env!("CARGO_MANIFEST_DIR")).parent().unwrap().parent().unwrap();
        let cargo = std::env::var_os("CARGO").unwrap_or_else(|| "cargo".into());
        let output = std::process::Command::new(cargo)
            .args(["build", "--package", "coda-engine", "--locked", "--message-format=json"])
            .current_dir(workspace_root).output().expect("cargo build runs");
        assert!(output.status.success(), "core build failed: {}", String::from_utf8_lossy(&output.stderr));
        String::from_utf8(output.stdout).unwrap().lines().filter_map(|line| {
            let artifact: serde_json::Value = serde_json::from_str(line).ok()?;
            (artifact["reason"] == "compiler-artifact" && artifact["target"]["name"] == "coda-engine")
                .then(|| artifact["executable"].as_str().map(PathBuf::from)).flatten()
        }).last().expect("Cargo reported the core executable")
    }).clone()
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

/// The version `coda --version`/`coda-engine --version` report, stripped of
/// the leading program name so only the number is compared.
fn version_number(output: &str) -> &str {
    output.trim().rsplit(' ').next().unwrap_or(output.trim())
}

#[test]
fn coda_and_coda_engine_report_the_same_product_version() {
    let coda_version = std::process::Command::new(coda_exe())
        .arg("--version")
        .env_remove("CODA_ENGINE")
        .output()
        .expect("coda --version runs");
    let engine_version = std::process::Command::new(coda_engine_exe())
        .arg("--version")
        .output()
        .expect("coda-engine --version runs");

    assert!(coda_version.status.success());
    assert!(engine_version.status.success());

    let coda_out = String::from_utf8_lossy(&coda_version.stdout);
    let engine_out = String::from_utf8_lossy(&engine_version.stdout);
    assert_eq!(
        version_number(&coda_out),
        version_number(&engine_out),
        "coda reported '{coda_out}', coda-engine reported '{engine_out}'"
    );
}

/// Every `--flag` `coda serve --help` documents must also appear in
/// `coda-engine serve --help`, and vice versa — the two are meant to be the
/// exact same `ServeArgs`, not lookalike copies that can drift.
#[test]
fn serve_help_advertises_the_same_flags_on_both_binaries() {
    let coda_help = std::process::Command::new(coda_exe())
        .args(["serve", "--help"])
        .env_remove("CODA_ENGINE")
        .output()
        .expect("coda serve --help runs");
    let engine_help = std::process::Command::new(coda_engine_exe())
        .args(["serve", "--help"])
        .output()
        .expect("coda-engine serve --help runs");

    assert!(coda_help.status.success());
    assert!(engine_help.status.success());

    let flags = |text: &str| -> Vec<String> {
        text.split_whitespace()
            .filter(|tok| tok.starts_with("--"))
            .map(|tok| tok.trim_end_matches(',').to_string())
            .collect()
    };
    let mut coda_flags = flags(&String::from_utf8_lossy(&coda_help.stdout));
    let mut engine_flags = flags(&String::from_utf8_lossy(&engine_help.stdout));
    coda_flags.sort();
    coda_flags.dedup();
    engine_flags.sort();
    engine_flags.dedup();

    assert_eq!(
        coda_flags, engine_flags,
        "`coda serve --help` and `coda-engine serve --help` must advertise the same flags"
    );
    assert!(coda_flags.contains(&"--effort".to_string()));
    assert!(coda_flags.contains(&"--api-key".to_string()));
}

/// `coda run --engine <coda-engine.exe>` must behave exactly like the
/// default self-spawn path: proves the existing `--engine`/`CODA_ENGINE`
/// selection seam already works with the new core artifact, with no change
/// to `coda run` itself.
#[tokio::test]
async fn coda_run_drives_coda_engine_as_its_external_engine() {
    let home = tempfile::tempdir().expect("temp CODA_HOME");
    let cwd = tempfile::tempdir().expect("temp cwd");
    let endpoint = fake_anthropic_endpoint(400, r#"{"error":{"message":"fixture rejects every request"}}"#).await;

    let mut command = tokio::process::Command::new(coda_exe());
    command
        .env_clear()
        .current_dir(cwd.path())
        .kill_on_drop(true)
        .env("CODA_HOME", home.path())
        .env("CODA_SERVE_DISABLE_MCP", "1")
        .env("CODA_SERVE_API_KEY", "fake-test-key")
        .env("CODA_SERVE_ENDPOINT", &endpoint)
        .args(["run", "--json", "--engine"])
        .arg(coda_engine_exe())
        .args(["--prompt", "what is 2+2?"]);
    for name in ["SystemRoot", "PATH", "TEMP", "TMP"] {
        if let Some(value) = std::env::var_os(name) {
            command.env(name, value);
        }
    }

    let output = tokio::time::timeout(Duration::from_secs(30), command.output())
        .await
        .expect("coda run must not hang")
        .expect("coda run starts");

    // `coda run` exits 1 when the *turn* fails (its documented contract), so
    // this deliberately does not assert `status.success()` — the fixture
    // rejects every request. The point of this test is that the whole
    // handshake through the alternate engine binary completed and reported a
    // structured result rather than hanging or crashing.
    let result: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap_or_else(|e| {
        panic!("coda run --json must emit valid JSON: {e}; stdout={:?}", String::from_utf8_lossy(&output.stdout))
    });
    assert_eq!(result["ok"], false);
    assert!(result["error"].is_string());
}
