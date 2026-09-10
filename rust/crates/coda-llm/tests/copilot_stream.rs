//! End-to-end streaming tests for the Copilot provider against a real TCP server.
//!
//! Unit tests cover the SSE framing and each protocol decoder in isolation.
//! These drive the full path — socket, chunked body, UTF-8 carry buffer,
//! decoder, channel — because that is where the seams are.

use std::sync::Arc;
use std::time::Duration;

use coda_llm::copilot::{CopilotClient, CopilotConfig, CopilotEndpoint};
use coda_llm::{
    ChatRequest, Content, LlmClient, LlmError, Message, ModelInfo, RetryPolicy,
};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

// ─── Diagnostics test harness ──────────────────────────────────────────────

fn diag_ctx(dir: &std::path::Path) -> coda_diagnostics::DiagnosticContext {
    let logger = coda_diagnostics::Logger::open(
        coda_diagnostics::Options {
            directory: dir.to_path_buf(),
            file: None,
            role: coda_diagnostics::ProcessRole::Run,
            version: "test".into(),
            verbosity: coda_diagnostics::Verbosity::Trace,
        },
        coda_diagnostics::Limits::default(),
    )
    .expect("logger opens");
    coda_diagnostics::DiagnosticContext::root(Arc::new(logger), "run-1")
}

fn diag_lines(ctx: &coda_diagnostics::DiagnosticContext) -> Vec<serde_json::Value> {
    let path = ctx.logger().status().path.expect("a log path");
    std::fs::read_to_string(path)
        .unwrap()
        .lines()
        .filter(|l| !l.is_empty())
        .map(|l| serde_json::from_str(l).unwrap())
        .collect()
}

// ─── Test infrastructure ─────────────────────────────────────────────────────

/// One-shot server that writes a sequence of string chunks, then closes.
async fn serve_text(status: u16, headers: &str, body: Vec<String>) -> String {
    serve(
        status,
        headers,
        body.into_iter().map(|s| s.into_bytes()).collect(),
    )
    .await
}

/// One-shot server that writes a sequence of raw byte chunks, then closes.
async fn serve(status: u16, headers: &str, body: Vec<Vec<u8>>) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();
    let headers = headers.to_string();

    tokio::spawn(async move {
        let Ok((mut socket, _)) = listener.accept().await else {
            return;
        };
        let mut buf = vec![0u8; 8192];
        let _ = socket.read(&mut buf).await;

        let reason = if status == 200 { "OK" } else { "Error" };
        let head = format!("HTTP/1.1 {status} {reason}\r\n{headers}\r\n");
        if socket.write_all(head.as_bytes()).await.is_err() {
            return;
        }
        for chunk in body {
            if socket.write_all(&chunk).await.is_err() {
                return;
            }
            let _ = socket.flush().await;
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        let _ = socket.shutdown().await;
    });

    format!("http://127.0.0.1:{port}")
}

/// Two-request server: first request gets `first_response`, second gets `second_response`.
async fn serve_sequence(
    first_status: u16,
    first_headers: &'static str,
    first_body: Vec<String>,
    second_status: u16,
    second_headers: &'static str,
    second_body: Vec<String>,
) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();

    tokio::spawn(async move {
        for (status, headers, body) in [
            (first_status, first_headers, first_body),
            (second_status, second_headers, second_body),
        ] {
            let Ok((mut socket, _)) = listener.accept().await else {
                return;
            };
            let mut buf = vec![0u8; 8192];
            let _ = socket.read(&mut buf).await;

            let reason = if status == 200 { "OK" } else { "Error" };
            let head = format!("HTTP/1.1 {status} {reason}\r\n{headers}\r\n");
            if socket.write_all(head.as_bytes()).await.is_err() {
                return;
            }
            for chunk in body {
                if socket.write_all(chunk.as_bytes()).await.is_err() {
                    return;
                }
                let _ = socket.flush().await;
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
            let _ = socket.shutdown().await;
        }
    });

    format!("http://127.0.0.1:{port}")
}

