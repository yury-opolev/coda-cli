//! Real out-of-process conformance for Stage D's read surfaces:
//! `session/getHistory`, `session/listSessions`, `config/describe`,
//! `config/set` and `mcp/list` — driven against the compiled `coda-engine`
//! binary over real stdio, with a real streaming SSE provider fixture.
//!
//! The properties under test are the ones a client cannot verify for itself:
//! that the committed prefix and the live turn never overlap, that paging
//! survives (or explicitly refuses) a turn completing mid-read, that opaque
//! provider material never crosses the wire, and that a legacy client that
//! knows none of these methods still works exactly as it did.

mod support;

use std::time::Duration;

use coda_client::{Connection, Engine, Inbound};
use coda_proto::messages::{method, ClientCapabilities, InitializeParams, PromptParams};
use serde_json::json;
use support::*;

const SENTINEL_SIGNATURE: &str = "SIGNATURE-MUST-NOT-CROSS-THE-WIRE";
const SENTINEL_IMAGE_B64: &str = "QkFTRTY0LU1VU1QtTk9ULUNST1NT";

async fn start(
    sandbox: &Sandbox,
    provider: &ScriptedProvider,
    state_events: bool,
) -> (Engine, tokio::sync::mpsc::UnboundedReceiver<Inbound>, serde_json::Value) {
    let command = base_engine_command(&coda_engine_exe(), sandbox)
        .arg("--no-mcp")
        .arg("--api-key")
        .arg("fake-test-key")
        .arg("--endpoint")
        .arg(&provider.endpoint);
    let (engine, inbound) = Engine::spawn(command).expect("engine spawns");

    let mut params = InitializeParams::new("history-conformance");
    if state_events {
        params = params.with_client_capabilities(ClientCapabilities {
            state_events: Some(true),
            ..Default::default()
        });
    }
    let init = tokio::time::timeout(
        Duration::from_secs(20),
        engine.connection().request(method::INITIALIZE, Some(serde_json::to_value(params).unwrap())),
    )
    .await
    .expect("initialize must not hang")
    .expect("initialize succeeds");
    (engine, inbound, init)
}

async fn run_turn(connection: &Connection, text: &str) -> serde_json::Value {
    tokio::time::timeout(
        Duration::from_secs(30),
        connection.request(method::PROMPT, Some(serde_json::to_value(PromptParams::text(text)).unwrap())),
    )
    .await
    .expect("prompt must not hang")
    .expect("prompt answers")
}

async fn history(connection: &Connection, params: serde_json::Value) -> serde_json::Value {
    connection
        .request(method::GET_HISTORY, Some(params))
        .await
        .expect("session/getHistory must not error")
}

// ─────────────────────────────────────────────────────────────────────────────
// History: fences, paging, live projection
// ─────────────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn history_pages_reconstruct_the_conversation_exactly_once() {
    let sandbox = Sandbox::new();
    let provider = scripted_provider(vec![
        text_turn("first answer"),
        text_turn("second answer"),
        text_turn("third answer"),
    ])
    .await;
    let (engine, _inbound, init) = start(&sandbox, &provider, true).await;
    let connection = engine.connection();
    let instance = init["engineInstanceId"].as_str().expect("instance").to_string();

    for prompt in ["one", "two", "three"] {
        assert_eq!(run_turn(&connection, prompt).await["ok"], true);
    }

    let first = history(&connection, json!({ "limit": 100 })).await;
    assert_eq!(first["isLiveSession"], true);
    assert_eq!(first["engineInstanceId"], instance);
    assert_eq!(first["historyLength"], 6, "three exchanges committed");
    assert_eq!(first["totalKnown"], 6);
    assert_eq!(first["truncated"], false);
    assert!(first["cursor"].as_i64().is_some_and(|c| c > 0), "the live read carries its cursor");
    let epoch = first["historyEpoch"].as_i64().expect("live sessions have an epoch");

    // Page through in twos, pinned to the fence seen on page one.
    let mut indices: Vec<i64> = Vec::new();
    let mut since = 0i64;
    loop {
        let page = history(
            &connection,
            json!({
                "sinceIndex": since,
                "limit": 2,
                "historyEpoch": epoch,
                "expectedHistoryLength": 6,
                "engineInstanceId": instance,
            }),
        )
        .await;
        for entry in page["entries"].as_array().expect("entries") {
            indices.push(entry["index"].as_i64().expect("index"));
        }
        since = page["nextIndex"].as_i64().expect("nextIndex");
        if !page["truncated"].as_bool().unwrap_or(false) {
            break;
        }
    }
    assert_eq!(indices, (0..6).collect::<Vec<_>>(), "gapless, no duplicates, no omissions");

    // A user-role entry that is really a prompt is labelled as one.
    assert_eq!(first["entries"][0]["role"], "user");
    assert_eq!(first["entries"][0]["entryKind"], "userPrompt");
    assert_eq!(first["entries"][1]["role"], "assistant");
    assert_eq!(first["entries"][1]["entryKind"], "assistant");
}

