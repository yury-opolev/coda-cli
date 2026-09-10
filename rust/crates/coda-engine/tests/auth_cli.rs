//! `coda-engine auth` → **real engine** conformance.
//!
//! `auth_commands.rs` proves the engine binary exposes the same three
//! commands. This file proves the pair that matters on this binary: a
//! credential stored by `coda-engine auth login` authenticates a *separately
//! started* `coda-engine serve` process against the same profile, and one that
//! `coda-engine auth logout` removed does not.
//!
//! Deliberately narrow. The full matrix — deployment switching, a denied
//! login, the environment selection, the production DPAPI store — lives in
//! `coda`'s `tests/auth_cli.rs` and is not duplicated here: both suites drive
//! the *same* fixture module (included below by path, one file, not a copy),
//! and both commands are the same `coda-boot` runner, so what is left to prove
//! here is that this binary really is wired to it end to end.
//!
//! Hermetic exactly as the other suite is: a temporary `CODA_HOME`, the
//! shipping `GH_COPILOT_*` overrides pointed at a loopback fixture, no
//! browser, no real provider, no paid completion.

#[path = "../../coda/tests/support/copilot_fixture.rs"]
mod copilot_fixture;

use std::path::PathBuf;

use copilot_fixture::{
    open_engine, run_auth, run_auth_with_stdin, run_turn, serve_command, serve_startup_result,
    session_models, DeviceOutcome, FakeCopilotHost, Sandbox, Tokens, ADVERTISED_MODELS,
    ANTHROPIC_ADVERTISED_MODELS, ANTHROPIC_MESSAGES_PATH, ANTHROPIC_MODEL, ANTHROPIC_MODELS_PATH,
    MESSAGES_MODEL,
};

fn engine_exe() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_coda-engine"))
}

#[tokio::test]
async fn the_engine_binary_signs_in_and_a_fresh_engine_authenticates_with_what_it_stored() {
    let host = FakeCopilotHost::start(
        Tokens {
            github: "ghu_fixture-durable-engine".to_owned(),
            copilot: "fixture-copilot-token-engine".to_owned(),
            api_key: "sk-ant-fixture-engine-0123456789".to_owned(),
        },
        DeviceOutcome::Authorize,
    );
    let sandbox = Sandbox::new();
    let env = host.overrides();

    // ── The actual command, on this binary ───────────────────────────────────
    let login = run_auth(&engine_exe(), sandbox.home(), &["login", "copilot", "--public"], &env);
    assert_eq!(login.code, 0, "{}", login.all());
    assert!(login.stdout.contains("signed in to GitHub Copilot"), "{}", login.all());
    assert!(
        login
            .stdout
            .contains(&format!("connected to github-copilot; {ADVERTISED_MODELS} models available")),
        "{}",
        login.all()
    );
    assert!(!login.all().contains(&host.tokens.github), "the durable token was printed");
    assert!(!login.all().contains(&host.tokens.copilot), "the Copilot token was printed");

    let settings = sandbox.settings().expect("the login writes settings.json");
    assert_eq!(settings["defaultProvider"], "github-copilot", "{settings}");

    // ── A fresh engine process, same profile, no key on its command line ─────
    let before_engine = host.request_count();
    let command = serve_command(&engine_exe(), &sandbox, &env, &["--model", MESSAGES_MODEL]);
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();

    let reply = session_models(&connection).await;
    assert_eq!(reply["source"], "live", "model discovery must be live:\n{}", host.wire_summary());
    assert_eq!(reply["providerId"], "github-copilot", "{reply:#}");

    run_turn(&connection, &mut inbound, "what is 2+2?").await;
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    let served = host.requests();
    let inference: Vec<_> =
        served[before_engine..].iter().filter(|r| r.path == "/v1/messages").collect();
    assert_eq!(inference.len(), 1, "expected one streamed turn:\n{}", host.wire_summary());
    assert_eq!(
        inference[0].authorization(),
        format!("Bearer {}", host.tokens.copilot),
        "the engine's inference request did not carry the stored Copilot token"
    );
    assert!(inference[0].body.contains("what is 2+2?"), "{}", inference[0].body);

    // ── The actual logout, and a fresh engine that cannot authenticate ───────
    let logout = run_auth(&engine_exe(), sandbox.home(), &["logout"], &env);
    assert_eq!(logout.code, 0, "{}", logout.all());
    assert!(
        logout.stdout.contains("Removed the stored credential for GitHub Copilot."),
        "{}",
        logout.all()
    );
    assert!(!sandbox.credential_path("github-copilot").is_file(), "the credential file survived");

    let after_logout = host.request_count();
    let command = serve_command(&engine_exe(), &sandbox, &env, &["--model", MESSAGES_MODEL]);
    let (engine, _inbound) = open_engine(command).await;
    let reply = session_models(&engine.connection()).await;
    assert_eq!(
        reply["source"], "catalog",
        "a signed-out engine must not report live model discovery:\n{}",
        host.wire_summary()
    );
    let _ = engine.shutdown(std::time::Duration::from_secs(10)).await;

    let (code, stderr) =
        serve_startup_result(&engine_exe(), &sandbox, &env, &["--provider", "github-copilot"]).await;
    assert_eq!(code, Some(1), "a named provider with no credential must refuse to start: {stderr}");
    assert_eq!(
        host.request_count(),
        after_logout,
        "a signed-out engine still reached the provider:\n{}",
        host.wire_summary()
    );
}