/// N-request server: accepts one TCP connection per entry in `responses`,
/// sends the corresponding (status, headers, body) for each, then closes.
async fn serve_n(responses: Vec<(u16, &'static str, Vec<String>)>) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();

    tokio::spawn(async move {
        for (status, headers, body) in responses {
            let Ok((mut socket, _)) = listener.accept().await else {
                return;
            };
            let mut buf = vec![0u8; 8192];
            let _ = socket.read(&mut buf).await;

            let reason = if status == 200 { "OK" } else { "Error" };
            let head = format!("HTTP/1.1 {status} {reason}\r\n{headers}\r\n");
            if socket.write_all(head.as_bytes()).await.is_err() {
                return;
            }
            for chunk in body {
                if socket.write_all(chunk.as_bytes()).await.is_err() {
                    return;
                }
                let _ = socket.flush().await;
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
            let _ = socket.shutdown().await;
        }
    });

    format!("http://127.0.0.1:{port}")
}

fn sse_headers() -> &'static str {
    "content-type: text/event-stream\r\nconnection: close\r\n"
}

fn json_headers() -> &'static str {
    "content-type: application/json\r\nconnection: close\r\n"
}

fn client(base_url: String) -> CopilotClient {
    CopilotClient::new(
        CopilotConfig::with_token("test-key")
            .with_base_url(base_url)
            .with_retry(RetryPolicy::none()),
    )
    .expect("client")
}

/// Pre-populates the model metadata cache for `base_url` so tests that don't
/// exercise endpoint selection avoid an HTTP round trip to `/models`.
fn client_with_endpoint(base_url: String, model_id: &str, endpoint: &str) -> CopilotClient {
    let model = ModelInfo {
        supported_endpoints: vec![endpoint.to_string()],
        ..ModelInfo::new(model_id)
    };
    coda_llm::copilot::models::cache_set(&base_url, vec![model]);
    client(base_url)
}

fn request() -> ChatRequest {
    ChatRequest::new("gpt-4o", vec![Message::user("hello")])
}

// ─── Chat-completions helpers ─────────────────────────────────────────────────

fn chat_text_chunk(text: &str) -> String {
    let payload = serde_json::json!({
        "choices": [{ "delta": { "content": text }, "finish_reason": null }]
    });
    format!("data: {payload}\n\n")
}

fn chat_done_chunk(stop_reason: &str) -> String {
    let payload = serde_json::json!({
        "choices": [{ "delta": {}, "finish_reason": stop_reason }]
    });
    format!("data: {payload}\n\ndata: [DONE]\n\n")
}

// ─── Responses-API helpers ────────────────────────────────────────────────────

fn responses_text_delta(text: &str) -> String {
    let payload = serde_json::json!({
        "type": "response.output_text.delta",
        "delta": text
    });
    format!("event: response.output_text.delta\ndata: {payload}\n\n")
}

fn responses_completed(input_tokens: u32, output_tokens: u32) -> String {
    let payload = serde_json::json!({
        "type": "response.completed",
        "response": { "usage": { "input_tokens": input_tokens, "output_tokens": output_tokens } }
    });
    format!("event: response.completed\ndata: {payload}\n\n")
}

// ─── Anthropic-Messages helpers ───────────────────────────────────────────────

fn anthropic_event(name: &str, data: serde_json::Value) -> String {
    format!("event: {name}\ndata: {data}\n\n")
}

fn anthropic_message_stop() -> String {
    anthropic_event("message_stop", serde_json::json!({ "type": "message_stop" }))
}

// ─── Chat-completions integration tests ──────────────────────────────────────

#[tokio::test]
async fn streams_chat_text_response_end_to_end() {
    let final_chunk = serde_json::json!({
        "choices": [{ "delta": {}, "finish_reason": "stop" }],
        "usage": { "prompt_tokens": 10, "completion_tokens": 5 }
    });
    let body = vec![
        chat_text_chunk("Hello"),
        chat_text_chunk(", world"),
        format!("data: {final_chunk}\n\ndata: [DONE]\n\n"),
    ];

    let url = serve_text(200, sse_headers(), body).await;
    let c = client_with_endpoint(url, "gpt-4o", "/chat/completions");
    let stream = c.stream(request()).await.expect("stream");
    let response = stream.collect().await.expect("complete");

    assert_eq!(response.text, "Hello, world");
    assert_eq!(response.stop_reason.as_deref(), Some("end_turn"));
    assert_eq!(response.usage.input_tokens, 10);
    assert_eq!(response.usage.output_tokens, 5);
}

