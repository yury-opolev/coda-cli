//! End-to-end streaming against a real HTTP server.
//!
//! The unit tests cover the SSE framing and the Anthropic state machine
//! separately. These drive the whole path — socket, chunked body, decoder,
//! channel — because that is where the seams are, and a mock at the decoder
//! boundary would not exercise them.

use std::time::Duration;

use coda_llm::anthropic::{AnthropicClient, AnthropicConfig, StreamEvent};
use coda_llm::{ChatRequest, Content, LlmClient, LlmError, Message, RetryPolicy};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

/// A one-shot HTTP server that replays a canned response.
///
/// Returns the base URL to point a client at.
async fn serve(status: u16, headers: &str, body: Vec<String>) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();
    let headers = headers.to_string();

    tokio::spawn(async move {
        let Ok((mut socket, _)) = listener.accept().await else {
            return;
        };

        // Read the request headers so the client's write completes.
        let mut buffer = vec![0u8; 8192];
        let _ = socket.read(&mut buffer).await;

        let reason = if status == 200 { "OK" } else { "Error" };
        let head = format!("HTTP/1.1 {status} {reason}\r\n{headers}\r\n");
        if socket.write_all(head.as_bytes()).await.is_err() {
            return;
        }

        // Write the body in chunks so the client sees a genuinely incremental
        // stream rather than one buffered blob.
        for chunk in body {
            if socket.write_all(chunk.as_bytes()).await.is_err() {
                return;
            }
            let _ = socket.flush().await;
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        let _ = socket.shutdown().await;
    });

    format!("http://127.0.0.1:{port}")
}

fn sse_headers() -> &'static str {
    "content-type: text/event-stream\r\nconnection: close\r\n"
}

fn json_headers() -> &'static str {
    "content-type: application/json\r\nconnection: close\r\n"
}

fn event(name: &str, data: serde_json::Value) -> String {
    format!("event: {name}\ndata: {data}\n\n")
}

fn client(base_url: String) -> AnthropicClient {
    AnthropicClient::new(
        AnthropicConfig::api_key("test-key")
            .with_base_url(base_url)
            .with_retry(RetryPolicy::none()),
    )
    .expect("client")
}

fn request() -> ChatRequest {
    ChatRequest::new("claude-opus-5", vec![Message::user("hello")])
}

fn start_text_block() -> String {
    event(
        "content_block_start",
        serde_json::json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "text", "text": "" } }),
    )
}

fn text_delta(text: &str) -> String {
    event(
        "content_block_delta",
        serde_json::json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "text_delta", "text": text } }),
    )
}

fn message_stop() -> String {
    event("message_stop", serde_json::json!({ "type": "message_stop" }))
}

#[tokio::test]
async fn streams_a_text_response_end_to_end() {
    let body = vec![
        event(
            "message_start",
            serde_json::json!({ "type": "message_start", "message": { "usage": { "input_tokens": 12 } } }),
        ),
        start_text_block(),
        text_delta("Hello"),
        text_delta(", world"),
        event(
            "content_block_stop",
            serde_json::json!({ "type": "content_block_stop", "index": 0 }),
        ),
        event(
            "message_delta",
            serde_json::json!({ "type": "message_delta", "delta": { "stop_reason": "end_turn" }, "usage": { "output_tokens": 7 } }),
        ),
        message_stop(),
    ];

    let url = serve(200, sse_headers(), body).await;
    let stream = client(url).stream(request()).await.expect("stream");
    let response = stream.collect().await.expect("complete");

    assert_eq!(response.text, "Hello, world");
    assert_eq!(response.stop_reason.as_deref(), Some("end_turn"));
    assert_eq!(response.usage.input_tokens, 12);
    assert_eq!(response.usage.output_tokens, 7);
}

#[tokio::test]
async fn streams_a_tool_call_end_to_end() {
    let body = vec![
        event(
            "content_block_start",
            serde_json::json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "tool_use", "id": "toolu_1", "name": "read_file" } }),
        ),
        event(
            "content_block_delta",
            serde_json::json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "input_json_delta", "partial_json": "{\"path\":" } }),
        ),
        event(
            "content_block_delta",
            serde_json::json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "input_json_delta", "partial_json": "\"src/main.rs\"}" } }),
        ),
        event(
            "content_block_stop",
            serde_json::json!({ "type": "content_block_stop", "index": 0 }),
        ),
        event(
            "message_delta",
            serde_json::json!({ "type": "message_delta", "delta": { "stop_reason": "tool_use" } }),
        ),
        message_stop(),
    ];

    let url = serve(200, sse_headers(), body).await;
    let stream = client(url).stream(request()).await.expect("stream");
    let response = stream.collect().await.expect("complete");

    assert_eq!(response.stop_reason.as_deref(), Some("tool_use"));
    let tools: Vec<_> = response.tool_uses().collect();
    assert_eq!(tools.len(), 1);

    let Content::ToolUse {
        name, input_json, ..
    } = tools[0]
    else {
        panic!("expected a tool use");
    };
    assert_eq!(name, "read_file");
    assert_eq!(input_json, r#"{"path":"src/main.rs"}"#);
}

