//! `coda auth` end-to-end: the real binary, an isolated profile, and no
//! provider network access.
//!
//! These prove the subcommand is actually wired to the shared runner — the
//! process really opens the profile it was pointed at, really reaches the
//! login transaction, and exits with the documented code — while stopping
//! short of anything that would contact a provider.

use std::path::PathBuf;
use std::process::{Command, Stdio};

/// The `coda` binary this test run built. Never a PATH lookup, never skipped.
fn coda_exe() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_coda"))
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

/// Environment that must not leak into an `auth` child: diagnostics
/// redirection, an ambient key, and every Copilot endpoint override.
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
    run_auth_with_env(home, args, stdin, &[])
}

fn run_auth_with_env(
    home: &std::path::Path,
    args: &[&str],
    stdin: Option<&str>,
    env: &[(&str, &str)],
) -> Outcome {
    let mut command = Command::new(coda_exe());
    command
        .arg("auth")
        .args(args)
        .env("CODA_HOME", home)
        // No browser: a test must not open a window on the machine running it.
        .env("CODA_AUTH_NO_BROWSER", "1")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for name in CLEARED_ENV {
        command.env_remove(name);
    }
    for (key, value) in env {
        command.env(key, value);
    }
    let mut child = command.spawn().expect("the coda binary starts");
    {
        use std::io::Write;
        let mut pipe = child.stdin.take().expect("stdin");
        if let Some(text) = stdin {
            pipe.write_all(text.as_bytes()).expect("write stdin");
        }
    }
    let output = child.wait_with_output().expect("the coda binary exits");
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
fn status_reports_every_identity_of_an_empty_profile_and_succeeds() {
    let home = home();
    let outcome = run_auth(home.path(), &["status"], None);
    assert_eq!(outcome.code, 0, "{}", outcome.all());
    for label in ["Claude.ai subscription", "Anthropic API key", "GitHub Copilot"] {
        assert!(outcome.stdout.contains(label), "{}", outcome.all());
    }
    assert!(outcome.stdout.to_lowercase().contains("not signed in"), "{}", outcome.all());
}

/// Authorization URLs and device codes belong in the ephemeral CLI surface
/// only. An auth command must not open the diagnostic log at all.
#[test]
fn an_auth_command_writes_no_diagnostic_log() {
    let home = home();
    assert_eq!(run_auth(home.path(), &["status"], None).code, 0);
    let diagnostics = home.path().join(".coda").join("logs");
    assert!(!diagnostics.exists(), "auth must not open the diagnostic log: {diagnostics:?}");
}

#[test]
fn logout_of_an_empty_profile_says_what_it_did_not_do() {
    let home = home();
    let outcome = run_auth(home.path(), &["logout"], None);
    assert_eq!(outcome.code, 0, "{}", outcome.all());
    let lowered = outcome.stdout.to_lowercase();
    assert!(lowered.contains("no stored credential was removed"), "{}", outcome.all());
    assert!(lowered.contains("nothing was revoked"), "{}", outcome.all());
}

#[test]
fn a_login_without_a_provider_and_without_a_terminal_is_a_usage_error() {
    let home = home();
    let outcome = run_auth(home.path(), &["login"], Some(""));
    assert_eq!(outcome.code, 2, "{}", outcome.all());
    assert!(outcome.stderr.to_lowercase().contains("provider"), "{}", outcome.all());
}

#[test]
fn provider_inapplicable_options_are_refused_with_the_usage_code() {
    let home = home();
    for args in [
        vec!["login", "claude", "--public"],
        vec!["login", "copilot", "--api-key-stdin"],
        vec!["login", "api-key", "--enterprise-domain", "octocorp.ghe.com"],
    ] {
        let outcome = run_auth(home.path(), &args, Some(""));
        assert_eq!(outcome.code, 2, "{args:?}: {}", outcome.all());
    }
}

#[test]
fn a_key_from_a_pipe_requires_the_explicit_option_and_is_never_echoed() {
    let home = home();
    let outcome = run_auth(home.path(), &["login", "api-key"], Some("sk-secret-sentinel\n"));
    assert_eq!(outcome.code, 2, "{}", outcome.all());
    assert!(outcome.stderr.contains("--api-key-stdin"), "{}", outcome.all());
    assert!(!outcome.all().contains("sk-secret-sentinel"), "{}", outcome.all());

    // Explicitly requested, but malformed: refused before anything is stored,
    // and still never echoed.
    let outcome = run_auth(
        home.path(),
        &["login", "api-key", "--api-key-stdin"],
        Some("sk-secret-sentinel\u{7}bad\n"),
    );
    assert_eq!(outcome.code, 2, "{}", outcome.all());
    assert!(!outcome.all().contains("sk-secret-sentinel"), "{}", outcome.all());
}

/// The route really reaches the login transaction: the profile is opened, the
/// service is built, and the environment source is consulted — which fails
/// closed here because no key is exported, with no provider contacted.
#[test]
fn an_environment_login_with_no_exported_key_fails_operationally_without_a_network_call() {
    let home = home();
    let outcome = run_auth(home.path(), &["login", "api-key", "--use-env"], Some(""));
    assert_eq!(outcome.code, 1, "{}", outcome.all());
    assert!(!outcome.all().to_lowercase().contains("signed in to"), "{}", outcome.all());
}

/// An exported key is *availability*, never an assertion about the account
/// this machine is signed in as — and it survives a logout, because a logout
/// removes stored credentials rather than unsetting a shell variable.
#[test]
fn an_exported_key_is_reported_as_availability_and_outlives_a_logout() {
    let home = home();
    let env = [("ANTHROPIC_API_KEY", "sk-env-sentinel")];

    let outcome = run_auth_with_env(home.path(), &["status"], None, &env);
    assert_eq!(outcome.code, 0, "{}", outcome.all());
    assert!(outcome.stdout.contains("ANTHROPIC_API_KEY"), "{}", outcome.all());
    assert!(
        outcome.stdout.to_lowercase().contains("availability only"),
        "{}",
        outcome.all()
    );
    assert!(!outcome.all().contains("sk-env-sentinel"), "{}", outcome.all());

    let outcome = run_auth_with_env(home.path(), &["logout"], None, &env);
    assert_eq!(outcome.code, 0, "{}", outcome.all());
    assert!(
        outcome.stdout.contains("ANTHROPIC_API_KEY is still set"),
        "{}",
        outcome.all()
    );
    assert!(!outcome.all().contains("sk-env-sentinel"), "{}", outcome.all());
}

/// The deployment shown is the one the service resolved, including when an
/// exported endpoint override moved it somewhere the saved tenant does not
/// describe.
#[test]
fn the_effective_copilot_deployment_is_disclosed_truthfully() {
    let home = home();
    std::fs::create_dir_all(home.path().join(".coda")).expect("profile dir");
    std::fs::write(
        home.path().join(".coda").join("settings.json"),
        r#"{ "theme": "dark", "defaultProvider": "github-copilot", "githubEnterpriseDomain": "octocorp.ghe.com" }"#,
    )
    .expect("settings");

    let outcome = run_auth(home.path(), &["status"], None);
    assert_eq!(outcome.code, 0, "{}", outcome.all());
    assert!(outcome.stdout.contains("octocorp.ghe.com"), "{}", outcome.all());
    // A chosen provider with no credential is "sign in to it", never a
    // silently different account.
    assert!(outcome.stdout.contains("github-copilot"), "{}", outcome.all());
    assert!(
        outcome.stdout.to_lowercase().contains("no usable credential"),
        "{}",
        outcome.all()
    );

    let outcome = run_auth_with_env(
        home.path(),
        &["status"],
        None,
        &[("GH_COPILOT_API_BASE_URL", "https://copilot.internal.example/api")],
    );
    assert_eq!(outcome.code, 0, "{}", outcome.all());
    assert!(outcome.stdout.contains("copilot.internal.example"), "{}", outcome.all());
    assert!(
        outcome.stdout.to_lowercase().contains("overrides are in force"),
        "{}",
        outcome.all()
    );
}

/// A settings file that cannot be parsed is a *settings* fault. Reporting it
/// as a credential-store error sends the user to look at the wrong thing.
#[test]
fn a_malformed_settings_file_is_diagnosed_as_settings_not_as_credentials() {
    let home = home();
    std::fs::create_dir_all(home.path().join(".coda")).expect("profile dir");
    let settings = home.path().join(".coda").join("settings.json");
    std::fs::write(&settings, "{ \"defaultProvider\": \"claude-ai\", oops \"sk-not-a-key\" }")
        .expect("settings");

    let outcome = run_auth(home.path(), &["logout"], None);
    assert_eq!(outcome.code, 1, "{}", outcome.all());
    let lowered = outcome.stderr.to_lowercase();
    assert!(lowered.contains("settings"), "{}", outcome.all());
    assert!(lowered.contains("not valid json"), "{}", outcome.all());
    assert!(!lowered.contains("credential store error"), "{}", outcome.all());
    // The parse error's own text quotes the offending input; that must not be
    // what the user is shown.
    assert!(!outcome.all().contains("sk-not-a-key"), "{}", outcome.all());
    assert!(outcome.stderr.contains("settings.json"), "{}", outcome.all());

    // Status still works, and still says which file is at fault.
    let outcome = run_auth(home.path(), &["status"], None);
    assert_eq!(outcome.code, 0, "{}", outcome.all());
    assert!(outcome.stdout.contains("settings.json"), "{}", outcome.all());
    assert!(!outcome.all().contains("sk-not-a-key"), "{}", outcome.all());
}

/// Values that came out of a file anyone can edit must not be able to move the
/// cursor or invent a line of this command's own report.
#[test]
fn hostile_settings_values_cannot_repaint_or_forge_the_real_report() {
    let home = home();
    std::fs::create_dir_all(home.path().join(".coda")).expect("profile dir");
    std::fs::write(
        home.path().join(".coda").join("settings.json"),
        // A real settings file can hold any JSON string, escapes included.
        r#"{ "defaultProvider": "claude-ai\u001b[2J\r\n  Selected provider: github-copilot (verified)",
             "githubEnterpriseDomain": "octocorp.ghe.com\nSigned in as: root" }"#,
    )
    .expect("settings");

    let outcome = run_auth(home.path(), &["status"], None);
    assert_eq!(outcome.code, 0, "{}", outcome.all());
    assert!(!outcome.stdout.contains('\u{1b}'), "an escape sequence reached the terminal");
    assert!(!outcome.stdout.contains('\r'), "a carriage return reached the terminal");
    let verdicts = outcome
        .stdout
        .lines()
        .filter(|line| line.trim_start().starts_with("Selected provider:"))
        .count();
    assert_eq!(verdicts, 1, "a settings value forged a verdict:\n{}", outcome.stdout);
    assert!(
        !outcome.stdout.lines().any(|line| line.trim_start().starts_with("Signed in as: root")),
        "{}",
        outcome.stdout
    );
}

#[test]
fn the_other_modes_still_parse_alongside_auth() {
    for args in [
        vec!["--help"],
        vec!["serve", "--help"],
        vec!["run", "--help"],
        vec!["auth", "--help"],
        vec!["auth", "login", "--help"],
    ] {
        let output = Command::new(coda_exe())
            .args(&args)
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .output()
            .expect("the coda binary runs");
        assert!(output.status.success(), "{args:?} must succeed");
    }

    let output = Command::new(coda_exe())
        .arg("--help")
        .output()
        .expect("the coda binary runs");
    let help = String::from_utf8_lossy(&output.stdout);
    for expected in ["serve", "run", "auth"] {
        assert!(help.contains(expected), "`coda --help` must list {expected}:\n{help}");
    }
}
