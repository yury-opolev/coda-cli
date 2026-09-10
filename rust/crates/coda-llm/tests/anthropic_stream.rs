//! End-to-end streaming against a real HTTP server.
//!
//! The unit tests cover the SSE framing and the Anthropic state machine
//! separately. These drive the whole path — socket, chunked body, decoder,
//! channel — because that is where the seams are, and a mock at the decoder
//! boundary would not exercise them.

use std::sync::Arc;
use std::time::Duration;

use coda_llm::anthropic::{AnthropicClient, AnthropicConfig, StreamEvent};
use coda_llm::{ChatRequest, Content, LlmClient, LlmError, Message, RetryPolicy};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

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

/// A one-shot HTTP server that replays a canned response.
///
/// Returns the base URL to point a client at.
async fn serve_text(status: u16, headers: &str, body: Vec<String>) -> String {
    serve(status, headers, body.into_iter().map(String::into_bytes).collect()).await
}

/// A server that accepts one connection per entry in `responses`, replaying
/// each in turn — used to simulate a client retrying against the same URL.
async fn serve_n(responses: Vec<(u16, &'static str, String)>) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();

    tokio::spawn(async move {
        for (status, headers, body) in responses {
            let Ok((mut socket, _)) = listener.accept().await else { return };
            let mut buffer = vec![0u8; 8192];
            let _ = socket.read(&mut buffer).await;
            let reason = if status == 200 { "OK" } else { "Error" };
            let head = format!(
                "HTTP/1.1 {status} {reason}\r\n{headers}content-length: {}\r\nconnection: close\r\n\r\n",
                body.len()
            );
            if socket.write_all(head.as_bytes()).await.is_err() {
                return;
            }
            let _ = socket.write_all(body.as_bytes()).await;
            let _ = socket.shutdown().await;
        }
    });

    format!("http://127.0.0.1:{port}")
}

/// A one-shot HTTP server that replays a canned byte stream.
async fn serve(status: u16, headers: &str, body: Vec<Vec<u8>>) -> String {
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

    let url = serve_text(200, sse_headers(), body).await;
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

    let url = serve_text(200, sse_headers(), body).await;
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

    let url = serve_text(200, sse_headers(), body).await;
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

    let url = serve_text(200, sse_headers(), body).await;
    let stream = client(url).stream(request()).await.expect("stream");
    let response = stream.collect().await.expect("complete");

    assert_eq!(response.text, "split");
}

#[tokio::test]
async fn a_truncated_stream_is_reported_rather_than_silently_short() {
    // No message_stop: the connection just closes.
    let body = vec![start_text_block(), text_delta("partial")];

    let url = serve_text(200, sse_headers(), body).await;
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
    let url = serve_text(400, json_headers(), body).await;

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
    let url = serve_text(401, json_headers(), body).await;

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

    let url = serve_text(200, sse_headers(), body).await;
    let stream = client(url).stream(request()).await.expect("stream");

    let error = stream.collect().await.expect_err("should fail");
    assert!(error.to_string().contains("Overloaded"));
    assert!(error.is_retryable(), "an overload deserves a retry");
}

/// BUG4 regression: an SSE "error" event that arrives as the very last bytes
/// of the connection, with no trailing blank line, is only ever seen by
/// `SseDecoder::finish()`'s flush — not the main per-chunk loop. A decode
/// failure there must surface as the real decoded error, never be silently
/// discarded and misreported as a generic `IncompleteStream` (which reads
/// as "we never heard the end" rather than "the provider told us something,
/// and rejecting/decoding it failed").
#[tokio::test]
async fn a_decode_error_on_the_final_flushed_sse_event_is_not_misreported_as_incomplete_stream() {
    let error_event = serde_json::json!({ "type": "error", "error": { "type": "overloaded_error", "message": "Overloaded-at-eof" } });
    // Deliberately no second `\n\n`: the connection closes right after the
    // single blank-line-less write, so this event is only ever captured by
    // `SseDecoder::finish()`, not the main per-chunk `push()` loop.
    let raw = format!("event: error\ndata: {error_event}\n");
    let body = vec![start_text_block().into_bytes(), raw.into_bytes()];

    let url = serve(200, sse_headers(), body).await;
    let stream = client(url).stream(request()).await.expect("stream");

    let error = stream.collect().await.expect_err("the flushed error event must still fail the stream");
    assert!(
        !matches!(error, LlmError::IncompleteStream),
        "a decode failure on the flushed final event must not be misclassified as IncompleteStream, got: {error:?}"
    );
    assert!(error.to_string().contains("Overloaded-at-eof"), "the actual decoded error must surface: {error}");
}

#[tokio::test]
async fn dropping_the_stream_stops_the_pump_promptly() {
    // The server sends one event and then holds the connection open forever.
    // A pump that only noticed cancellation on its next send would sit here
    // until the idle timeout, so this asserts it wakes on the dropped consumer.
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let port = listener.local_addr().expect("addr").port();
    let (closed_tx, closed_rx) = tokio::sync::oneshot::channel();

    tokio::spawn(async move {
        let Ok((mut socket, _)) = listener.accept().await else {
            return;
        };
        let mut buffer = vec![0u8; 8192];
        let _ = socket.read(&mut buffer).await;

        let head = format!("HTTP/1.1 200 OK\r\n{}\r\n", sse_headers());
        let _ = socket.write_all(head.as_bytes()).await;
        let _ = socket.write_all(start_text_block().as_bytes()).await;
        let _ = socket.write_all(text_delta("first").as_bytes()).await;
        let _ = socket.flush().await;

        // Block until the client goes away, then report it.
        let mut sink = vec![0u8; 1024];
        let _ = socket.read(&mut sink).await;
        let _ = closed_tx.send(());
    });

    let url = format!("http://127.0.0.1:{port}");
    let mut stream = client(url).stream(request()).await.expect("stream");
    assert_eq!(
        stream.next().await.expect("an event").expect("no error"),
        StreamEvent::TextDelta("first".into())
    );

    drop(stream);

    // The connection must close well within the 120s idle timeout.
    tokio::time::timeout(Duration::from_secs(5), closed_rx)
        .await
        .expect("the pump did not release the connection after the consumer was dropped")
        .expect("server task should report closure");
}

#[tokio::test]
async fn decodes_multibyte_text_split_across_transport_chunks() {
    // Transport chunks split at arbitrary byte offsets, which routinely lands
    // mid-character for non-ASCII content. Splitting the body at every byte
    // proves no boundary can break the decoder.
    let full = format!(
        "{}{}{}",
        start_text_block(),
        text_delta("café ☕ 日本語 🚀"),
        message_stop()
    );
    let bytes = full.as_bytes();

    for split in 1..bytes.len() {
        // Split the raw bytes, so a chunk can legitimately end mid-character.
        let body = vec![bytes[..split].to_vec(), bytes[split..].to_vec()];

        let url = serve(200, sse_headers(), body).await;
        let stream = client(url).stream(request()).await.expect("stream");
        let response = stream
            .collect()
            .await
            .unwrap_or_else(|error| panic!("split at byte {split} failed: {error}"));

        assert_eq!(
            response.text, "café ☕ 日本語 🚀",
            "split at byte {split} corrupted the text"
        );
    }
}

#[tokio::test]
async fn lists_models_over_http() {
    let body = vec![serde_json::json!({
        "data": [
            { "id": "claude-opus-5", "display_name": "Claude Opus 5", "context_window": 200000 }
        ]
    })
    .to_string()];
    let url = serve_text(200, json_headers(), body).await;

    let models = client(url).list_models().await.expect("models");
    assert_eq!(models.len(), 1);
    assert_eq!(models[0].id, "claude-opus-5");
    assert_eq!(models[0].context_limit, Some(200_000));
}

// ─── BUG4: diagnostics ───────────────────────────────────────────────────────

#[tokio::test]
async fn stream_opened_is_recorded_after_2xx_headers_before_any_pump_activity() {
    let dir = tempfile::tempdir().unwrap();
    let ctx = diag_ctx(dir.path());
    let body = vec![start_text_block(), text_delta("hi"), message_stop()];
    let url = serve_text(200, sse_headers(), body).await;

    coda_diagnostics::scope(ctx.clone(), async {
        let stream = client(url).stream(request()).await.expect("stream");
        stream.collect().await.expect("complete");
    })
    .await;

    let lines = diag_lines(&ctx);
    let opened = lines.iter().position(|l| l["kind"] == "stream_opened").expect("a stream_opened event");
    assert_eq!(lines[opened]["dispatch"], 1);
    assert_eq!(lines[opened]["protocol"], "anthropic_messages");
    assert_eq!(lines[opened]["route_source"], "fixed_provider_default");
    let result_pos = lines.iter().position(|l| l["kind"] == "http_result").expect("an http_result");
    assert!(result_pos < opened, "stream_opened must follow the 2xx http_result: {lines:?}");
}

/// Exact wire byte length for a non-ASCII payload: `body_bytes` must equal
/// the actual serialized UTF-8 byte length, not the character count.
#[tokio::test]
async fn request_shape_byte_count_is_exact_for_a_non_ascii_payload() {
    let dir = tempfile::tempdir().unwrap();
    let ctx = diag_ctx(dir.path());
    let body = vec![start_text_block(), text_delta("ok"), message_stop()];
    let url = serve_text(200, sse_headers(), body).await;

    let non_ascii_request =
        ChatRequest::new("claude-opus-5", vec![Message::user("café ☕ 日本語 🚀 — multi-byte canary")]);
    let expected_bytes = serde_json::to_vec(&coda_llm::anthropic::request::build(&non_ascii_request)).unwrap().len() as u64;

    coda_diagnostics::scope(ctx.clone(), async {
        let stream = client(url).stream(non_ascii_request).await.expect("stream");
        stream.collect().await.expect("complete");
    })
    .await;

    let lines = diag_lines(&ctx);
    let shape = lines.iter().find(|l| l["kind"] == "request_shape").expect("a request_shape event");
    assert_eq!(shape["body_bytes"].as_u64().unwrap(), expected_bytes);
    assert!(expected_bytes as usize > "café ☕ 日本語 🚀 — multi-byte canary".chars().count(), "sanity: multi-byte chars must make bytes exceed char count");
}

/// A 429 (with `Retry-After`) then success: exactly one `request_shape` per
/// physical attempt, in the same file-order as the legacy `http_attempt`/
/// `http_result`/`http_retry`/`http_recovery` events, and the retried body's
/// shape/bytes are identical across both attempts.
#[tokio::test]
async fn a_429_then_success_records_one_request_shape_per_attempt_in_order() {
    let dir = tempfile::tempdir().unwrap();
    let ctx = diag_ctx(dir.path());

    let url = serve_n(vec![
        (429, "retry-after: 0\r\n", r#"{"error":{"type":"rate_limit_error","code":"rate_limit_exceeded"}}"#.to_string()),
        (200, sse_headers(), {
            let mut s = String::new();
            for chunk in [start_text_block(), text_delta("ok"), message_stop()] {
                s.push_str(&chunk);
            }
            s
        }),
    ])
    .await;

    let policy = RetryPolicy {
        max_attempts: 3,
        initial_backoff: Duration::from_millis(1),
        max_backoff: Duration::from_millis(5),
    };
    let retrying_client = AnthropicClient::new(
        AnthropicConfig::api_key("test-key").with_base_url(url).with_retry(policy),
    )
    .expect("client");

    coda_diagnostics::scope(ctx.clone(), async {
        let stream = retrying_client.stream(request()).await.expect("stream");
        stream.collect().await.expect("complete");
    })
    .await;

    let lines = diag_lines(&ctx);
    let kinds: Vec<&str> = lines.iter().map(|l| l["kind"].as_str().unwrap()).collect();
    assert_eq!(
        kinds,
        vec![
            "http_attempt",
            "request_shape",
            "http_result",
            "http_failure_details",
            "http_retry",
            "http_attempt",
            "request_shape",
            "http_result",
            "http_recovery",
            "stream_opened",
        ],
        "unexpected event order: {lines:?}"
    );
    let shapes: Vec<_> = lines.iter().filter(|l| l["kind"] == "request_shape").collect();
    assert_eq!(shapes.len(), 2, "one request_shape per physical attempt");
    assert_eq!(shapes[0]["message_count"], shapes[1]["message_count"]);
    assert_eq!(shapes[0]["body_bytes"], shapes[1]["body_bytes"], "the retried body is identical");
}

