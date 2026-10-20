//! Resolves the output budget (`max_tokens`) for a model from the user's
//! settings, the provider's live model list and the models.dev catalogue.
//!
//! One function serves the main loop, subagents and the compaction reserve,
//! so the three can never disagree about how long a reply may be.

use std::collections::HashMap;

use coda_agent::{resolve_max_output_tokens, ModelBudget, OutputBudgetSource};
use serde_json::Value;

use crate::catalog::ModelCatalog;

/// Limits a provider's live `/models` list reported for one model.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct LiveLimits {
    pub context: Option<u32>,
    pub output: Option<u32>,
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

/// The budget to request for `model` on `provider`.
///
/// The model's own limit comes from the live list when it reported one (a
/// provider's proxy may cap below what models.dev records), else the
/// catalogue. The context window follows the same preference.
pub fn resolve(
    catalog: &ModelCatalog,
    live: &LiveLimitMap,
    settings: &Value,
    provider: &str,
    model: &str,
) -> ResolvedBudget {
    // Copilot serves `claude-opus-4-8` as `claude-opus-4.8`; its live list and
    // catalogue entry use the dotted id.
    let normalized = coda_llm::copilot::models::normalize_model_id(model);
    let live = live
        .get(&live_key(provider, model))
        .or_else(|| live.get(&live_key(provider, &normalized)))
        .copied()
        .unwrap_or_default();
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

    ResolvedBudget {
        budget: ModelBudget {
            max_tokens: resolve_max_output_tokens(model_output_limit, user_override),
            context_window: live.context.map(|n| n as usize).or(catalog_context),
        },
        source,
        model_output_limit,
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
            LiveLimits { context: Some(168_000), output: Some(16_000) },
        );
        let r = resolve(&catalog(), &live, &json!({}), "github-copilot", "big");
        assert_eq!(r.budget, ModelBudget { max_tokens: 16_000, context_window: Some(168_000) });
        assert_eq!(r.source, OutputBudgetSource::Live);
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
}
