//! Summary allowance and the reconstructed-request fit check.
//!
//! Every figure here is an estimate in the units of
//! [`super::TokenEstimator`]; the safety margin absorbs its imprecision.

use super::{DEFAULT_CONTEXT_WINDOW, FALLBACK_MAX_OUTPUT_TOKENS};

pub const SUMMARY_INITIAL_OUTPUT_TOKENS: u32 = 8_192;
pub const SUMMARY_RETRY_MAX_OUTPUT_TOKENS: u32 = 32_768;
pub const SUMMARY_MIN_OUTPUT_TOKENS: u32 = 1_024;
pub const COMPACTION_SAFETY_MARGIN: usize = 2_048;

/// Where the effective context window came from.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum WindowSource {
    Live,
    Catalog,
    Setting,
    #[default]
    Fallback,
}

impl WindowSource {
    pub fn as_str(self) -> &'static str {
        match self {
            WindowSource::Live => "live",
            WindowSource::Catalog => "catalog",
            WindowSource::Setting => "setting",
            WindowSource::Fallback => "fallback",
        }
    }
}

/// The limits one compaction works within.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CompactionLimits {
    /// Effective window; [`DEFAULT_CONTEXT_WINDOW`] when unknown.
    pub context_window: usize,
    pub window_source: WindowSource,
    pub model_output_limit: Option<u32>,
    /// System prompt + tool schemas (estimate).
    pub request_overhead_tokens: usize,
    /// The turn's `max_tokens`.
    pub reply_reserve_tokens: usize,
}

impl Default for CompactionLimits {
    fn default() -> Self {
        Self {
            context_window: DEFAULT_CONTEXT_WINDOW,
            window_source: WindowSource::Fallback,
            model_output_limit: None,
            request_overhead_tokens: 0,
            reply_reserve_tokens: FALLBACK_MAX_OUTPUT_TOKENS,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SummaryAllowance {
    pub initial: u32,
    /// Only when strictly larger than `initial`.
    pub retry: Option<u32>,
}

fn model_limit(limits: &CompactionLimits) -> u32 {
    limits.model_output_limit.unwrap_or(u32::MAX)
}

/// `None` when the room left after the input and the safety margin is below
/// [`SUMMARY_MIN_OUTPUT_TOKENS`].
pub fn summary_allowance(limits: &CompactionLimits, input_tokens: usize) -> Option<SummaryAllowance> {
    let room = limits
        .context_window
        .saturating_sub(input_tokens)
        .saturating_sub(COMPACTION_SAFETY_MARGIN);
    let room = u32::try_from(room).unwrap_or(u32::MAX);
    if room < SUMMARY_MIN_OUTPUT_TOKENS {
        return None;
    }
    let initial = SUMMARY_INITIAL_OUTPUT_TOKENS.min(model_limit(limits)).min(room);
    let retry = SUMMARY_RETRY_MAX_OUTPUT_TOKENS.min(model_limit(limits)).min(room);
    Some(SummaryAllowance { initial, retry: (retry > initial).then_some(retry) })
}

/// Largest summariser input that still leaves the initial allowance.
pub fn max_summary_input_tokens(limits: &CompactionLimits, system_tokens: usize) -> usize {
    let initial = SUMMARY_INITIAL_OUTPUT_TOKENS.min(model_limit(limits)) as usize;
    limits
        .context_window
        .saturating_sub(COMPACTION_SAFETY_MARGIN)
        .saturating_sub(initial)
        .saturating_sub(system_tokens)
}

fn required_tokens(limits: &CompactionLimits, history_tokens: usize) -> usize {
    history_tokens
        .saturating_add(limits.request_overhead_tokens)
        .saturating_add(limits.reply_reserve_tokens)
        .saturating_add(COMPACTION_SAFETY_MARGIN)
}

/// Whether a request carrying `history_tokens` of history fits the window.
pub fn request_fits(limits: &CompactionLimits, history_tokens: usize) -> bool {
    required_tokens(limits, history_tokens) <= limits.context_window
}

/// Tokens that must be freed for [`request_fits`] to hold (0 when it does).
pub fn overflow_tokens(limits: &CompactionLimits, history_tokens: usize) -> usize {
    required_tokens(limits, history_tokens).saturating_sub(limits.context_window)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn limits(window: usize, model_output_limit: Option<u32>) -> CompactionLimits {
        CompactionLimits { context_window: window, model_output_limit, ..CompactionLimits::default() }
    }

    #[test]
    fn fit_and_overflow_agree() {
        let l = CompactionLimits { reply_reserve_tokens: 1_000, ..limits(10_000, None) };
        // 10_000 - 1_000 - 2_048 = 6_952 tokens of history fit.
        assert!(request_fits(&l, 6_952));
        assert_eq!(overflow_tokens(&l, 6_952), 0);
        assert!(!request_fits(&l, 6_953));
        assert_eq!(overflow_tokens(&l, 7_000), 48);
    }

    #[test]
    fn max_input_leaves_the_initial_allowance() {
        assert_eq!(max_summary_input_tokens(&limits(200_000, None), 1_000), 200_000 - 2_048 - 8_192 - 1_000);
        assert_eq!(max_summary_input_tokens(&limits(200_000, Some(4_000)), 0), 200_000 - 2_048 - 4_000);
        assert_eq!(max_summary_input_tokens(&limits(5_000, None), 0), 0);
    }
}