/// The Anthropic console-key route, on **this** binary.
///
/// The counterpart to the Copilot test above, and just as narrow: `coda`'s
/// suite owns the full API-key matrix (the padded `--use-env` key, the refused
/// override, endpoint precedence, what is and is not written). What is left to
/// prove here is that `coda-engine auth login api-key` and `coda-engine serve`
/// are wired to the same runner and the same endpoint resolver — an actual
/// key, read from stdin, validated at the endpoint `ANTHROPIC_BASE_URL` names,
/// stored in the production credential store, and then spent by a separately
/// started `coda-engine serve` that has no key and no endpoint on its argv.
#[tokio::test]
async fn the_engine_binary_signs_in_with_an_api_key_and_spends_it_at_the_configured_endpoint() {
    let host = FakeCopilotHost::start(
        Tokens {
            github: "ghu_fixture-durable-engine-key".to_owned(),
            copilot: "fixture-copilot-token-engine-key".to_owned(),
            api_key: "sk-ant-fixture-engine-key-0123456789".to_owned(),
        },
        DeviceOutcome::Authorize,
    );
    let sandbox = Sandbox::new();
    let env = host.anthropic_overrides();
    let key = host.tokens.api_key.clone();

    let login = run_auth_with_stdin(
        &engine_exe(),
        sandbox.home(),
        &["login", "api-key", "--api-key-stdin"],
        &env,
        Some(&key),
    );
    assert_eq!(login.code, 0, "{}", login.all());
    assert!(
        login.all().contains(&format!(
            "connected to anthropic; {ANTHROPIC_ADVERTISED_MODELS} models available"
        )),
        "{}",
        login.all()
    );
    assert!(!login.all().contains(&key), "the API key was printed:\n{}", login.all());

    // The mandatory pre-commit probe and the post-commit check, both at the
    // configured endpoint, both carrying the key.
    let listings = host.requests_for(ANTHROPIC_MODELS_PATH);
    assert_eq!(listings.len(), 2, "{}", host.wire_summary());
    for listing in &listings {
        assert_eq!(listing.header("x-api-key"), key, "{}", host.wire_summary());
    }

    let settings = sandbox.settings().expect("the login writes settings.json");
    assert_eq!(settings["defaultProvider"], "anthropic", "{settings}");

    // ── A fresh engine process, same profile, nothing on its command line ────
    let before_engine = host.request_count();
    let command = serve_command(&engine_exe(), &sandbox, &env, &["--model", ANTHROPIC_MODEL]);
    let (engine, mut inbound) = open_engine(command).await;
    let connection = engine.connection();

    let reply = session_models(&connection).await;
    assert_eq!(reply["source"], "live", "model discovery must be live:\n{}", host.wire_summary());
    assert_eq!(reply["providerId"], "anthropic", "{reply:#}");

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

    // ── And the actual logout takes it away ──────────────────────────────────
    let logout = run_auth(&engine_exe(), sandbox.home(), &["logout"], &env);
    assert_eq!(logout.code, 0, "{}", logout.all());
    assert!(!sandbox.credential_path("anthropic-api-key").is_file(), "the credential survived");

    let after_logout = host.request_count();
    let (code, stderr) =
        serve_startup_result(&engine_exe(), &sandbox, &env, &["--provider", "anthropic"]).await;
    assert_eq!(code, Some(1), "a signed-out provider must refuse to start: {stderr}");
    assert_eq!(
        host.request_count(),
        after_logout,
        "a signed-out engine still reached the endpoint:\n{}",
        host.wire_summary()
    );
}
