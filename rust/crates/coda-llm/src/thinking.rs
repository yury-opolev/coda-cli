//! Which thinking contract a Claude model speaks: manual or adaptive.
//!
//! Anthropic Messages has two mutually exclusive ways to ask for reasoning:
//!
//! - **Manual** — `thinking: {type: "enabled", budget_tokens: N}`. Older
//!   models (Claude 4.5 and earlier) accept only this and reject adaptive.
//! - **Adaptive** — `thinking: {type: "adaptive"}` with the level in
//!   `output_config.effort`. Newer models (Opus/Sonnet 4.6+, Haiku 5+, every
//!   Claude 5 family) accept this, and most of them reject the manual form
//!   with a 400.
//!
//! The mode is therefore a property of the model, resolved *before*
//! serialization and carried on the request, so every caller (foreground
//! turns, subagents, compaction summaries, side questions) shares one answer.
//!
//! Provenance, strongest first:
//!
//! 1. **Model metadata** — Copilot's `/models` advertises
//!    `capabilities.supports.adaptive_thinking`. An explicit flag wins.
//! 2. **Compatibility table** — the documented Anthropic model table
//!    (<https://platform.claude.com/docs/en/build-with-claude/thinking#configuring-thinking>),
//!    keyed on the model family and version. Used for the direct Anthropic
//!    provider, which advertises nothing, and for Copilot rows without the flag.
//! 3. **Metadata budgets** — a row advertising `min/max_thinking_budget` but no
//!    adaptive flag, for a model the table does not know, is manual.
//! 4. **Legacy default** — anything else keeps the historical manual form, so
//!    an unknown model behaves exactly as before rather than being switched to
//!    a mode older models reject.

/// The thinking request shape a model accepts.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ThinkingMode {
    /// `thinking.type: enabled` with a `budget_tokens` reasoning budget.
    Manual,
    /// `thinking.type: adaptive` with `output_config.effort`.
    Adaptive,
}

impl ThinkingMode {
    pub fn as_str(self) -> &'static str {
        match self {
            ThinkingMode::Manual => "manual",
            ThinkingMode::Adaptive => "adaptive",
        }
    }
}

/// Where a [`ThinkingPolicy`] came from — a fixed, closed set for diagnostics.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ThinkingSource {
    ModelMetadata,
    CompatibilityTable,
    LegacyDefault,
}

impl ThinkingSource {
    pub fn as_str(self) -> &'static str {
        match self {
            ThinkingSource::ModelMetadata => "model_metadata",
            ThinkingSource::CompatibilityTable => "compatibility_table",
            ThinkingSource::LegacyDefault => "legacy_default",
        }
    }
}

/// The resolved thinking contract for one request.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ThinkingPolicy {
    pub mode: ThinkingMode,
    pub source: ThinkingSource,
    /// The largest manual budget the model advertised, when it did.
    pub max_budget: Option<u32>,
}

/// What a model row advertised about thinking. All fields are optional:
/// absence means "not stated", never "unsupported".
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct ThinkingSupport {
    pub adaptive: Option<bool>,
    pub min_budget: Option<u32>,
    pub max_budget: Option<u32>,
}

/// Upper bound accepted for an advertised thinking budget. Metadata is
/// untrusted input; an absurd value is discarded rather than sent.
pub const MAX_ADVERTISED_BUDGET: u32 = 1_000_000;

impl ThinkingSupport {
    /// Builds support from raw advertised values, discarding budgets that are
    /// zero, above [`MAX_ADVERTISED_BUDGET`], or inverted.
    pub fn from_advertised(adaptive: Option<bool>, min: Option<u64>, max: Option<u64>) -> Self {
        let valid = |n: u64| u32::try_from(n).ok().filter(|n| *n > 0 && *n <= MAX_ADVERTISED_BUDGET);
        let (mut min_budget, mut max_budget) = (min.and_then(valid), max.and_then(valid));
        if let (Some(lo), Some(hi)) = (min_budget, max_budget) {
            if lo > hi {
                min_budget = None;
                max_budget = None;
            }
        }
        Self { adaptive, min_budget, max_budget }
    }

    fn advertises_budgets(&self) -> bool {
        self.min_budget.is_some() || self.max_budget.is_some()
    }
}

/// Resolves the thinking policy for `model`, given what its metadata row
/// advertised (if anything). See the module docs for the precedence.
pub fn resolve(model: &str, support: Option<&ThinkingSupport>) -> ThinkingPolicy {
    let max_budget = support.and_then(|s| s.max_budget);
    let policy = |mode, source| ThinkingPolicy { mode, source, max_budget };

    if let Some(adaptive) = support.and_then(|s| s.adaptive) {
        let mode = if adaptive { ThinkingMode::Adaptive } else { ThinkingMode::Manual };
        return policy(mode, ThinkingSource::ModelMetadata);
    }
    if let Some(mode) = compatibility_mode(model) {
        return policy(mode, ThinkingSource::CompatibilityTable);
    }
    if support.is_some_and(ThinkingSupport::advertises_budgets) {
        return policy(ThinkingMode::Manual, ThinkingSource::ModelMetadata);
    }
    policy(ThinkingMode::Manual, ThinkingSource::LegacyDefault)
}

