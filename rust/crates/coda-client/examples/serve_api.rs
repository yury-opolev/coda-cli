//! Minimal non-TUI consumer of the public serve contract.
//!
//! Usage: cargo run -p coda-client --example serve_api -- <engine> <workspace> [prompt]
//! The engine uses its configured provider credentials. This example prints
//! metadata only and never authorizes tools or invents answers to questions.

use std::path::PathBuf;
use std::time::Duration;

use anyhow::{bail, Context, Result};
use coda_client::{Connection, Engine, EngineCommand, Inbound};
use serde_json::{json, Value};
use tokio::sync::mpsc;

#[tokio::main]
async fn main() -> Result<()> {
    let mut args = std::env::args_os().skip(1);
    let engine_path = args.next().context("usage: serve_api <engine> <workspace> [prompt]")?;
    let workspace = PathBuf::from(args.next().context("workspace argument is required")?);
    let prompt = args.next().map(|text| text.into_string())
        .transpose().map_err(|_| anyhow::anyhow!("prompt must be UTF-8"))?;
    if args.next().is_some() {
        bail!("quote the prompt as a single argument");
    }

    let command = EngineCommand::new(engine_path).arg("serve").arg("--no-mcp").working_dir(workspace);
    let (engine, inbound) = Engine::spawn(command)?;
    let connection = engine.connection();
    let result = drive(&connection, inbound, prompt).await;
    drop(connection);
    let shutdown = engine.shutdown(Duration::from_secs(5)).await;
    result?;
    shutdown.context("engine shutdown failed")?;
    Ok(())
}

async fn drive(
    connection: &Connection,
    mut inbound: mpsc::UnboundedReceiver<Inbound>,
    prompt: Option<String>,
) -> Result<()> {
    connection.request("initialize", Some(json!({
        "protocolVersion": coda_proto::PROTOCOL_VERSION,
        "clientInfo": "serve-api-reference-client",
        "clientCapabilities": { "stateEvents": true }
    }))).await?;
    let initial = connection.request("session/getState", None).await?;
    print_state(&initial)?;

    if let Some(prompt) = prompt {
        let mut pending = connection.send_request("session/prompt", Some(json!({ "text": prompt })))?;
        let result = loop {
            tokio::select! {
                biased;
                message = inbound.recv() => match message {
                    Some(Inbound::Notification { method, params }) => {
                        println!("{}", json!({
                            "event": method,
                            "seq": params.as_ref().and_then(|p| p.get("seq")),
                            "phase": params.as_ref().and_then(|p| p.get("phase")),
                        }));
                    }
                    Some(Inbound::Request { responder, .. }) => {
                        // Wait for cancellation to be acknowledged before
                        // failing the request; an old core must not interpret
                        // an unanswered question as a default selection.
                        connection.request("session/interrupt", None).await?;
                        responder.fail(
                            coda_proto::error_codes::REQUEST_CANCELLED,
                            "the non-interactive reference client cannot answer this request",
                        );
                    }
                    None => bail!("engine disconnected; command outcome is unknown"),
                },
                result = &mut pending => break result,
            }
        };
        let result = result.context("engine response was lost; command outcome is unknown")?
            .map_err(coda_client::ClientError::Rpc)?;
        println!("{}", json!({ "promptCompleted": result.get("ok"), "interrupted": result.get("interrupted") }));
        print_state(&connection.request("session/getState", None).await?)?;
    }
    connection.request("shutdown", None).await?;
    Ok(())
}

fn print_state(state: &Value) -> Result<()> {
    println!("{}", serde_json::to_string_pretty(&json!({
        "engineInstanceId": state.get("engineInstanceId"),
        "sessionId": state.get("sessionId"),
        "cursor": state.get("cursor"),
        "lifecycle": state.get("lifecycle"),
        "phase": state.pointer("/turn/phase"),
        "pendingMessages": state.pointer("/steering/pendingCount"),
    }))?);
    Ok(())
}
