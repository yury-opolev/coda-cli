//! Provider errors and how to react to them.
//!
//! The agent loop needs one question answered about any failure: retry, wait,
//! or give up. Classification lives here so every provider answers it the same
//! way, and so the loop never has to inspect an HTTP status itself.

use std::time::Duration;

/// What the caller should do about a failure.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FailureKind {
    /// Worth retrying after a short backoff.
    Transient,
    /// Rate limited; retry only after the indicated delay.
    RateLimited,
    /// Retrying cannot help.
    Permanent,
}

impl FailureKind {
    pub fn is_retryable(self) -> bool {
        matches!(self, FailureKind::Transient | FailureKind::RateLimited)
    }
}

#[derive(Debug, thiserror::Error)]
pub enum LlmError {
    #[error("{message}")]
    Api {
        status: u16,
        message: String,
        kind: FailureKind,
        /// Honoured from `Retry-After` when the provider sends it.
        retry_after: Option<Duration>,
        /// Raw response body, preserved so retry-condition detection can inspect
        /// it without being limited by the 500-char message truncation. Not
        /// included in `Display` so it never leaks into user-facing text.
        body: Option<String>,
    },

    #[error("the request was cancelled")]
    Cancelled,

    #[error("the stream ended before the model finished")]
    IncompleteStream,

    #[error("transport error: {0}")]
    Transport(String),

    #[error("could not parse the provider's response: {0}")]
    Protocol(String),

    #[error("not authenticated: {0}")]
    Unauthorized(String),
}

impl LlmError {
    /// How the caller should react.
    pub fn kind(&self) -> FailureKind {
        match self {
            LlmError::Api { kind, .. } => *kind,
            // A dropped connection mid-stream is usually worth one more try.
            LlmError::Transport(_) | LlmError::IncompleteStream => FailureKind::Transient,
            LlmError::Cancelled | LlmError::Unauthorized(_) | LlmError::Protocol(_) => {
                FailureKind::Permanent
            }
        }
    }

    pub fn is_retryable(&self) -> bool {
        self.kind().is_retryable()
    }

    /// The delay the provider asked for, if any.
    pub fn retry_after(&self) -> Option<Duration> {
        match self {
            LlmError::Api { retry_after, .. } => *retry_after,
            _ => None,
        }
    }

    /// Builds an error from an HTTP status and body.
    pub fn from_status(status: u16, body: &str, retry_after: Option<Duration>) -> LlmError {
        let message = extract_message(body).unwrap_or_else(|| {
            if body.trim().is_empty() {
                format!("HTTP {status}")
            } else {
                format!("HTTP {status}: {}", truncate(body, 500))
            }
        });

        if status == 401 || status == 403 {
            return LlmError::Unauthorized(message);
        }

        LlmError::Api {
            status,
            message,
            kind: classify(status),
            retry_after,
            body: if body.is_empty() { None } else { Some(body.to_string()) },
        }
    }
}

/// Maps an HTTP status onto a reaction.
pub fn classify(status: u16) -> FailureKind {
    match status {
        429 => FailureKind::RateLimited,
        // 408 request timeout and 409 conflict are both worth another attempt.
        408 | 409 => FailureKind::Transient,
        // Every 5xx except "not implemented" is worth retrying: the request
        // itself is fine, the server is not.
        501 => FailureKind::Permanent,
        500..=599 => FailureKind::Transient,
        _ => FailureKind::Permanent,
    }
}

/// Pulls a human-readable message out of a provider error body.
///
/// Anthropic and OpenAI both nest it under `error.message`; some gateways put
/// it at the top level.
fn extract_message(body: &str) -> Option<String> {
    let value: serde_json::Value = serde_json::from_str(body).ok()?;

    let candidate = value
        .get("error")
        .and_then(|error| error.get("message"))
        .or_else(|| value.get("message"))
        .or_else(|| value.get("error").filter(|e| e.is_string()))?;

    candidate.as_str().map(|text| truncate(text, 500))
}

fn truncate(text: &str, max: usize) -> String {
    let text = text.trim();
    if text.chars().count() <= max {
        return text.to_string();
    }
    let kept: String = text.chars().take(max).collect();
    format!("{kept}…")
}