/// The documented thinking mode for a Claude model id, or `None` when the id
/// is not a recognised Claude family.
///
/// Opus and Sonnet 4.6 accept both forms; adaptive is chosen because manual
/// thinking is deprecated there. Every later model rejects the manual form.
pub fn compatibility_mode(model: &str) -> Option<ThinkingMode> {
    let (family, version) = claude_family_version(model)?;
    let adaptive = match family {
        "fable" | "mythos" => true,
        "opus" | "sonnet" => version >= (4, 6),
        "haiku" => version >= (5, 0),
        _ => return None,
    };
    Some(if adaptive { ThinkingMode::Adaptive } else { ThinkingMode::Manual })
}

/// Parses `claude-<family>-<major>[-.]<minor>…` into the family name and a
/// `(major, minor)` version. A trailing date stamp (`-20250929`) is not a
/// minor version. Legacy `claude-3-5-sonnet` ids return the family with the
/// version found after it, which is none, so they parse as `(0, 0)`.
pub(crate) fn claude_family_version(model: &str) -> Option<(&'static str, (u32, u32))> {
    let lower = model.to_ascii_lowercase();
    let rest = lower.strip_prefix("claude-")?;
    let mut parts = rest.split(['-', '.']);
    let family = match parts.next()? {
        "opus" => "opus",
        "sonnet" => "sonnet",
        "haiku" => "haiku",
        "fable" => "fable",
        "mythos" => "mythos",
        _ => return None,
    };
    let small_number = |s: &str| (!s.is_empty() && s.len() <= 2).then(|| s.parse::<u32>().ok()).flatten();
    let major = parts.next().and_then(small_number).unwrap_or(0);
    let minor = parts.next().and_then(small_number).unwrap_or(0);
    Some((family, (major, minor)))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mode(model: &str) -> Option<ThinkingMode> {
        compatibility_mode(model)
    }

    #[test]
    fn adaptive_only_and_dual_mode_models_resolve_to_adaptive() {
        for model in [
            "claude-opus-5.5", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5.5", "claude-sonnet-5",
            "claude-haiku-5.5", "claude-opus-4.8", "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4.6",
            "claude-fable-5-2", "claude-mythos-preview",
        ] {
            assert_eq!(mode(model), Some(ThinkingMode::Adaptive), "{model}");
        }
    }

    #[test]
    fn older_models_keep_manual_thinking() {
        for model in [
            "claude-sonnet-4-5-20250929", "claude-opus-4-5-20251101", "claude-haiku-4-5-20251001",
            "claude-haiku-4.5", "claude-opus-4-1", "claude-sonnet-4", "claude-3-7-sonnet-latest",
        ] {
            assert_ne!(mode(model), Some(ThinkingMode::Adaptive), "{model}");
        }
        assert_eq!(mode("claude-sonnet-4-5-20250929"), Some(ThinkingMode::Manual));
    }

    #[test]
    fn non_claude_models_are_not_in_the_table() {
        assert_eq!(mode("gpt-6-sol"), None);
        assert_eq!(mode("claude-3-7-sonnet-latest"), None);
    }

    #[test]
    fn an_explicit_metadata_flag_wins_over_the_table() {
        let off = ThinkingSupport { adaptive: Some(false), ..Default::default() };
        assert_eq!(resolve("claude-opus-5.5", Some(&off)).mode, ThinkingMode::Manual);
        let on = ThinkingSupport { adaptive: Some(true), ..Default::default() };
        let policy = resolve("claude-haiku-4.5", Some(&on));
        assert_eq!((policy.mode, policy.source), (ThinkingMode::Adaptive, ThinkingSource::ModelMetadata));
    }

    /// Copilot's live row for Claude Sonnet 5 advertises manual budgets *and*
    /// adaptive thinking; Anthropic rejects the manual form there, so the
    /// adaptive flag must decide.
    #[test]
    fn adaptive_flag_beats_advertised_budgets() {
        let row = ThinkingSupport::from_advertised(Some(true), Some(1024), Some(32_000));
        let policy = resolve("claude-sonnet-5", Some(&row));
        assert_eq!(policy.mode, ThinkingMode::Adaptive);
        assert_eq!(policy.max_budget, Some(32_000));
    }

    #[test]
    fn unflagged_rows_use_the_table_then_budgets_then_the_legacy_default() {
        let budgets = ThinkingSupport::from_advertised(None, Some(1024), Some(32_000));
        assert_eq!(resolve("claude-opus-4.8", Some(&budgets)).source, ThinkingSource::CompatibilityTable);
        let unknown = resolve("vendor-model", Some(&budgets));
        assert_eq!((unknown.mode, unknown.source), (ThinkingMode::Manual, ThinkingSource::ModelMetadata));
        let legacy = resolve("vendor-model", None);
        assert_eq!((legacy.mode, legacy.source), (ThinkingMode::Manual, ThinkingSource::LegacyDefault));
    }

    #[test]
    fn untrusted_budgets_are_bounded() {
        let absurd = ThinkingSupport::from_advertised(None, Some(0), Some(u64::MAX));
        assert_eq!((absurd.min_budget, absurd.max_budget), (None, None));
        let inverted = ThinkingSupport::from_advertised(None, Some(5000), Some(1000));
        assert_eq!((inverted.min_budget, inverted.max_budget), (None, None));
    }
}