/// A page whose pinned fence has moved is refused with a typed error rather
/// than answered with content from a different conversation state.
#[tokio::test]
async fn a_turn_committing_between_pages_is_refused_not_silently_interleaved() {
    let sandbox = Sandbox::new();
    let provider = scripted_provider(vec![text_turn("one"), text_turn("two")]).await;
    let (engine, _inbound, _init) = start(&sandbox, &provider, true).await;
    let connection = engine.connection();

    run_turn(&connection, "first").await;
    let page_one = history(&connection, json!({ "limit": 1 })).await;
    let fence = page_one["historyLength"].as_i64().expect("historyLength");
    assert_eq!(fence, 2);

    // A turn completes between pages.
    run_turn(&connection, "second").await;

    let err = connection
        .request(
            method::GET_HISTORY,
            Some(json!({ "sinceIndex": 1, "limit": 10, "expectedHistoryLength": fence })),
        )
        .await
        .expect_err("the moved fence must be refused");
    assert!(err.to_string().contains("no longer matches"), "typed and explanatory: {err}");

    // Without the pin, the same read succeeds against the new state — the
    // client opted out of the guard, and the engine says which state it got.
    let unpinned = history(&connection, json!({ "sinceIndex": 1, "limit": 10 })).await;
    assert_eq!(unpinned["historyLength"], 4);
}

/// `historyEpoch` invalidates indices on fork; a stale epoch is a typed
/// error, never a partially-valid page.
#[tokio::test]
async fn forking_bumps_the_epoch_and_stale_indices_are_refused() {
    let sandbox = Sandbox::new();
    let provider = scripted_provider(vec![text_turn("one")]).await;
    let (engine, _inbound, _init) = start(&sandbox, &provider, true).await;
    let connection = engine.connection();

    run_turn(&connection, "first").await;
    let before = history(&connection, json!({})).await;
    let old_epoch = before["historyEpoch"].as_i64().expect("epoch");

    connection.request("session/fork", Some(json!({}))).await.expect("fork");

    let err = connection
        .request(method::GET_HISTORY, Some(json!({ "historyEpoch": old_epoch })))
        .await
        .expect_err("a pre-fork epoch must not be answered");
    assert!(err.to_string().contains("stale"), "{err}");

    let after = history(&connection, json!({})).await;
    assert!(
        after["historyEpoch"].as_i64().expect("epoch") > old_epoch,
        "the epoch must move so a client knows its indices were invalidated"
    );
    assert_ne!(after["sessionId"], before["sessionId"], "a fork is a new session id");
}

