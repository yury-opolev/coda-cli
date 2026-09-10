//! Typed, allowlisted detail carried by the HTTP/stream diagnostic events.
//!
//! Everything here is either a fixed classification (`Protocol`,
//! `RouteSource`, `ErrorBodyKind`) or a bounded, allowlisted extraction
//! (`FieldState`, `RequestShape`). Nothing in this module can carry an
//! arbitrary provider string to the log: extractors check fixed allowlists,
//! and the writer revalidates error detail even if a caller constructs it
//! directly. Request-shape extraction caps counts at [`MAX_COUNT`].

use serde::Serialize;

/// Which wire protocol a physical HTTP attempt spoke. Fixed, closed set —
/// never derived from a model id or provider string at the call site.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Protocol {
    AnthropicMessages,
    CopilotChatCompletions,
    CopilotResponses,
    CopilotMessages,
}

impl Protocol {
    pub fn as_str(self) -> &'static str {
        match self {
            Protocol::AnthropicMessages => "anthropic_messages",
            Protocol::CopilotChatCompletions => "copilot_chat_completions",
            Protocol::CopilotResponses => "copilot_responses",
            Protocol::CopilotMessages => "copilot_messages",
        }
    }
}

/// Why this particular endpoint/protocol was chosen for a dispatch. Fixed,
/// closed set — this is routing provenance, never a free-text explanation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum RouteSource {
    /// The provider has exactly one endpoint; nothing was chosen.
    FixedProviderDefault,
    /// A cached model row named a recognized endpoint.
    ModelMetadata,
    /// No cached metadata for this model at all (never fetched, or the model
    /// id is not in the cached catalog) — fell back to the fixed default.
    MetadataMissingDefault,
    /// Metadata exists for this model, but none of its advertised endpoints
    /// were recognized — fell back to the fixed default rather than
    /// claiming a metadata-driven route.
    MetadataUnrecognizedDefault,
    /// A prior dispatch mismatched its endpoint; this dispatch is the
    /// reroute onto a different one, from refreshed metadata.
    RerouteAfterMismatch,
}

impl RouteSource {
    pub fn as_str(self) -> &'static str {
        match self {
            RouteSource::FixedProviderDefault => "fixed_provider_default",
            RouteSource::ModelMetadata => "model_metadata",
            RouteSource::MetadataMissingDefault => "metadata_missing_default",
            RouteSource::MetadataUnrecognizedDefault => "metadata_unrecognized_default",
            RouteSource::RerouteAfterMismatch => "reroute_after_mismatch",
        }
    }
}

/// Numeric counts in [`RequestShape`] saturate here rather than ever
/// overflowing or acting as a proxy for exact user content size.
pub const MAX_COUNT: u32 = 10_000;

/// Saturating cast used by every count in [`RequestShape`].
pub fn bounded_count(n: usize) -> u32 {
    u32::try_from(n).unwrap_or(u32::MAX).min(MAX_COUNT)
}

/// The shape of one already-built outgoing request body — counts and
/// boolean presence flags only, computed from the actual final wire JSON.
/// Never carries a key or value from the user's own message/tool content.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Default)]
pub struct RequestShape {
    /// Number of serialized wire items: `messages` (Chat/Anthropic) or
    /// `input` (Responses) — documents wire items, not user turns.
    pub message_count: u32,
    pub tools_count: u32,
    /// `system`, `instructions`, or a Chat system-role item is present.
    pub system_present: bool,
    /// The serialized `stream` value, when present, was `true`.
    pub stream_requested: bool,
    /// `reasoning` (Responses) or `thinking` (Anthropic) is present.
    pub reasoning_present: bool,
    pub max_tokens_present: bool,
    pub max_output_tokens_present: bool,
    pub max_completion_tokens_present: bool,
    pub temperature_present: bool,
    pub tool_choice_present: bool,
    pub parallel_tool_calls_present: bool,
    pub response_format_present: bool,
}