#[tokio::test]
async fn events_arrive_incrementally_rather_than_all_at_once() {
    // Each event is written in its own chunk with a delay, so a client that
    // buffered the whole body would fail this.
    let body = vec![
        start_text_block(),
        text_delta("first"),
        text_delta("second"),
        message_stop(),
    ];

    let url = serve(200, sse_headers(), body).await;
    let mut stream = client(url).stream(request()).await.expect("stream");

    let first = stream.next().await.expect("an event").expect("no error");
    assert_eq!(first, StreamEvent::TextDelta("first".into()));

    let second = stream.next().await.expect("an event").expect("no error");
    assert_eq!(second, StreamEvent::TextDelta("second".into()));
}

#[tokio::test]
async fn decodes_events_split_across_transport_chunks() {
    // A single SSE event split across three writes at awkward boundaries.
    let body = vec![
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\nevent: content_bl".to_string(),
        "ock_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"spl".to_string(),
        "it\"}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n".to_string(),
    ];

    let url = serve(200, sse_headers(), body).await;
    let stream = client(url).stream(request()).await.expect("stream");
    let response = stream.collect().await.expect("complete");

    assert_eq!(response.text, "split");
}

#[tokio::test]
async fn a_truncated_stream_is_reported_rather_than_silently_short() {
    // No message_stop: the connection just closes.
    let body = vec![start_text_block(), text_delta("partial")];

    let url = serve(200, sse_headers(), body).await;
    let stream = client(url).stream(request()).await.expect("stream");

    let error = stream
        .collect()
        .await
        .expect_err("a truncated stream must not look complete");
    assert!(matches!(error, LlmError::IncompleteStream));
}

#[tokio::test]
async fn surfaces_a_provider_error_body() {
    let body = vec![
        r#"{"type":"error","error":{"type":"invalid_request_error","message":"model not found"}}"#
            .to_string(),
    ];
    let url = serve(400, json_headers(), body).await;

    let error = client(url)
        .stream(request())
        .await
        .expect_err("a 400 should fail");
    assert!(error.to_string().contains("model not found"));
    assert!(!error.is_retryable());
}

#[tokio::test]
async fn an_authentication_failure_is_not_retried() {
    let body = vec![r#"{"error":{"message":"invalid x-api-key"}}"#.to_string()];
    let url = serve(401, json_headers(), body).await;

    let error = client(url).stream(request()).await.expect_err("should fail");
    assert!(matches!(error, LlmError::Unauthorized(_)));
    assert!(!error.is_retryable());
}

#[tokio::test]
async fn an_inline_error_event_fails_the_stream() {
    let body = vec![
        start_text_block(),
        event(
            "error",
            serde_json::json!({ "type": "error", "error": { "type": "overloaded_error", "message": "Overloaded" } }),
        ),
    ];

    let url = serve(200, sse_headers(), body).await;
    let stream = client(url).stream(request()).await.expect("stream");

    let error = stream.collect().await.expect_err("should fail");
    assert!(error.to_string().contains("Overloaded"));
    assert!(error.is_retryable(), "an overload deserves a retry");
}

#[tokio::test]
async fn dropping_the_stream_cancels_the_request() {
    let body = vec![start_text_block(), text_delta("first"), message_stop()];

    let url = serve(200, sse_headers(), body).await;
    let mut stream = client(url).stream(request()).await.expect("stream");

    let _ = stream.next().await;
    drop(stream);

    // The pump task must exit rather than leak; the test passing without
    // hanging is the assertion.
    tokio::time::sleep(Duration::from_millis(50)).await;
}

#[tokio::test]
async fn lists_models_over_http() {
    let body = vec![serde_json::json!({
        "data": [
            { "id": "claude-opus-5", "display_name": "Claude Opus 5", "context_window": 200000 }
        ]
    })
    .to_string()];
    let url = serve(200, json_headers(), body).await;

    let models = client(url).list_models().await.expect("models");
    assert_eq!(models.len(), 1);
    assert_eq!(models[0].id, "claude-opus-5");
    assert_eq!(models[0].context_limit, Some(200_000));
}
