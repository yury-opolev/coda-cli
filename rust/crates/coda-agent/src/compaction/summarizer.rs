//! The summariser seam: a dedicated, explicitly budgeted model call whose
//! completion is validated before anything is installed.
//!
//! Distinct from [`crate::autonomy::ForkedAgent`], whose short-answer cap
//! (judge, stand-in answerer, classifier) must stay small.

use coda_llm::{Message, Usage};
use tokio_util::sync::CancellationToken;

/// One summariser call.
#[derive(Debug, Clone)]
pub struct SummaryRequest {
    pub system: String,
    pub messages: Vec<Message>,
    pub max_output_tokens: u32,
}

/// The collected reply, with the provider's stop reason and usage.
#[derive(Debug, Clone, Default)]
pub struct SummaryResponse {
    pub text: String,
    pub stop_reason: Option<String>,
    pub usage: Usage,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SummarizerError {
    Cancelled,
    /// `category` is a closed diagnostics label, never provider text.
    Failed { category: &'static str },
}

#[async_trait::async_trait]
pub trait Summarizer: Send + Sync {
    async fn summarize(
        &self,
        request: SummaryRequest,
        cancel: CancellationToken,
    ) -> Result<SummaryResponse, SummarizerError>;
}

/// How a summariser reply ended, normalised across providers.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SummaryCompletion {
    Complete,
    Truncated,
    Empty,
    Rejected(&'static str),
}

const COMPLETE_REASONS: [&str; 4] = ["end_turn", "stop", "stop_sequence", "completed"];
const TRUNCATED_REASONS: [&str; 5] =
    ["max_tokens", "length", "max_output_tokens", "incomplete", "model_context_window_exceeded"];
const REJECTED_LABELS: [&str; 4] = ["tool_use", "content_filter", "refusal", "pause_turn"];

impl SummaryCompletion {
    /// `Complete` needs a normal stop and nonempty trimmed text; a normal stop
    /// with blank text is `Empty`; output-limit stops are `Truncated` whatever
    /// the text; anything else, including an unknown (`None`) stop, is
    /// `Rejected`.
    pub fn classify(stop_reason: Option<&str>, text: &str) -> Self {
        match stop_reason {
            Some(r) if COMPLETE_REASONS.contains(&r) => {
                if text.trim().is_empty() {
                    Self::Empty
                } else {
                    Self::Complete
                }
            }
            Some(r) if TRUNCATED_REASONS.contains(&r) => Self::Truncated,
            other => Self::Rejected(rejected_label(other)),
        }
    }
}

fn rejected_label(stop_reason: Option<&str>) -> &'static str {
    match stop_reason {
        None => "unknown",
        Some(r) => REJECTED_LABELS.iter().find(|l| **l == r).copied().unwrap_or("other"),
    }
}

/// Closed label for diagnostics: `"end_turn"` for the complete set,
/// `"max_tokens"` for the truncated set, else the rejected label.
pub fn normalized_stop_reason(stop_reason: Option<&str>) -> &'static str {
    match stop_reason {
        Some(r) if COMPLETE_REASONS.contains(&r) => "end_turn",
        Some(r) if TRUNCATED_REASONS.contains(&r) => "max_tokens",
        other => rejected_label(other),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalized_stop_reason_is_a_closed_label() {
        assert_eq!(normalized_stop_reason(Some("stop")), "end_turn");
        assert_eq!(normalized_stop_reason(Some("length")), "max_tokens");
        assert_eq!(normalized_stop_reason(Some("refusal")), "refusal");
        assert_eq!(normalized_stop_reason(Some("secret prompt text")), "other");
        assert_eq!(normalized_stop_reason(None), "unknown");
    }
}
