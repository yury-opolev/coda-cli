//! Safe, privacy-preserving classification of [`LlmError`] for diagnostics.
//!
//! `LlmError`'s `Display`/`Debug` and its raw `body`/message strings can carry
//! provider-authored text, and `Transport`/`Protocol`/`Unauthorized` can embed
//! a credential-bearing URL. None of that is safe to persist. Everything
//! exported here is either a fixed, closed classification or a narrowly
//! validated, bounded, allowlisted extraction — never the raw text itself.

use regex::Regex;
use std::sync::OnceLock;

use crate::error::LlmError;

/// A fixed, closed classification safe to persist verbatim — never derived
/// from provider text.
pub fn category(err: &LlmError) -> &'static str {
    match err {
        LlmError::Api { status, .. } if *status == 429 => "rate_limited",
        LlmError::Api { status, .. } if (500..=599).contains(status) => "server_error",
        LlmError::Api { .. } => "client_error",
        LlmError::Cancelled => "cancelled",
        LlmError::IncompleteStream => "incomplete_stream",
        LlmError::Transport(_) => "transport",
        LlmError::Protocol(_) => "protocol",
        LlmError::Unauthorized(_) => "unauthorized",
    }
}

/// The HTTP status, when the error carries one.
pub fn status(err: &LlmError) -> Option<u16> {
    match err {
        LlmError::Api { status, .. } => Some(*status),
        _ => None,
    }
}

/// Maximum length of a validated request-parameter path or provider request
/// id. Anything longer is treated as absent rather than truncated — a
/// truncated arbitrary string could still leak a meaningful prefix.
const MAX_FIELD_LEN: usize = 64;

/// A small, fixed set of top-level parameter names safe to record verbatim
/// when the provider names exactly one of them — never anything else, and
/// never derived from free-text `message`.
const TOP_LEVEL_PARAM_ALLOWLIST: &[&str] = &[
    "model",
    "max_tokens",
    "temperature",
    "input",
    "tools",
    "tool_choice",
    "stream",
    "reasoning",
    "system",
];

fn indexed_param_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r"^input\[[0-9]{1,6}\]\.(summary|id|encrypted_content)$")
            .expect("fixed regex is valid")
    })
}

/// Extracts a bounded, allowlisted request-parameter path from a structured
/// `error.param` (or top-level `param`) field in the raw provider response
/// body — never from the free-text `message`. Returns `None` for anything
/// that is missing, oversized, or does not match the fixed allowlist.
pub fn parameter(err: &LlmError) -> Option<String> {
    let LlmError::Api { body: Some(body), .. } = err else {
        return None;
    };
    let value: serde_json::Value = serde_json::from_str(body).ok()?;
    let param = value
        .get("error")
        .and_then(|e| e.get("param"))
        .or_else(|| value.get("param"))?
        .as_str()?;

    if param.len() > MAX_FIELD_LEN {
        return None;
    }
    if indexed_param_regex().is_match(param) || TOP_LEVEL_PARAM_ALLOWLIST.contains(&param) {
        Some(param.to_owned())
    } else {
        None
    }
}

/// Validates an HTTP provider request-id header value: bounded length and
/// restricted to an unremarkable id charset (alphanumeric, `-`, `_`, `.`).
/// An oversized or unusual value is treated as absent rather than truncated
/// or logged as-is — a request id is not supposed to be a place to smuggle
/// arbitrary text.
pub fn validated_request_id(raw: &str) -> Option<String> {
    let trimmed = raw.trim();
    if trimmed.is_empty() || trimmed.len() > MAX_FIELD_LEN {
        return None;
    }
    if trimmed
        .bytes()
        .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.'))
    {
        Some(trimmed.to_owned())
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::FailureKind;

    fn api(status: u16, body: Option<&str>) -> LlmError {
        LlmError::Api {
            status,
            message: "irrelevant for these tests".into(),
            kind: FailureKind::Permanent,
            retry_after: None,
            body: body.map(str::to_owned),
        }
    }

    #[test]
    fn categories_are_fixed_and_never_include_provider_text() {
        assert_eq!(category(&api(429, None)), "rate_limited");
        assert_eq!(category(&api(503, None)), "server_error");
        assert_eq!(category(&api(400, None)), "client_error");
        assert_eq!(category(&LlmError::Cancelled), "cancelled");
        assert_eq!(category(&LlmError::IncompleteStream), "incomplete_stream");
        assert_eq!(category(&LlmError::Transport("reset by peer, key=sk-live-abc".into())), "transport");
        assert_eq!(category(&LlmError::Protocol("bad json".into())), "protocol");
        assert_eq!(category(&LlmError::Unauthorized("token xyz".into())), "unauthorized");
    }

    #[test]
    fn status_is_only_present_on_the_api_variant() {
        assert_eq!(status(&api(400, None)), Some(400));
        assert_eq!(status(&LlmError::Transport("x".into())), None);
    }

    #[test]
    fn extracts_the_reported_missing_reasoning_summary_parameter() {
        let body = r#"{"error":{"type":"invalid_request_error","message":"Missing required parameter: 'input[22].summary'.","param":"input[22].summary"}}"#;
        assert_eq!(parameter(&api(400, Some(body))), Some("input[22].summary".into()));
    }

    #[test]
    fn extracts_a_top_level_allowlisted_parameter() {
        let body = r#"{"error":{"param":"max_tokens","message":"whatever the provider wants to say here"}}"#;
        assert_eq!(parameter(&api(400, Some(body))), Some("max_tokens".into()));
    }

    #[test]
    fn never_extracts_an_arbitrary_parameter_name() {
        let body = r#"{"error":{"param":"credentials.apiKey","message":"..."}}"#;
        assert_eq!(parameter(&api(400, Some(body))), None);
    }

    #[test]
    fn never_falls_back_to_the_free_text_message() {
        // Even though the message mentions a recognizable path, only a
        // structured `param` field may ever be extracted.
        let body = r#"{"error":{"message":"Missing required parameter: 'input[22].summary'."}}"#;
        assert_eq!(parameter(&api(400, Some(body))), None);
    }

    #[test]
    fn rejects_an_oversized_param_value() {
        let long = "input[1].".to_string() + &"x".repeat(200);
        let body = format!(r#"{{"error":{{"param":"{long}"}}}}"#);
        assert_eq!(parameter(&api(400, Some(&body))), None);
    }

    #[test]
    fn no_body_means_no_parameter() {
        assert_eq!(parameter(&api(400, None)), None);
        assert_eq!(parameter(&LlmError::Transport("x".into())), None);
    }

    #[test]
    fn validates_an_ordinary_request_id() {
        assert_eq!(validated_request_id("req_01H8XYZ"), Some("req_01H8XYZ".into()));
    }

    #[test]
    fn rejects_an_empty_or_oversized_or_unusual_request_id() {
        assert_eq!(validated_request_id(""), None);
        assert_eq!(validated_request_id(&"a".repeat(200)), None);
        assert_eq!(validated_request_id("has space and \"quotes\""), None);
    }
}