#[tokio::test]
async fn streams_chat_tool_call_end_to_end() {
    let chunk1 = serde_json::json!({
        "choices": [{ "delta": { "tool_calls": [
            { "index": 0, "id": "call_1", "type": "function",
              "function": { "name": "read_file", "arguments": "" } }
        ] } }]
    });
    let chunk2 = serde_json::json!({
        "choices": [{ "delta": { "tool_calls": [
            { "index": 0, "function": { "arguments": "{\"path\":\"a.rs\"}" } }
        ] } }]
    });
    let chunk3 = serde_json::json!({
        "choices": [{ "delta": {}, "finish_reason": "tool_calls" }]
    });
    let body = vec![
        format!("data: {chunk1}\n\n"),
        format!("data: {chunk2}\n\n"),
        format!("data: {chunk3}\n\ndata: [DONE]\n\n"),
    ];

    let url = serve_text(200, sse_headers(), body).await;
    let c = client_with_endpoint(url, "gpt-4o", "/chat/completions");
    let stream = c.stream(request()).await.expect("stream");
    let response = stream.collect().await.expect("complete");

    assert_eq!(response.stop_reason.as_deref(), Some("tool_use"));
    let tools: Vec<_> = response.tool_uses().collect();
    assert_eq!(tools.len(), 1);
    let Content::ToolUse { name, input_json, .. } = tools[0] else {
        panic!("expected tool use");
    };
    assert_eq!(name, "read_file");
    assert!(input_json.contains("a.rs"));
}

#[tokio::test]
async fn chat_truncated_stream_is_incomplete_error() {
    let body = vec![chat_text_chunk("partial but no [DONE]")];

    let url = serve_text(200, sse_headers(), body).await;
    let c = client_with_endpoint(url, "gpt-4o", "/chat/completions");
    let stream = c.stream(request()).await.expect("stream");

    let error = stream.collect().await.expect_err("truncated stream must fail");
    assert!(matches!(error, LlmError::IncompleteStream));
}

// ─── Responses-API integration tests ─────────────────────────────────────────

#[tokio::test]
async fn streams_responses_text_response_end_to_end() {
    let body = vec![
        responses_text_delta("Hello"),
        responses_text_delta(", world"),
        responses_completed(12, 7),
    ];

    let url = serve_text(200, sse_headers(), body).await;
    let c = client_with_endpoint(url, "gpt-4o", "/responses");
    let stream = c.stream(request()).await.expect("stream");
    let response = stream.collect().await.expect("complete");

    assert_eq!(response.text, "Hello, world");
    assert_eq!(response.stop_reason.as_deref(), Some("end_turn"));
    assert_eq!(response.usage.input_tokens, 12);
    assert_eq!(response.usage.output_tokens, 7);
}

#[tokio::test]
async fn streams_responses_tool_call_end_to_end() {
    let item_added = serde_json::json!({
        "type": "response.output_item.added",
        "output_index": 0,
        "item": { "type": "function_call", "call_id": "c1", "name": "read_file" }
    });
    let args_delta = serde_json::json!({
        "type": "response.function_call_arguments.delta",
        "output_index": 0,
        "delta": "{\"path\":\"src/main.rs\"}"
    });
    let completed = serde_json::json!({
        "type": "response.completed",
        "response": {}
    });
    let body = vec![
        format!("event: response.output_item.added\ndata: {item_added}\n\n"),
        format!("event: response.function_call_arguments.delta\ndata: {args_delta}\n\n"),
        format!("event: response.completed\ndata: {completed}\n\n"),
    ];

    let url = serve_text(200, sse_headers(), body).await;
    let c = client_with_endpoint(url, "gpt-4o", "/responses");
    let stream = c.stream(request()).await.expect("stream");
    let response = stream.collect().await.expect("complete");

    assert_eq!(response.stop_reason.as_deref(), Some("tool_use"));
    let tools: Vec<_> = response.tool_uses().collect();
    assert_eq!(tools.len(), 1);
    let Content::ToolUse { name, .. } = tools[0] else {
        panic!("expected tool use");
    };
    assert_eq!(name, "read_file");
}

#[tokio::test]
async fn responses_truncated_stream_is_incomplete_error() {
    let body = vec![responses_text_delta("partial no terminal event")];

    let url = serve_text(200, sse_headers(), body).await;
    let c = client_with_endpoint(url, "gpt-4o", "/responses");
    let stream = c.stream(request()).await.expect("stream");

    let error = stream.collect().await.expect_err("truncated stream must fail");
    assert!(matches!(error, LlmError::IncompleteStream));
}

// ─── Anthropic-Messages-via-Copilot tests ────────────────────────────────────

