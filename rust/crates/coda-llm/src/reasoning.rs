//! Resolves the reasoning-effort capability of a `(provider, model)` pair.
//!
//! Two providers, two very different mechanisms:
//!
//! - **Copilot/OpenAI** models advertise their levels at runtime under
//!   `capabilities.supports.reasoning_effort`, so the answer comes from the
//!   model listing.
//! - **Anthropic** models advertise nothing, so the answer comes from static
//!   rules keyed on the model id.
//!
//! The distinction matters more than it looks. For a Copilot model, *not yet
//! having fetched the model list* is *indeterminate*, not "unsupported" —
//! conflating the two silently drops a user's configured effort level, which
//! is exactly the bug the C# comments warn about in `ResolveStoredLevel`.

/// What a model supports.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReasoningCapability {
    pub supported: bool,
    /// Provider-native level names, lowest to highest.
    pub levels: Vec<String>,
    /// Whether "auto" (no explicit level) is allowed.
    pub supports_auto: bool,
}

impl ReasoningCapability {
    pub fn unsupported() -> Self {
        Self { supported: false, levels: Vec::new(), supports_auto: false }
    }

    fn with_levels(levels: &[&str]) -> Self {
        Self {
            supported: true,
            levels: levels.iter().map(|s| (*s).to_owned()).collect(),
            supports_auto: true,
        }
    }
}

/// Every documented level (Anthropic's effort table): Opus 4.7+, Sonnet 5+,
/// Haiku 5.5+, and the Fable/Mythos families.
const ANTHROPIC_OPUS_LEVELS: &[&str] = &["low", "medium", "high", "xhigh", "max"];
/// Opus 4.6, Sonnet 4.6 and Mythos Preview offer `max` but not `xhigh`.
const ANTHROPIC_MAX_WITHOUT_XHIGH_LEVELS: &[&str] = &["low", "medium", "high", "max"];

/// The Copilot provider id, matched case-insensitively.
pub const COPILOT_PROVIDER_ID: &str = "github-copilot";

/// Resolves capability from static Anthropic rules, keyed on the model family
/// and version so `opus-4-8` and `opus-4.8` agree. Source:
/// <https://platform.claude.com/docs/en/build-with-claude/effort#effort-levels>.
/// Older models (4.5 and earlier) are reported unsupported, as before.
pub fn resolve_anthropic(model: &str) -> ReasoningCapability {
    let Some((family, version)) = crate::thinking::claude_family_version(model) else {
        return ReasoningCapability::unsupported();
    };
    let preview = model.to_ascii_lowercase().contains("preview");
    let levels = match family {
        "mythos" if preview => ANTHROPIC_MAX_WITHOUT_XHIGH_LEVELS,
        "fable" | "mythos" => ANTHROPIC_OPUS_LEVELS,
        "opus" if version >= (4, 7) => ANTHROPIC_OPUS_LEVELS,
        "sonnet" if version >= (5, 0) => ANTHROPIC_OPUS_LEVELS,
        "haiku" if version >= (5, 5) => ANTHROPIC_OPUS_LEVELS,
        "opus" | "sonnet" if version == (4, 6) => ANTHROPIC_MAX_WITHOUT_XHIGH_LEVELS,
        _ => return ReasoningCapability::unsupported(),
    };
    ReasoningCapability::with_levels(levels)
}

/// Resolves capability from levels a Copilot model advertised.
///
/// An empty or absent list means the provider did not declare support.
pub fn resolve_copilot(levels: Option<&[String]>) -> ReasoningCapability {
    match levels {
        Some(levels) if !levels.is_empty() => ReasoningCapability {
            supported: true,
            levels: levels.to_vec(),
            supports_auto: true,
        },
        _ => ReasoningCapability::unsupported(),
    }
}

/// Resolves capability for a `(provider, model)` pair.
///
/// `advertised` is the model's runtime level list where one is known.
pub fn resolve(
    provider_id: &str,
    model: &str,
    advertised: Option<&[String]>,
) -> ReasoningCapability {    if provider_id.eq_ignore_ascii_case(COPILOT_PROVIDER_ID) {
        resolve_copilot(advertised)
    } else {
        resolve_anthropic(model)
    }
}

