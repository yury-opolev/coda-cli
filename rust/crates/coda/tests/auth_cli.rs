//! `coda auth` → **real engine** conformance: what a login actually enables,
//! and what a logout actually takes away.
//!
//! `auth_commands.rs` proves the subcommand is wired up (options, exit codes,
//! disclosure). This file proves the thing that matters afterwards: a
//! credential stored by the *actual* command authenticates an *actual,
//! separately started* engine process against the same profile — and once the
//! actual logout command has run, a fresh engine cannot.
//!
//! # What is real here, and what is not
//!
//! Real: the `coda` binary, the device-code login flow, the credential store
//! (on Windows that is production DPAPI — no backend override is set
//! anywhere), `settings.json`, the engine's own provider selection, its
//! Copilot client, and the JSON-RPC serve seam.
//!
//! Not real, and deliberately so: the *host*. GitHub's device, token, exchange
//! and inference endpoints are a loopback fixture reached through the shipping
//! `GH_COPILOT_*` overrides — the same mechanism an enterprise proxy uses.
//! No DNS lookup, no provider account, no browser (`CODA_AUTH_NO_BROWSER`),
//! no paid completion: the fixture answers a streamed turn itself.
//!
//! # Why the assertions are on the wire
//!
//! Every claim about authentication is checked against the request the
//! provider fixture received — its path, and the `Authorization` header it
//! carried. A test that only checked exit codes would pass for an engine that
//! authenticated with nothing, with a stale token, or against the wrong host.
//!
//! # The Anthropic endpoint seam, and why these tests can exist at all
//!
//! An API-key login always validates before it commits, and that validation is
//! a live model listing. Until the Anthropic base URL had a seam, driving one
//! to a *successful* commit from a test would have meant sending a fabricated
//! key to Anthropic's real endpoint — so this file could not cover it.
//!
//! It now can, through the product's own configuration: `ANTHROPIC_BASE_URL`,
//! resolved once by `coda_auth::service::endpoint` and shared by the
//! pre-commit probe, the post-commit connection check and the engine. That is
//! the same mechanism the `GH_COPILOT_*` overrides are, and it is a user-facing
//! knob, not test scaffolding: nothing here sets a flag the product does not
//! ship. What the tests below assert is the consequence — that all three of
//! those places really do honour it, that the key on the wire is the key the
//! login proved, and that a refused override stops before anything is sent or
//! written.
//!
//! No real key, no real host: the fixture is a loopback socket, the "key" is a
//! fabricated string, and the suite's proxy containment refuses any request
//! that escapes to a real host instead of letting it out.

#[path = "support/copilot_fixture.rs"]
mod copilot_fixture;

use std::path::PathBuf;

use copilot_fixture::{
    open_engine, run_auth, run_auth_with_stdin, run_turn, serve_command, serve_startup_result,
    session_models, DeviceOutcome, FakeCopilotHost, Sandbox, Tokens, ADVERTISED_MODELS,
    ANTHROPIC_ADVERTISED_MODELS, ANTHROPIC_MESSAGES_PATH, ANTHROPIC_MODEL, ANTHROPIC_MODELS_PATH,
    ANTHROPIC_SECOND_MODEL, CHAT_MODEL, MESSAGES_MODEL,
};

fn coda_exe() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_coda"))
}

fn tokens(suffix: &str) -> Tokens {
    Tokens {
        github: format!("ghu_fixture-durable-{suffix}"),
        copilot: format!("fixture-copilot-token-{suffix}"),
        api_key: format!("sk-ant-fixture-{suffix}-0123456789"),
    }
}

/// The env a child needs to reach one fixture instead of github.com.
fn fixture_env(host: &FakeCopilotHost) -> Vec<(&'static str, String)> {
    host.overrides()
}

fn models_of(reply: &serde_json::Value) -> Vec<String> {
    reply["models"]
        .as_array()
        .expect("models is an array")
        .iter()
        .map(|model| model["id"].as_str().expect("a model id").to_owned())
        .collect()
}

/// Every file under `root` that is valid UTF-8, with its path.
///
/// Used to prove a secret is nowhere in the profile. Binary files (the
/// encrypted credential itself) are skipped, which is the point: the assertion
/// is about text a person or a log shipper could read.
fn readable_files(root: &std::path::Path) -> Vec<(PathBuf, String)> {
    let mut found = Vec::new();
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let Ok(entries) = std::fs::read_dir(&dir) else { continue };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                stack.push(path);
            } else if let Ok(text) = std::fs::read_to_string(&path) {
                found.push((path, text));
            }
        }
    }
    found
}

