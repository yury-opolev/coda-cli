//! End-to-end streaming tests for the Copilot provider against a real TCP server.
//!
//! Unit tests cover the SSE framing and each protocol decoder in isolation.
//! These drive the full path — socket, chunked body, UTF-8 carry buffer,
//! decoder, channel — because that is where the seams are.

use std::time::Duration;

use coda_llm::copilot::{CopilotClient, CopilotConfig, CopilotEndpoint};
use coda_llm::{
    ChatRequest, Content, LlmClient, LlmError, Message, ModelInfo, RetryPolicy,
};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

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
