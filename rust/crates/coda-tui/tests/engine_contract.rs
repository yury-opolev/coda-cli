//! End-to-end checks against a real `coda serve` process.
//!
//! These verify the wire contract against the actual engine rather than a mock,
//! which is the point of the strangler approach: if the C# host changes its
//! protocol, these fail rather than the UI silently misbehaving.
//!
//! They skip when no engine is installed, so the suite still runs on a machine
//! without Coda.

use std::time::Duration;

use coda_client::{Engine, EngineCommand, Inbound};
use coda_proto::messages::{method, HistoryResult, InitializeParams, InitializeResult, ModelsResult, OkResult};
use serde_json::json;

/// Locates an engine, or returns `None` so the test can skip.
fn engine_command() -> Option<EngineCommand> {
    let program = std::env::var("CODA_ENGINE").unwrap_or_else(|_| "coda".to_string());

    // `--version` is credential-free, so it is a safe probe.
    let probe = std::process::Command::new(&program)
        .arg("--version")
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status();

    match probe {
        Ok(status) if status.success() => Some(
            EngineCommand::new(program)
                .arg("serve")
                .working_dir(std::env::temp_dir()),
        ),
        _ => None,
    }
}

/// Runs the handshake and hands back the live connection.
async fn connect() -> Option<(
    Engine,
    coda_client::Connection,
    tokio::sync::mpsc::UnboundedReceiver<Inbound>,
    InitializeResult,
)> {
    let command = engine_command()?;
    let (engine, inbound) = Engine::spawn(command).expect("spawn the engine");
    let connection = engine.connection();

    let params = serde_json::to_value(InitializeParams::new("coda-tui-tests")).expect("serialise");
    let result = tokio::time::timeout(
        Duration::from_secs(90),
        connection.request(method::INITIALIZE, Some(params)),
    )
    .await
    .expect("initialize timed out")
    .expect("initialize failed");

    let initialized: InitializeResult = serde_json::from_value(result).expect("parse initialize");
    Some((engine, connection, inbound, initialized))
}

#[tokio::test]
async fn completes_the_handshake_against_a_real_engine() {
    let Some((engine, _connection, _inbound, initialized)) = connect().await else {
        eprintln!("skipping: no `coda` engine on PATH");
        return;
    };

    assert_eq!(
        initialized.protocol_version,
        coda_proto::PROTOCOL_VERSION,
        "the engine speaks a protocol version this client does not"
    );
    assert_eq!(initialized.server_info, "coda");
    assert!(
        !initialized.session_id.is_empty(),
        "the engine must return a session id"
    );

    engine.shutdown(Duration::from_secs(5)).await.expect("shutdown");
}

#[tokio::test]
async fn lists_models_over_the_wire() {
    let Some((engine, connection, _inbound, _)) = connect().await else {
        eprintln!("skipping: no `coda` engine on PATH");
        return;
    };

    let value = tokio::time::timeout(
        Duration::from_secs(90),
        connection.request(method::MODELS, Some(json!({ "refresh": false }))),
    )
    .await
    .expect("models timed out");

    // A machine without configured credentials can legitimately fail here; the
    // assertion is that the *shape* is understood either way.
    if let Ok(value) = value {
        let result: ModelsResult = serde_json::from_value(value).expect("parse models result");
        assert!(
            ["live", "catalog", "builtin", ""].contains(&result.source.as_str()),
            "unexpected model source {:?}",
            result.source
        );
    }

    engine.shutdown(Duration::from_secs(5)).await.expect("shutdown");
}

#[tokio::test]
async fn reports_an_error_for_an_unknown_method() {
    let Some((engine, connection, _inbound, _)) = connect().await else {
        eprintln!("skipping: no `coda` engine on PATH");
        return;
    };

    let error = tokio::time::timeout(
        Duration::from_secs(30),
        connection.request("does/notExist", Some(json!({}))),
    )
    .await
    .expect("request timed out")
    .expect_err("an unknown method should be rejected");

    match error {
        coda_client::ClientError::Rpc(rpc) => assert_eq!(
            rpc.code,
            coda_proto::error_codes::METHOD_NOT_FOUND,
            "expected method-not-found, got {rpc:?}"
        ),
        other => panic!("expected a JSON-RPC error, got {other:?}"),
    }

    engine.shutdown(Duration::from_secs(5)).await.expect("shutdown");
}

#[tokio::test]
async fn returns_an_empty_history_for_a_fresh_session() {
    let Some((engine, connection, _inbound, _)) = connect().await else {
        eprintln!("skipping: no `coda` engine on PATH");
        return;
    };

    let value = tokio::time::timeout(
        Duration::from_secs(30),
        connection.request(method::HISTORY, Some(json!({}))),
    )
    .await
    .expect("history timed out")
    .expect("history failed");

    let result: HistoryResult = serde_json::from_value(value).expect("parse history");
    assert!(
        result.messages.is_empty(),
        "a fresh session should have no history"
    );

    engine.shutdown(Duration::from_secs(5)).await.expect("shutdown");
}

#[tokio::test]
async fn interrupt_is_accepted_with_no_turn_running() {
    let Some((engine, connection, _inbound, _)) = connect().await else {
        eprintln!("skipping: no `coda` engine on PATH");
        return;
    };

    let value = tokio::time::timeout(
        Duration::from_secs(30),
        connection.request(method::INTERRUPT, Some(json!({}))),
    )
    .await
    .expect("interrupt timed out")
    .expect("interrupt failed");

    let result: OkResult = serde_json::from_value(value).expect("parse interrupt");
    assert!(result.ok);

    engine.shutdown(Duration::from_secs(5)).await.expect("shutdown");
}