// ─────────────────────────────────────────────────────────────────────────────
// 1. The lifecycle: login → fresh engine authenticates → logout → it cannot
// ─────────────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn a_real_login_authenticates_a_fresh_engine_and_a_logout_takes_it_away() {
    let host = FakeCopilotHost::start(tokens("alpha"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();
    let env = fixture_env(&host);

    // ── The actual command ───────────────────────────────────────────────────
    let login = run_auth(&coda_exe(), sandbox.home(), &["login", "copilot", "--public"], &env);
    assert_eq!(login.code, 0, "{}", login.all());
    assert!(login.stdout.contains("signed in to GitHub Copilot"), "{}", login.all());
    // The connection check is a live, uncached model listing against the host
    // the login authenticated to.
    assert!(
        login
            .stdout
            .contains(&format!("connected to github-copilot; {ADVERTISED_MODELS} models available")),
        "{}",
        login.all()
    );
    // The deployment actually contacted is disclosed, overrides and all.
    assert!(
        login.stdout.contains("endpoint overrides are in force"),
        "{}",
        login.all()
    );
    // Neither token is ever echoed.
    assert!(!login.all().contains(&host.tokens.github), "the durable token was printed");
    assert!(!login.all().contains(&host.tokens.copilot), "the Copilot token was printed");
    // An auth command does not open the diagnostic log.
    assert!(
        !sandbox.home().join(".coda").join("logs").exists(),
        "auth must not open the diagnostic log"
    );

    // ── What went over the wire during the login ─────────────────────────────
    let exchanges = host.requests_for(copilot_fixture::EXCHANGE_PATH);
    assert_eq!(exchanges.len(), 1, "the durable token must be exchanged exactly once");
    assert_eq!(
        exchanges[0].authorization(),
        format!("token {}", host.tokens.github),
        "the exchange must present the durable GitHub token"
    );
    let verification = host.requests_for("/models");
    assert_eq!(verification.len(), 1, "the login verifies with one model listing");
    assert_eq!(
        verification[0].authorization(),
        format!("Bearer {}", host.tokens.copilot),
        "the verification probe must use the exchanged Copilot token"
    );

    // ── What was written ─────────────────────────────────────────────────────
    let settings = sandbox.settings().expect("the login writes settings.json");
    assert_eq!(settings["defaultProvider"], "github-copilot", "{settings}");
    let credential = sandbox.credential_path("github-copilot");
    assert!(credential.is_file(), "no credential file at {credential:?}");
    let at_rest = std::fs::read(&credential).expect("read the credential file");
    assert!(
        !String::from_utf8_lossy(&at_rest).contains(&host.tokens.copilot),
        "the credential is stored in clear text"
    );

    // ── A fresh engine, same profile, no key on its command line ─────────────
    let before_engine = host.request_count();
    let command = serve_command(
        &coda_exe(),
        &sandbox,
        &env,
        &["--model", MESSAGES_MODEL],
    );
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();

    let reply = session_models(&connection).await;
    assert_eq!(reply["source"], "live", "model discovery must be live:\n{}", host.wire_summary());
    assert_eq!(reply["providerId"], "github-copilot", "{reply:#}");
    let ids = models_of(&reply);
    assert!(ids.contains(&MESSAGES_MODEL.to_owned()), "{ids:?}");
    assert!(ids.contains(&CHAT_MODEL.to_owned()), "{ids:?}");

    // A real turn, at the fixture, with the stored credential.
    run_turn(&connection, &mut inbound, "what is 2+2?").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    let served = host.requests();
    let engine_requests = &served[before_engine..];
    assert!(
        !engine_requests.is_empty(),
        "the fresh engine never contacted the provider it was signed in to"
    );
    let inference: Vec<_> =
        engine_requests.iter().filter(|r| r.path == "/v1/messages").collect();
    assert_eq!(inference.len(), 1, "expected exactly one streamed turn: {engine_requests:?}");
    assert_eq!(
        inference[0].authorization(),
        format!("Bearer {}", host.tokens.copilot),
        "the engine's inference request did not carry the stored Copilot token"
    );
    // Identity headers travel with it, or the real API rejects the request
    // with "missing Editor-Version header for IDE auth".
    assert!(!inference[0].header("editor-version").is_empty(), "{:?}", inference[0]);
    assert!(!inference[0].header("copilot-integration-id").is_empty(), "{:?}", inference[0]);
    assert!(inference[0].body.contains("what is 2+2?"), "{}", inference[0].body);
    // The durable GitHub token is never an inference credential.
    assert!(
        !engine_requests
            .iter()
            .any(|r| r.authorization().contains(&host.tokens.github)),
        "the durable GitHub token reached an inference endpoint"
    );

    // ── Nothing in the profile holds either token in clear text ──────────────
    //
    // Covers the diagnostic log the engine (unlike an auth command) does open:
    // an authorization header is not an operational fact.
    for (path, text) in readable_files(sandbox.home()) {
        assert!(!text.contains(&host.tokens.copilot), "the Copilot token is readable in {path:?}");
        assert!(!text.contains(&host.tokens.github), "the durable token is readable in {path:?}");
    }

    // ── The sign-in stayed inside this profile ───────────────────────────────    //
    // A second, empty profile must be signed out even though this machine has
    // just completed a login. Anything machine-global — a keyring service, a
    // shared file — would show up here.
    let elsewhere = Sandbox::new();
    let status = run_auth(&coda_exe(), elsewhere.home(), &["status"], &env);
    assert_eq!(status.code, 0, "{}", status.all());
    assert!(
        status.stdout.to_lowercase().contains("not signed in"),
        "a login leaked out of the profile it was made in:\n{}",
        status.stdout
    );

    // ── And this profile's own report agrees with the engine ─────────────────
    let status = run_auth(&coda_exe(), sandbox.home(), &["status"], &env);
    assert_eq!(status.code, 0, "{}", status.all());
    assert!(status.stdout.contains("github-copilot"), "{}", status.all());
    assert!(
        status.stdout.contains("endpoint overrides are in force"),
        "status must disclose the host it actually resolved:\n{}",
        status.stdout
    );
    assert!(!status.all().contains(&host.tokens.copilot), "{}", status.all());

    // ── The actual logout ────────────────────────────────────────────────────
    let logout = run_auth(&coda_exe(), sandbox.home(), &["logout"], &env);
    assert_eq!(logout.code, 0, "{}", logout.all());
    assert!(
        logout.stdout.contains("Removed the stored credential for GitHub Copilot."),
        "{}",
        logout.all()
    );
    assert!(logout.stdout.contains("The saved provider choice was cleared."), "{}", logout.all());
    assert!(logout.stdout.to_lowercase().contains("nothing was revoked"), "{}", logout.all());
    assert!(!credential.is_file(), "the credential file survived a logout: {credential:?}");
    let settings = sandbox.settings().expect("settings.json still exists");
    assert!(settings.get("defaultProvider").is_none_or(|v| v.is_null()), "{settings}");

    // ── A fresh engine after the logout cannot authenticate ──────────────────
    let after_logout = host.request_count();
    let command = serve_command(&coda_exe(), &sandbox, &env, &["--model", MESSAGES_MODEL]);
    let (engine, _inbound) = open_engine(command).await;
    let reply = session_models(&engine.connection()).await;
    assert_eq!(
        reply["source"], "catalog",
        "a signed-out engine must not report live model discovery:\n{}", host.wire_summary()
    );
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;
    assert_eq!(
        host.request_count(),
        after_logout,
        "a signed-out engine still reached the provider"
    );

    // And asking for that provider by name fails closed rather than starting
    // an engine that cannot authenticate.
    let (code, stderr) =
        serve_startup_result(&coda_exe(), &sandbox, &env, &["--provider", "github-copilot"]).await;
    assert_eq!(code, Some(1), "a named provider with no credential must refuse to start: {stderr}");
    assert!(
        stderr.to_lowercase().contains("provider"),
        "the refusal must name the problem: {stderr}"
    );
    assert_eq!(host.request_count(), after_logout, "a refused startup still contacted the provider");
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Switching hosts: the new credential goes to the new host, and only there
// ─────────────────────────────────────────────────────────────────────────────

/// Signing in again against a different resolved deployment must move both the
/// credential *and* the endpoint together. The failure this rules out is the
/// dangerous half-move: a new token sent to the old host, or the old token
/// sent to the new one.
#[tokio::test]
async fn signing_in_to_another_host_moves_the_engine_and_never_reuses_the_old_token() {
    let first = FakeCopilotHost::start(tokens("first"), DeviceOutcome::Authorize);
    let second = FakeCopilotHost::start(tokens("second"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();

    let login = run_auth(
        &coda_exe(),
        sandbox.home(),
        &["login", "copilot", "--public"],
        &fixture_env(&first),
    );
    assert_eq!(login.code, 0, "{}", login.all());

    // The first host must not be asked for anything *during* the second
    // login either — the baseline is taken before that login runs, not just
    // compared afterwards, so a request the second login made to the first
    // host in passing cannot slip past an assertion that only ever samples
    // the count once everything is already over.
    let first_before_second_login = first.request_count();

    // The second sign-in resolves a different tenant: the saved enterprise
    // domain changes and every endpoint moves with it.
    let login = run_auth(
        &coda_exe(),
        sandbox.home(),
        &["login", "copilot", "--enterprise-domain", "octocorp.invalid"],
        &fixture_env(&second),
    );
    assert_eq!(login.code, 0, "{}", login.all());
    assert!(login.stdout.contains("signed in to GitHub Copilot"), "{}", login.all());
    assert_eq!(
        first.request_count(),
        first_before_second_login,
        "the first host was contacted during the second login"
    );

    // The first host is not asked for anything by the second login.
    let first_after_switch = first.request_count();
    assert!(
        !second
            .requests()
            .iter()
            .any(|r| r.authorization().contains(&first.tokens.github)
                || r.authorization().contains(&first.tokens.copilot)),
        "a token belonging to the first host was sent to the second"
    );

    // A fresh engine follows the *new* credential to the *new* host.
    let command = serve_command(
        &coda_exe(),
        &sandbox,
        &fixture_env(&second),
        &["--model", MESSAGES_MODEL],
    );
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();
    let reply = session_models(&connection).await;
    assert_eq!(reply["source"], "live", "{}", second.wire_summary());
    assert_eq!(reply["providerId"], "github-copilot", "{reply:#}");
    run_turn(&connection, &mut inbound, "what is 2+2?").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    let inference: Vec<_> = second.requests_for("/v1/messages");
    assert_eq!(inference.len(), 1, "the engine did not stream a turn at the new host");
    assert_eq!(
        inference[0].authorization(),
        format!("Bearer {}", second.tokens.copilot),
        "the engine authenticated to the new host with the wrong token"
    );
    assert_eq!(
        first.request_count(),
        first_after_switch,
        "the engine still contacted the host the profile has left"
    );
    assert!(
        !second
            .requests()
            .iter()
            .any(|r| r.authorization().contains(&first.tokens.copilot)),
        "the superseded token was sent to the new host"
    );
}

/// An engine that was already running when the profile moved must not follow
/// it. Its endpoints were fixed when it started, so authenticating the *new*
/// account's token against the *old* host would hand a live credential to a
/// deployment it does not belong to — the mirror image of the failure above,
/// and the one a long-lived engine is exposed to.
///
/// The engine's lifetime is owned here, by the test: an `auth` command never
/// starts, stops or repoints one.
///
/// `multi_thread`: `run_auth` below blocks the calling thread until the
/// switch's `auth` child exits, and it runs while the engine spawned earlier
/// is still up — on the default single-threaded test runtime that would
/// starve the engine's own reader/writer tasks (its stdout drain, its stderr
/// ring) for the whole duration of the blocking call, on the same thread that
/// is supposed to be servicing them.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn an_engine_started_before_a_switch_never_sends_the_new_token_to_the_old_host() {
    let old = FakeCopilotHost::start(tokens("old-host"), DeviceOutcome::Authorize);
    let new = FakeCopilotHost::start(tokens("new-host"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();

    assert_eq!(
        run_auth(&coda_exe(), sandbox.home(), &["login", "copilot", "--public"], &old.overrides())
            .code,
        0
    );

    // A running engine, bound to the old host and the old account.
    let command = serve_command(
        &coda_exe(),
        &sandbox,
        &fixture_env(&old),
        &["--model", MESSAGES_MODEL],
    );
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();
    run_turn(&connection, &mut inbound, "before the switch").await;
    assert_eq!(old.requests_for("/v1/messages").len(), 1, "{}", old.wire_summary());

    // The profile moves to another account on another host, in another
    // process, while that engine is still up.
    let switch = run_auth(
        &coda_exe(),
        sandbox.home(),
        &["login", "copilot", "--public"],
        &new.overrides(),
    );
    assert_eq!(switch.code, 0, "{}", switch.all());

    // Whatever the running engine does next, the new account's token must not
    // reach the old host.
    run_turn(&connection, &mut inbound, "after the switch").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    assert!(
        !old.requests()
            .iter()
            .any(|request| request.authorization().contains(&new.tokens.copilot)
                || request.authorization().contains(&new.tokens.github)),
        "the new account's credential was sent to the host the profile has left:\n{}",
        old.wire_summary()
    );
    // Non-vacuous, and precise about what actually happened: the old host
    // served exactly one turn — the one from before the switch. The turn
    // afterwards produced no request at all, because the running engine's
    // credential source is bound to the account it started with and fails
    // closed once the profile holds a different one.
    assert_eq!(
        old.requests_for("/v1/messages").len(),
        1,
        "the engine issued a second turn at the old host after the profile moved:\n{}",
        old.wire_summary()
    );
    assert!(
        old.requests()
            .iter()
            .filter(|request| !request.authorization().is_empty())
            .all(|request| request.authorization()
                == format!("Bearer {}", old.tokens.copilot)
                || request.authorization() == format!("token {}", old.tokens.github)),
        "the old host received a credential that was not the one it issued:\n{}",
        old.wire_summary()
    );
    // And the running engine did not follow the profile to the new host: its
    // endpoints were fixed when it started.
    assert!(
        new.requests_for("/v1/messages").is_empty(),
        "a running engine repointed itself at the new host:\n{}",
        new.wire_summary()
    );
}

/// A login the user denies must leave the profile exactly as it was — the
/// credential, the saved choice, and therefore the engine that depends on
/// them.
#[tokio::test]
async fn a_denied_login_leaves_the_working_credential_and_a_working_engine() {
    let working = FakeCopilotHost::start(tokens("working"), DeviceOutcome::Authorize);
    let refusing = FakeCopilotHost::start(tokens("refusing"), DeviceOutcome::Deny);
    let sandbox = Sandbox::new();

    let login = run_auth(
        &coda_exe(),
        sandbox.home(),
        &["login", "copilot", "--public"],
        &fixture_env(&working),
    );
    assert_eq!(login.code, 0, "{}", login.all());
    let credential = sandbox.credential_path("github-copilot");
    let before = std::fs::read(&credential).expect("the credential file");
    let settings_before = sandbox.settings().expect("settings.json");

    let denied = run_auth(
        &coda_exe(),
        sandbox.home(),
        &["login", "copilot", "--enterprise-domain", "octocorp.invalid"],
        &fixture_env(&refusing),
    );
    assert_eq!(denied.code, 130, "a denied grant is a cancellation: {}", denied.all());
    assert!(!denied.all().contains(&working.tokens.copilot), "{}", denied.all());

    assert_eq!(
        std::fs::read(&credential).expect("the credential file"),
        before,
        "a denied login rewrote the stored credential"
    );
    assert_eq!(
        sandbox.settings().expect("settings.json"),
        settings_before,
        "a denied login changed the saved provider selection"
    );

    // The engine still works, against the host that is still signed in.
    let before_engine = working.request_count();
    let command = serve_command(
        &coda_exe(),
        &sandbox,
        &fixture_env(&working),
        &["--model", MESSAGES_MODEL],
    );
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();
    assert_eq!(session_models(&connection).await["source"], "live", "{}", working.wire_summary());
    run_turn(&connection, &mut inbound, "what is 2+2?").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    let served = working.requests();
    let inference: Vec<_> =
        served[before_engine..].iter().filter(|r| r.path == "/v1/messages").collect();
    assert_eq!(inference.len(), 1, "the surviving credential no longer starts a turn");
    assert_eq!(
        inference[0].authorization(),
        format!("Bearer {}", working.tokens.copilot)
    );
    assert_eq!(refusing.requests_for("/models").len(), 0, "the refused host was authenticated to");
}

/// A login the *host* refuses part-way through — the user authorized, but the
/// token exchange answered `401` — is a failure, not a downgrade. Nothing may
/// be stored on the strength of it, and the account that works must survive.
#[tokio::test]
async fn a_refused_token_exchange_stores_nothing_and_keeps_the_working_account() {
    let working = FakeCopilotHost::start(tokens("kept"), DeviceOutcome::Authorize);
    let refusing = FakeCopilotHost::start(tokens("refused"), DeviceOutcome::RejectExchange);
    let sandbox = Sandbox::new();

    assert_eq!(
        run_auth(
            &coda_exe(),
            sandbox.home(),
            &["login", "copilot", "--public"],
            &fixture_env(&working)
        )
        .code,
        0
    );
    let credential = sandbox.credential_path("github-copilot");
    let before = std::fs::read(&credential).expect("the credential file");
    let settings_before = sandbox.settings().expect("settings.json");

    let failed = run_auth(
        &coda_exe(),
        sandbox.home(),
        &["login", "copilot", "--public"],
        &fixture_env(&refusing),
    );
    assert_eq!(failed.code, 1, "a refused exchange is an operational failure: {}", failed.all());
    assert!(!failed.stdout.contains("signed in to"), "{}", failed.all());
    assert!(!failed.all().contains(&refusing.tokens.github), "{}", failed.all());

    assert_eq!(
        std::fs::read(&credential).expect("the credential file"),
        before,
        "a refused exchange rewrote the stored credential"
    );
    assert_eq!(
        sandbox.settings().expect("settings.json"),
        settings_before,
        "a refused exchange changed the saved provider selection"
    );
    // The durable token must not have been stored as a fallback bearer: the
    // refusing host is never asked for inference at all.
    assert_eq!(refusing.requests_for("/models").len(), 0, "{}", refusing.wire_summary());

    // The engine still authenticates as the account that was working.
    let before_engine = working.request_count();
    let command = serve_command(
        &coda_exe(),
        &sandbox,
        &fixture_env(&working),
        &["--model", MESSAGES_MODEL],
    );
    let (engine, _inbound) = open_engine(command).await;
    assert_eq!(
        session_models(&engine.connection()).await["source"],
        "live",
        "{}",
        working.wire_summary()
    );
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;
    assert!(working.request_count() > before_engine, "{}", working.wire_summary());
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. The environment selection
// ─────────────────────────────────────────────────────────────────────────────

/// `--use-env` must prove itself *before* it displaces anything. With no
/// exported key there is nothing to prove, so the account that currently works
/// stays — and the engine that depends on it keeps working.
#[tokio::test]
async fn an_environment_login_without_the_variable_displaces_nothing() {
    let host = FakeCopilotHost::start(tokens("env"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();
    let env = fixture_env(&host);

    assert_eq!(
        run_auth(&coda_exe(), sandbox.home(), &["login", "copilot", "--public"], &env).code,
        0
    );
    let credential = sandbox.credential_path("github-copilot");
    let before = std::fs::read(&credential).expect("the credential file");
    let settings_before = sandbox.settings().expect("settings.json");

    // `run_auth` clears ANTHROPIC_API_KEY, so this child genuinely has none.
    let attempt = run_auth(&coda_exe(), sandbox.home(), &["login", "api-key", "--use-env"], &env);
    assert_eq!(attempt.code, 1, "{}", attempt.all());
    assert!(attempt.stderr.contains("ANTHROPIC_API_KEY"), "{}", attempt.all());
    assert!(!attempt.all().to_lowercase().contains("signed in to"), "{}", attempt.all());

    assert_eq!(
        std::fs::read(&credential).expect("the credential file"),
        before,
        "a failed environment login removed the working credential"
    );
    assert_eq!(
        sandbox.settings().expect("settings.json"),
        settings_before,
        "a failed environment login changed the saved provider selection"
    );

    // Still signed in, on a fresh engine.
    let command = serve_command(&coda_exe(), &sandbox, &env, &["--model", MESSAGES_MODEL]);
    let (engine, _inbound) = open_engine(command).await;
    let reply = session_models(&engine.connection()).await;
    assert_eq!(reply["source"], "live", "{}", host.wire_summary());
    assert_eq!(reply["providerId"], "github-copilot", "{reply:#}");
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;
}

/// The state an environment selection leaves behind — a saved choice with no
/// stored key — is a real state a profile can be in, and both commands and the
/// engine have to handle it honestly:
///
/// * a logout clears the choice even though there was no blob to remove, and
///   says the variable is still set rather than claiming a global sign-out;
/// * a fresh engine in a shell *without* the variable is not signed in, and
///   asking for that provider by name fails closed instead of quietly using
///   another account that is sitting right there.
#[tokio::test]
async fn a_saved_environment_choice_signs_out_cleanly_and_never_covers_for_a_missing_key() {
    let host = FakeCopilotHost::start(tokens("mixed"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();
    let env = fixture_env(&host);

    // A profile that selected the exported key: a choice, and no stored key.
    sandbox.write_settings(serde_json::json!({ "defaultProvider": "anthropic" }));

    // No variable in this shell: not signed in, and no fallback to anything.
    let (code, stderr) =
        serve_startup_result(&coda_exe(), &sandbox, &env, &["--provider", "anthropic"]).await;
    assert_eq!(code, Some(1), "a chosen provider with no key must refuse to start: {stderr}");

    let command = serve_command(&coda_exe(), &sandbox, &env, &[]);
    let (engine, _inbound) = open_engine(command).await;
    let reply = session_models(&engine.connection()).await;
    assert_eq!(reply["source"], "catalog", "an unauthenticated engine must not list live:\n{}", host.wire_summary());
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;
    assert_eq!(host.request_count(), 0, "nothing should have been asked of any provider");

    // Signing out of that choice removes the choice, reports the variable, and
    // claims nothing else.
    let mut with_key = env.clone();
    with_key.push(("ANTHROPIC_API_KEY", "sk-env-sentinel".to_owned()));
    let logout = run_auth(&coda_exe(), sandbox.home(), &["logout"], &with_key);
    assert_eq!(logout.code, 0, "{}", logout.all());
    assert!(logout.stdout.contains("No stored credential was removed."), "{}", logout.all());
    assert!(logout.stdout.contains("The saved provider choice was cleared."), "{}", logout.all());
    assert!(logout.stdout.contains("ANTHROPIC_API_KEY is still set"), "{}", logout.all());
    assert!(!logout.all().contains("sk-env-sentinel"), "{}", logout.all());

    let settings = sandbox.settings().expect("settings.json");
    assert!(settings.get("defaultProvider").is_none_or(|v| v.is_null()), "{settings}");
}

/// A saved choice naming a provider with no credential must not be quietly
/// satisfied by a *different* account that happens to be stored — and naming
/// that other account explicitly must still work.
///
/// This is the fail-closed rule from both directions in one profile: the
/// stored Copilot credential is sitting right there while the saved choice
/// says Anthropic, and no exported key exists.
#[tokio::test]
async fn a_saved_choice_for_a_provider_with_no_credential_never_borrows_another_account() {
    let host = FakeCopilotHost::start(tokens("precedence"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();
    let env = fixture_env(&host);

    assert_eq!(
        run_auth(&coda_exe(), sandbox.home(), &["login", "copilot", "--public"], &env).code,
        0
    );
    // The saved choice is moved to a provider this profile has no credential
    // for, without touching the stored Copilot credential.
    sandbox.write_settings(serde_json::json!({ "defaultProvider": "anthropic" }));
    let after_login = host.request_count();

    // No explicit request: the choice stands, and it is unsatisfiable, so the
    // engine starts unauthenticated rather than as GitHub Copilot.
    //
    // The discriminator is deliberately *not* `providerId`: with no client
    // wired, that field carries the engine's assumed-provider label for the
    // catalogue it is listing, which is not a claim about an account. What
    // proves the account was not borrowed is that no listing was live and the
    // stored credential never authenticated anything.
    let command = serve_command(&coda_exe(), &sandbox, &env, &["--model", MESSAGES_MODEL]);
    let (engine, _inbound) = open_engine(command).await;
    let reply = session_models(&engine.connection()).await;
    assert_eq!(
        reply["source"], "catalog",
        "an unsatisfiable saved choice must not produce a live listing:\n{}",
        host.wire_summary()
    );
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;
    assert_eq!(
        host.request_count(),
        after_login,
        "the stored Copilot credential was used to satisfy another provider's choice:\n{}",
        host.wire_summary()
    );

    // The same is true of a turn: the lazy wiring a prompt performs re-runs
    // the same refused selection instead of reaching for what is stored.
    let command = serve_command(&coda_exe(), &sandbox, &env, &["--model", MESSAGES_MODEL]);
    let (engine, mut inbound) = open_engine(command).await;
    run_turn(&engine.connection(), &mut inbound, "what is 2+2?").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;
    assert_eq!(
        host.request_count(),
        after_login,
        "a prompt borrowed the stored credential for a provider that was not chosen:\n{}",
        host.wire_summary()
    );

    // Asked for by name, the stored account is exactly what starts.
    let command = serve_command(
        &coda_exe(),
        &sandbox,
        &env,
        &["--provider", "github-copilot", "--model", MESSAGES_MODEL],
    );
    let (engine, _inbound) = open_engine(command).await;
    let reply = session_models(&engine.connection()).await;
    assert_eq!(reply["providerId"], "github-copilot", "{reply:#}");
    assert_eq!(reply["source"], "live", "{}", host.wire_summary());
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. The production Windows credential store
// ─────────────────────────────────────────────────────────────────────────────
/// A credential written the way the .NET Windows build writes it must
/// authenticate a real engine.
///
/// What is and is not proven here, precisely:
///
/// * **Proven:** the *document* is the .NET `Credential` shape — camelCase
///   field names and `"OAuth"` for the kind, written out literally in this
///   test rather than produced by the Rust serializer. The *ciphertext* comes
///   from the actual .NET DPAPI call —
///   `ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)`,
///   the exact call `LlmAuth.Storage.Windows.DpapiTokenStore.SetAsync` makes —
///   run out-of-process through Windows PowerShell
///   ([`copilot_fixture::dotnet_dpapi_protect`]), not the Rust `DpapiStore`
///   writer: seeding this document with the Rust writer would only prove the
///   Rust reader agrees with the Rust writer, a tautology. The *file name* is
///   the one .NET's `PathFor` produces for `llmauth:github-copilot`, also
///   written out literally. A real engine process then reads that ciphertext
///   and authenticates with the token inside, so what is proven is the Rust
///   reader against .NET's own writer.
/// * **Not proven, and not claimable:** that the two implementations produce
///   *identical* ciphertext for the same plaintext. DPAPI's output is salted
///   and keyed to the Windows user, so there is no fixed known-answer blob
///   either implementation could ship; what this test proves instead — that
///   each side's writer is readable by the other's reader — is the
///   compatibility guarantee that actually matters.
#[cfg(windows)]
#[tokio::test]
async fn a_dotnet_shaped_credential_in_the_production_store_authenticates_a_fresh_engine() {
    let host = FakeCopilotHost::start(
        Tokens {
            github: "ghu_fixture-durable-dotnet".to_owned(),
            copilot: "fixture-dotnet-seeded-token".to_owned(),
            api_key: "sk-ant-fixture-dotnet-0123456789".to_owned(),
        },
        DeviceOutcome::Authorize,
    );
    let sandbox = Sandbox::new();
    let env = fixture_env(&host);

    // The .NET document, spelled out. Far-future expiry so nothing refreshes:
    // this test is about reading what is there.
    let document = r#"{"providerId":"github-copilot","kind":"OAuth","accessToken":"fixture-dotnet-seeded-token","refreshToken":"ghu_fixture-durable-dotnet","expiresAt":"2999-01-01T00:00:00+00:00","scopes":["user:inference"]}"#;

    let directory = sandbox.home().join(".coda").join("credentials");
    std::fs::create_dir_all(&directory).expect("credential directory");
    let ciphertext = copilot_fixture::dotnet_dpapi_protect(&directory, document);
    let expected = directory.join("llmauth_github-copilot.cred");
    std::fs::write(&expected, &ciphertext).expect("write the .NET-produced credential file");

    // The .NET file name, spelled out rather than derived.
    assert!(expected.is_file(), "the .NET file name was not produced: {expected:?}");
    let at_rest = std::fs::read(&expected).expect("read the credential file");
    assert!(
        !String::from_utf8_lossy(&at_rest).contains("fixture-dotnet-seeded-token"),
        "DPAPI did not encrypt the credential"
    );

    sandbox.write_settings(serde_json::json!({ "defaultProvider": "github-copilot" }));

    let command = serve_command(&coda_exe(), &sandbox, &env, &["--model", MESSAGES_MODEL]);
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();
    let reply = session_models(&connection).await;
    assert_eq!(reply["source"], "live", "the engine could not read the seeded credential:\n{}", host.wire_summary());
    run_turn(&connection, &mut inbound, "what is 2+2?").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    let inference = host.requests_for("/v1/messages");
    assert_eq!(inference.len(), 1, "the seeded credential never reached an inference request");
    assert_eq!(
        inference[0].authorization(),
        format!("Bearer {}", host.tokens.copilot),
        "the engine did not authenticate with the seeded .NET credential"
    );

    // And `auth status` agrees with the engine about the same store.
    let status = run_auth(&coda_exe(), sandbox.home(), &["status"], &env);
    assert_eq!(status.code, 0, "{}", status.all());
    assert!(status.stdout.contains("GitHub Copilot"), "{}", status.all());
    assert!(!status.all().contains(&host.tokens.copilot), "{}", status.all());
}

/// A credential file that cannot be decrypted is *unreadable*, never *absent*.
/// The engine must refuse the provider that was asked for rather than quietly
/// authenticating as a different account that happens to be available.
#[cfg(windows)]
#[tokio::test]
async fn an_unreadable_credential_refuses_the_named_provider_and_never_falls_back() {
    let host = FakeCopilotHost::start(tokens("corrupt"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();

    let directory = sandbox.home().join(".coda").join("credentials");
    std::fs::create_dir_all(&directory).expect("credential directory");
    std::fs::write(
        directory.join("llmauth_github-copilot.cred"),
        b"this is not DPAPI ciphertext",
    )
    .expect("seed an unreadable credential");
    sandbox.write_settings(serde_json::json!({ "defaultProvider": "github-copilot" }));

    // An ambient key is deliberately present: it is a *different account*, and
    // must never stand in for the one that was named.
    let mut env = fixture_env(&host);
    env.push(("ANTHROPIC_API_KEY", "sk-ambient-must-not-be-used".to_owned()));

    let (code, stderr) =
        serve_startup_result(&coda_exe(), &sandbox, &env, &["--provider", "github-copilot"]).await;
    assert_eq!(code, Some(1), "an unreadable credential must fail closed: {stderr}");
    assert!(!stderr.contains("sk-ambient-must-not-be-used"), "{stderr}");
    assert_eq!(host.request_count(), 0, "a refused startup contacted the provider");

    // The report says the same thing, and still does not invent a fallback.
    let status = run_auth(&coda_exe(), sandbox.home(), &["status"], &env);
    assert_eq!(status.code, 0, "{}", status.all());
    assert!(!status.all().contains("sk-ambient-must-not-be-used"), "{}", status.all());
    assert!(
        status.stdout.to_lowercase().contains("could not be read")
            || status.stdout.to_lowercase().contains("unreadable"),
        "an unreadable credential must not read as signed out:\n{}",
        status.stdout
    );
}

/// A credential *directory* this build cannot safely use is not an empty
/// profile either. The 32-byte key file is how the AES format identifies
/// itself; one of the wrong length means credentials may be sitting there
/// unreadable, so the store refuses to open rather than presenting a
/// logged-out profile that a login would overwrite.
#[cfg(windows)]
#[tokio::test]
async fn a_credential_store_that_cannot_be_opened_is_never_reported_as_signed_out() {
    let host = FakeCopilotHost::start(tokens("keyfault"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();

    let directory = sandbox.home().join(".coda").join("credentials");
    std::fs::create_dir_all(&directory).expect("credential directory");
    std::fs::write(directory.join("key.bin"), b"7 bytes").expect("seed a wrong-sized key");
    std::fs::write(directory.join("llmauth_github-copilot.cred"), b"opaque")
        .expect("seed a credential beside it");
    sandbox.write_settings(serde_json::json!({ "defaultProvider": "github-copilot" }));

    let mut env = fixture_env(&host);
    env.push(("ANTHROPIC_API_KEY", "sk-ambient-must-not-be-used".to_owned()));

    let status = run_auth(&coda_exe(), sandbox.home(), &["status"], &env);
    assert_eq!(status.code, 1, "an unopenable store is an operational failure: {}", status.all());
    assert!(
        !status.all().to_lowercase().contains("not signed in"),
        "an unopenable store must not read as a signed-out profile:\n{}",
        status.all()
    );
    assert!(!status.all().contains("sk-ambient-must-not-be-used"), "{}", status.all());

    // A login must not run either: it would write into a directory whose
    // existing contents this build cannot account for.
    let login = run_auth(&coda_exe(), sandbox.home(), &["login", "copilot", "--public"], &env);
    assert_eq!(login.code, 1, "{}", login.all());
    assert_eq!(host.request_count(), 0, "a refused login still started a device flow");
    assert!(
        std::fs::read(directory.join("key.bin")).expect("the key file") == b"7 bytes",
        "the unreadable key file was destroyed"
    );
    assert!(
        std::fs::read(directory.join("llmauth_github-copilot.cred")).expect("the credential file")
            == b"opaque",
        "a refused login overwrote the corrupt credential file it could not account for"
    );

    // And the engine fails closed for the provider that was asked for.
    let (code, stderr) =
        serve_startup_result(&coda_exe(), &sandbox, &env, &["--provider", "github-copilot"]).await;
    assert_eq!(code, Some(1), "an unopenable store must fail closed: {stderr}");
    assert_eq!(host.request_count(), 0, "a refused startup contacted the provider");
}


// ─────────────────────────────────────────────────────────────────────────────
// 5. The Anthropic console key, end to end, through the endpoint seam
// ─────────────────────────────────────────────────────────────────────────────

/// The env that points an Anthropic API-key request at the fixture: the one
/// shipping variable, and nothing else.
fn anthropic_env(host: &FakeCopilotHost) -> Vec<(&'static str, String)> {
    host.anthropic_overrides()
}

/// A GET the fixture served for the Anthropic model list.
fn anthropic_model_listings(host: &FakeCopilotHost) -> Vec<copilot_fixture::Recorded> {
    host.requests_for(ANTHROPIC_MODELS_PATH)
}

/// The real command, with the key arriving on stdin exactly as a
/// non-interactive host supplies it, all the way to a fresh engine spending
/// that key at the same host.
///
/// The chain being proved, in one profile:
///
/// 1. `auth login api-key --api-key-stdin` reads one line from stdin — never
///    an argv, never a variable, never an echoed terminal;
/// 2. the **mandatory pre-commit probe** runs against the endpoint
///    `ANTHROPIC_BASE_URL` names, which is the whole reason this test can
///    exist without a real key or a real host;
/// 3. the commit writes a credential into the *production* store (on Windows
///    that is DPAPI: no backend override is set anywhere in this suite);
/// 4. the post-commit connection check runs against the same endpoint;
/// 5. a **separately started** engine, with no `--api-key` and no `--endpoint`
///    on its command line, finds that credential, resolves the same endpoint
///    from the same variable, lists models live and streams a turn with the
///    key on it.
#[tokio::test]
async fn an_api_key_login_from_stdin_authenticates_a_fresh_engine_at_the_configured_endpoint() {
    let host = FakeCopilotHost::start(tokens("apikey"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();
    let env = anthropic_env(&host);
    let key = host.tokens.api_key.clone();

    // ── The actual command ───────────────────────────────────────────────────
    let login = run_auth_with_stdin(
        &coda_exe(),
        sandbox.home(),
        &["login", "api-key", "--api-key-stdin"],
        &env,
        Some(&key),
    );
    assert_eq!(login.code, 0, "{}", login.all());
    assert!(
        login
            .all()
            .contains(&format!("connected to anthropic; {ANTHROPIC_ADVERTISED_MODELS} models available")),
        "{}",
        login.all()
    );
    // The override is disclosed — by host, before the key was read and again
    // after the commit — and the key itself never is.
    assert!(login.all().contains("ANTHROPIC_BASE_URL"), "{}", login.all());
    assert!(login.all().contains("127.0.0.1"), "{}", login.all());
    assert!(!login.all().contains(&key), "the API key was printed:\n{}", login.all());

    // ── What went over the wire ──────────────────────────────────────────────
    let listings = anthropic_model_listings(&host);
    assert_eq!(
        listings.len(),
        2,
        "expected the mandatory pre-commit probe and the post-commit check:\n{}",
        host.wire_summary()
    );
    for listing in &listings {
        assert_eq!(listing.method, "GET", "{}", host.wire_summary());
        assert_eq!(
            listing.header("x-api-key"),
            key,
            "a model listing did not carry the key being signed in with:\n{}",
            host.wire_summary()
        );
        assert_eq!(listing.authorization(), "", "an API key must not be sent as a bearer token");
    }

    // ── What was written ─────────────────────────────────────────────────────
    let settings = sandbox.settings().expect("the login writes settings.json");
    assert_eq!(settings["defaultProvider"], "anthropic", "{settings}");
    let credential = sandbox.credential_path("anthropic-api-key");
    assert!(credential.is_file(), "no credential file at {credential:?}");
    let at_rest = std::fs::read(&credential).expect("read the credential file");
    assert!(
        !String::from_utf8_lossy(&at_rest).contains(&key),
        "the API key is stored in clear text"
    );
    for (path, text) in readable_files(sandbox.home()) {
        assert!(!text.contains(&key), "the API key is readable in {path:?}");
    }

    // ── A fresh engine, same profile, no key and no endpoint on its argv ─────
    let before_engine = host.request_count();
    let command = serve_command(&coda_exe(), &sandbox, &env, &["--model", ANTHROPIC_MODEL]);
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();

    let reply = session_models(&connection).await;
    assert_eq!(reply["source"], "live", "model discovery must be live:\n{}", host.wire_summary());
    assert_eq!(reply["providerId"], "anthropic", "{reply:#}");
    let ids = models_of(&reply);
    assert!(ids.contains(&ANTHROPIC_MODEL.to_owned()), "{ids:?}");
    assert!(ids.contains(&ANTHROPIC_SECOND_MODEL.to_owned()), "{ids:?}");

    run_turn(&connection, &mut inbound, "what is 2+2?").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    let served = host.requests();
    let inference: Vec<_> = served[before_engine..]
        .iter()
        .filter(|request| request.path == ANTHROPIC_MESSAGES_PATH)
        .collect();
    assert_eq!(inference.len(), 1, "expected one streamed turn:\n{}", host.wire_summary());
    assert_eq!(
        inference[0].header("x-api-key"),
        key,
        "the engine's inference request did not carry the stored key:\n{}",
        host.wire_summary()
    );
    assert!(inference[0].body.contains("what is 2+2?"), "{}", inference[0].body);

    // ── And the status report names the host, never the key ──────────────────
    let status = run_auth(&coda_exe(), sandbox.home(), &["status"], &env);
    assert_eq!(status.code, 0, "{}", status.all());
    assert!(status.all().contains("ANTHROPIC_BASE_URL"), "{}", status.all());
    assert!(!status.all().contains(&key), "{}", status.all());
}

/// `--use-env` stores **no key**, and what it validates has to be exactly what
/// the engine later sends.
///
/// The exported value is deliberately padded. The login normalises it before
/// probing; an engine that normalised differently — or not at all — would send
/// a value the fixture does not accept, so "the pre-commit check passed" would
/// be a claim about a key nothing can actually use.
///
/// Also proved here: no credential blob is written, the choice *is*, a shell
/// without the variable is not signed in and reaches nothing, and the actual
/// logout clears the choice while saying the variable is still there.
#[tokio::test]
async fn an_environment_api_key_login_stores_nothing_and_the_engine_sends_the_normalised_key() {
    let host = FakeCopilotHost::start(tokens("useenv"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();
    let key = host.tokens.api_key.clone();
    // Whitespace and control characters on both ends: a real export picks
    // these up from a script or a pasted line.
    let padded = format!("\u{1}\r\n  {key} \t\u{2}");

    let mut env = anthropic_env(&host);
    env.push(("ANTHROPIC_API_KEY", padded.clone()));

    let login = run_auth(&coda_exe(), sandbox.home(), &["login", "api-key", "--use-env"], &env);
    assert_eq!(login.code, 0, "{}", login.all());
    assert!(login.all().contains("no API key was stored"), "{}", login.all());
    assert!(
        login
            .all()
            .contains(&format!("connected to anthropic; {ANTHROPIC_ADVERTISED_MODELS} models available")),
        "{}",
        login.all()
    );
    assert!(!login.all().contains(&key), "the exported key was printed:\n{}", login.all());

    // Nothing was stored: the choice is in settings, and no readable file
    // anywhere in the profile carries the key.
    let settings = sandbox.settings().expect("the login writes settings.json");
    assert_eq!(settings["defaultProvider"], "anthropic", "{settings}");
    let credential = sandbox.credential_path("anthropic-api-key");
    if credential.is_file() {
        let at_rest = std::fs::read(&credential).expect("read the credential file");
        assert!(
            !String::from_utf8_lossy(&at_rest).contains(&key),
            "an environment login wrote the key it promised not to store"
        );
    }
    for (path, text) in readable_files(sandbox.home()) {
        assert!(!text.contains(&key), "the API key is readable in {path:?}");
    }

    // Both probes carried the *normalised* value, not the padded one.
    let listings = anthropic_model_listings(&host);
    assert_eq!(listings.len(), 2, "{}", host.wire_summary());
    for listing in &listings {
        assert_eq!(listing.header("x-api-key"), key, "{}", host.wire_summary());
    }

    // ── A fresh engine, same environment ─────────────────────────────────────
    let before_engine = host.request_count();
    let command = serve_command(&coda_exe(), &sandbox, &env, &["--model", ANTHROPIC_MODEL]);
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();
    let reply = session_models(&connection).await;
    assert_eq!(reply["source"], "live", "{}", host.wire_summary());
    assert_eq!(reply["providerId"], "anthropic", "{reply:#}");
    run_turn(&connection, &mut inbound, "what is 2+2?").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    let served = host.requests();
    let inference: Vec<_> = served[before_engine..]
        .iter()
        .filter(|request| request.path == ANTHROPIC_MESSAGES_PATH)
        .collect();
    assert_eq!(inference.len(), 1, "{}", host.wire_summary());
    assert_eq!(
        inference[0].header("x-api-key"),
        key,
        "the engine sent a key that is not the one the login validated:\n{}",
        host.wire_summary()
    );

    // ── The same profile in a shell without the variable ─────────────────────
    let without_key = anthropic_env(&host);
    let before_unauthenticated = host.request_count();
    let (code, stderr) =
        serve_startup_result(&coda_exe(), &sandbox, &without_key, &["--provider", "anthropic"])
            .await;
    assert_eq!(code, Some(1), "a chosen provider with no key must refuse to start: {stderr}");

    let command = serve_command(&coda_exe(), &sandbox, &without_key, &[]);
    let (engine, _inbound) = open_engine(command).await;
    let reply = session_models(&engine.connection()).await;
    assert_eq!(
        reply["source"], "catalog",
        "an engine with no key must not list live:\n{}",
        host.wire_summary()
    );
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;
    assert_eq!(
        host.request_count(),
        before_unauthenticated,
        "an engine with no key still reached the endpoint:\n{}",
        host.wire_summary()
    );

    // ── The actual logout: the choice goes, the variable is reported ─────────
    let logout = run_auth(&coda_exe(), sandbox.home(), &["logout"], &env);
    assert_eq!(logout.code, 0, "{}", logout.all());
    assert!(logout.stdout.contains("The saved provider choice was cleared."), "{}", logout.all());
    assert!(logout.stdout.contains("ANTHROPIC_API_KEY is still set"), "{}", logout.all());
    assert!(!logout.all().contains(&key), "{}", logout.all());
    let settings = sandbox.settings().expect("settings.json");
    assert!(settings.get("defaultProvider").is_none_or(|v| v.is_null()), "{settings}");
}

/// A stored key, signed out for real: the blob goes, and a fresh engine in a
/// shell with no variable has nothing to authenticate with and reaches nothing.
#[tokio::test]
async fn signing_out_of_a_stored_api_key_leaves_a_fresh_engine_with_no_way_in() {
    let host = FakeCopilotHost::start(tokens("apikeyout"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();
    let env = anthropic_env(&host);
    let key = host.tokens.api_key.clone();

    assert_eq!(
        run_auth_with_stdin(
            &coda_exe(),
            sandbox.home(),
            &["login", "api-key", "--api-key-stdin"],
            &env,
            Some(&key),
        )
        .code,
        0
    );
    assert!(sandbox.credential_path("anthropic-api-key").is_file());

    let logout = run_auth(&coda_exe(), sandbox.home(), &["logout"], &env);
    assert_eq!(logout.code, 0, "{}", logout.all());
    assert!(!sandbox.credential_path("anthropic-api-key").is_file(), "the credential survived");
    assert!(!logout.all().contains(&key), "{}", logout.all());

    let after_logout = host.request_count();
    let (code, stderr) =
        serve_startup_result(&coda_exe(), &sandbox, &env, &["--provider", "anthropic"]).await;
    assert_eq!(code, Some(1), "a signed-out provider must refuse to start: {stderr}");

    let command = serve_command(&coda_exe(), &sandbox, &env, &["--model", ANTHROPIC_MODEL]);
    let (engine, _inbound) = open_engine(command).await;
    let reply = session_models(&engine.connection()).await;
    assert_eq!(
        reply["source"], "catalog",
        "a signed-out engine must not report live model discovery:\n{}",
        host.wire_summary()
    );
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;
    assert_eq!(
        host.request_count(),
        after_logout,
        "a signed-out engine still reached the endpoint:\n{}",
        host.wire_summary()
    );
}

/// An environment login checks *before* it deletes.
///
/// The profile is signed in to Copilot and working; the exported key is one
/// the endpoint refuses. The pre-commit probe is what stands between "switch
/// providers" and "delete the account that works for one that does not", so
/// the refusal must leave both the stored credential and the saved choice
/// byte-for-byte as they were.
#[tokio::test]
async fn a_refused_environment_key_never_displaces_the_account_that_works() {
    let host = FakeCopilotHost::start(tokens("refused"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();
    let mut env = fixture_env(&host);
    env.extend(host.anthropic_overrides());

    assert_eq!(
        run_auth(&coda_exe(), sandbox.home(), &["login", "copilot", "--public"], &env).code,
        0
    );
    let credential = sandbox.credential_path("github-copilot");
    let before = std::fs::read(&credential).expect("the credential file");
    let settings_before = sandbox.settings().expect("settings.json");

    let mut refused = env.clone();
    refused.push(("ANTHROPIC_API_KEY", "sk-ant-NOT-THE-FIXTURE-KEY".to_owned()));
    let attempt = run_auth(&coda_exe(), sandbox.home(), &["login", "api-key", "--use-env"], &refused);
    assert_eq!(attempt.code, 1, "{}", attempt.all());
    assert!(!attempt.all().to_lowercase().contains("signed in to"), "{}", attempt.all());
    assert!(!attempt.all().contains("sk-ant-NOT-THE-FIXTURE-KEY"), "{}", attempt.all());

    assert_eq!(
        std::fs::read(&credential).expect("the credential file"),
        before,
        "a refused environment login removed the working credential"
    );
    assert_eq!(
        sandbox.settings().expect("settings.json"),
        settings_before,
        "a refused environment login changed the saved provider selection"
    );

    // Still signed in to Copilot, on a fresh engine.
    let command = serve_command(&coda_exe(), &sandbox, &env, &["--model", MESSAGES_MODEL]);
    let (engine, _inbound) = open_engine(command).await;
    let reply = session_models(&engine.connection()).await;
    assert_eq!(reply["source"], "live", "{}", host.wire_summary());
    assert_eq!(reply["providerId"], "github-copilot", "{reply:#}");
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;
}

/// A refused `ANTHROPIC_BASE_URL` fails **before** anything is sent or
/// written, in the command and in the engine alike — and never falls back to
/// Anthropic's own host, which is the failure this rule exists to prevent.
///
/// The engine is started as `--provider anthropic`, because that is the
/// configuration the refusal is *about*: the variable redirects Anthropic
/// API-key requests and nothing else, so an engine that is going to spend an
/// API key must refuse to start, while one signed in to another account must
/// not (proved by
/// [`a_refused_endpoint_override_never_stops_a_copilot_session`]).
///
/// The shapes are the dangerous ones: plaintext to somewhere that is not this
/// machine, credentials in the authority, and a query string (a base URL is
/// not the place for one, and a rejected value must not be echoed back).
#[tokio::test]
async fn a_refused_endpoint_override_stops_before_the_network_and_before_the_store() {
    let host = FakeCopilotHost::start(tokens("badurl"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();
    let copilot_env = fixture_env(&host);

    // A working Copilot account to protect.
    assert_eq!(
        run_auth(&coda_exe(), sandbox.home(), &["login", "copilot", "--public"], &copilot_env).code,
        0
    );
    let credential = sandbox.credential_path("github-copilot");
    let before = std::fs::read(&credential).expect("the credential file");
    let settings_before = sandbox.settings().expect("settings.json");
    let quiet = host.request_count();

    for bad in [
        "http://gateway.example.com",
        "https://user:pw@gateway.example.com",
        "https://gateway.example.com/?key=LEAKED",
        "ftp://gateway.example.com",
        "not-a-url",
    ] {
        let mut env = copilot_env.clone();
        env.push(("ANTHROPIC_BASE_URL", bad.to_owned()));
        env.push(("ANTHROPIC_API_KEY", host.tokens.api_key.clone()));

        // The command refuses, names the variable, and echoes nothing.
        let attempt =
            run_auth(&coda_exe(), sandbox.home(), &["login", "api-key", "--use-env"], &env);
        assert_eq!(attempt.code, 1, "{bad} was accepted:\n{}", attempt.all());
        assert!(attempt.all().contains("ANTHROPIC_BASE_URL"), "{bad}:\n{}", attempt.all());
        assert!(!attempt.all().contains("LEAKED"), "{bad}:\n{}", attempt.all());
        assert!(!attempt.all().contains(&host.tokens.api_key), "{bad}:\n{}", attempt.all());

        // `auth status` diagnoses it instead of failing, and refreshes nothing.
        let status = run_auth(&coda_exe(), sandbox.home(), &["status"], &env);
        assert_eq!(status.code, 0, "{bad}:\n{}", status.all());
        assert!(status.all().contains("ANTHROPIC_BASE_URL"), "{bad}:\n{}", status.all());
        assert!(!status.all().contains("LEAKED"), "{bad}:\n{}", status.all());

        // An engine that is going to spend an Anthropic API key refuses to
        // start at all rather than resolving elsewhere.
        let (code, stderr) =
            serve_startup_result(&coda_exe(), &sandbox, &env, &["--provider", "anthropic"]).await;
        assert_eq!(code, Some(1), "{bad} started an engine: {stderr}");
        assert!(
            stderr.contains("ANTHROPIC_BASE_URL"),
            "{bad}: the refusal must name the variable it refused: {stderr}"
        );
        assert!(!stderr.contains("LEAKED"), "{bad}: {stderr}");
        assert!(!stderr.contains(&host.tokens.api_key), "{bad}: {stderr}");
    }

    // Nothing was contacted and nothing was touched.
    assert_eq!(
        host.request_count(),
        quiet,
        "a refused endpoint still produced a request:\n{}",
        host.wire_summary()
    );
    assert_eq!(
        std::fs::read(&credential).expect("the credential file"),
        before,
        "a refused endpoint mutated the credential store"
    );
    assert_eq!(
        sandbox.settings().expect("settings.json"),
        settings_before,
        "a refused endpoint mutated the saved settings"
    );

    // And with no override at all, the same profile is untouched and working.
    let command = serve_command(&coda_exe(), &sandbox, &copilot_env, &["--model", MESSAGES_MODEL]);
    let (engine, _inbound) = open_engine(command).await;
    let reply = session_models(&engine.connection()).await;
    assert_eq!(reply["source"], "live", "{}", host.wire_summary());
    assert_eq!(reply["providerId"], "github-copilot", "{reply:#}");
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;
}

/// A refused `ANTHROPIC_BASE_URL` bars Anthropic **API-key** requests and
/// nothing else: a profile signed in to Copilot starts, discovers models
/// live, and runs a real turn with the variable set to a value the resolver
/// refuses.
///
/// This is the scope rule with teeth. The variable is ordinary user
/// configuration for one identity; refusing to start *every* engine because
/// it is malformed would take away the account that works — the same mistake
/// `a_refused_environment_key_never_displaces_the_account_that_works` guards
/// in the login. An exported `ANTHROPIC_API_KEY` is set too, so this also
/// proves the saved Copilot choice is respected rather than displaced by an
/// ambient key that could not be spent anyway.
#[tokio::test]
async fn a_refused_endpoint_override_never_stops_a_copilot_session() {
    let host = FakeCopilotHost::start(tokens("badurl-copilot"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();
    let mut env = fixture_env(&host);

    let login = run_auth(&coda_exe(), sandbox.home(), &["login", "copilot", "--public"], &env);
    assert_eq!(login.code, 0, "{}", login.all());

    // The dangerous shape: plaintext to somewhere that is not this machine.
    env.push(("ANTHROPIC_BASE_URL", "http://gateway.example.com".to_owned()));
    env.push(("ANTHROPIC_API_KEY", host.tokens.api_key.clone()));

    let command = serve_command(&coda_exe(), &sandbox, &env, &["--model", MESSAGES_MODEL]);
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();
    let reply = session_models(&connection).await;
    assert_eq!(
        reply["source"], "live",
        "a refused Anthropic endpoint stopped a Copilot session:\n{}",
        host.wire_summary()
    );
    assert_eq!(reply["providerId"], "github-copilot", "{reply:#}");
    run_turn(&connection, &mut inbound, "what is 2+2?").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    // The turn really went to the Copilot endpoints with the Copilot token,
    // and the exported API key was never spent anywhere.
    assert!(
        !host.requests_for(ANTHROPIC_MESSAGES_PATH).is_empty(),
        "the session never ran a turn:\n{}",
        host.wire_summary()
    );
    for request in host.requests_for(ANTHROPIC_MESSAGES_PATH) {
        assert!(
            !request.authorization().is_empty(),
            "the turn carried no credential:\n{}",
            host.wire_summary()
        );
        assert_eq!(
            request.header("x-api-key"),
            "",
            "the turn carried an API key:\n{}",
            host.wire_summary()
        );
    }
    let summary = host.wire_summary();
    assert!(summary.contains("copilot-token"), "{summary}");
    assert!(!summary.contains("api-key"), "{summary}");
    for request in host.requests() {
        assert_ne!(
            request.header("x-api-key"),
            host.tokens.api_key,
            "an API key was spent by a Copilot session:\n{}",
            host.wire_summary()
        );
    }
}

/// loopback hosts rather than by reading the resolver's source — and without
/// mutating this test process's own environment: both values are set on the
/// child alone.
///
/// The variable points at a *second*, real fixture. If precedence inverted,
/// that host would be the one that received the key.
#[tokio::test]
async fn an_explicit_endpoint_outranks_the_environment_variable_on_the_wire() {
    let chosen = FakeCopilotHost::start(tokens("explicit"), DeviceOutcome::Authorize);
    let ignored = FakeCopilotHost::start(tokens("ignored"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();
    let key = chosen.tokens.api_key.clone();

    // The environment says one host; the command line says another.
    let env = vec![("ANTHROPIC_BASE_URL", ignored.base.clone())];
    let command = serve_command(
        &coda_exe(),
        &sandbox,
        &env,
        &["--model", ANTHROPIC_MODEL, "--api-key", &key, "--endpoint", &chosen.base],
    );
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();

    let reply = session_models(&connection).await;
    assert_eq!(reply["source"], "live", "{}", chosen.wire_summary());
    assert_eq!(reply["providerId"], "anthropic", "{reply:#}");
    run_turn(&connection, &mut inbound, "what is 2+2?").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    assert!(
        chosen.request_count() > 0,
        "the explicit endpoint received nothing:\n{}",
        chosen.wire_summary()
    );
    assert_eq!(
        ignored.request_count(),
        0,
        "ANTHROPIC_BASE_URL overrode an explicit --endpoint:\n{}",
        ignored.wire_summary()
    );
    for request in chosen.requests() {
        assert_eq!(request.header("x-api-key"), key, "{}", chosen.wire_summary());
    }
}

/// The override applies to Anthropic **API keys** and to nothing else: a
/// Copilot login and a Copilot engine resolve their own endpoints and never
/// see it. Routing a Copilot token to a host configured for an API key would
/// hand one provider's credential to another's server.
#[tokio::test]
async fn the_anthropic_override_never_redirects_a_copilot_session() {
    let copilot = FakeCopilotHost::start(tokens("scoped"), DeviceOutcome::Authorize);
    let anthropic = FakeCopilotHost::start(tokens("scoped-anthropic"), DeviceOutcome::Authorize);
    let sandbox = Sandbox::new();

    let mut env = fixture_env(&copilot);
    env.extend(anthropic.anthropic_overrides());

    let login = run_auth(&coda_exe(), sandbox.home(), &["login", "copilot", "--public"], &env);
    assert_eq!(login.code, 0, "{}", login.all());

    let command = serve_command(&coda_exe(), &sandbox, &env, &["--model", MESSAGES_MODEL]);
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();
    let reply = session_models(&connection).await;
    assert_eq!(reply["providerId"], "github-copilot", "{reply:#}");
    run_turn(&connection, &mut inbound, "what is 2+2?").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    assert_eq!(
        anthropic.request_count(),
        0,
        "the Anthropic endpoint override captured Copilot traffic:\n{}",
        anthropic.wire_summary()
    );
    assert!(copilot.request_count() > 0, "{}", copilot.wire_summary());
}