impl RequestShape {
    /// Computes the shape from the already-built final wire JSON body — the
    /// same `serde_json::Value` passed to `.json(body)` — never re-derived
    /// from the caller's own `ChatRequest`, so it always reflects exactly
    /// what is (or was) actually sent on the wire.
    pub fn from_wire_json(body: &serde_json::Value) -> Self {
        let message_count = body
            .get("messages")
            .and_then(serde_json::Value::as_array)
            .map(Vec::len)
            .or_else(|| body.get("input").and_then(serde_json::Value::as_array).map(Vec::len))
            .unwrap_or(0);
        let tools_count = body.get("tools").and_then(serde_json::Value::as_array).map(Vec::len).unwrap_or(0);

        Self {
            message_count: bounded_count(message_count),
            tools_count: bounded_count(tools_count),
            system_present: body.get("system").is_some()
                || body.get("instructions").is_some()
                || body.get("messages").and_then(serde_json::Value::as_array).is_some_and(|items| {
                    items.iter().any(|item| {
                        item.get("role").and_then(serde_json::Value::as_str) == Some("system")
                    })
                }),
            stream_requested: body.get("stream").and_then(serde_json::Value::as_bool).unwrap_or(false),
            reasoning_present: body.get("reasoning").is_some() || body.get("thinking").is_some(),
            max_tokens_present: body.get("max_tokens").is_some(),
            max_output_tokens_present: body.get("max_output_tokens").is_some(),
            max_completion_tokens_present: body.get("max_completion_tokens").is_some(),
            temperature_present: body.get("temperature").is_some(),
            tool_choice_present: body.get("tool_choice").is_some(),
            parallel_tool_calls_present: body.get("parallel_tool_calls").is_some(),
            response_format_present: body.get("response_format").is_some(),
        }
    }
}


/// The three distinct HTTP-body outcomes a caller must never confuse:
/// `NonJson`/`Empty` mean nothing structured could ever be recognized,
/// `Oversized` means recognition was deliberately skipped, `Unreadable`
/// means the transport itself failed while reading the body (never
/// mislabelled as an ordinary empty body).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ErrorBodyKind {
    Json,
    NonJson,
    Empty,
    Unreadable,
    Oversized,
}

/// Maximum length of a single validated field value (a `type`/`code`/`param`
/// string). Longer values are [`FieldState::Omitted`] rather than truncated
/// — a truncated arbitrary string could still leak a meaningful prefix.
pub const MAX_FIELD_LEN: usize = 64;

/// Maximum size of an HTTP error body considered for *diagnostic* structured
/// extraction. The raw body used for existing retry/`LlmError` behaviour is
/// unbounded and unaffected; this cap only governs what this crate persists.
pub const MAX_ERROR_BODY_DIAGNOSTIC_BYTES: usize = 64 * 1024;

/// The outcome of looking for one allowlisted structured error field.
/// `T` is `&'static str` for `type`/`code` (a fixed vocabulary) or `String`
/// for `param` (an open but bounded/pattern-validated path).
///
/// Use [`recognized_field`] and [`recognized_parameter`] for extraction.
/// This public type can also be constructed directly, so the diagnostic
/// writer revalidates recognized fields before persisting them.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(tag = "state", rename_all = "snake_case")]
pub enum FieldState<T> {
    /// Present, and its value matched the fixed allowlist/pattern.
    Recognized { value: T },
    /// Present (valid JSON, right shape) but not in the allowlist/pattern —
    /// the actual value is never recorded.
    Unrecognized,
    /// The body was valid JSON and this field was simply not present.
    Missing,
    /// Present but excluded because it exceeded a bound ([`MAX_FIELD_LEN`]
    /// for a single field, [`MAX_ERROR_BODY_DIAGNOSTIC_BYTES`] for the body).
    Omitted,
    /// The body was not valid JSON (or unreadable/empty), so whether this
    /// field exists cannot even be determined.
    Unavailable,
}

/// The bounded, allowlisted structured detail extracted from one HTTP error
/// body — never the raw body, never the free-text `message`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct ErrorBodyDetail {
    pub body_kind: ErrorBodyKind,
    pub error_type: FieldState<&'static str>,
    pub error_code: FieldState<&'static str>,
    pub parameter: FieldState<String>,
}

