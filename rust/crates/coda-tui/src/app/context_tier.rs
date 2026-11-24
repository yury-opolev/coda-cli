//! `/context tier` and `--context`: context-tier RPCs and persistence.
//!
//! The engine owns capability and validation (`model/contextCapability`,
//! `session/setContextTier`); this module only asks, reports what the engine
//! said — requested and effective apart — and saves the per-model preference
//! the engine reads back (`contextTierByModel`).

use coda_proto::messages::{method, ContextCapabilityResult};

use super::App;
use crate::transcript::NoticeLevel;

/// Usage for `/context`.
pub(super) const CONTEXT_USAGE: &str =
    "Usage: /context [tier [default|long_context|auto] [--session]]";

/// The report for `/context tier` and for a successful change.
pub(super) fn format_capability(cap: &ContextCapabilityResult) -> String {
    let mut out = String::from("Context tier\n");
    if let Some(model) = &cap.model {
        out.push_str(&format!("  model      {model}\n"));
    }
    match cap.state.as_str() {
        "tiered" => {
            out.push_str("  tiers      ");
            let tiers: Vec<String> = cap
                .tiers
                .iter()
                .map(|t| {
                    let preferred = if cap.preferred_tier.as_deref() == Some(t.tier.as_str()) {
                        " (provider preferred)"
                    } else {
                        ""
                    };
                    format!("{} {} tokens{preferred}", t.tier, thousands(t.context_window))
                })
                .collect();
            out.push_str(&tiers.join(", "));
            out.push('\n');
        }
        "single" => out.push_str("  tiers      one fixed window (no tier to choose)\n"),
        _ => out.push_str(
            "  tiers      unknown — the provider has not reported this model's tiers \
             (try /models --refresh)\n",
        ),
    }
    let requested = match (&cap.requested, cap.requested_source.as_deref()) {
        (Some(tier), Some(source)) => format!("{tier} ({source})"),
        (Some(tier), None) => tier.clone(),
        (None, _) => "unset (provider-reported window)".to_owned(),
    };
    out.push_str(&format!("  requested  {requested}\n"));
    out.push_str(&format!(
        "  effective  {}\n",
        cap.effective.as_deref().unwrap_or("(not a selectable tier)")
    ));
    match cap.effective_window {
        Some(window) => out.push_str(&format!(
            "  window     {} tokens ({})\n",
            thousands(window),
            cap.window_source
        )),
        None => out.push_str("  window     unknown\n"),
    }
    if let Some(note) = &cap.note {
        out.push_str(&format!("  note       {note}\n"));
    }
    if let Some(disclosure) = &cap.disclosure {
        out.push_str(&format!("  cost       {disclosure}\n"));
    }
    out.trim_end().to_owned()
}

fn thousands(n: u64) -> String {
    let digits = n.to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i) % 3 == 0 {
            out.push(',');
        }
        out.push(c);
    }
    out
}

/// A failed call explained; an engine that predates tiers says so.
fn describe_error(error: &impl std::fmt::Display) -> String {
    let text = error.to_string();
    if text.contains("contextCapability") || text.contains("setContextTier") || text.contains("-32601") {
        "this engine does not support context tiers; upgrade the engine".to_owned()
    } else {
        text
    }
}

impl App {
    /// `/context` with its optional `tier` subcommand.
    pub(super) async fn cmd_context(&mut self, words: &[&str]) {
        match words.first() {
            None => self.output(self.context_text()),
            Some(&"tier") => self.cmd_context_tier(&words[1..]).await,
            Some(_) => self.notice(CONTEXT_USAGE, NoticeLevel::Warning),
        }
    }

    async fn cmd_context_tier(&mut self, args: &[&str]) {
        if !self.require_engine("/context tier") {
            return;
        }
        let session_only = args.contains(&"--session");
        let tier = args.iter().copied().find(|a| !a.starts_with("--"));
        let capability = match self
            .fetch::<ContextCapabilityResult>(method::CONTEXT_CAPABILITY, Some(serde_json::json!({})))
            .await
        {
            Ok(capability) => capability,
            Err(error) => {
                self.notice(
                    format!("Could not read context tiers: {}", describe_error(&error)),
                    NoticeLevel::Warning,
                );
                return;
            }
        };
        let Some(tier) = tier else {
            self.output(format_capability(&capability));
            return;
        };
        let (Some(provider), Some(model)) = (capability.provider_id.clone(), capability.model.clone()) else {
            self.notice("The engine did not identify the active model.", NoticeLevel::Error);
            return;
        };
        let result = match self
            .fetch::<ContextCapabilityResult>(
                method::SET_CONTEXT_TIER,
                Some(serde_json::json!({
                    "tier": tier,
                    "expectedModel": model,
                    "expectedProvider": provider,
                })),
            )
            .await
        {
            Ok(result) => result,
            Err(error) => {
                self.notice(
                    format!("Could not set the context tier: {}", describe_error(&error)),
                    NoticeLevel::Error,
                );
                return;
            }
        };
        if !result.ok {
            self.notice(
                format!(
                    "Context tier not changed: {}",
                    result.note.as_deref().unwrap_or("the engine refused the request")
                ),
                NoticeLevel::Warning,
            );
            return;
        }
        self.output(format_capability(&result));
        if session_only {
            self.notice("Context tier set for this session only.", NoticeLevel::Info);
            return;
        }
        // `auto` resets: the saved preference is removed, not set to a value.
        let saved_tier = result.requested.clone();
        let saved = self
            .persist_engine_default(move |settings| {
                settings.set_context_tier_for(&provider, &model, saved_tier.as_deref())
            })
            .await;
        let label = result.requested.as_deref().unwrap_or("auto (provider default)");
        match saved {
            super::settings::Saved::Ok => {
                self.notice(format!("Context tier {label} saved for this model."), NoticeLevel::Info)
            }
            super::settings::Saved::Refused => {
                let note = self.not_saved_remotely();
                self.notice(format!("Context tier {label} for this session.{note}"), NoticeLevel::Info)
            }
            super::settings::Saved::Failed(error) => self.notice(
                format!("Context tier {label} for this session; could not save: {error}"),
                NoticeLevel::Warning,
            ),
        }
    }

