//! Retrying transient provider failures.
//!
//! Providers fail constantly for uninteresting reasons: overloaded servers,
//! rate limits, dropped connections. The loop should not surface those to the
//! user, but it must also not retry forever, and it must never retry something
//! that cannot succeed.

use std::time::Duration;

use crate::error::{FailureKind, LlmError};

/// How many attempts to make and how long to wait between them.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RetryPolicy {
    /// Total attempts, including the first. One means no retries.
    pub max_attempts: u32,
    pub initial_backoff: Duration,
    /// Backoff never grows past this, so a long outage does not turn into a
    /// multi-minute stall.
    pub max_backoff: Duration,
}

impl Default for RetryPolicy {
    fn default() -> Self {
        Self {
            max_attempts: 4,
            initial_backoff: Duration::from_millis(500),
            max_backoff: Duration::from_secs(30),
        }
    }
}

impl RetryPolicy {
    /// A policy that never retries, for tests and one-shot calls.
    pub fn none() -> Self {
        Self {
            max_attempts: 1,
            ..Self::default()
        }
    }

    /// How long to wait before `attempt`, or `None` to stop.
    ///
    /// `attempt` is one-based: the delay before the second attempt is
    /// `delay_before(2)`.
    pub fn delay_before(&self, attempt: u32, error: &LlmError) -> Option<Duration> {
        if attempt > self.max_attempts || !error.is_retryable() {
            return None;
        }

        // A provider that told us how long to wait knows better than we do.
        if let Some(requested) = error.retry_after() {
            return Some(requested.min(self.max_backoff));
        }

        // Exponential backoff on the number of retries already made.
        let exponent = attempt.saturating_sub(2);
        let scale = 2u32.saturating_pow(exponent.min(16));
        let delay = self
            .initial_backoff
            .saturating_mul(scale)
            .min(self.max_backoff);

        // Rate limits without a Retry-After deserve a longer initial pause.
        if error.kind() == FailureKind::RateLimited {
            return Some((delay * 2).min(self.max_backoff));
        }
        Some(delay)
    }

    /// Whether another attempt should be made after `error` on `attempt`.
    pub fn should_retry(&self, attempt: u32, error: &LlmError) -> bool {
        self.delay_before(attempt + 1, error).is_some()
    }
}

