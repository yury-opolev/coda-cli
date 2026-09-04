//! Model metadata from a models.dev snapshot: display names, context limits
//! and prices.
//!
//! Ports the C# `ModelCatalog`. Prices are data, not code — a hardcoded table
//! is wrong the moment a provider changes one, and wrong prices are worse than
//! none because they get believed.
//!
//! Layered the same way the C# layers it: an on-disk cache written by a live
//! refresh, then the bundled snapshot. The bundled copy is the same file the
//! C# ships, so both engines quote the same numbers.

use std::collections::HashMap;
use std::path::PathBuf;

use serde::Deserialize;

/// The snapshot shipped with the binary, so pricing works with no network and
/// no cache.
const BUNDLED: &str = include_str!("../resources/models-snapshot.json");

/// What a model costs, in US dollars per million tokens.
#[derive(Debug, Clone, Copy, PartialEq, Deserialize)]
pub struct ModelCost {
    #[serde(default)]
    pub input: f64,
    #[serde(default)]
    pub output: f64,
    #[serde(default)]
    pub cache_read: f64,
    #[serde(default)]
    pub cache_write: f64,
}

#[derive(Debug, Clone, Copy, Deserialize)]
struct Limit {
    #[serde(default)]
    context: Option<i64>,
}

#[derive(Debug, Clone, Deserialize)]
struct Entry {
    #[serde(default)]
    name: Option<String>,
    #[serde(default)]
    limit: Option<Limit>,
    #[serde(default)]
    cost: Option<ModelCost>,
}

#[derive(Debug, Clone, Deserialize)]
struct Provider {
    #[serde(default)]
    models: HashMap<String, Entry>,
}

/// One model as the catalogue describes it.
#[derive(Debug, Clone, PartialEq)]
pub struct CatalogModel {
    pub id: String,
    pub display_name: Option<String>,
    pub context_limit: Option<i64>,
    pub cost: Option<ModelCost>,
}

/// Model metadata, keyed by provider then model id.
#[derive(Debug, Clone, Default)]
pub struct ModelCatalog {
    providers: HashMap<String, HashMap<String, CatalogModel>>,
}

impl ModelCatalog {
    /// Parses a models.dev document, ignoring anything malformed.
    ///
    /// A snapshot that has grown a field this build does not know about must
    /// not cost the user their model list, so unknown keys are skipped rather
    /// than failing the parse.
    pub fn parse(json: &str) -> Self {
        let raw: HashMap<String, Provider> = serde_json::from_str(json).unwrap_or_default();
        let providers = raw
            .into_iter()
            .map(|(provider, entry)| {
                let models = entry
                    .models
                    .into_iter()
                    .map(|(id, model)| {
                        let described = CatalogModel {
                            display_name: model.name,
                            context_limit: model.limit.and_then(|l| l.context),
                            cost: model.cost,
                            id: id.clone(),
                        };
                        (id, described)
                    })
                    .collect();
                (provider, models)
            })
            .collect();
        Self { providers }
    }

    /// The catalogue to use: the refreshed cache if there is one, else the
    /// snapshot shipped with the binary.
    pub fn load() -> Self {
        if let Some(cached) = cache_path().and_then(|p| std::fs::read_to_string(p).ok()) {
            let catalog = Self::parse(&cached);
            // A cache that parsed to nothing is a corrupt or truncated write,
            // not an empty catalogue; fall through rather than reporting that
            // no models exist.
            if !catalog.is_empty() {
                return catalog;
            }
        }
        Self::parse(BUNDLED)
    }

    pub fn is_empty(&self) -> bool {
        self.providers.values().all(HashMap::is_empty)
    }

    /// Every model a provider offers.
    pub fn models_for(&self, provider: &str) -> Vec<CatalogModel> {
        let mut models: Vec<CatalogModel> = self
            .providers
            .get(provider)
            .map(|m| m.values().cloned().collect())
            .unwrap_or_default();
        // Stable order: a model list that reshuffles between calls makes the
        // browser jump under the user's cursor.
        models.sort_by(|a, b| a.id.cmp(&b.id));
        models
    }

    /// One model, searched across providers when the provider is unknown.
    ///
    /// Falling back matters because the id the engine reports and the provider
    /// it connected with do not always agree — a Copilot-hosted Claude model
    /// keeps its Anthropic id.
    pub fn find(&self, provider: Option<&str>, id: &str) -> Option<&CatalogModel> {
        if let Some(found) = provider.and_then(|p| self.providers.get(p)?.get(id)) {
            return Some(found);
        }
        self.providers.values().find_map(|models| models.get(id))
    }
}

/// Where a live refresh leaves its copy, matching the C# path exactly so the
/// two engines share one cache.
fn cache_path() -> Option<PathBuf> {
    directories::BaseDirs::new()
        .map(|dirs| dirs.home_dir().join(".coda").join("cache").join("models.json"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_bundled_snapshot_parses() {
        let catalog = ModelCatalog::parse(BUNDLED);
        assert!(!catalog.is_empty(), "the shipped snapshot described nothing");
        assert!(
            !catalog.models_for("anthropic").is_empty(),
            "no anthropic models"
        );
        assert!(
            !catalog.models_for("github-copilot").is_empty(),
            "no copilot models"
        );
    }

    #[test]
    fn a_known_model_carries_its_price() {
        // The number that makes a cost estimate possible at all.
        let catalog = ModelCatalog::parse(BUNDLED);
        let model = catalog
            .find(Some("anthropic"), "claude-opus-4-5")
            .expect("claude-opus-4-5 is in the snapshot");
        let cost = model.cost.expect("no price");
        assert!(cost.input > 0.0, "input price missing");
        assert!(
            cost.output > cost.input,
            "output should cost more than input: {cost:?}"
        );
        assert_eq!(model.context_limit, Some(200_000));
    }

    #[test]
    fn a_model_is_found_without_naming_its_provider() {
        // The id the engine reports and the provider it connected with do not
        // always agree: a Copilot-hosted Claude model keeps its Anthropic id.
        let catalog = ModelCatalog::parse(BUNDLED);
        assert!(catalog.find(None, "claude-opus-4-5").is_some());
    }

    #[test]
    fn an_unknown_model_has_no_price_rather_than_a_wrong_one() {
        let catalog = ModelCatalog::parse(BUNDLED);
        assert!(catalog.find(None, "no-such-model").is_none());
    }

    #[test]
    fn rubbish_parses_to_an_empty_catalogue_rather_than_panicking() {
        assert!(ModelCatalog::parse("not json").is_empty());
        assert!(ModelCatalog::parse("").is_empty());
        assert!(ModelCatalog::parse("[]").is_empty());
    }

    #[test]
    fn an_entry_missing_its_price_still_lists() {
        // A model with no cost block must still appear, without one — losing
        // it from the list would be a worse failure than lacking a price.
        let catalog = ModelCatalog::parse(
            r#"{"acme":{"models":{"cheap":{"name":"Cheap","limit":{"context":1000}}}}}"#,
        );
        let model = catalog.find(Some("acme"), "cheap").expect("model dropped");
        assert_eq!(model.display_name.as_deref(), Some("Cheap"));
        assert_eq!(model.context_limit, Some(1000));
        assert!(model.cost.is_none());
    }

    #[test]
    fn the_model_list_is_ordered_the_same_way_every_time() {
        let catalog = ModelCatalog::parse(BUNDLED);
        let first = catalog.models_for("anthropic");
        let second = catalog.models_for("anthropic");
        assert_eq!(first, second, "the order changed between calls");
    }
}
