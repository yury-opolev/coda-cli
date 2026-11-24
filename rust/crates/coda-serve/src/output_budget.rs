//! Resolves the output budget (`max_tokens`) for a model from the user's
//! settings, the provider's live model list and the models.dev catalogue.
//!
//! One function serves the main loop, subagents and the compaction reserve,
//! so the three can never disagree about how long a reply may be.

use std::collections::HashMap;

use coda_agent::{resolve_max_output_tokens, ModelBudget, OutputBudgetSource, WindowSource};
use serde_json::Value;

use crate::catalog::ModelCatalog;

/// Limits a provider's live `/models` list reported for one model.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct LiveLimits {
    pub context: Option<u32>,
    pub output: Option<u32>,
    /// Context tiers the row advertised; `None` when it carried none.
    pub tiers: Option<coda_llm::ContextTiers>,
}

/// What is known about a model's selectable context tiers.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ContextCapability {
    /// No live metadata for the model (not fetched yet, or the fetch failed).
    Unknown,
    /// Live metadata exists but describes one fixed window.
    Single { tiers: Option<coda_llm::ContextTiers> },
    /// More than one tier is selectable.
    Tiered(coda_llm::ContextTiers),
}

impl ContextCapability {
    pub fn as_str(&self) -> &'static str {
        match self {
            ContextCapability::Unknown => "unknown",
            ContextCapability::Single { .. } => "single",
            ContextCapability::Tiered(_) => "tiered",
        }
    }
}

/// Why a requested tier is not in effect — a fixed, closed set.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TierNote {
    /// The model's metadata describes no tiers at all.
    SingleWindow,
    /// The model has tiers, but not the one requested.
    TierUnavailable,
    /// No live metadata yet, so no tier can be confirmed.
    CapabilityUnknown,
}

impl TierNote {
    pub fn as_str(self) -> &'static str {
        match self {
            TierNote::SingleWindow => "single_window",
            TierNote::TierUnavailable => "tier_unavailable",
            TierNote::CapabilityUnknown => "capability_unknown",
        }
    }

    /// A user-facing explanation.
    pub fn message(self, tier: coda_llm::ContextTier) -> String {
        match self {
            TierNote::SingleWindow => format!(
                "{} is not available: this model exposes one fixed context window",
                tier.as_str()
            ),
            TierNote::TierUnavailable => {
                format!("{} is not offered for this model", tier.as_str())
            }
            TierNote::CapabilityUnknown => format!(
                "{} cannot be confirmed: the provider has not reported this model's context tiers \
                 (refresh the model list and try again)",
                tier.as_str()
            ),
        }
    }
}

/// The requested and effective context tier for one budget.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ContextResolution {
    /// What the user (or a saved preference) asked for; `None` = unset.
    pub requested: Option<coda_llm::ContextTier>,
    /// The tier the effective window belongs to, when that is known.
    pub effective: Option<coda_llm::ContextTier>,
    pub capability: ContextCapability,
    /// Set when `requested` is not in effect.
    pub note: Option<TierNote>,
}

/// Live limits keyed by [`live_key`].
pub type LiveLimitMap = HashMap<String, LiveLimits>;

/// The key a live limit is stored under: provider and model, because the same
/// id can mean different deployments with different limits on two providers.
pub fn live_key(provider: &str, model: &str) -> String {
    format!("{provider}/{model}")
}

/// A resolved budget plus what it was derived from, for diagnostics.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ResolvedBudget {
    pub budget: ModelBudget,
    pub source: OutputBudgetSource,
    pub model_output_limit: Option<u32>,
    /// Where `budget.context_window` came from (`Fallback` when unknown).
    pub context_source: WindowSource,
    /// The context tier behind `budget.context_window`.
    pub context: ContextResolution,
}

/// Reads the user's `maxOutputTokens` override for a model.
///
/// `maxOutputTokensByModel["<provider>/<model>"]` wins over the global
/// `maxOutputTokens`, using the same key format as `effortByModel`. A value
/// that is not a positive integer is ignored, like every other malformed
/// setting: the engine must start whatever the file says.
pub fn max_output_tokens_from(settings: &Value, provider: &str, model: &str) -> Option<u32> {
    let positive = |v: &Value| v.as_u64().filter(|n| *n > 0).map(|n| u32::try_from(n).unwrap_or(u32::MAX));
    settings
        .get("maxOutputTokensByModel")
        .and_then(|m| m.get(live_key(provider, model)))
        .and_then(positive)
        .or_else(|| settings.get("maxOutputTokens").and_then(positive))
}