#[tokio::test]
async fn streams_via_anthropic_messages_endpoint() {
    let body = vec![
        anthropic_event(
            "content_block_start",
            serde_json::json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "text", "text": "" } }),
        ),
        anthropic_event(
            "content_block_delta",
            serde_json::json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "text_delta", "text": "Anthropic reply" } }),
        ),
        anthropic_event(
            "content_block_stop",
            serde_json::json!({ "type": "content_block_stop", "index": 0 }),
        ),
        anthropic_event(
            "message_delta",
            serde_json::json!({ "type": "message_delta", "delta": { "stop_reason": "end_turn" }, "usage": { "output_tokens": 3 } }),
        ),
        anthropic_message_stop(),
    ];

    let url = serve_text(200, sse_headers(), body).await;
    let c = client_with_endpoint(url, "claude-opus-5", "/v1/messages");
    let stream = c
        .stream(ChatRequest::new("claude-opus-5", vec![Message::user("hi")]))
        .await
        .expect("stream");
    let response = stream.collect().await.expect("complete");

    assert_eq!(response.text, "Anthropic reply");
    assert_eq!(response.stop_reason.as_deref(), Some("end_turn"));
}

// ─── Endpoint selection via live metadata ────────────────────────────────────

#[tokio::test]
async fn selects_responses_endpoint_from_live_model_metadata() {
    let models_json = serde_json::json!({
        "data": [{
            "id": "gpt-4o",
            "capabilities": { "type": "chat" },
            "supported_endpoints": ["/responses"]
        }]
    })
    .to_string();

    let sse_body = vec![
        responses_text_delta("from responses"),
        responses_completed(5, 3),
    ];

    // First request → GET /models; second request → POST /responses.
    let url = serve_sequence(
        200, json_headers(), vec![models_json],
        200, sse_headers(), sse_body,
    ).await;

    // Fresh client with no cached metadata forces a /models call.
    coda_llm::copilot::models::cache_invalidate(&url);
    let stream = client(url).stream(request()).await.expect("stream");
    let response = stream.collect().await.expect("complete");

    assert_eq!(response.text, "from responses");
}

// ─── Error handling ───────────────────────────────────────────────────────────

#[tokio::test]
async fn surfaces_a_provider_error_body() {
    let body = vec![
        r#"{"error":{"message":"model not found","type":"invalid_request_error"}}"#.to_string(),
    ];
    let url = serve_text(400, json_headers(), body).await;

    let error = client_with_endpoint(url, "gpt-4o", "/chat/completions")
        .stream(request())
        .await
        .expect_err("400 must fail before streaming");
    assert!(error.to_string().contains("model not found"));
    assert!(!error.is_retryable());
}