/// The committed fence and the live turn never overlap, at any instant.
#[tokio::test]
async fn the_live_turn_is_never_also_counted_as_committed_history() {
    let sandbox = Sandbox::new();
    // A tool-use turn parks the engine on a reverse request, giving a stable
    // mid-turn instant to observe without any sleep.
    let provider = scripted_provider(vec![tool_use_turn(
        "toolu_1",
        "ask_user_question",
        json!({ "question": "continue?", "options": ["yes", "no"] }),
    )])
    .await;
    let (engine, mut inbound, _init) = start(&sandbox, &provider, true).await;
    let connection = engine.connection();

    let prompt = connection
        .send_request(method::PROMPT, Some(serde_json::to_value(PromptParams::text("do it")).unwrap()))
        .expect("prompt queues");

    // Wait for the reverse request: that *is* the mid-turn barrier.
    let responder = loop {
        match tokio::time::timeout(Duration::from_secs(30), inbound.recv())
            .await
            .expect("must not hang")
            .expect("inbound open")
        {
            Inbound::Request { responder, .. } => break responder,
            Inbound::Notification { .. } => continue,
        }
    };

    let mid = history(&connection, json!({ "includeLive": true })).await;
    assert_eq!(mid["historyLength"], 0, "nothing is committed while the turn is in flight");
    assert!(mid["entries"].as_array().expect("entries").is_empty());
    let live = mid["liveEntries"].as_array().expect("liveEntries present when asked");
    assert!(!live.is_empty(), "the in-flight turn is visible");
    assert_eq!(live[0]["blocks"][0]["text"], "do it", "the accepted prompt is entry zero");

    // The same instant through getState must agree exactly.
    let state = connection.request(method::GET_STATE, Some(json!({}))).await.expect("getState");
    assert_eq!(state["historyLength"], mid["historyLength"]);
    assert_eq!(state["turn"]["liveEntries"], mid["liveEntries"].clone());

    // Live entries are omitted entirely unless asked for.
    let without = history(&connection, json!({})).await;
    assert!(without.get("liveEntries").is_none(), "absent, never an empty array");

    responder.respond(json!({ "answer": "no" }));
    let _ = tokio::time::timeout(Duration::from_secs(30), prompt).await;
}

// ─────────────────────────────────────────────────────────────────────────────
// Saved sessions
// ─────────────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn saved_sessions_are_listed_and_readable_by_validated_id_only() {
    let sandbox = Sandbox::new();
    let provider = scripted_provider(vec![text_turn("saved answer")]).await;
    let (engine, _inbound, init) = start(&sandbox, &provider, true).await;
    let connection = engine.connection();
    let session_id = init["sessionId"].as_str().expect("sessionId").to_string();

    run_turn(&connection, "remember this").await;

    let listed = connection
        .request(method::LIST_SESSIONS, Some(json!({})))
        .await
        .expect("listSessions");
    let sessions = listed["sessions"].as_array().expect("sessions");
    let current = sessions
        .iter()
        .find(|s| s["sessionId"] == session_id.as_str())
        .expect("the running session is listed");
    assert_eq!(current["isCurrent"], true);
    assert_eq!(current["messageCount"], 2);
    assert_eq!(current["preview"], "remember this");
    assert_eq!(current["previewTruncated"], false);
    let json = serde_json::to_string(&listed).expect("serialisable");
    assert!(
        !json.contains(".coda") && !json.contains(".json"),
        "SECURITY: a client is never handed a filesystem path: {json}"
    );

    // Reading the *current* id through the saved path resolves to the live
    // session (it is the same conversation), and carries live metadata.
    let same = history(&connection, json!({ "sessionId": session_id })).await;
    assert_eq!(same["isLiveSession"], true);

    // An invalid id is refused before any filesystem access is attempted.
    for bad in ["../../etc/passwd", "not a session", ""] {
        if bad.is_empty() {
            continue; // empty means "the live session", covered above
        }
        assert!(
            connection
                .request(method::GET_HISTORY, Some(json!({ "sessionId": bad })))
                .await
                .is_err(),
            "`{bad}` must be refused"
        );
    }

    // A well-formed but unknown id is a typed not-found, not an empty success.
    assert!(
        connection
            .request(
                method::GET_HISTORY,
                Some(json!({ "sessionId": "00000000-0000-4000-8000-000000000000" })),
            )
            .await
            .is_err(),
        "an unknown session must not answer with an empty conversation"
    );
}

// ─────────────────────────────────────────────────────────────────────────────
// Opaque provider material never crosses the wire
// ─────────────────────────────────────────────────────────────────────────────