/// Reads the user's saved context tier for a model from
/// `contextTierByModel["<provider>/<model>"]`. An unrecognised value is
/// ignored, like every other malformed setting.
pub fn context_tier_from_settings(
    settings: &Value,
    provider: &str,
    model: &str,
) -> Option<coda_llm::ContextTier> {
    settings
        .get("contextTierByModel")?
        .get(live_key(provider, model))?
        .as_str()
        .and_then(coda_llm::ContextTier::parse)
}

/// The budget to request for `model` on `provider`, applying the saved
/// context-tier preference (if any).
pub fn resolve(
    catalog: &ModelCatalog,
    live: &LiveLimitMap,
    settings: &Value,
    provider: &str,
    model: &str,
) -> ResolvedBudget {
    let requested = context_tier_from_settings(settings, provider, model);
    resolve_with_tier(catalog, live, settings, provider, model, requested)
}

/// The budget to request for `model` on `provider` with an explicit
/// requested context tier (`None` = unset).
///
/// The model's own limit comes from the live list when it reported one (a
/// provider's proxy may cap below what models.dev records), else the
/// catalogue. The context window follows the same preference — unless a
/// requested tier the model advertises applies, in which case that tier's
/// window is the effective window.
pub fn resolve_with_tier(
    catalog: &ModelCatalog,
    live: &LiveLimitMap,
    settings: &Value,
    provider: &str,
    model: &str,
    requested: Option<coda_llm::ContextTier>,
) -> ResolvedBudget {
    // Copilot serves `claude-opus-4-8` as `claude-opus-4.8`; its live list and
    // catalogue entry use the dotted id.
    let normalized = coda_llm::copilot::models::normalize_model_id(model);
    let live_row = live
        .get(&live_key(provider, model))
        .or_else(|| live.get(&live_key(provider, &normalized)))
        .copied();
    let live = live_row.unwrap_or_default();
    let cataloged = catalog
        .find(Some(provider), &normalized)
        .filter(|_| *normalized != *model)
        .or_else(|| catalog.find(Some(provider), model));
    let catalog_output =
        cataloged.and_then(|m| m.output_limit).and_then(|n| u32::try_from(n).ok()).filter(|n| *n > 0);
    let catalog_context = cataloged.and_then(|m| m.context_limit).and_then(|n| usize::try_from(n).ok());

    let (model_output_limit, limit_source) = match (live.output, catalog_output) {
        (Some(n), _) => (Some(n), OutputBudgetSource::Live),
        (None, Some(n)) => (Some(n), OutputBudgetSource::Catalog),
        (None, None) => (None, OutputBudgetSource::Fallback),
    };
    let user_override = max_output_tokens_from(settings, provider, model);
    let source = if user_override.is_some() { OutputBudgetSource::Setting } else { limit_source };

    let (legacy_window, legacy_source) = match (live.context, catalog_context) {
        (Some(n), _) => (Some(n as usize), WindowSource::Live),
        (None, Some(n)) => (Some(n), WindowSource::Catalog),
        (None, None) => (None, WindowSource::Fallback),
    };

    let capability = match live_row {
        None => ContextCapability::Unknown,
        Some(row) => match row.tiers {
            Some(tiers) if tiers.is_tiered() => ContextCapability::Tiered(tiers),
            tiers => ContextCapability::Single { tiers },
        },
    };
    let (context_window, context_source, context) =
        resolve_tier(requested, capability, legacy_window, legacy_source);

    ResolvedBudget {
        budget: ModelBudget {
            max_tokens: resolve_max_output_tokens(model_output_limit, user_override),
            context_window,
        },
        source,
        model_output_limit,
        context_source,
        context,
    }
}