/// Parses a `Retry-After` header, which may be seconds or an HTTP date.
pub fn parse_retry_after(value: &str) -> Option<Duration> {
    let value = value.trim();
    if let Ok(seconds) = value.parse::<u64>() {
        // Cap it: a provider asking us to sleep for hours should surface as an
        // error rather than a silently hung agent.
        return Some(Duration::from_secs(seconds.min(300)));
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rate_limiting_is_its_own_kind() {
        assert_eq!(classify(429), FailureKind::RateLimited);
        assert!(classify(429).is_retryable());
    }

    #[test]
    fn server_errors_are_transient() {
        for status in [500u16, 502, 503, 504, 529] {
            assert_eq!(classify(status), FailureKind::Transient, "{status}");
        }
    }

    #[test]
    fn not_implemented_is_permanent_despite_being_5xx() {
        assert_eq!(classify(501), FailureKind::Permanent);
    }

    #[test]
    fn client_errors_are_permanent() {
        for status in [400u16, 404, 422] {
            assert_eq!(classify(status), FailureKind::Permanent, "{status}");
        }
    }

    #[test]
    fn timeouts_and_conflicts_are_worth_retrying() {
        assert_eq!(classify(408), FailureKind::Transient);
        assert_eq!(classify(409), FailureKind::Transient);
    }

    #[test]
    fn authentication_failures_surface_as_unauthorized() {
        for status in [401u16, 403] {
            let error = LlmError::from_status(status, "{}", None);
            assert!(matches!(error, LlmError::Unauthorized(_)), "{status}");
            assert!(!error.is_retryable());
        }
    }

    #[test]
    fn extracts_a_nested_provider_message() {
        let body = r#"{"type":"error","error":{"type":"invalid_request_error","message":"model not found"}}"#;
        let error = LlmError::from_status(400, body, None);
        assert_eq!(error.to_string(), "model not found");
    }

    #[test]
    fn extracts_a_top_level_message() {
        let error = LlmError::from_status(400, r#"{"message":"bad input"}"#, None);
        assert_eq!(error.to_string(), "bad input");
    }

    #[test]
    fn falls_back_to_the_status_for_an_empty_body() {
        let error = LlmError::from_status(500, "", None);
        assert_eq!(error.to_string(), "HTTP 500");
    }

    #[test]
    fn falls_back_to_the_raw_body_when_it_is_not_json() {
        let error = LlmError::from_status(502, "<html>bad gateway</html>", None);
        assert!(error.to_string().contains("bad gateway"));
    }

    #[test]
    fn truncates_an_enormous_message() {
        let body = format!(r#"{{"message":"{}"}}"#, "x".repeat(5000));
        let error = LlmError::from_status(400, &body, None);
        assert!(error.to_string().chars().count() <= 501);
        assert!(error.to_string().ends_with('…'));
    }

    #[test]
    fn a_cancelled_request_is_never_retried() {
        assert_eq!(LlmError::Cancelled.kind(), FailureKind::Permanent);
        assert!(!LlmError::Cancelled.is_retryable());
    }

    #[test]
    fn a_dropped_stream_is_worth_one_more_try() {
        assert!(LlmError::IncompleteStream.is_retryable());
        assert!(LlmError::Transport("reset".into()).is_retryable());
    }

    #[test]
    fn a_protocol_error_is_permanent() {
        // Retrying cannot fix a response we do not understand.
        assert!(!LlmError::Protocol("unexpected shape".into()).is_retryable());
    }

    #[test]
    fn surfaces_the_retry_after_delay() {
        let error = LlmError::from_status(429, "{}", Some(Duration::from_secs(30)));
        assert_eq!(error.retry_after(), Some(Duration::from_secs(30)));
        assert_eq!(error.kind(), FailureKind::RateLimited);
    }

    #[test]
    fn parses_a_numeric_retry_after() {
        assert_eq!(parse_retry_after("30"), Some(Duration::from_secs(30)));
        assert_eq!(parse_retry_after("  5 "), Some(Duration::from_secs(5)));
    }

    #[test]
    fn caps_an_absurd_retry_after() {
        // Sleeping for an hour would look like a hang.
        assert_eq!(parse_retry_after("100000"), Some(Duration::from_secs(300)));
    }

    #[test]
    fn ignores_an_http_date_retry_after() {
        assert_eq!(parse_retry_after("Wed, 21 Oct 2026 07:28:00 GMT"), None);
    }
}