/// A thinking signature is provider ciphertext the engine must replay to the
/// provider and must never publish. This drives a real reasoning turn whose
/// signature is a recognisable sentinel and asserts it appears nowhere in any
/// Stage D read surface — nor in the state snapshot, nor in the event stream.
#[tokio::test]
async fn opaque_provider_material_never_appears_on_any_read_surface() {
    let sandbox = Sandbox::new();
    let thinking_turn = message_start()
        + &sse_event(
            "content_block_start",
            json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "thinking", "thinking": "" } }),
        )
        + &sse_event(
            "content_block_delta",
            json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "thinking_delta", "thinking": "weighing it up" } }),
        )
        + &sse_event(
            "content_block_delta",
            json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "signature_delta", "signature": SENTINEL_SIGNATURE } }),
        )
        + &sse_event("content_block_stop", json!({ "type": "content_block_stop", "index": 0 }))
        + &sse_event(
            "content_block_start",
            json!({ "type": "content_block_start", "index": 1, "content_block": { "type": "text", "text": "" } }),
        )
        + &sse_event(
            "content_block_delta",
            json!({ "type": "content_block_delta", "index": 1, "delta": { "type": "text_delta", "text": "done" } }),
        )
        + &sse_event("content_block_stop", json!({ "type": "content_block_stop", "index": 1 }))
        + &sse_event(
            "message_delta",
            json!({ "type": "message_delta", "delta": { "stop_reason": "end_turn" }, "usage": { "output_tokens": 3 } }),
        )
        + &sse_event("message_stop", json!({ "type": "message_stop" }));

    let provider = scripted_provider(vec![thinking_turn]).await;
    let (engine, mut inbound, _init) = start(&sandbox, &provider, true).await;
    let connection = engine.connection();

    // An image in the prompt: its base64 must be reduced to metadata too.
    let prompt_params = json!({
        "text": "think about this",
        "images": [{ "mediaType": "image/png", "base64": SENTINEL_IMAGE_B64 }],
    });
    let result = tokio::time::timeout(
        Duration::from_secs(30),
        connection.request(method::PROMPT, Some(prompt_params)),
    )
    .await
    .expect("must not hang")
    .expect("prompt answers");
    assert_eq!(result["ok"], true);

    let surfaces = [
        history(&connection, json!({ "limit": 100, "includeLive": true })).await,
        connection.request(method::GET_STATE, Some(json!({}))).await.expect("getState"),
        connection.request(method::LIST_SESSIONS, Some(json!({}))).await.expect("listSessions"),
        connection.request(method::CONFIG_DESCRIBE, Some(json!({}))).await.expect("describe"),
        connection.request(method::MCP_LIST, Some(json!({}))).await.expect("mcpList"),
    ];
    for surface in &surfaces {
        let text = serde_json::to_string(surface).expect("serialisable");
        assert!(
            !text.contains(SENTINEL_SIGNATURE),
            "SECURITY: the thinking signature must never cross the wire: {text}"
        );
        assert!(
            !text.contains(SENTINEL_IMAGE_B64),
            "SECURITY: image base64 must never cross the wire: {text}"
        );
    }

    // The reasoning *summary* is still carried — the block is not simply
    // dropped, so a client can render what the model thought.
    let hist = &surfaces[0];
    let has_reasoning = hist["entries"]
        .as_array()
        .expect("entries")
        .iter()
        .flat_map(|e| e["blocks"].as_array().cloned().unwrap_or_default())
        .any(|b| b["kind"] == "reasoningSummary" && b["text"] == "weighing it up");
    assert!(has_reasoning, "the readable reasoning summary survives: {hist}");

    // The image became metadata, not a payload.
    let image_block = hist["entries"]
        .as_array()
        .expect("entries")
        .iter()
        .flat_map(|e| e["blocks"].as_array().cloned().unwrap_or_default())
        .find(|b| b["kind"] == "image")
        .expect("the image is reported as metadata");
    assert_eq!(image_block["mediaType"], "image/png");
    assert!(image_block["byteLength"].as_i64().is_some_and(|n| n > 0));
    assert!(image_block.get("base64").is_none());

    // And nothing sensitive leaked through the event stream either.
    let mut frames = String::new();
    while let Ok(Some(msg)) = tokio::time::timeout(Duration::from_millis(50), inbound.recv()).await {
        if let Inbound::Notification { method, params } = msg {
            frames.push_str(&method);
            frames.push_str(&params.map(|p| p.to_string()).unwrap_or_default());
        }
    }
    assert!(!frames.contains(SENTINEL_SIGNATURE), "SECURITY: not in the event stream either");
    assert!(!frames.contains(SENTINEL_IMAGE_B64), "SECURITY: not in the event stream either");
}