impl ErrorBodyDetail {
    pub(crate) fn normalized(self) -> Self {
        match self.body_kind {
            ErrorBodyKind::Oversized => Self::omitted(self.body_kind),
            ErrorBodyKind::NonJson | ErrorBodyKind::Empty | ErrorBodyKind::Unreadable => {
                Self::unavailable(self.body_kind)
            }
            ErrorBodyKind::Json => Self {
                error_type: normalize_known_field(self.error_type, ERROR_TYPE_ALLOWLIST),
                error_code: normalize_known_field(self.error_code, ERROR_CODE_ALLOWLIST),
                parameter: match self.parameter {
                    FieldState::Recognized { value } => recognized_parameter_text(&value),
                    other => other,
                },
                ..self
            },
        }
    }

    /// A body kind for which no field can even be attempted (non-JSON,
    /// empty, or unreadable): every field is [`FieldState::Unavailable`].
    pub fn unavailable(body_kind: ErrorBodyKind) -> Self {
        Self {
            body_kind,
            error_type: FieldState::Unavailable,
            error_code: FieldState::Unavailable,
            parameter: FieldState::Unavailable,
        }
    }

    /// A body deliberately not parsed because it exceeded the size bound:
    /// every field is [`FieldState::Omitted`].
    pub fn omitted(body_kind: ErrorBodyKind) -> Self {
        Self {
            body_kind,
            error_type: FieldState::Omitted,
            error_code: FieldState::Omitted,
            parameter: FieldState::Omitted,
        }
    }
}

/// The fixed allowlist of provider error `type` values safe to record
/// verbatim. Anything else observed in a structured response is
/// [`FieldState::Unrecognized`] — never copied.
pub const ERROR_TYPE_ALLOWLIST: &[&str] = &[
    "invalid_request_error",
    "authentication_error",
    "permission_error",
    "not_found_error",
    "rate_limit_error",
    "overloaded_error",
    "api_error",
    "server_error",
    "model_error",
    "billing_error",
    "insufficient_quota",
];

/// The fixed allowlist of provider error `code` values safe to record
/// verbatim.
pub const ERROR_CODE_ALLOWLIST: &[&str] = &[
    "model_not_found",
    "context_length_exceeded",
    "unsupported_parameter",
    "unsupported_value",
    "invalid_value",
    "missing_required_parameter",
    "invalid_api_key",
    "rate_limit_exceeded",
    "insufficient_quota",
    "content_filter",
    "tool_use_failed",
];

/// Classifies one structured JSON field against a fixed allowlist.
///
/// `raw` distinguishes "the key was absent" (`None` → [`FieldState::Missing`])
/// from "the key was present but not a recognizable string" (`Some` of a
/// non-string/null value → [`FieldState::Unrecognized`]) — collapsing those
/// would hide a provider sending a structurally different shape.
pub fn recognized_field(
    raw: Option<&serde_json::Value>,
    allowlist: &'static [&'static str],
) -> FieldState<&'static str> {
    let Some(value) = raw else { return FieldState::Missing };
    let Some(text) = value.as_str() else { return FieldState::Unrecognized };
    recognized_text(text, allowlist)
}

fn recognized_text(
    text: &str,
    allowlist: &'static [&'static str],
) -> FieldState<&'static str> {
    if text.len() > MAX_FIELD_LEN {
        return FieldState::Omitted;
    }
    match allowlist.iter().find(|candidate| **candidate == text) {
        Some(&matched) => FieldState::Recognized { value: matched },
        None => FieldState::Unrecognized,
    }
}

fn normalize_known_field(
    field: FieldState<&'static str>,
    allowlist: &'static [&'static str],
) -> FieldState<&'static str> {
    match field {
        FieldState::Recognized { value } => recognized_text(value, allowlist),
        other => other,
    }
}

pub fn recognized_parameter(raw: Option<&serde_json::Value>) -> FieldState<String> {
    match raw {
        None => FieldState::Missing,
        Some(value) => match value.as_str() {
            Some(text) => recognized_parameter_text(text),
            None => FieldState::Unrecognized,
        },
    }
}

