//! Safe, privacy-preserving classification of [`LlmError`] for diagnostics.
//!
//! `LlmError`'s `Display`/`Debug` and its raw `body`/message strings can carry
//! provider-authored text, and `Transport`/`Protocol`/`Unauthorized` can embed
//! a credential-bearing URL. None of that is safe to persist. Everything
//! exported here is either a fixed, closed classification or a narrowly
//! validated, bounded, allowlisted extraction — never the raw text itself.

use coda_diagnostics::detail::{
    self, ErrorBodyDetail, ErrorBodyKind, FieldState, ERROR_CODE_ALLOWLIST, ERROR_TYPE_ALLOWLIST,
};

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
///
/// Re-exported from `coda_diagnostics` so the bound used here and the bound
/// used by [`coda_diagnostics::detail::recognized_field`] for the newer
/// structured `type`/`code` extraction are guaranteed identical, not just
/// coincidentally equal.
const MAX_FIELD_LEN: usize = detail::MAX_FIELD_LEN;

/// Extracts a bounded, allowlisted request-parameter path from a structured
/// `error.param` (or top-level `param`) field in the raw provider response
/// body — never from the free-text `message`. Returns `None` for anything
/// that is missing, oversized, or does not match the fixed allowlist.
pub fn parameter(err: &LlmError) -> Option<String> {
    match error_detail(err)?.parameter {
        FieldState::Recognized { value } => Some(value),
        _ => None,
    }
}

/// Bounded, allowlisted structured extraction of one HTTP error body: at
/// most [`detail::MAX_ERROR_BODY_DIAGNOSTIC_BYTES`] are ever inspected, and
/// only the fixed `error.type`/`error.code`/`error.param` (or their
/// top-level counterparts) fields are ever extracted — never the free-text
/// `message`, and never the raw body itself.
///
/// This is a distinct, deliberately bounded pass from the one already made
/// for [`LlmError::from_status`]/retry-condition detection, which keeps the
/// full, unbounded body in memory (`LlmError::Api::body`) for that existing
/// behaviour. Nothing here changes what is kept there.
pub fn error_body_detail(body: &str) -> ErrorBodyDetail {
    if body.len() > detail::MAX_ERROR_BODY_DIAGNOSTIC_BYTES {
        return ErrorBodyDetail::omitted(ErrorBodyKind::Oversized);
    }
    if body.trim().is_empty() {
        return ErrorBodyDetail::unavailable(ErrorBodyKind::Empty);
    }
    let Ok(value) = serde_json::from_str::<serde_json::Value>(body) else {
        return ErrorBodyDetail::unavailable(ErrorBodyKind::NonJson);
    };

    let error_obj = value
        .get("error")
        // The Responses API's inline `response.failed`/`error` events nest
        // the structured error one level deeper, under `response.error` —
        // the same shape `read_error_message` in `copilot::responses`
        // already falls back to for the free-text message.
        .or_else(|| value.get("response").and_then(|r| r.get("error")));
    let type_raw = error_obj.and_then(|e| e.get("type")).or_else(|| value.get("type"));
    let code_raw = error_obj.and_then(|e| e.get("code")).or_else(|| value.get("code"));
    let param_raw = error_obj.and_then(|e| e.get("param")).or_else(|| value.get("param"));

    ErrorBodyDetail {
        body_kind: ErrorBodyKind::Json,
        error_type: detail::recognized_field(type_raw, ERROR_TYPE_ALLOWLIST),
        error_code: detail::recognized_field(code_raw, ERROR_CODE_ALLOWLIST),
        parameter: detail::recognized_parameter(param_raw),
    }
}

/// Same as [`error_body_detail`], but from the `Result` of an in-flight
/// `response.text()` read: a genuine read failure (`Err`) is `Unreadable`,
/// never silently treated as an ordinary empty body.
pub fn error_body_detail_from_text(text_result: &Result<String, reqwest::Error>) -> ErrorBodyDetail {
    match text_result {
        Err(_) => ErrorBodyDetail::unavailable(ErrorBodyKind::Unreadable),
        Ok(body) => error_body_detail(body),
    }
}

/// The same structured extraction as [`error_body_detail`], applied to
/// whatever body an already-classified [`LlmError`] carries — `None` only
/// when the error variant carries no body at all to extract from (a
/// transport/protocol/auth failure, or an `Api` error with no body).
pub fn error_detail(err: &LlmError) -> Option<ErrorBodyDetail> {
    let LlmError::Api { body: Some(body), .. } = err else {
        return None;
    };
    Some(error_body_detail(body))
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
    fn oversized_bodies_cannot_bypass_the_cap_through_the_legacy_parameter() {
        let body = format!(
            r#"{{"error":{{"param":"max_tokens","message":"{}"}}}}"#,
            "x".repeat(detail::MAX_ERROR_BODY_DIAGNOSTIC_BYTES)
        );
        let error = api(400, Some(&body));
        assert!(parameter(&error).is_none());
        assert_eq!(error_detail(&error).unwrap().parameter, FieldState::Omitted);
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
