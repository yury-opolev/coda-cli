//! `coda-engine auth` end-to-end.
//!
//! The engine binary offers the same host-local maintenance surface as `coda`
//! — status, login, logout — without gaining a terminal renderer. These tests
//! run the real binary against an isolated profile and never contact a
//! provider.

use std::path::PathBuf;
use std::process::{Command, Stdio};

fn engine_exe() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_coda-engine"))
}

struct Outcome {
    code: i32,
    stdout: String,
    stderr: String,
}

impl Outcome {
    fn all(&self) -> String {
        format!("{}\n{}", self.stdout, self.stderr)
    }
}

/// Environment that must not leak into an `auth` child.
const CLEARED_ENV: &[&str] = &[
    "ANTHROPIC_API_KEY",
    "CODA_LOG",
    "CODA_DIAG_DIR",
    "CODA_DIAG_VERBOSITY",
    "CODA_DIAG_RUN_ID",
    "GH_COPILOT_ENTERPRISE_DOMAIN",
    "GH_COPILOT_DEVICE_CODE_URL",
    "GH_COPILOT_TOKEN_URL",
    "GH_COPILOT_COPILOT_TOKEN_URL",
    "GH_COPILOT_API_BASE_URL",
];

fn run_auth(home: &std::path::Path, args: &[&str], stdin: Option<&str>) -> Outcome {
    let mut command = Command::new(engine_exe());
    command
        .arg("auth")
        .args(args)
        .env("CODA_HOME", home)
        .env("CODA_AUTH_NO_BROWSER", "1")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for name in CLEARED_ENV {
        command.env_remove(name);
    }
    let mut child = command.spawn().expect("the coda-engine binary starts");
    {
        use std::io::Write;
        let mut pipe = child.stdin.take().expect("stdin");
        if let Some(text) = stdin {
            pipe.write_all(text.as_bytes()).expect("write stdin");
        }
    }
    let output = child.wait_with_output().expect("the coda-engine binary exits");
    Outcome {
        code: output.status.code().expect("an exit code"),
        stdout: String::from_utf8_lossy(&output.stdout).into_owned(),
        stderr: String::from_utf8_lossy(&output.stderr).into_owned(),
    }
}

fn home() -> tempfile::TempDir {
    tempfile::tempdir().expect("temp CODA_HOME")
}

#[test]
fn the_engine_reports_status_for_an_empty_profile() {
    let home = home();
    let outcome = run_auth(home.path(), &["status"], None);
    assert_eq!(outcome.code, 0, "{}", outcome.all());
    for label in ["Claude.ai subscription", "Anthropic API key", "GitHub Copilot"] {
        assert!(outcome.stdout.contains(label), "{}", outcome.all());
    }
}

#[test]
fn the_engine_signs_out_without_claiming_a_provider_side_revocation() {
    let home = home();
    let outcome = run_auth(home.path(), &["logout"], None);
    assert_eq!(outcome.code, 0, "{}", outcome.all());
    assert!(
        outcome.stdout.to_lowercase().contains("nothing was revoked"),
        "{}",
        outcome.all()
    );
}

#[test]
fn the_engine_uses_the_same_usage_and_cancellation_contract() {
    let home = home();
    assert_eq!(run_auth(home.path(), &["login"], Some("")).code, 2);
    assert_eq!(run_auth(home.path(), &["login", "claude", "--public"], Some("")).code, 2);
    assert_eq!(run_auth(home.path(), &["login", "api-key"], Some("")).code, 2);
    assert_eq!(run_auth(home.path(), &["login", "api-key", "--use-env"], Some("")).code, 1);
}

// The "no renderer" claim is not made here: running a command proves only that
// it runs. `tests/independence.rs` is the guard, and it checks the dependency
// graph.

/// Adding `auth` must not disturb the two serve entry forms the engine
/// contract depends on.
#[test]
fn serve_bare_and_explicit_forms_still_parse() {
    for args in [
        vec!["--help"],
        vec!["serve", "--help"],
        vec!["auth", "--help"],
        vec!["auth", "logout", "--help"],
    ] {
        let output = Command::new(engine_exe())
            .args(&args)
            .output()
            .expect("the coda-engine binary runs");
        assert!(output.status.success(), "{args:?} must succeed");
    }

    let output = Command::new(engine_exe()).arg("--help").output().expect("runs");
    let help = String::from_utf8_lossy(&output.stdout);
    for expected in ["serve", "auth"] {
        assert!(help.contains(expected), "`coda-engine --help` must list {expected}:\n{help}");
    }
}