// ─────────────────────────────────────────────────────────────────────────────
// config/describe + config/set
// ─────────────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn config_describe_states_real_scopes_and_config_set_delegates() {
    let sandbox = Sandbox::new();
    let provider = scripted_provider(vec![]).await;
    let (engine, mut inbound, _init) = start(&sandbox, &provider, true).await;
    let connection = engine.connection();

    let described =
        connection.request(method::CONFIG_DESCRIBE, Some(json!({}))).await.expect("describe");
    let entries = described["entries"].as_array().expect("entries").clone();
    let find = |key: &str| -> serde_json::Value {
        entries.iter().find(|e| e["key"] == key).cloned().unwrap_or_else(|| panic!("no `{key}`"))
    };

    assert_eq!(find("permissionMode")["appliesAt"], "nextPermissionCheck");
    assert_eq!(find("model")["appliesAt"], "nextTurn");
    assert_eq!(find("provider")["appliesAt"], "newEngineInstance");
    assert_eq!(find("theme")["appliesAt"], "clientLocal");
    assert_eq!(find("theme")["mutable"], false);
    assert!(find("theme")["reason"].as_str().is_some_and(|r| r.len() > 20));

    // The TUI's reason for linking `coda-agent` today: output-style names.
    let styles = find("outputStyle")["allowedValues"].as_array().expect("styles").clone();
    let names: Vec<&str> = styles.iter().filter_map(|s| s["value"].as_str()).collect();
    for expected in ["default", "concise", "explanatory", "code-reviewer"] {
        assert!(names.contains(&expected), "`{expected}` must be published: {names:?}");
    }

    // ── set: delegated, validated, read back ─────────────────────────────
    let ok = connection
        .request(method::CONFIG_SET, Some(json!({ "key": "permissionMode", "value": "plan" })))
        .await
        .expect("config/set");
    assert_eq!(ok["ok"], true);
    assert_eq!(ok["appliedAt"], "nextPermissionCheck");
    assert_eq!(ok["effective"], "plan", "read back from the engine, not echoed");

    // A config mutation must be *pushed*, not only pollable: a `stateEvents`
    // client that never re-snapshots must still converge on `config.next`.
    let mut pushed: Option<serde_json::Value> = None;
    while let Ok(Some(msg)) = tokio::time::timeout(Duration::from_millis(200), inbound.recv()).await
    {
        if let Inbound::Notification { method, params } = msg {
            if method == "event/configChanged" {
                pushed = params;
                break;
            }
        }
    }
    let pushed = pushed.expect("config/set must publish event/configChanged");
    assert_eq!(pushed["key"], "permissionMode");
    assert_eq!(pushed["next"]["permissionMode"], "plan");
    let pushed_text = pushed.to_string();
    assert!(
        !pushed_text.contains("fake-test-key"),
        "SECURITY: a config event must never carry a credential: {pushed_text}"
    );

    let refreshed =
        connection.request(method::CONFIG_DESCRIBE, Some(json!({}))).await.expect("describe");
    let mode = refreshed["entries"]
        .as_array()
        .expect("entries")
        .iter()
        .find(|e| e["key"] == "permissionMode")
        .expect("permissionMode");
    assert_eq!(mode["value"], "plan", "describe and set agree");

    // An unrecognised value is refused, not silently defaulted.
    let bad = connection
        .request(method::CONFIG_SET, Some(json!({ "key": "permissionMode", "value": "yolo-plus" })))
        .await
        .expect("config/set answers");
    assert_eq!(bad["ok"], false);
    assert!(bad["error"].as_str().is_some_and(|e| e.contains("yolo-plus")));
    assert_eq!(bad["effective"], "plan", "and nothing changed");

    // An immutable key is refused with the catalog's own reason.
    let immutable = connection
        .request(method::CONFIG_SET, Some(json!({ "key": "theme", "value": "dark" })))
        .await
        .expect("config/set answers");
    assert_eq!(immutable["ok"], false);
    assert_eq!(immutable["error"], find("theme")["reason"], "one reason, not two");

    // A key the engine does not own at all.
    let unknown = connection
        .request(method::CONFIG_SET, Some(json!({ "key": "notAKey", "value": 1 })))
        .await
        .expect("config/set answers");
    assert_eq!(unknown["ok"], false);
    assert!(unknown["error"].as_str().is_some());

    // A wrong-typed value is a typed protocol error, not a coerced write.
    assert!(
        connection
            .request(method::CONFIG_SET, Some(json!({ "key": "model", "value": 42 })))
            .await
            .is_err(),
        "a non-string model must be refused"
    );
}