    /// `--context <tier>`: a session-only launch override, refused loudly
    /// when the active model cannot honour it.
    pub async fn apply_cli_context(&mut self, tier: &str) -> anyhow::Result<()> {
        let result = self
            .fetch::<ContextCapabilityResult>(
                method::SET_CONTEXT_TIER,
                Some(serde_json::json!({ "tier": tier })),
            )
            .await
            .map_err(|error| anyhow::anyhow!("could not apply --context: {}", describe_error(&error)))?;
        if !result.ok {
            anyhow::bail!(
                "--context {tier} was not applied: {}",
                result.note.as_deref().unwrap_or("unsupported tier")
            );
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{Paths, Settings};

    fn capability(value: serde_json::Value) -> ContextCapabilityResult {
        serde_json::from_value(value).unwrap()
    }

    #[test]
    fn the_report_keeps_requested_effective_and_window_apart() {
        let text = format_capability(&capability(serde_json::json!({
            "ok": true, "state": "tiered", "model": "claude-sonnet-5.5",
            "tiers": [
                { "tier": "default", "contextWindow": 200000 },
                { "tier": "long_context", "contextWindow": 936000 }
            ],
            "preferredTier": "long_context",
            "requested": "default", "requestedSource": "saved",
            "effective": "default", "effectiveWindow": 200000, "windowSource": "tier",
            "disclosure": "Requests whose input exceeds 200000 tokens are billed at the long-context rate."
        })));
        assert!(text.contains("default 200,000 tokens, long_context 936,000 tokens (provider preferred)"), "{text}");
        assert!(text.contains("requested  default (saved)"));
        assert!(text.contains("effective  default"));
        assert!(text.contains("window     200,000 tokens (tier)"));
        assert!(text.contains("cost       Requests whose input"));
    }

    #[test]
    fn single_and_unknown_states_are_explained_not_faked() {
        let single = format_capability(&capability(serde_json::json!({
            "ok": true, "state": "single", "effectiveWindow": 400000, "windowSource": "live"
        })));
        assert!(single.contains("one fixed window"));
        assert!(single.contains("requested  unset"));

        let unknown = format_capability(&capability(serde_json::json!({
            "ok": true, "state": "unknown", "requested": "long_context", "requestedSource": "saved",
            "windowSource": "catalog", "effectiveWindow": 200000,
            "note": "long_context cannot be confirmed"
        })));
        assert!(unknown.contains("unknown"));
        assert!(unknown.contains("/models --refresh"));
        assert!(unknown.contains("effective  (not a selectable tier)"));
        assert!(unknown.contains("note       long_context cannot be confirmed"));
    }

    #[test]
    fn saving_and_resetting_a_tier_touches_only_that_model() {
        let dir = tempfile::tempdir().unwrap();
        let paths = Paths { user_root: dir.path().into(), project_root: dir.path().into() };
        let mut settings = Settings::empty_at(paths.settings());
        settings.set_context_tier_for("p", "other", Some("long_context"));
        settings.set_context_tier_for("p", "model", Some("default"));
        settings.save().unwrap();
        let mut settings = Settings::load(&paths).unwrap();
        assert_eq!(settings.context_tier_for("p", "model"), Some("default"));
        settings.set_context_tier_for("p", "model", None);
        settings.save().unwrap();
        let saved = Settings::load(&paths).unwrap();
        assert_eq!(saved.context_tier_for("p", "model"), None);
        assert_eq!(saved.context_tier_for("p", "other"), Some("long_context"));
    }

    #[test]
    fn an_engine_without_tiers_is_named_as_such() {
        assert!(describe_error(&"method not found: model/contextCapability").contains("upgrade the engine"));
        assert_eq!(describe_error(&"socket closed"), "socket closed");
    }
}