#[tokio::test]
async fn authentication_failure_surfaces_as_unauthorized() {
    let body = vec![r#"{"error":{"message":"invalid token"}}"#.to_string()];
    let url = serve_text(401, json_headers(), body).await;

    let error = client_with_endpoint(url, "gpt-4o", "/chat/completions")
        .stream(request())
        .await
        .expect_err("401 must fail");
    assert!(matches!(error, LlmError::Unauthorized(_)));
    assert!(!error.is_retryable());
}

// ─── Multibyte split tests ────────────────────────────────────────────────────

#[tokio::test]
async fn chat_decodes_multibyte_text_split_at_every_byte_offset() {
    let full = format!(
        "{}{}{}",
        "data: {\"choices\":[{\"delta\":{\"content\":null},\"finish_reason\":null}]}\n\n",
        chat_text_chunk("café ☕ 日本語 🚀"),
        chat_done_chunk("stop"),
    );
    let bytes = full.as_bytes();

    for split in 1..bytes.len() {
        let body = vec![bytes[..split].to_vec(), bytes[split..].to_vec()];
        let url = serve(200, sse_headers(), body).await;
        let c = client_with_endpoint(url, "gpt-4o", "/chat/completions");
        let stream = c.stream(request()).await.expect("stream");
        let response = stream
            .collect()
            .await
            .unwrap_or_else(|e| panic!("chat split at byte {split} failed: {e}"));
        assert_eq!(
            response.text, "café ☕ 日本語 🚀",
            "chat split at byte {split} corrupted the text"
        );
    }
}

#[tokio::test]
async fn responses_decodes_multibyte_text_split_at_every_byte_offset() {
    let full = format!(
        "{}{}",
        responses_text_delta("café ☕ 🚀"),
        responses_completed(5, 3),
    );
    let bytes = full.as_bytes();

    for split in 1..bytes.len() {
        let body = vec![bytes[..split].to_vec(), bytes[split..].to_vec()];
        let url = serve(200, sse_headers(), body).await;
        let c = client_with_endpoint(url, "gpt-4o", "/responses");
        let stream = c.stream(request()).await.expect("stream");
        let response = stream
            .collect()
            .await
            .unwrap_or_else(|e| panic!("responses split at byte {split} failed: {e}"));
        assert_eq!(
            response.text, "café ☕ 🚀",
            "responses split at byte {split} corrupted the text"
        );
    }
}

// ─── Model listing ────────────────────────────────────────────────────────────

#[tokio::test]
async fn lists_models_over_http() {
    let body = vec![serde_json::json!({
        "data": [
            {
                "id": "gpt-4o",
                "name": "GPT-4o",
                "capabilities": {
                    "type": "chat",
                    "limits": { "max_context_window_tokens": 128000 }
                },
                "supported_endpoints": ["/chat/completions", "/responses"]
            }
        ]
    })
    .to_string()];

    let url = serve_text(200, json_headers(), body).await;
    // Start fresh (no cached models for this URL).
    coda_llm::copilot::models::cache_invalidate(&url);

    let models = client(url).list_models().await.expect("models");
    assert_eq!(models.len(), 1);
    assert_eq!(models[0].id, "gpt-4o");
    assert_eq!(models[0].display_name.as_deref(), Some("GPT-4o"));
    assert_eq!(models[0].context_limit, Some(128_000));
    assert_eq!(
        coda_llm::copilot::models::resolve_endpoint(&models[0]),
        CopilotEndpoint::Responses
    );
}

// ─── Endpoint-mismatch retry tests ───────────────────────────────────────────

/// Flow: GET /models → chat-only, POST /chat → 400 mismatch, GET /models →
/// /responses, POST /responses → SSE. Verifies the retry path routes to the
/// better endpoint and returns the final text.
#[tokio::test]
async fn mismatch_retry_succeeds_on_better_endpoint() {
    let models_chat_only = serde_json::json!({
        "data": [{ "id": "gpt-4o", "supported_endpoints": ["/chat/completions"] }]
    })
    .to_string();

    let mismatch_400 =
        r#"{"error":{"message":"The model is not accessible via /chat/completions"}}"#.to_string();

    let models_with_responses = serde_json::json!({
        "data": [{ "id": "gpt-4o", "supported_endpoints": ["/responses"] }]
    })
    .to_string();

    let url = serve_n(vec![
        (200, json_headers(), vec![models_chat_only]),
        (400, json_headers(), vec![mismatch_400]),
        (200, json_headers(), vec![models_with_responses]),
        (200, sse_headers(), vec![responses_text_delta("retry succeeded"), responses_completed(5, 3)]),
    ])
    .await;

    coda_llm::copilot::models::cache_invalidate(&url);
    let stream = client(url).stream(request()).await.expect("stream");
    let response = stream.collect().await.expect("complete");

    assert_eq!(response.text, "retry succeeded");
}

/// Flow: GET /models → chat-only, POST /chat → 400 mismatch, GET /models →
/// still chat-only. Verifies the client gives up and surfaces the original 400
/// rather than looping or masking it.
#[tokio::test]
async fn mismatch_retry_gives_up_when_re_resolution_still_yields_chat() {
    let models_chat_only = serde_json::json!({
        "data": [{ "id": "gpt-4o", "supported_endpoints": ["/chat/completions"] }]
    })
    .to_string();

    let mismatch_400 =
        r#"{"error":{"message":"The model is not accessible via /chat/completions"}}"#.to_string();

    let url = serve_n(vec![
        (200, json_headers(), vec![models_chat_only.clone()]),
        (400, json_headers(), vec![mismatch_400]),
        (200, json_headers(), vec![models_chat_only]),
    ])
    .await;

    coda_llm::copilot::models::cache_invalidate(&url);
    let error = client(url)
        .stream(request())
        .await
        .expect_err("should fail with the original 400");

    assert!(
        error.to_string().contains("/chat/completions"),
        "original 400 body must surface, got: {error}"
    );
    assert!(!error.is_retryable());
}

// ─── BUG4: diagnostics — dispatch/protocol/route provenance ─────────────────

/// The reroute after a chat-completions mismatch is two distinct physical
/// dispatches: dispatch 1 (chat, mismatched, no stream ever opens) and
/// dispatch 2 (responses, succeeds) — each with its own fresh request shape
/// and correct route-source provenance, and `stream_opened` recorded only
/// for the dispatch that actually got a 2xx.
#[tokio::test]
async fn mismatch_reroute_records_two_dispatches_with_fresh_shape_and_stream_opened() {
    let dir = tempfile::tempdir().unwrap();
    let ctx = diag_ctx(dir.path());

    let models_chat_only = serde_json::json!({
        "data": [{ "id": "gpt-4o", "supported_endpoints": ["/chat/completions"] }]
    })
    .to_string();
    let mismatch_400 =
        r#"{"error":{"message":"The model is not accessible via /chat/completions"}}"#.to_string();
    let models_with_responses = serde_json::json!({
        "data": [{ "id": "gpt-4o", "supported_endpoints": ["/responses"] }]
    })
    .to_string();

    let url = serve_n(vec![
        (200, json_headers(), vec![models_chat_only]),
        (400, json_headers(), vec![mismatch_400]),
        (200, json_headers(), vec![models_with_responses]),
        (200, sse_headers(), vec![responses_text_delta("retry succeeded"), responses_completed(5, 3)]),
    ])
    .await;
    coda_llm::copilot::models::cache_invalidate(&url);

    coda_diagnostics::scope(ctx.clone(), async {
        let stream = client(url).stream(request()).await.expect("stream");
        stream.collect().await.expect("complete");
    })
    .await;

    let lines = diag_lines(&ctx);
    let shapes: Vec<_> = lines.iter().filter(|l| l["kind"] == "request_shape").collect();
    assert_eq!(shapes.len(), 2, "one request_shape per physical dispatch: {lines:?}");
    assert_eq!(shapes[0]["dispatch"], 1);
    assert_eq!(shapes[0]["protocol"], "copilot_chat_completions");
    assert_eq!(shapes[0]["route_source"], "model_metadata", "the cached model row named /chat/completions, a recognized endpoint");
    assert_eq!(shapes[1]["dispatch"], 2);
    assert_eq!(shapes[1]["protocol"], "copilot_responses");
    assert_eq!(shapes[1]["route_source"], "reroute_after_mismatch");

    let failure_details: Vec<_> = lines.iter().filter(|l| l["kind"] == "http_failure_details").collect();
    assert_eq!(failure_details.len(), 1, "only dispatch 1 ever failed: {lines:?}");
    assert_eq!(failure_details[0]["dispatch"], 1);

    let opened: Vec<_> = lines.iter().filter(|l| l["kind"] == "stream_opened").collect();
    assert_eq!(opened.len(), 1, "stream_opened only for the dispatch that actually got 2xx: {lines:?}");
    assert_eq!(opened[0]["dispatch"], 2);
    assert_eq!(opened[0]["protocol"], "copilot_responses");
    assert_eq!(opened[0]["route_source"], "reroute_after_mismatch");

    // stream_opened must physically follow both request_shape/http_failure_details
    // for dispatch 1, and follow (not precede) dispatch 2's own request_shape.
    let opened_pos = lines.iter().position(|l| l["kind"] == "stream_opened").unwrap();
    let dispatch2_shape_pos =
        lines.iter().position(|l| l["kind"] == "request_shape" && l["dispatch"] == 2).unwrap();
    assert!(dispatch2_shape_pos < opened_pos);
}

/// A model whose metadata was never fetched at all (no cache, no `/models`
/// entry for it) is `metadata_missing_default`, distinct from a model whose
/// metadata exists but names nothing recognized
/// (`metadata_unrecognized_default`) — collapsing the two would let a
/// caller believe an absent catalog and an unrecognized catalog entry are
/// the same routing decision.
#[tokio::test]
async fn missing_vs_unrecognized_metadata_are_recorded_distinctly() {
    let dir = tempfile::tempdir().unwrap();
    let ctx = diag_ctx(dir.path());
    let body = vec![chat_done_chunk("stop")];
    let url = serve_text(200, sse_headers(), body).await;

    // No cache at all for this base URL/model → MetadataMissingDefault.
    coda_llm::copilot::models::cache_invalidate(&url);
    // Pre-seed the cache with an *empty* model list is impossible (empty
    // lists are never cached — see `models::cache_set`), so populate a
    // *different* model id: `gpt-4o` itself is still absent from the cache.
    coda_llm::copilot::models::cache_set(&url, vec![ModelInfo::new("some-other-model")]);

    coda_diagnostics::scope(ctx.clone(), async {
        let stream = client(url).stream(request()).await.expect("stream");
        stream.collect().await.expect("complete");
    })
    .await;

    let lines = diag_lines(&ctx);
    let shape = lines.iter().find(|l| l["kind"] == "request_shape").expect("a request_shape event");
    assert_eq!(shape["route_source"], "metadata_missing_default");

    let directory = tempfile::tempdir().unwrap();
    let ctx = diag_ctx(directory.path());
    let url = serve_text(200, sse_headers(), vec![chat_done_chunk("stop")]).await;
    let mut model = ModelInfo::new("gpt-4o");
    model.supported_endpoints = vec!["/v1/embeddings".into()];
    coda_llm::copilot::models::cache_set(&url, vec![model]);
    coda_diagnostics::scope(ctx.clone(), async {
        client(url).stream(request()).await.unwrap().collect().await.unwrap();
    }).await;
    let lines = diag_lines(&ctx);
    let shape = lines.iter().find(|line| line["kind"] == "request_shape").unwrap();
    assert_eq!(shape["protocol"], "copilot_chat_completions");
    assert_eq!(shape["route_source"], "metadata_unrecognized_default");
}

#[tokio::test]
async fn an_incomplete_http_error_body_is_unreadable_not_empty() {
    let directory = tempfile::tempdir().unwrap();
    let ctx = diag_ctx(directory.path());
    let url = serve_text(
        400,
        "content-type: application/json\r\ncontent-length: 500\r\nconnection: close\r\n",
        vec!["{}".into()],
    ).await;
    let result = tokio::time::timeout(Duration::from_secs(5), coda_diagnostics::scope(ctx.clone(), async {
        client_with_endpoint(url, "gpt-4o", "/responses").stream(request()).await
    })).await.expect("body EOF must not hang");
    assert!(matches!(result, Err(LlmError::Api { status: 400, .. })));
    let lines = diag_lines(&ctx);
    let detail = &lines.iter().find(|line| line["kind"] == "http_failure_details").unwrap()["detail"];
    assert_eq!(detail["body_kind"], "unreadable");
    for field in ["error_type", "error_code", "parameter"] {
        assert_eq!(detail[field]["state"], "unavailable");
    }
    assert!(!lines.iter().any(|line| line["kind"] == "stream_opened"));
}

/// Prestream failures (the HTTP retry policy exhausted, never a 2xx) must be
/// `request_failure`, never `stream_failure` — and no `stream_opened` is
/// ever recorded for that context, since headers were never accepted.
#[tokio::test]
async fn a_prestream_400_never_opens_a_stream_context() {
    let dir = tempfile::tempdir().unwrap();
    let ctx = diag_ctx(dir.path());
    let body = vec![r#"{"error":{"type":"invalid_request_error","code":"model_not_found"}}"#.to_string()];
    let url = serve_text(400, json_headers(), body).await;

    coda_diagnostics::scope(ctx.clone(), async {
        let _ = client_with_endpoint(url, "gpt-4o", "/chat/completions").stream(request()).await;
    })
    .await;

    let lines = diag_lines(&ctx);
    assert!(!lines.iter().any(|l| l["kind"] == "stream_opened"), "{lines:?}");
    // The client-level `record_stream_failure`/`request_failure` split lives
    // in coda-agent (which drives `client.stream()`); at this layer we only
    // assert the HTTP-level facts are present and no stream ever opened.
    assert!(lines.iter().any(|l| l["kind"] == "http_failure_details"), "{lines:?}");
}

/// Matrix over structured/unstructured provider error bodies: a recognized
/// `type`/`code`/`param`, an unrecognized-but-well-formed one, a non-JSON
/// body, an empty body, and an oversized body must each classify distinctly
/// — and the actual (never-allowlisted) values must never appear anywhere
/// in the persisted log.
#[tokio::test]
async fn error_body_kind_matrix_classifies_each_case_and_never_leaks_unrecognized_text() {
    let cases: Vec<(&str, String, &str)> = vec![
        (
            "recognized",
            r#"{"error":{"type":"invalid_request_error","code":"model_not_found","param":"model","message":"canary-recognized-message"}}"#.to_string(),
            "json",
        ),
        (
            "unrecognized",
            r#"{"error":{"type":"canary-unrecognized-type","code":"canary-unrecognized-code","param":"canary.unrecognized.param"}}"#.to_string(),
            "json",
        ),
        ("non_json", "canary-not-json-at-all <<>>".to_string(), "non_json"),
        ("empty", String::new(), "empty"),
        (
            "oversized",
            format!(r#"{{"error":{{"type":"invalid_request_error","code":"canary-oversized-{}"}}}}"#, "x".repeat(70_000)),
            "oversized",
        ),
    ];

    for (label, body, expected_kind) in cases {
        let dir = tempfile::tempdir().unwrap();
        let ctx = diag_ctx(dir.path());
        let url = serve_text(400, json_headers(), vec![body.clone()]).await;

        coda_diagnostics::scope(ctx.clone(), async {
            let _ = client_with_endpoint(url, "gpt-4o", "/chat/completions").stream(request()).await;
        })
        .await;

        let lines = diag_lines(&ctx);
        let details = lines
            .iter()
            .find(|l| l["kind"] == "http_failure_details")
            .unwrap_or_else(|| panic!("[{label}] expected http_failure_details, got: {lines:?}"));
        assert_eq!(details["detail"]["body_kind"], expected_kind, "[{label}]");

        if label == "recognized" {
            assert_eq!(details["detail"]["error_type"]["state"], "recognized");
            assert_eq!(details["detail"]["error_type"]["value"], "invalid_request_error");
            assert_eq!(details["detail"]["error_code"]["state"], "recognized");
            assert_eq!(details["detail"]["error_code"]["value"], "model_not_found");
            assert_eq!(details["detail"]["parameter"]["state"], "recognized");
            assert_eq!(details["detail"]["parameter"]["value"], "model");
        }
        if label == "unrecognized" {
            assert_eq!(details["detail"]["error_type"]["state"], "unrecognized");
            assert_eq!(details["detail"]["error_code"]["state"], "unrecognized");
            assert_eq!(details["detail"]["parameter"]["state"], "unrecognized");
        }
        if label == "non_json" || label == "empty" {
            assert_eq!(details["detail"]["error_type"]["state"], "unavailable");
            assert_eq!(details["detail"]["error_code"]["state"], "unavailable");
        }
        if label == "oversized" {
            assert_eq!(details["detail"]["error_type"]["state"], "omitted");
            assert_eq!(details["detail"]["error_code"]["state"], "omitted");
        }

        for line in &lines {
            let serialized = line.to_string();
            assert!(!serialized.contains("canary"), "[{label}] a canary value leaked verbatim: {serialized}");
        }
    }
}

/// Full-log privacy scan: prompt text, tool names/schemas, and the
/// provider's free-text `message` must never appear anywhere in the
/// persisted JSONL, at any verbosity, even when the request carries a
/// system prompt and tool definitions and the response 400s with a
/// structured body whose `message` echoes them back (as real gateways do).
#[tokio::test]
async fn full_log_privacy_scan_finds_no_prompt_tool_or_message_canaries() {
    let dir = tempfile::tempdir().unwrap();
    let ctx = diag_ctx(dir.path());

    let secret_system = "SYSTEM-CANARY-do-not-ever-persist-this-prompt";
    let secret_tool = "canary_tool_name_should_never_appear";
    let secret_arg_schema = r#"{"type":"object","properties":{"canary_secret_field":{"type":"string"}}}"#;
    let body = format!(
        r#"{{"error":{{"type":"invalid_request_error","message":"rejected: {secret_system} / {secret_tool}"}}}}"#
    );
    let url = serve_text(400, json_headers(), vec![body]).await;

    let request = ChatRequest::new("gpt-4o", vec![Message::user("what is my api key sk-test-canary-123?")])
        .with_system(secret_system)
        .with_tools(vec![coda_llm::ToolDefinition::new(secret_tool, "d", secret_arg_schema)]);

    coda_diagnostics::scope(ctx.clone(), async {
        let _ = client_with_endpoint(url, "gpt-4o", "/chat/completions").stream(request).await;
    })
    .await;

    let raw = std::fs::read_to_string(ctx.logger().status().path.expect("a log path")).unwrap();
    for canary in [secret_system, secret_tool, "canary_secret_field", "sk-test-canary-123", "rejected:"] {
        assert!(!raw.contains(canary), "canary `{canary}` leaked into the diagnostic log:\n{raw}");
    }
    // Still records the recognized, safe classification.
    assert!(raw.contains("invalid_request_error"));
    assert!(raw.contains("request_shape"));
    let shape = diag_lines(&ctx).into_iter().find(|line| line["kind"] == "request_shape").unwrap();
    assert_eq!(shape["system_present"], true, "the chat system-role item was not reported");
}