/// The level actually sent for a request, or `None` for auto/unsupported.
///
/// Requesting `max` on a model that stops at `high` clamps rather than failing,
/// matching the C# `ResolveAppliedLevel`: a user who set `max` globally should
/// not have every Sonnet request rejected.
pub fn resolve_applied_level(
    capability: &ReasoningCapability,
    requested: Option<&str>,
) -> Option<String> {
    let requested = requested?;
    if !capability.supported || requested.eq_ignore_ascii_case("auto") {
        return None;
    }

    let wanted = requested.to_ascii_lowercase();
    if capability.levels.iter().any(|l| l.eq_ignore_ascii_case(&wanted)) {
        return Some(wanted);
    }
    if wanted == "max" && capability.levels.iter().any(|l| l.eq_ignore_ascii_case("high")) {
        return Some("high".to_owned());
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn opus_models_expose_the_full_level_set() {
        for model in ["claude-opus-4.8", "claude-opus-4-7", "claude-opus-5", "claude-opus-5.5", "claude-sonnet-5.5",
            "claude-sonnet-5", "claude-haiku-5.5", "claude-fable-5-2"] {
            let cap = resolve_anthropic(model);
            assert!(cap.supported, "{model} should support reasoning");
            assert_eq!(cap.levels, ANTHROPIC_OPUS_LEVELS, "{model}");
            assert!(cap.supports_auto);
        }
    }

    /// Anthropic's effort table: Opus 4.6 and Sonnet 4.6 offer `max` but not
    /// `xhigh`, so `xhigh` must not be advertised (it would be a 400).
    #[test]
    fn four_six_models_offer_max_but_not_xhigh() {
        for model in ["claude-sonnet-4.6", "claude-opus-4-6"] {
            let cap = resolve_anthropic(model);
            assert!(cap.supported);
            assert_eq!(cap.levels, ANTHROPIC_MAX_WITHOUT_XHIGH_LEVELS, "{model}");
            assert_eq!(resolve_applied_level(&cap, Some("max")).as_deref(), Some("max"));
            assert_eq!(resolve_applied_level(&cap, Some("xhigh")), None, "{model}");
        }
    }

    #[test]
    fn an_unknown_anthropic_model_is_unsupported() {
        assert!(!resolve_anthropic("claude-haiku-4.5").supported);
        assert!(!resolve_anthropic("claude-sonnet-4-5-20250929").supported);
    }

    #[test]
    fn gateway_prefixed_ids_keep_their_levels() {
        assert_eq!(resolve_anthropic("anthropic/claude-opus-4-8").levels, ANTHROPIC_OPUS_LEVELS);
        assert_eq!(resolve_anthropic("us.anthropic.claude-sonnet-4-6-v1:0").levels, ANTHROPIC_MAX_WITHOUT_XHIGH_LEVELS);
    }

    /// For Copilot, having no advertised levels means "not declared". The
    /// caller must not treat that as a positive statement of support.
    #[test]
    fn a_copilot_model_without_advertised_levels_is_unsupported() {
        assert!(!resolve_copilot(None).supported);
        assert!(!resolve_copilot(Some(&[])).supported);
    }

    #[test]
    fn a_copilot_model_reports_exactly_what_it_advertised() {
        let levels = vec!["low".to_owned(), "high".to_owned()];
        let cap = resolve_copilot(Some(&levels));
        assert!(cap.supported);
        assert_eq!(cap.levels, levels, "levels pass through verbatim");
    }

    /// The provider decides which mechanism applies: a Copilot-hosted Claude
    /// model must use the advertised list, not the static Anthropic rules.
    #[test]
    fn the_provider_selects_the_resolution_mechanism() {
        let advertised = vec!["low".to_owned()];
        let via_copilot = resolve(COPILOT_PROVIDER_ID, "claude-opus-4.8", Some(&advertised));
        assert_eq!(via_copilot.levels, advertised);

        let via_anthropic = resolve("anthropic", "claude-opus-4.8", Some(&advertised));
        assert_eq!(via_anthropic.levels, ANTHROPIC_OPUS_LEVELS);
    }

    #[test]
    fn auto_and_unsupported_resolve_to_no_level() {
        let cap = resolve_anthropic("claude-opus-4.8");
        assert_eq!(resolve_applied_level(&cap, Some("auto")), None);
        assert_eq!(resolve_applied_level(&cap, None), None);
        assert_eq!(resolve_applied_level(&ReasoningCapability::unsupported(), Some("high")), None);
    }

    /// `max` on a model that stops at `high` clamps rather than failing, so a
    /// globally configured `max` does not break every request on that model.
    #[test]
    fn max_clamps_to_high_where_max_is_unavailable() {
        let levels = vec!["low".to_owned(), "medium".to_owned(), "high".to_owned()];
        let high_only = resolve_copilot(Some(&levels));
        assert_eq!(resolve_applied_level(&high_only, Some("max")).as_deref(), Some("high"));

        let opus = resolve_anthropic("claude-opus-4.8");
        assert_eq!(resolve_applied_level(&opus, Some("max")).as_deref(), Some("max"));
    }

    #[test]
    fn a_level_the_model_does_not_offer_resolves_to_none() {
        let levels = vec!["low".to_owned()];
        let cap = resolve_copilot(Some(&levels));
        assert_eq!(resolve_applied_level(&cap, Some("xhigh")), None);
    }

    /// xhigh is a valid Anthropic level for Opus 4.8 and Sonnet 5; it must not
    /// be rejected as unknown.
    #[test]
    fn xhigh_resolves_on_anthropic_opus_and_sonnet() {
        for model in ["claude-opus-4.8", "claude-sonnet-5"] {
            let cap = resolve_anthropic(model);
            assert_eq!(
                resolve_applied_level(&cap, Some("xhigh")).as_deref(),
                Some("xhigh"),
                "{model} must accept xhigh"
            );
        }
    }

    /// A Copilot model that only advertises up to "high" must reject "xhigh".
    #[test]
    fn xhigh_rejected_when_copilot_model_stops_at_high() {
        let levels = vec!["low".to_owned(), "medium".to_owned(), "high".to_owned()];
        let cap = resolve_copilot(Some(&levels));
        assert_eq!(
            resolve_applied_level(&cap, Some("xhigh")),
            None,
            "xhigh must be dropped when the model stops at high"
        );
    }

    #[test]
    fn level_matching_is_case_insensitive() {
        let cap = resolve_anthropic("claude-opus-4.8");
        assert_eq!(resolve_applied_level(&cap, Some("HIGH")).as_deref(), Some("high"));
    }
}
