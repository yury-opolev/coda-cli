//! Context tiers: the provider's distinct context-window sizes for one model.
//!
//! GitHub Copilot advertises tiers per model in `GET /models` under
//! `billing.token_prices`: a `default` entry and, for tiered models, a
//! `long_context` entry, each with a `context_max` (the largest input that tier
//! covers), plus an optional `preferred_tier`. Long-context pricing applies by
//! input size: a request whose input exceeds the default tier's `context_max`
//! is billed at the long-context rate.
//!
//! There is no documented request field or header that *negotiates* a tier,
//! so a tier is a client-side policy: it chooses the effective window Coda
//! budgets against (fit checks, occupancy, compaction), which in turn keeps
//! requests inside — or lets them grow past — the default tier's threshold.
//! See `docs/design/thinking-and-context-tiers.md`.

use serde_json::Value;

/// A selectable context tier.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ContextTier {
    Default,
    LongContext,
}

impl ContextTier {
    pub fn parse(raw: &str) -> Option<ContextTier> {
        match raw.trim().to_ascii_lowercase().as_str() {
            "default" => Some(ContextTier::Default),
            "long_context" | "long-context" | "long" => Some(ContextTier::LongContext),
            _ => None,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            ContextTier::Default => "default",
            ContextTier::LongContext => "long_context",
        }
    }
}

/// The tiers one model advertised. Present only when the row carried tier
/// metadata at all; `long_context_window` is `None` for a model that has a
/// single (default) tier.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ContextTiers {
    pub default_window: u32,
    pub long_context_window: Option<u32>,
    pub preferred: Option<ContextTier>,
}

/// Smallest and largest windows accepted from metadata. Metadata is untrusted:
/// anything outside is discarded rather than budgeted against.
pub const MIN_TIER_WINDOW: u32 = 1_024;
pub const MAX_TIER_WINDOW: u32 = 10_000_000;

impl ContextTiers {
    /// Parses `billing.token_prices` from a Copilot model row.
    ///
    /// Returns `None` when there is no usable default tier. A long-context
    /// entry that is out of bounds or not larger than the default is dropped,
    /// leaving a single-tier model rather than a fabricated second choice.
    pub fn from_copilot_row(item: &Value) -> Option<ContextTiers> {
        let prices = item.get("billing")?.get("token_prices")?;
        let window = |tier: &str| {
            prices
                .get(tier)?
                .get("context_max")?
                .as_u64()
                .and_then(|n| u32::try_from(n).ok())
                .filter(|n| (MIN_TIER_WINDOW..=MAX_TIER_WINDOW).contains(n))
        };
        let default_window = window("default")?;
        let long_context_window = window("long_context").filter(|long| *long > default_window);
        let preferred = prices
            .get("preferred_tier")
            .and_then(Value::as_str)
            .and_then(ContextTier::parse)
            .filter(|tier| *tier == ContextTier::Default || long_context_window.is_some());
        Some(ContextTiers { default_window, long_context_window, preferred })
    }

    /// The window of `tier`, when this model offers it.
    pub fn window(&self, tier: ContextTier) -> Option<u32> {
        match tier {
            ContextTier::Default => Some(self.default_window),
            ContextTier::LongContext => self.long_context_window,
        }
    }

    /// Whether more than one tier is selectable.
    pub fn is_tiered(&self) -> bool {
        self.long_context_window.is_some()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn parses_the_two_documented_tiers_and_aliases() {
        assert_eq!(ContextTier::parse("default"), Some(ContextTier::Default));
        assert_eq!(ContextTier::parse(" LONG_CONTEXT "), Some(ContextTier::LongContext));
        assert_eq!(ContextTier::parse("long-context"), Some(ContextTier::LongContext));
        assert_eq!(ContextTier::parse("huge"), None);
        assert_eq!(ContextTier::LongContext.as_str(), "long_context");
    }

    /// Copied from the live `claude-haiku-5.5` row (2026-11-24).
    #[test]
    fn reads_a_tiered_row_with_a_preferred_tier() {
        let row = json!({ "billing": { "token_prices": {
            "default": { "context_max": 100000, "input_price": 10 },
            "long_context": { "context_max": 936000, "input_price": 50 },
            "preferred_tier": "long_context"
        }}});
        let tiers = ContextTiers::from_copilot_row(&row).unwrap();
        assert_eq!(tiers.default_window, 100_000);
        assert_eq!(tiers.long_context_window, Some(936_000));
        assert_eq!(tiers.preferred, Some(ContextTier::LongContext));
        assert!(tiers.is_tiered());
        assert_eq!(tiers.window(ContextTier::LongContext), Some(936_000));
    }

    /// `gpt-5.3-codex` advertises only a default tier: one fixed window.
    #[test]
    fn a_default_only_row_is_single_tier() {
        let row = json!({ "billing": { "token_prices": { "default": { "context_max": 272000 } } } });
        let tiers = ContextTiers::from_copilot_row(&row).unwrap();
        assert!(!tiers.is_tiered());
        assert_eq!(tiers.window(ContextTier::LongContext), None);
    }

    #[test]
    fn rows_without_tier_metadata_report_none() {
        assert_eq!(ContextTiers::from_copilot_row(&json!({})), None);
        // mai-code-2: prices but no context_max.
        let row = json!({ "billing": { "token_prices": { "default": { "input_price": 100 } } } });
        assert_eq!(ContextTiers::from_copilot_row(&row), None);
    }

    #[test]
    fn untrusted_windows_are_bounded_and_never_inverted() {
        let absurd = json!({ "billing": { "token_prices": {
            "default": { "context_max": 200000 },
            "long_context": { "context_max": 99_000_000_000u64 },
            "preferred_tier": "long_context"
        }}});
        let tiers = ContextTiers::from_copilot_row(&absurd).unwrap();
        assert_eq!(tiers.long_context_window, None);
        assert_eq!(tiers.preferred, None, "a preferred tier that does not exist is dropped");

        let inverted = json!({ "billing": { "token_prices": {
            "default": { "context_max": 200000 }, "long_context": { "context_max": 100000 }
        }}});
        assert!(!ContextTiers::from_copilot_row(&inverted).unwrap().is_tiered());

        let tiny = json!({ "billing": { "token_prices": { "default": { "context_max": 10 } } } });
        assert_eq!(ContextTiers::from_copilot_row(&tiny), None);
    }
}