// ─────────────────────────────────────────────────────────────────────────────
// mcp/list
// ─────────────────────────────────────────────────────────────────────────────

/// With MCP switched off (`--no-mcp`), the inventory is honestly empty and
/// says *why* — an empty list must not read as "nothing is configured".
#[tokio::test]
async fn mcp_list_is_honest_when_mcp_is_disabled() {
    let sandbox = Sandbox::new();
    // A project `.mcp.json` exists, but MCP is off for this process.
    std::fs::write(
        sandbox.cwd.path().join(".mcp.json"),
        json!({
            "mcpServers": {
                "fs": {
                    "command": "/opt/tools/mcp-server-filesystem",
                    "args": ["--token=MUST-NOT-LEAK"],
                    "env": { "API_TOKEN": "coda-secret:mcp/fs/token" }
                }
            }
        })
        .to_string(),
    )
    .expect("write .mcp.json");

    let provider = scripted_provider(vec![]).await;
    let (engine, _inbound, _init) = start(&sandbox, &provider, true).await;
    let connection = engine.connection();

    let listed = connection.request(method::MCP_LIST, Some(json!({}))).await.expect("mcp/list");
    assert_eq!(listed["enabled"], false, "the client is told MCP is off for this process");
    assert_eq!(listed["managerAvailable"], false);
    assert!(listed["servers"].as_array().expect("servers").is_empty());

    let text = serde_json::to_string(&listed).expect("serialisable");
    assert!(!text.contains("MUST-NOT-LEAK"), "SECURITY: {text}");
    assert!(!text.contains("mcp/fs/token"), "SECURITY: {text}");
}

