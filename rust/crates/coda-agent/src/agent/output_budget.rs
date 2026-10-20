//! How many output tokens to request for one response.
//!
//! A fixed small budget truncates long answers, plans and reports mid-sentence,
//! while current models allow 64k–128k of output. The budget is therefore
//! derived from the active model's real output limit, bounded by a default that
//! keeps it within what proxies commonly accept, and adjustable per user.

/// Budget used when the model's own output limit is unknown, and the default
/// cap when it is known: large enough for a long answer, small enough that a
/// proxy capping below the model's advertised ceiling does not reject it.
pub const FALLBACK_MAX_OUTPUT_TOKENS: u32 = 32_000;

/// Smallest budget ever requested, unless the model itself allows less.
pub const MIN_MAX_OUTPUT_TOKENS: u32 = 4_096;

/// Room left between the estimated prompt and the context window when the
/// budget is clamped to the window. The estimate is a lower bound, so the
/// margin absorbs some of what it cannot see.
pub const CONTEXT_SAFETY_MARGIN: u32 = 2_048;

/// Where a resolved budget came from, for diagnostics.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OutputBudgetSource {
    /// The user's `maxOutputTokens` setting.
    Setting,
    /// The provider's live model list.
    Live,
    /// The bundled or cached models.dev catalogue.
    Catalog,
    /// Nothing knew the model's limit.
    Fallback,
}

impl OutputBudgetSource {
    pub fn as_str(self) -> &'static str {
        match self {
            OutputBudgetSource::Setting => "setting",
            OutputBudgetSource::Live => "live",
            OutputBudgetSource::Catalog => "catalog",
            OutputBudgetSource::Fallback => "fallback",
        }
    }
}

/// The per-model limits a loop needs to size each request.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ModelBudget {
    /// The `max_tokens` to request.
    pub max_tokens: u32,
    /// The model's context window, when known, so a request near the window
    /// can shrink its output budget instead of being rejected.
    pub context_window: Option<usize>,
}

/// Output tokens to request for one response on the active model.
///
/// `model_output_limit` is the model's own ceiling (live list or catalogue);
/// `user_override` is the user's setting. The default is
/// `min(model limit, FALLBACK)`, and an override is clamped to the model's
/// limit. The result never drops below [`MIN_MAX_OUTPUT_TOKENS`] unless the
/// model's own limit is lower still.
pub fn resolve_max_output_tokens(model_output_limit: Option<u32>, user_override: Option<u32>) -> u32 {
    let ceiling = model_output_limit.filter(|n| *n > 0).unwrap_or(FALLBACK_MAX_OUTPUT_TOKENS);
    let floor = MIN_MAX_OUTPUT_TOKENS.min(ceiling);
    user_override
        .filter(|n| *n > 0)
        .unwrap_or_else(|| ceiling.min(FALLBACK_MAX_OUTPUT_TOKENS))
        .clamp(floor, ceiling)
}

/// Shrinks `max_tokens` so the estimated prompt plus the reply fit the context
/// window (the Messages API rejects a request where they do not).
///
/// Never goes below [`MIN_MAX_OUTPUT_TOKENS`] (or `max_tokens`, if smaller):
/// a reply too small to say anything is worse than letting the provider's
/// overflow error trigger compaction.
pub fn clamp_to_context_window(
    max_tokens: u32,
    context_window: Option<usize>,
    estimated_input: usize,
) -> u32 {
    let Some(window) = context_window else {
        return max_tokens;
    };
    let floor = MIN_MAX_OUTPUT_TOKENS.min(max_tokens);
    let available = window
        .saturating_sub(estimated_input)
        .saturating_sub(CONTEXT_SAFETY_MARGIN as usize);
    let available = u32::try_from(available).unwrap_or(u32::MAX);
    max_tokens.min(available).max(floor)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolver_uses_catalog_output_limit() {
        assert_eq!(resolve_max_output_tokens(Some(64_000), None), 32_000);
        assert_eq!(resolve_max_output_tokens(Some(64_000), Some(64_000)), 64_000);
        // A model whose limit is below the default gets its own limit.
        assert_eq!(resolve_max_output_tokens(Some(16_000), None), 16_000);
    }

    #[test]
    fn resolver_falls_back_when_unknown() {
        assert_eq!(resolve_max_output_tokens(None, None), FALLBACK_MAX_OUTPUT_TOKENS);
        assert_eq!(resolve_max_output_tokens(Some(0), None), FALLBACK_MAX_OUTPUT_TOKENS);
    }

    #[test]
    fn resolver_clamps_override_to_model_limit() {
        assert_eq!(resolve_max_output_tokens(Some(64_000), Some(200_000)), 64_000);
        // An override below the floor is raised to it.
        assert_eq!(resolve_max_output_tokens(Some(64_000), Some(100)), MIN_MAX_OUTPUT_TOKENS);
        // An unknown model accepts an override above the default.
        assert_eq!(resolve_max_output_tokens(None, Some(50_000)), 32_000);
    }

    #[test]
    fn resolver_never_exceeds_a_model_limit_below_the_floor() {
        assert_eq!(resolve_max_output_tokens(Some(2_048), None), 2_048);
        assert_eq!(resolve_max_output_tokens(Some(2_048), Some(8_000)), 2_048);
    }

    #[test]
    fn resolver_clamps_to_context_window() {
        // Plenty of room: unchanged.
        assert_eq!(clamp_to_context_window(32_000, Some(200_000), 50_000), 32_000);
        // Input near the window: reduced to what fits.
        assert_eq!(
            clamp_to_context_window(32_000, Some(200_000), 180_000),
            200_000 - 180_000 - CONTEXT_SAFETY_MARGIN
        );
        // Input past the window: never below the floor.
        assert_eq!(clamp_to_context_window(32_000, Some(200_000), 199_000), MIN_MAX_OUTPUT_TOKENS);
        // Unknown window: unchanged.
        assert_eq!(clamp_to_context_window(32_000, None, 10_000_000), 32_000);
        // A budget already below the floor is not raised.
        assert_eq!(clamp_to_context_window(2_048, Some(200_000), 199_000), 2_048);
    }
}