/// Applies a requested tier to the legacy (limits-derived) window.
///
/// - Unset: the legacy window, unchanged; the effective tier is the one that
///   window falls in, for a tiered model.
/// - A supported tier on a tiered model: that tier's window, never larger
///   than the legacy window when one is known.
/// - Default on a single-tier model: the legacy window (its only tier).
/// - Anything else: the legacy window with a note saying why the request is
///   not in effect. The request itself is preserved, never rewritten.
fn resolve_tier(
    requested: Option<coda_llm::ContextTier>,
    capability: ContextCapability,
    legacy_window: Option<usize>,
    legacy_source: WindowSource,
) -> (Option<usize>, WindowSource, ContextResolution) {
    use coda_llm::ContextTier;
    let resolution = |effective, note| ContextResolution { requested, effective, capability, note };
    let Some(tier) = requested else {
        let effective = match capability {
            ContextCapability::Tiered(tiers) => legacy_window.map(|window| {
                if window <= tiers.default_window as usize {
                    ContextTier::Default
                } else {
                    // Past the default tier's size: long-context pricing applies.
                    ContextTier::LongContext
                }
            }),
            ContextCapability::Single { .. } => Some(ContextTier::Default),
            ContextCapability::Unknown => None,
        };
        return (legacy_window, legacy_source, resolution(effective, None));
    };
    match capability {
        ContextCapability::Tiered(tiers) if tiers.window(tier).is_some() => {
            let tier_window = tiers.window(tier).unwrap_or_default() as usize;
            let window = legacy_window.map_or(tier_window, |legacy| legacy.min(tier_window));
            (Some(window), WindowSource::Tier, resolution(Some(tier), None))
        }
        // A single fixed window is the model's default (and only) tier.
        ContextCapability::Single { .. } if tier == ContextTier::Default => {
            (legacy_window, legacy_source, resolution(Some(tier), None))
        }
        ContextCapability::Tiered(_) | ContextCapability::Single { tiers: Some(_) } => {
            (legacy_window, legacy_source, resolution(None, Some(TierNote::TierUnavailable)))
        }
        ContextCapability::Single { tiers: None } => {
            (legacy_window, legacy_source, resolution(None, Some(TierNote::SingleWindow)))
        }
        ContextCapability::Unknown => {
            (legacy_window, legacy_source, resolution(None, Some(TierNote::CapabilityUnknown)))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn catalog() -> ModelCatalog {
        ModelCatalog::parse(
            r#"{"github-copilot":{"models":{
                "big":{"limit":{"context":200000,"output":64000}},
                "small":{"limit":{"context":128000,"output":16000}},
                "nolimit":{"limit":{"context":100000}}
            }}}"#,
        )
    }

    #[test]
    fn the_catalogue_limit_caps_the_default() {
        let r = resolve(&catalog(), &LiveLimitMap::new(), &json!({}), "github-copilot", "big");
        assert_eq!(r.budget, ModelBudget { max_tokens: 32_000, context_window: Some(200_000) });
        assert_eq!(r.source, OutputBudgetSource::Catalog);
        assert_eq!(r.model_output_limit, Some(64_000));

        let small = resolve(&catalog(), &LiveLimitMap::new(), &json!({}), "github-copilot", "small");
        assert_eq!(small.budget.max_tokens, 16_000);
    }

    #[test]
    fn an_unknown_model_uses_the_fallback() {
        let r = resolve(&catalog(), &LiveLimitMap::new(), &json!({}), "github-copilot", "mystery");
        assert_eq!(r.budget.max_tokens, coda_agent::FALLBACK_MAX_OUTPUT_TOKENS);
        assert_eq!(r.budget.context_window, None);
        assert_eq!(r.source, OutputBudgetSource::Fallback);

        let r = resolve(&catalog(), &LiveLimitMap::new(), &json!({}), "github-copilot", "nolimit");
        assert_eq!(r.source, OutputBudgetSource::Fallback);
        assert_eq!(r.budget.context_window, Some(100_000));
    }

    #[test]
    fn a_live_limit_is_preferred_over_the_catalogue() {
        let mut live = LiveLimitMap::new();
        live.insert(
            live_key("github-copilot", "big"),
            LiveLimits { context: Some(168_000), output: Some(16_000), tiers: None },
        );
        let r = resolve(&catalog(), &live, &json!({}), "github-copilot", "big");
        assert_eq!(r.budget, ModelBudget { max_tokens: 16_000, context_window: Some(168_000) });
        assert_eq!(r.source, OutputBudgetSource::Live);
    }

    #[test]
    fn the_context_window_names_its_source() {
        let mut live = LiveLimitMap::new();
        live.insert(
            live_key("github-copilot", "mystery-1m"),
            LiveLimits { context: Some(1_000_000), output: Some(64_000), tiers: None },
        );
        let r = resolve(&catalog(), &live, &json!({}), "github-copilot", "mystery-1m");
        assert_eq!(r.budget.context_window, Some(1_000_000));
        assert_eq!(r.context_source, WindowSource::Live);

        let r = resolve(&catalog(), &live, &json!({}), "github-copilot", "big");
        assert_eq!(r.budget.context_window, Some(200_000));
        assert_eq!(r.context_source, WindowSource::Catalog);

        let r = resolve(&catalog(), &live, &json!({}), "github-copilot", "mystery");
        assert_eq!(r.budget.context_window, None);
        assert_eq!(r.context_source, WindowSource::Fallback);
    }

    #[test]
    fn a_dotted_copilot_alias_resolves_the_same_window() {
        let mut live = LiveLimitMap::new();
        live.insert(
            live_key("github-copilot", "claude-opus-4.8"),
            LiveLimits { context: Some(1_000_000), output: Some(64_000), tiers: None },
        );
        let dashed = resolve(&catalog(), &live, &json!({}), "github-copilot", "claude-opus-4-8");
        let dotted = resolve(&catalog(), &live, &json!({}), "github-copilot", "claude-opus-4.8");
        assert_eq!(dashed, dotted);
        assert_eq!(dashed.budget.context_window, Some(1_000_000));
        assert_eq!(dashed.context_source, WindowSource::Live);
    }

    #[test]
    fn a_setting_overrides_within_the_model_limit() {
        let settings = json!({ "maxOutputTokens": 64000 });
        let r = resolve(&catalog(), &LiveLimitMap::new(), &settings, "github-copilot", "big");
        assert_eq!(r.budget.max_tokens, 64_000);
        assert_eq!(r.source, OutputBudgetSource::Setting);

        let r = resolve(&catalog(), &LiveLimitMap::new(), &settings, "github-copilot", "small");
        assert_eq!(r.budget.max_tokens, 16_000, "an override is clamped to the model's limit");
    }

    #[test]
    fn a_per_model_setting_wins_over_the_global_one() {
        let settings = json!({
            "maxOutputTokens": 8000,
            "maxOutputTokensByModel": { "github-copilot/big": 48000 }
        });
        assert_eq!(max_output_tokens_from(&settings, "github-copilot", "big"), Some(48_000));
        assert_eq!(max_output_tokens_from(&settings, "github-copilot", "small"), Some(8_000));
    }

    #[test]
    fn a_malformed_setting_is_ignored() {
        for settings in [
            json!({ "maxOutputTokens": 0 }),
            json!({ "maxOutputTokens": -5 }),
            json!({ "maxOutputTokens": "lots" }),
            json!({ "maxOutputTokens": 1.5 }),
        ] {
            assert_eq!(max_output_tokens_from(&settings, "p", "m"), None, "{settings}");
        }
    }

    // ── context tiers ───────────────────────────────────────────────────────

    use coda_llm::{ContextTier, ContextTiers};

    fn tiered_live() -> LiveLimitMap {
        let mut live = LiveLimitMap::new();
        live.insert(
            live_key("github-copilot", "claude-sonnet-5.5"),
            LiveLimits {
                context: Some(1_000_000),
                output: Some(128_000),
                tiers: Some(ContextTiers {
                    default_window: 200_000,
                    long_context_window: Some(936_000),
                    preferred: None,
                }),
            },
        );
        live.insert(
            live_key("github-copilot", "gpt-5.3-codex"),
            LiveLimits {
                context: Some(400_000),
                output: Some(128_000),
                tiers: Some(ContextTiers { default_window: 272_000, long_context_window: None, preferred: None }),
            },
        );
        live.insert(
            live_key("github-copilot", "mai-code-2"),
            LiveLimits { context: Some(1_050_000), output: None, tiers: None },
        );
        live
    }

    fn tiered(requested: Option<ContextTier>, model: &str) -> ResolvedBudget {
        resolve_with_tier(&catalog(), &tiered_live(), &json!({}), "github-copilot", model, requested)
    }

    /// Unset keeps the historical window exactly (backward compatible) and
    /// reports which tier that window actually falls in.
    #[test]
    fn an_unset_tier_keeps_the_legacy_window_and_names_its_tier() {
        let r = tiered(None, "claude-sonnet-5.5");
        assert_eq!(r.budget.context_window, Some(1_000_000));
        assert_eq!(r.context_source, WindowSource::Live);
        assert_eq!(r.context.requested, None);
        assert_eq!(r.context.effective, Some(ContextTier::LongContext));
        assert!(matches!(r.context.capability, ContextCapability::Tiered(_)));
        assert_eq!(r.context.note, None);
    }

    #[test]
    fn a_tiered_model_budgets_against_the_selected_tier() {
        let default = tiered(Some(ContextTier::Default), "claude-sonnet-5.5");
        assert_eq!(default.budget.context_window, Some(200_000));
        assert_eq!(default.context_source, WindowSource::Tier);
        assert_eq!(default.context.effective, Some(ContextTier::Default));

        let long = tiered(Some(ContextTier::LongContext), "claude-sonnet-5.5");
        assert_eq!(long.budget.context_window, Some(936_000));
        assert_eq!(long.context.effective, Some(ContextTier::LongContext));
        assert_eq!(long.budget.max_tokens, default.budget.max_tokens, "the output limit is independent");
    }

    /// A single fixed window is reported as such, and asking for a tier it
    /// does not have is refused with a reason — never faked.
    #[test]
    fn a_single_window_model_refuses_long_context_with_a_reason() {
        let r = tiered(Some(ContextTier::LongContext), "gpt-5.3-codex");
        assert_eq!(r.budget.context_window, Some(400_000), "window unchanged");
        assert_eq!(r.context.requested, Some(ContextTier::LongContext));
        assert_eq!(r.context.effective, None);
        assert_eq!(r.context.note, Some(TierNote::TierUnavailable));
        assert!(matches!(r.context.capability, ContextCapability::Single { .. }));

        let no_billing = tiered(Some(ContextTier::Default), "mai-code-2");
        assert_eq!(no_billing.context.note, None, "a single window is the model's default tier");
        assert_eq!(no_billing.context.effective, Some(ContextTier::Default));
        assert_eq!(no_billing.budget.context_window, Some(1_050_000));
        let no_billing_long = tiered(Some(ContextTier::LongContext), "mai-code-2");
        assert_eq!(no_billing_long.context.note, Some(TierNote::SingleWindow));
    }

    /// No live row at all: capability unknown (never fetched, or the fetch
    /// failed) — distinct from "single window", and nothing is changed.
    #[test]
    fn an_unknown_capability_is_reported_and_changes_nothing() {
        let r = resolve_with_tier(
            &catalog(), &LiveLimitMap::new(), &json!({}), "github-copilot", "big",
            Some(ContextTier::LongContext),
        );
        assert_eq!(r.context.capability, ContextCapability::Unknown);
        assert_eq!(r.context.note, Some(TierNote::CapabilityUnknown));
        assert_eq!(r.budget.context_window, Some(200_000), "catalogue window, as before");
        assert_eq!(r.context.effective, None);
    }

    /// The saved per-model preference is what plain `resolve` applies, and an
    /// invalid saved value is ignored like every other malformed setting.
    #[test]
    fn the_saved_preference_drives_plain_resolution() {
        let settings = json!({ "contextTierByModel": {
            "github-copilot/claude-sonnet-5.5": "default",
            "github-copilot/gpt-5.3-codex": "gigantic"
        }});
        assert_eq!(context_tier_from_settings(&settings, "github-copilot", "claude-sonnet-5.5"), Some(ContextTier::Default));
        assert_eq!(context_tier_from_settings(&settings, "github-copilot", "gpt-5.3-codex"), None);
        let r = resolve(&catalog(), &tiered_live(), &settings, "github-copilot", "claude-sonnet-5.5");
        assert_eq!(r.budget.context_window, Some(200_000));
    }
}