/// With MCP enabled but no manager (the servers all failed / none started),
/// the file inventory is still described — with names only.
#[tokio::test]
async fn mcp_list_describes_configured_servers_without_secrets() {
    let sandbox = Sandbox::new();
    std::fs::write(
        sandbox.cwd.path().join(".mcp.json"),
        json!({
            "mcpServers": {
                "remote": {
                    "url": "https://bot:hunter2@mcp.example.com/sse?apiKey=LEAKED#frag",
                    "headers": { "X-Api-Key": "HEADER-MUST-NOT-LEAK" }
                },
                "off": {
                    "command": "/opt/tools/never-run",
                    "disabled": true,
                    "env": { "SECRET_ENV": "PLAINTEXT-MUST-NOT-LEAK" }
                }
            }
        })
        .to_string(),
    )
    .expect("write .mcp.json");

    let provider = scripted_provider(vec![]).await;
    // Deliberately no `--no-mcp`: the config layer is read, and connecting to
    // a nonexistent command simply fails, which is the state under test.
    let command = base_engine_command(&coda_engine_exe(), &sandbox)
        .arg("--api-key")
        .arg("fake-test-key")
        .arg("--endpoint")
        .arg(&provider.endpoint);
    let (engine, _inbound) = Engine::spawn(command).expect("engine spawns");
    let connection = engine.connection();
    tokio::time::timeout(
        Duration::from_secs(30),
        connection.request(
            method::INITIALIZE,
            Some(serde_json::to_value(InitializeParams::new("mcp-conformance")).unwrap()),
        ),
    )
    .await
    .expect("initialize must not hang")
    .expect("initialize succeeds");

    let listed = connection.request(method::MCP_LIST, Some(json!({}))).await.expect("mcp/list");
    assert_eq!(listed["enabled"], true);

    let text = serde_json::to_string(&listed).expect("serialisable");
    for secret in ["hunter2", "LEAKED", "HEADER-MUST-NOT-LEAK", "PLAINTEXT-MUST-NOT-LEAK"] {
        assert!(!text.contains(secret), "SECURITY: `{secret}` must never appear: {text}");
    }

    let servers = listed["servers"].as_array().expect("servers");
    let remote = servers.iter().find(|s| s["name"] == "remote").expect("remote listed");
    assert_eq!(remote["transport"], "http");
    assert_eq!(remote["targetKind"], "url");
    assert_eq!(remote["targetDisplay"], "https://mcp.example.com/sse");

    let off = servers.iter().find(|s| s["name"] == "off").expect("disabled server listed");
    assert_eq!(off["configured"], "disabled");
    assert_eq!(off["runtimeStatus"], "notAttempted");
    assert!(off.get("toolCount").is_none(), "an unknown count is absent, never a fake zero");
    assert_eq!(
        off["envVarNames"].as_array().map(|v| v.len()),
        Some(1),
        "names are published, values are not"
    );
    assert_eq!(off["envVarNames"][0], "SECRET_ENV");
}

// ─────────────────────────────────────────────────────────────────────────────
// Legacy compatibility
// ─────────────────────────────────────────────────────────────────────────────

/// A client that never negotiates `stateEvents`, never calls a Stage D method
/// and only uses the routes that shipped before this contract must be
/// completely unaffected.
#[tokio::test]
async fn a_legacy_client_that_knows_none_of_this_still_works() {
    let sandbox = Sandbox::new();
    let provider = scripted_provider(vec![text_turn("legacy answer")]).await;
    let (engine, mut inbound, init) = start(&sandbox, &provider, false).await;
    let connection = engine.connection();

    // Pre-existing initialize fields are unchanged.
    assert_eq!(init["protocolVersion"], coda_proto::messages::PROTOCOL_VERSION);
    assert!(init["sessionId"].as_str().is_some());

    assert_eq!(run_turn(&connection, "hello").await["ok"], true);

    // The pre-existing history routes still answer in their original shape.
    let legacy = connection.request(method::HISTORY, Some(json!({}))).await.expect("history");
    let messages = legacy["messages"].as_array().expect("messages");
    assert_eq!(messages.len(), 2);
    assert_eq!(messages[0]["role"], "user");
    assert_eq!(messages[0]["content"], "hello");
    assert_eq!(messages[1]["content"], "legacy answer");

    let paged = connection
        .request(method::MESSAGES, Some(json!({ "sinceIndex": 1 })))
        .await
        .expect("messages");
    assert_eq!(paged["messages"].as_array().map(Vec::len), Some(1));
    assert_eq!(paged["nextIndex"], 2);

    // No gated state event was written to a connection that did not ask.
    let mut gated_seen = Vec::new();
    while let Ok(Some(msg)) = tokio::time::timeout(Duration::from_millis(50), inbound.recv()).await {
        if let Inbound::Notification { method, .. } = msg {
            if matches!(
                method.as_str(),
                "event/activity"
                    | "event/turnEnded"
                    | "event/lifecycle"
                    | "event/requestPending"
                    | "event/requestResolved"
            ) {
                gated_seen.push(method);
            }
        }
    }
    assert!(gated_seen.is_empty(), "gated events must stay off an unnegotiated connection: {gated_seen:?}");
}