pub(crate) fn recognized_parameter_text(text: &str) -> FieldState<String> {
    const TOP_LEVEL: &[&str] = &[
        "model", "max_tokens", "temperature", "input", "tools", "tool_choice",
        "stream", "reasoning", "system",
    ];
    if text.len() > MAX_FIELD_LEN {
        return FieldState::Omitted;
    }
    let indexed = text.strip_prefix("input[").and_then(|tail| tail.split_once("]."))
        .is_some_and(|(index, field)| {
            !index.is_empty() && index.len() <= 6
                && index.bytes().all(|byte| byte.is_ascii_digit())
                && matches!(field, "summary" | "id" | "encrypted_content")
        });
    if indexed || TOP_LEVEL.contains(&text) {
        FieldState::Recognized { value: text.to_owned() }
    } else {
        FieldState::Unrecognized
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn protocol_and_route_source_strings_are_fixed() {
        assert_eq!(Protocol::AnthropicMessages.as_str(), "anthropic_messages");
        assert_eq!(Protocol::CopilotChatCompletions.as_str(), "copilot_chat_completions");
        assert_eq!(Protocol::CopilotResponses.as_str(), "copilot_responses");
        assert_eq!(Protocol::CopilotMessages.as_str(), "copilot_messages");
        assert_eq!(RouteSource::FixedProviderDefault.as_str(), "fixed_provider_default");
        assert_eq!(RouteSource::ModelMetadata.as_str(), "model_metadata");
        assert_eq!(RouteSource::MetadataMissingDefault.as_str(), "metadata_missing_default");
        assert_eq!(RouteSource::MetadataUnrecognizedDefault.as_str(), "metadata_unrecognized_default");
        assert_eq!(RouteSource::RerouteAfterMismatch.as_str(), "reroute_after_mismatch");
    }

    #[test]
    fn bounded_count_saturates_rather_than_overflowing() {
        assert_eq!(bounded_count(5), 5);
        assert_eq!(bounded_count(usize::MAX), MAX_COUNT);
        assert_eq!(bounded_count(MAX_COUNT as usize + 1), MAX_COUNT);
    }

    #[test]
    fn recognized_field_distinguishes_missing_unrecognized_and_recognized() {
        let body = json!({ "type": "invalid_request_error", "weird": "server_error", "null_field": null, "num": 1 });
        assert_eq!(
            recognized_field(body.get("type"), ERROR_TYPE_ALLOWLIST),
            FieldState::Recognized { value: "invalid_request_error" }
        );
        assert_eq!(recognized_field(body.get("missing_key"), ERROR_TYPE_ALLOWLIST), FieldState::Missing);
        assert_eq!(recognized_field(body.get("null_field"), ERROR_TYPE_ALLOWLIST), FieldState::Unrecognized);
        assert_eq!(recognized_field(body.get("num"), ERROR_TYPE_ALLOWLIST), FieldState::Unrecognized);

        let unknown = json!({ "type": "some_new_unlisted_type" });
        assert_eq!(recognized_field(unknown.get("type"), ERROR_TYPE_ALLOWLIST), FieldState::Unrecognized);
    }

    #[test]
    fn recognized_field_omits_an_oversized_value_rather_than_truncating() {
        let long = "x".repeat(200);
        let body = json!({ "type": long });
        assert_eq!(recognized_field(body.get("type"), ERROR_TYPE_ALLOWLIST), FieldState::Omitted);
    }

    #[test]
    fn error_body_detail_helpers_produce_the_expected_fixed_shapes() {
        let unavailable = ErrorBodyDetail::unavailable(ErrorBodyKind::NonJson);
        assert_eq!(unavailable.body_kind, ErrorBodyKind::NonJson);
        assert_eq!(unavailable.error_type, FieldState::<&'static str>::Unavailable);
        assert_eq!(unavailable.parameter, FieldState::<String>::Unavailable);

        let omitted = ErrorBodyDetail::omitted(ErrorBodyKind::Oversized);
        assert_eq!(omitted.error_code, FieldState::<&'static str>::Omitted);
    }

    #[test]
    fn field_state_serializes_with_a_closed_state_tag_and_no_value_when_absent() {
        let value = serde_json::to_value(FieldState::<&str>::Missing).unwrap();
        assert_eq!(value["state"], "missing");
        assert!(value.get("value").is_none());

        let recognized = serde_json::to_value(FieldState::Recognized { value: "invalid_request_error" }).unwrap();
        assert_eq!(recognized["state"], "recognized");
        assert_eq!(recognized["value"], "invalid_request_error");
    }
}