/// The shared HTTP retry loop, parameterised by the request builder.
///
/// Both provider clients (Anthropic and Copilot) use the same pattern: build a
/// request, send it, classify the response, and retry on transient errors.
/// The only difference is how the request is built (which headers, URL, body),
/// so the policy + error handling lives here and the builder is a closure.
///
/// `make_builder` is called fresh on every attempt because `reqwest::RequestBuilder`
/// is not `Clone`; the URL, body, and auth headers must all be captured by the
/// closure.
///
/// The `provider` label is used only in tracing — pass a short identifier such
/// as `"anthropic"` or `"copilot"`.
///
/// `dispatch` identifies which *physical dispatch* this call is (a fresh
/// endpoint/body choice — Copilot's chat-mismatch reroute calls this a
/// second time with `dispatch: 2`); `protocol`/`route_source`/`shape`
/// describe the request this dispatch is sending and are recorded, per
/// physical attempt, as [`coda_diagnostics::Event::RequestShape`] — computed
/// once by the caller from the same final JSON passed to `.json(body)`, not
/// re-derived here.
///
/// Records HTTP attempt/result/retry/recovery diagnostics, request-shape
/// detail, and (on a non-2xx response) a bounded structured extraction of
/// the error body, under the ambient [`coda_diagnostics`] context, when one
/// is present. The provider request-id header is read here, before the
/// response body is consumed — this is the only place it is ever available.
#[allow(clippy::too_many_arguments)]
pub(crate) async fn send_with_retry<F>(
    policy: &RetryPolicy,
    provider: &str,
    dispatch: u32,
    protocol: &'static str,
    route_source: coda_diagnostics::detail::RouteSource,
    shape: coda_diagnostics::detail::RequestShape,
    mut make_builder: F,
) -> Result<reqwest::Response, LlmError>
where
    F: FnMut() -> reqwest::RequestBuilder,
{
    let ctx = coda_diagnostics::current();
    let mut attempt = 1u32;
    let mut retried_at_least_once = false;
    loop {
        if let Some(ctx) = &ctx {
            ctx.record(coda_diagnostics::Event::HttpAttempt { attempt });
        }
        let started = std::time::Instant::now();

        // Build the request (fresh per attempt: URL/body/auth headers are
        // recaptured by the closure), splitting the client out so the exact
        // already-serialized wire bytes can be measured before it is ever
        // sent — this is the only point they are available without
        // re-encoding the JSON a second time.
        let (client, built) = make_builder().build_split();
        let error = match built {
            Err(e) => {
                // A build failure never reaches the network: no request
                // existed to measure, so no `RequestShape` is recorded (a
                // future reader must not read its absence as "0 bytes").
                // Classification matches the pre-existing behaviour for a
                // `.send()` build failure: `LlmError::Transport`.
                if let Some(ctx) = &ctx {
                    ctx.record(coda_diagnostics::Event::HttpResult {
                        attempt,
                        status: None,
                        duration_ms: started.elapsed().as_millis() as u64,
                        provider_request_id: None,
                    });
                }
                LlmError::Transport(e.to_string())
            }
            Ok(request) => {
                let body_bytes = request
                    .body()
                    .and_then(reqwest::Body::as_bytes)
                    .map(|bytes| bytes.len() as u64);
                if let Some(ctx) = &ctx {
                    ctx.record(coda_diagnostics::Event::RequestShape {
                        dispatch,
                        attempt,
                        protocol,
                        route_source: route_source.as_str(),
                        shape,
                        body_bytes,
                    });
                }

                // `Client::execute` is exactly what `RequestBuilder::send`
                // calls internally once a request is built — same timeouts,
                // same redirect handling — so behaviour is unchanged.
                match client.execute(request).await {
                    Ok(response) if response.status().is_success() => {
                        let provider_request_id = extract_request_id(&response);
                        if let Some(ctx) = &ctx {
                            ctx.record(coda_diagnostics::Event::HttpResult {
                                attempt,
                                status: Some(response.status().as_u16()),
                                duration_ms: started.elapsed().as_millis() as u64,
                                provider_request_id,
                            });
                            if retried_at_least_once {
                                ctx.record(coda_diagnostics::Event::HttpRecovery { attempt });
                            }
                        }
                        return Ok(response);
                    }
                    Ok(response) => {
                        let status = response.status().as_u16();
                        // Read headers BEFORE consuming the body: `.text()` moves
                        // the response, and this is the only chance to see them.
                        let provider_request_id = extract_request_id(&response);
                        let retry_after = response
                            .headers()
                            .get("retry-after")
                            .and_then(|v| v.to_str().ok())
                            .and_then(crate::error::parse_retry_after);
                        // Captured as a `Result`, not `.unwrap_or_default()`,
                        // so a genuine body-read failure is distinguishable
                        // from an ordinary empty body (`Unreadable`, never
                        // mislabelled `Empty`) in the diagnostic extraction.
                        let text_result = response.text().await;
                        if let Some(ctx) = &ctx {
                            ctx.record(coda_diagnostics::Event::HttpResult {
                                attempt,
                                status: Some(status),
                                duration_ms: started.elapsed().as_millis() as u64,
                                provider_request_id,
                            });
                            ctx.record(coda_diagnostics::Event::HttpFailureDetails {
                                dispatch,
                                attempt,
                                status,
                                detail: crate::diagnostics::error_body_detail_from_text(&text_result),
                            });
                        }
                        let body = text_result.unwrap_or_default();
                        LlmError::from_status(status, &body, retry_after)
                    }
                    Err(e) if e.is_timeout() => {
                        if let Some(ctx) = &ctx {
                            ctx.record(coda_diagnostics::Event::HttpResult {
                                attempt,
                                status: None,
                                duration_ms: started.elapsed().as_millis() as u64,
                                provider_request_id: None,
                            });
                        }
                        LlmError::Transport(format!("request timed out: {e}"))
                    }
                    Err(e) => {
                        if let Some(ctx) = &ctx {
                            ctx.record(coda_diagnostics::Event::HttpResult {
                                attempt,
                                status: None,
                                duration_ms: started.elapsed().as_millis() as u64,
                                provider_request_id: None,
                            });
                        }
                        LlmError::Transport(e.to_string())
                    }
                }
            }
        };

        let category = crate::diagnostics::category(&error);
        match policy.delay_before(attempt + 1, &error) {
            Some(delay) => {
                if let Some(ctx) = &ctx {
                    ctx.record(coda_diagnostics::Event::HttpRetry {
                        attempt,
                        delay_ms: delay.as_millis() as u64,
                        category,
                    });
                }
                // Scrubbed: no `%error` here — its `Display` can carry
                // provider-authored text or a credential-bearing URL. The
                // fixed category is enough to see a retry is happening.
                tracing::warn!(attempt, ?delay, category, "{provider} request failed; retrying");
                tokio::time::sleep(delay).await;
                attempt += 1;
                retried_at_least_once = true;
            }
            None => return Err(error),
        }
    }
}

