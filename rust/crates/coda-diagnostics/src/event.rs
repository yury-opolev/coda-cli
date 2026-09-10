//! The fixed, allowlisted set of diagnostic events.
//!
//! Every variant is exhaustively enumerated and every field is a primitive
//! (string/number/bool). There is deliberately no `message: String` catch-all
//! field anywhere in this file: a free-text field is exactly the shape that
//! swallows a prompt, a stack trace, or a credential-bearing URL. Anything
//! that does not fit one of these variants is not diagnosed — it is dropped
//! at the call site instead of being force-fit into a string.

use serde::Serialize;

use crate::context::Verbosity;
use crate::detail;

/// A typed, privacy-safe diagnostic event.
///
/// `#[serde(tag = "kind")]` makes each record self-describing without needing
/// a free-text field: readers switch on `kind` and know exactly which other
/// fields are present.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Event {
    /// The process (any of `coda`, `coda-tui`, or an engine child) started.
    /// The envelope's own `role`/`version` fields already carry this
    /// process's identity, so this variant carries no fields of its own.
    ProcessStart,
    /// The process is about to exit. Recorded explicitly before
    /// `process::exit` in headless paths, where `Drop` never runs.
    ProcessEnd { exit_code: i32 },

    /// The frontend launched an engine child process.
    EngineStart { pid: Option<u32> },
    /// The engine child exited (observed by the frontend).
    EngineEnd { exit_code: Option<i32> },
    /// The engine reported its own diagnostic log path back to the frontend.
    EngineLogPath { path: String },

    /// `initialize` created a brand-new session.
    SessionInitialized,
    /// `initialize` resumed a persisted session.
    SessionResumed,

    /// A prompt turn began.
    TurnStart,
    /// A prompt turn finished normally. `stop_reason` originates from
    /// arbitrary provider stream data (not from this crate's own closed
    /// enum), so [`Event::normalized`] rewrites it down to
    /// [`KNOWN_STOP_REASONS`] before this ever reaches the writer — a
    /// malicious/unexpected provider value can never become a raw log field.
    TurnEnd { stop_reason: Option<String> },
    /// A prompt turn ended in failure. `category` is a fixed, closed
    /// classification — never the provider's own error text.
    TurnFailed {
        category: &'static str,
        #[serde(skip_serializing_if = "Option::is_none")]
        status: Option<u16>,
    },

    /// One HTTP attempt was made to the provider.
    HttpAttempt { attempt: u32 },
    /// An HTTP attempt completed (successfully or not).
    HttpResult {
        attempt: u32,
        #[serde(skip_serializing_if = "Option::is_none")]
        status: Option<u16>,
        duration_ms: u64,
        #[serde(skip_serializing_if = "Option::is_none")]
        provider_request_id: Option<String>,
    },
    /// An HTTP attempt failed and will be retried after `delay_ms`.
    HttpRetry {
        attempt: u32,
        delay_ms: u64,
        category: &'static str,
    },
    /// A subsequent attempt succeeded after one or more prior failures.
    HttpRecovery { attempt: u32 },

    /// The shape/protocol/route of one physical HTTP attempt's already-built
    /// request body, recorded immediately before it is executed. `dispatch`
    /// counts physical dispatches (a fresh endpoint/body choice — e.g. a
    /// Copilot chat-mismatch reroute is dispatch 2), independent of the
    /// legacy `attempt` counter on [`Event::HttpAttempt`]/[`Event::HttpResult`]
    /// (which restarts at 1 on every dispatch). `body_bytes` is the exact
    /// length of the already-serialized wire body — `None` only when no
    /// body could be measured (a build failure before any bytes existed),
    /// never an invented `0`.
    RequestShape {
        dispatch: u32,
        attempt: u32,
        protocol: &'static str,
        route_source: &'static str,
        #[serde(flatten)]
        shape: detail::RequestShape,
        #[serde(skip_serializing_if = "Option::is_none")]
        body_bytes: Option<u64>,
    },
    /// A bounded, allowlisted structured extraction of a non-2xx HTTP error
    /// body, recorded once its body has been read (never the raw body
    /// itself, and never the free-text `message`).
    HttpFailureDetails {
        dispatch: u32,
        attempt: u32,
        status: u16,
        detail: detail::ErrorBodyDetail,
    },
    /// A physical HTTP attempt received a successful (2xx) response and
    /// headers; streaming of the body is about to begin. Any failure
    /// recorded after this point for the same context is necessarily
    /// post-headers (an inline provider error, a truncated/invalid stream,
    /// or a dropped connection) — never a request the provider rejected
    /// outright.
    StreamOpened {
        dispatch: u32,
        protocol: &'static str,
        route_source: &'static str,
    },
    /// The outer request failed before any [`Event::StreamOpened`] was ever
    /// recorded for this context — i.e. before headers were accepted (the
    /// HTTP retry policy already exhausted its attempts, or the request
    /// could not even be built/sent). If no [`Event::HttpAttempt`]/
    /// [`Event::RequestShape`] appears at all under the same envelope
    /// `request_id`, the failure happened locally (credential lookup,
    /// request construction) rather than as a provider refusal.
    RequestFailure {
        category: &'static str,
        #[serde(skip_serializing_if = "Option::is_none")]
        status: Option<u16>,
        #[serde(skip_serializing_if = "Option::is_none")]
        detail: Option<detail::ErrorBodyDetail>,
    },

    /// The streamed response failed after headers were already accepted
    /// (incomplete stream, inline provider error event, etc).
    StreamFailure {
        category: &'static str,
        #[serde(skip_serializing_if = "Option::is_none")]
        status: Option<u16>,
        #[serde(skip_serializing_if = "Option::is_none")]
        provider_request_id: Option<String>,
        /// A bounded, allowlisted request-parameter path such as
        /// `input[22].summary` — never arbitrary provider text.
        #[serde(skip_serializing_if = "Option::is_none")]
        parameter: Option<String>,
        /// The same bounded, allowlisted structured error extraction used by
        /// [`Event::HttpFailureDetails`], for a failure that arrived as an
        /// inline SSE error event rather than a non-2xx HTTP status. Absent
        /// when the underlying error carried no body to extract from.
        #[serde(skip_serializing_if = "Option::is_none")]
        detail: Option<detail::ErrorBodyDetail>,
    },
    /// The transport itself failed (connection reset, timeout, TLS, DNS).
    TransportFailure { category: &'static str },
    /// The response could not be parsed as the expected protocol.
    ProtocolFailure { category: &'static str },
    /// Startup/auth/configuration failed before any turn began.
    StartupFailure { category: &'static str },

    /// The writer itself became unhealthy (I/O error, unavailable directory).
    /// Payload-free by construction — `reason` is a fixed short label, not the
    /// underlying `io::Error` message (which can embed a path or OS text).
    WriterUnhealthy { reason: &'static str },
    /// A record was dropped because it exceeded the per-record byte bound.
    /// The record's own JSON is intentionally never partially written.
    RecordDropped { attempted_bytes: usize },

    /// One outer per-model-request attempt began. Optional detail: gated to
    /// `Debug`/`Trace` by [`Event::minimum_verbosity`] — essential
    /// lifecycle/failure events (`TurnStart`/`TurnFailed`/`HttpResult`/
    /// `HttpRetry`/...) are unaffected and always present.
    ModelRequestStart { attempt: u32 },
    /// The same outer attempt finished. `outcome` is a small, closed
    /// classification — never the underlying error text.
    ModelRequestEnd {
        attempt: u32,
        duration_ms: u64,
        outcome: &'static str,
    },
}

impl Event {
    /// The minimum [`Verbosity`] at which this event is recorded.
    ///
    /// Essential lifecycle/error/HTTP-result/retry events are all
    /// `Verbosity::Normal` (present at every configured verbosity); only a
    /// small, explicitly-named set of purely optional detail events (e.g.
    /// [`Event::ModelRequestStart`]/[`Event::ModelRequestEnd`]) requires
    /// `Debug` or louder. `--diagnostic-verbosity` therefore only ever trades
    /// off *additional* detail, never essential discoverability.
    pub fn minimum_verbosity(&self) -> Verbosity {
        match self {
            Event::ModelRequestStart { .. } | Event::ModelRequestEnd { .. } => Verbosity::Debug,
            _ => Verbosity::Normal,
        }
    }

    /// Rewrites any field that originates from arbitrary, untrusted provider
    /// data down to a closed, safe vocabulary. Error details are revalidated
    /// here even when constructed without the usual extraction helpers.
    ///
    /// [`writer::Logger::record`](crate::writer::Logger::record) calls this
    /// on every event *before* building the JSON envelope, so a malicious or
    /// merely-unexpected value streamed back by a provider (which this
    /// crate never controls) is reduced to the relevant allowlist or a
    /// fixed unknown/omitted state before it can reach disk.
    pub(crate) fn normalized(self) -> Event {
        match self {
            Event::TurnEnd { stop_reason } => {
                Event::TurnEnd { stop_reason: normalize_stop_reason(stop_reason.as_deref()) }
            }
            Event::HttpFailureDetails { dispatch, attempt, status, detail } => {
                Event::HttpFailureDetails { dispatch, attempt, status, detail: detail.normalized() }
            }
            Event::RequestFailure { category, status, detail } => {
                Event::RequestFailure { category, status, detail: detail.map(detail::ErrorBodyDetail::normalized) }
            }
            Event::StreamFailure { category, status, provider_request_id, parameter, detail } => {
                let detail = detail.map(detail::ErrorBodyDetail::normalized);
                let parameter = if let Some(detail) = &detail {
                    match &detail.parameter {
                        detail::FieldState::Recognized { value } => Some(value.clone()),
                        _ => None,
                    }
                } else {
                    parameter.and_then(|value| match detail::recognized_parameter_text(&value) {
                        detail::FieldState::Recognized { value } => Some(value),
                        _ => None,
                    })
                };
                Event::StreamFailure { category, status, provider_request_id, parameter, detail }
            }
            other => other,
        }
    }
}

/// The fixed, closed vocabulary of stop reasons this writer will ever record
/// as-is. Anything else — including a value smuggled in by a
/// compromised/misbehaving provider stream — is collapsed to `"unknown"` by
/// [`normalize_stop_reason`] rather than ever being copied into a log line.
pub const KNOWN_STOP_REASONS: &[&str] =
    &["end_turn", "max_tokens", "tool_use", "stop_sequence", "cancelled", "hook_abort"];

/// Normalizes an arbitrary, provider-supplied stop reason down to
/// [`KNOWN_STOP_REASONS`]. `None` stays `None`; any non-`None` value outside
/// the allowlist — however it is shaped, however long, whatever it embeds —
/// becomes the fixed literal `"unknown"`, never copied verbatim.
pub fn normalize_stop_reason(raw: Option<&str>) -> Option<String> {
    raw.map(|value| {
        KNOWN_STOP_REASONS
            .iter()
            .find(|known| **known == value)
            .copied()
            .unwrap_or("unknown")
            .to_owned()
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn events_serialize_with_a_closed_kind_tag_and_no_freeform_message_field() {
        let event = Event::TurnFailed { category: "transport", status: None };
        let value = serde_json::to_value(&event).unwrap();
        assert_eq!(value["kind"], "turn_failed");
        assert_eq!(value["category"], "transport");
        assert!(value.get("message").is_none());
    }

    #[test]
    fn optional_fields_are_omitted_rather_than_null() {
        let event = Event::HttpResult {
            attempt: 1,
            status: None,
            duration_ms: 12,
            provider_request_id: None,
        };
        let value = serde_json::to_value(&event).unwrap();
        assert!(value.get("status").is_none());
        assert!(value.get("provider_request_id").is_none());
        assert_eq!(value["duration_ms"], 12);
    }

    #[test]
    fn essential_lifecycle_and_failure_events_are_always_recorded_at_normal() {
        let essentials = [
            Event::ProcessStart,
            Event::TurnStart,
            Event::TurnFailed { category: "transport", status: None },
            Event::HttpResult { attempt: 1, status: Some(200), duration_ms: 1, provider_request_id: None },
            Event::HttpRetry { attempt: 1, delay_ms: 10, category: "rate_limited" },
            Event::TransportFailure { category: "transport" },
        ];
        for event in essentials {
            assert_eq!(
                event.minimum_verbosity(),
                Verbosity::Normal,
                "essential event {event:?} must remain visible at every verbosity"
            );
        }
    }

    #[test]
    fn optional_per_model_request_detail_requires_debug_or_louder() {
        assert_eq!(Event::ModelRequestStart { attempt: 1 }.minimum_verbosity(), Verbosity::Debug);
        assert_eq!(
            Event::ModelRequestEnd { attempt: 1, duration_ms: 5, outcome: "success" }
                .minimum_verbosity(),
            Verbosity::Debug
        );
    }

    #[test]
    fn normalize_stop_reason_passes_every_known_value_through_unchanged() {
        for known in KNOWN_STOP_REASONS {
            assert_eq!(normalize_stop_reason(Some(known)).as_deref(), Some(*known));
        }
    }

    #[test]
    fn normalize_stop_reason_leaves_none_as_none() {
        assert_eq!(normalize_stop_reason(None), None);
    }

    #[test]
    fn normalize_stop_reason_collapses_a_malicious_or_unrecognized_value_to_a_fixed_label() {
        let malicious = "https://user:sk-live-secret9999@internal.example.com/leak";
        assert_eq!(normalize_stop_reason(Some(malicious)).as_deref(), Some("unknown"));
        assert_eq!(normalize_stop_reason(Some("some_other_never_allowlisted_reason")).as_deref(), Some("unknown"));
    }

    #[test]
    fn event_normalized_rewrites_turn_end_stop_reason_but_leaves_other_variants_untouched() {
        let malicious = "https://user:sk-live-secret9999@internal.example.com/leak";
        let normalized = Event::TurnEnd { stop_reason: Some(malicious.into()) }.normalized();
        assert_eq!(normalized, Event::TurnEnd { stop_reason: Some("unknown".into()) });

        let untouched = Event::TurnFailed { category: "transport", status: Some(500) };
        assert_eq!(untouched.clone().normalized(), untouched);
    }
}