/// Reads a provider request-id header, validated and bounded. Absent or
/// unusual values are treated as absent — never invented, never logged
/// as-is beyond the validated form.
fn extract_request_id(response: &reqwest::Response) -> Option<String> {
    response
        .headers()
        .get("x-request-id")
        .or_else(|| response.headers().get("request-id"))
        .and_then(|v| v.to_str().ok())
        .and_then(crate::diagnostics::validated_request_id)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn transient() -> LlmError {
        LlmError::Api {
            status: 503,
            message: "overloaded".into(),
            kind: FailureKind::Transient,
            retry_after: None,
            body: None,
        }
    }

    fn permanent() -> LlmError {
        LlmError::Api {
            status: 400,
            message: "bad request".into(),
            kind: FailureKind::Permanent,
            retry_after: None,
            body: None,
        }
    }

    fn rate_limited(retry_after: Option<Duration>) -> LlmError {
        LlmError::Api {
            status: 429,
            message: "slow down".into(),
            kind: FailureKind::RateLimited,
            retry_after,
            body: None,
        }
    }

    #[test]
    fn a_permanent_failure_is_never_retried() {
        let policy = RetryPolicy::default();
        assert!(policy.delay_before(2, &permanent()).is_none());
        assert!(!policy.should_retry(1, &permanent()));
    }

    #[test]
    fn a_transient_failure_is_retried() {
        let policy = RetryPolicy::default();
        assert!(policy.delay_before(2, &transient()).is_some());
        assert!(policy.should_retry(1, &transient()));
    }

    #[test]
    fn retries_stop_at_the_attempt_limit() {
        let policy = RetryPolicy {
            max_attempts: 3,
            ..RetryPolicy::default()
        };
        assert!(policy.delay_before(2, &transient()).is_some());
        assert!(policy.delay_before(3, &transient()).is_some());
        assert!(
            policy.delay_before(4, &transient()).is_none(),
            "a fourth attempt exceeds the limit of three"
        );
    }

    #[test]
    fn a_no_retry_policy_makes_a_single_attempt() {
        let policy = RetryPolicy::none();
        assert!(policy.delay_before(2, &transient()).is_none());
        assert!(!policy.should_retry(1, &transient()));
    }

    #[test]
    fn backoff_grows_exponentially() {
        let policy = RetryPolicy {
            initial_backoff: Duration::from_millis(100),
            max_attempts: 10,
            max_backoff: Duration::from_secs(60),
        };

        assert_eq!(
            policy.delay_before(2, &transient()),
            Some(Duration::from_millis(100))
        );
        assert_eq!(
            policy.delay_before(3, &transient()),
            Some(Duration::from_millis(200))
        );
        assert_eq!(
            policy.delay_before(4, &transient()),
            Some(Duration::from_millis(400))
        );
    }

    #[test]
    fn backoff_is_capped() {
        let policy = RetryPolicy {
            initial_backoff: Duration::from_secs(1),
            max_attempts: 30,
            max_backoff: Duration::from_secs(5),
        };

        for attempt in 2..25 {
            let delay = policy.delay_before(attempt, &transient()).expect("a delay");
            assert!(
                delay <= Duration::from_secs(5),
                "attempt {attempt} waited {delay:?}, past the cap"
            );
        }
    }

    #[test]
    fn a_rate_limit_waits_longer_than_a_plain_transient_failure() {
        let policy = RetryPolicy::default();
        let transient_delay = policy.delay_before(2, &transient()).expect("a delay");
        let limited_delay = policy
            .delay_before(2, &rate_limited(None))
            .expect("a delay");

        assert!(
            limited_delay > transient_delay,
            "backing off harder on a rate limit avoids compounding it"
        );
    }

    #[test]
    fn an_explicit_retry_after_is_honoured() {
        let policy = RetryPolicy::default();
        let delay = policy
            .delay_before(2, &rate_limited(Some(Duration::from_secs(12))))
            .expect("a delay");

        assert_eq!(
            delay,
            Duration::from_secs(12),
            "the provider's own guidance should win"
        );
    }

    #[test]
    fn an_absurd_retry_after_is_still_capped() {
        let policy = RetryPolicy {
            max_backoff: Duration::from_secs(10),
            ..RetryPolicy::default()
        };
        let delay = policy
            .delay_before(2, &rate_limited(Some(Duration::from_secs(600))))
            .expect("a delay");

        assert_eq!(delay, Duration::from_secs(10));
    }

    #[test]
    fn a_cancelled_request_is_never_retried() {
        let policy = RetryPolicy::default();
        assert!(policy.delay_before(2, &LlmError::Cancelled).is_none());
    }

    #[test]
    fn a_dropped_stream_is_retried() {
        let policy = RetryPolicy::default();
        assert!(policy.delay_before(2, &LlmError::IncompleteStream).is_some());
    }

    #[test]
    fn an_authentication_failure_is_never_retried() {
        let policy = RetryPolicy::default();
        assert!(policy
            .delay_before(2, &LlmError::Unauthorized("no token".into()))
            .is_none());
    }

    // ── HTTP-attempt diagnostics ────────────────────────────────────────────

    use std::sync::Arc;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpListener;
    use coda_diagnostics::{Limits, Logger, Options};

    fn test_ctx(dir: &std::path::Path) -> coda_diagnostics::DiagnosticContext {
        let logger = Logger::open(
            Options {
                directory: dir.to_path_buf(),
                file: None,
                role: coda_diagnostics::ProcessRole::Run,
                version: "test".into(),
                verbosity: coda_diagnostics::Verbosity::Normal,
            },
            Limits::default(),
        )
        .expect("logger opens");
        coda_diagnostics::DiagnosticContext::root(Arc::new(logger), "run-1")
    }

    fn read_recorded_lines(ctx: &coda_diagnostics::DiagnosticContext) -> Vec<serde_json::Value> {
        let path = ctx.logger().status().path.expect("a log path");
        std::fs::read_to_string(path)
            .unwrap()
            .lines()
            .filter(|l| !l.is_empty())
            .map(|l| serde_json::from_str(l).unwrap())
            .collect()
    }

    /// A one-shot server that replies once with a fixed status/headers/body.
    async fn respond_once(status: u16, headers: &str, body: &str) -> String {
        let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let port = listener.local_addr().expect("addr").port();
        let headers = headers.to_string();
        let body = body.to_string();
        tokio::spawn(async move {
            let Ok((mut socket, _)) = listener.accept().await else { return };
            let mut buffer = vec![0u8; 8192];
            let _ = socket.read(&mut buffer).await;
            let reason = if status == 200 { "OK" } else { "Error" };
            let response = format!(
                "HTTP/1.1 {status} {reason}\r\n{headers}content-length: {}\r\nconnection: close\r\n\r\n{body}",
                body.len()
            );
            let _ = socket.write_all(response.as_bytes()).await;
            let _ = socket.shutdown().await;
        });
        format!("http://127.0.0.1:{port}")
    }

    /// A tiny, fixed test request-shape/protocol/route-source triple for
    /// tests that only care about the retry/HTTP-diagnostics behaviour, not
    /// about the shape fields themselves.
    fn test_shape_args() -> (
        &'static str,
        coda_diagnostics::detail::RouteSource,
        coda_diagnostics::detail::RequestShape,
    ) {
        (
            coda_diagnostics::detail::Protocol::AnthropicMessages.as_str(),
            coda_diagnostics::detail::RouteSource::FixedProviderDefault,
            coda_diagnostics::detail::RequestShape::default(),
        )
    }

    #[tokio::test]
    async fn a_successful_attempt_records_attempt_and_result_with_the_request_id() {
        let dir = tempfile::tempdir().unwrap();
        let ctx = test_ctx(dir.path());
        let url = respond_once(200, "x-request-id: req-abc-123\r\n", "{}").await;
        let client = reqwest::Client::new();

        coda_diagnostics::scope(ctx.clone(), async {
            let (protocol, route_source, shape) = test_shape_args();
            send_with_retry(&RetryPolicy::none(), "test", 1, protocol, route_source, shape, || client.get(&url))
                .await
                .expect("success");
        })
        .await;

        let lines = read_recorded_lines(&ctx);
        assert_eq!(lines[0]["kind"], "http_attempt");
        assert_eq!(lines[0]["attempt"], 1);
        assert_eq!(lines[1]["kind"], "request_shape", "request_shape precedes the physical send: {lines:?}");
        assert_eq!(lines[2]["kind"], "http_result");
        assert_eq!(lines[2]["status"], 200);
        assert_eq!(lines[2]["provider_request_id"], "req-abc-123");
        assert!(!lines.iter().any(|l| l["kind"] == "http_retry"));
    }

    #[tokio::test]
    async fn a_retry_then_success_records_retry_and_recovery() {
        let dir = tempfile::tempdir().unwrap();
        let ctx = test_ctx(dir.path());

        // Two servers: the client only ever connects once per attempt, so
        // simulate "fails then succeeds" with two independent one-shot ports
        // and a make_builder that alternates between them.
        let failing_url = respond_once(503, "", "overloaded").await;
        let ok_url = respond_once(200, "", "{}").await;
        let client = reqwest::Client::new();
        let call = std::sync::atomic::AtomicU32::new(0);

        let policy = RetryPolicy {
            max_attempts: 3,
            initial_backoff: Duration::from_millis(1),
            max_backoff: Duration::from_millis(5),
        };

        coda_diagnostics::scope(ctx.clone(), async {
            let (protocol, route_source, shape) = test_shape_args();
            send_with_retry(&policy, "test", 1, protocol, route_source, shape, || {
                let n = call.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                let url = if n == 0 { &failing_url } else { &ok_url };
                client.get(url)
            })
            .await
            .expect("eventually succeeds");
        })
        .await;

        let lines = read_recorded_lines(&ctx);
        assert!(lines.iter().any(|l| l["kind"] == "http_retry"), "{lines:?}");
        assert!(lines.iter().any(|l| l["kind"] == "http_recovery"), "{lines:?}");
        // The 503 body text must never appear in any recorded field.
        for line in &lines {
            let serialized = line.to_string();
            assert!(!serialized.contains("overloaded"));
        }
    }

    // ── BUG4 regression: structured error/request-shape diagnostics ────────
    //
    // These reproduce the actual gap: today a loopback provider 400 with a
    // structured `error.type`/`error.code`/`error.param` body, and the exact
    // outgoing request shape/bytes, are simply never persisted. This test is
    // written BEFORE the fix lands and is expected to fail (RED) against the
    // pre-fix `send_with_retry`.
    #[tokio::test]
    async fn a_structured_400_persists_recognized_type_code_param_and_request_shape() {
        let dir = tempfile::tempdir().unwrap();
        let ctx = test_ctx(dir.path());
        let body = r#"{"error":{"type":"invalid_request_error","code":"unsupported_parameter","param":"max_tokens","message":"top secret provider prose that must never be persisted"}}"#;
        let url = respond_once(400, "", body).await;
        let client = reqwest::Client::new();

        coda_diagnostics::scope(ctx.clone(), async {
            let shape = coda_diagnostics::detail::RequestShape::from_wire_json(
                &serde_json::json!({"model": "m", "messages": [{"role":"user","content":"hi"}], "max_tokens": 100, "stream": true}),
            );
            let _ = send_with_retry(
                &RetryPolicy::none(),
                "test",
                1,
                coda_diagnostics::detail::Protocol::AnthropicMessages.as_str(),
                coda_diagnostics::detail::RouteSource::FixedProviderDefault,
                shape,
                || client.post(&url).json(&serde_json::json!({"model": "m", "messages": [{"role":"user","content":"hi"}], "max_tokens": 100, "stream": true})),
            )
            .await;
        })
        .await;

        let lines = read_recorded_lines(&ctx);
        let shape = lines.iter().find(|l| l["kind"] == "request_shape");
        assert!(shape.is_some(), "expected a request_shape event, got: {lines:?}");
        let shape = shape.unwrap();
        assert_eq!(shape["dispatch"], 1);
        assert_eq!(shape["protocol"], "anthropic_messages");
        assert_eq!(shape["route_source"], "fixed_provider_default");
        assert_eq!(shape["message_count"], 1);
        assert_eq!(shape["max_tokens_present"], true);
        assert_eq!(shape["stream_requested"], true);
        // The exact serialized wire body length, not a second `to_string()`.
        let expected_bytes =
            serde_json::to_vec(&serde_json::json!({"model": "m", "messages": [{"role":"user","content":"hi"}], "max_tokens": 100, "stream": true}))
                .unwrap()
                .len() as u64;
        assert_eq!(shape["body_bytes"].as_u64().unwrap(), expected_bytes);

        let failure_details = lines.iter().find(|l| l["kind"] == "http_failure_details");
        assert!(failure_details.is_some(), "expected an http_failure_details event, got: {lines:?}");
        let details = &failure_details.unwrap()["detail"];
        assert_eq!(details["error_type"]["value"], "invalid_request_error");
        assert_eq!(details["error_code"]["value"], "unsupported_parameter");
        assert_eq!(details["parameter"]["value"], "max_tokens");

        for line in &lines {
            let serialized = line.to_string();
            assert!(!serialized.contains("top secret"), "provider message text must never be persisted: {serialized}");
        }
    }

    #[tokio::test]
    async fn an_unusual_request_id_header_is_recorded_as_absent_not_verbatim() {
        let dir = tempfile::tempdir().unwrap();
        let ctx = test_ctx(dir.path());
        let url = respond_once(200, "x-request-id: has space\r\n", "{}").await;
        let client = reqwest::Client::new();

        coda_diagnostics::scope(ctx.clone(), async {
            let (protocol, route_source, shape) = test_shape_args();
            send_with_retry(&RetryPolicy::none(), "test", 1, protocol, route_source, shape, || client.get(&url))
                .await
                .expect("success");
        })
        .await;

        let lines = read_recorded_lines(&ctx);
        let result = lines.iter().find(|l| l["kind"] == "http_result").expect("an http_result event");
        assert!(result["provider_request_id"].is_null());
    }
}
