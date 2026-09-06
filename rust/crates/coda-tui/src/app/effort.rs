//! Effort RPCs and persistence; the picker itself performs no I/O.

use coda_proto::messages::{self, method};
use super::App;
use crate::config::{ConfigError, Paths, Settings};
use crate::surface::effort::{EffortPickerSurface, PickerCapability};
use crate::transcript::NoticeLevel;

fn save_effort(paths: &Paths, provider: &str, model: &str, current: Option<&str>) -> Result<(), ConfigError> {
    let mut settings = Settings::load(paths)?;
    settings.set_effort_for(provider, model, current);
    settings.save()
}

fn identity(capability: &messages::ReasoningCapabilityResult) -> Option<(String, String)> {
    Some((capability.provider_id.clone()?, capability.model.clone()?))
        .filter(|(provider, model)| !provider.is_empty() && !model.is_empty())
}

impl App {
    pub(super) async fn adjust_model_effort(&mut self, model: String, direction: i32) {
        let result = match self.fetch::<messages::ModelEffortResult>(
            method::ADJUST_MODEL_EFFORT,
            Some(serde_json::json!({
                "model": model,
                "direction": direction,
                "expectedProvider": self.connected_provider,
            })),
        ).await {
            Ok(result) => result,
            Err(error) => {
                self.notice(format!("Could not adjust effort: {error}"), NoticeLevel::Error);
                return;
            }
        };
        if !result.ok {
            self.reload_browser_with_status(format!("Effort unchanged: {}", result.note)).await;
            return;
        }
        if result.model != model || result.provider_id.is_empty() {
            self.notice("The engine returned an unexpected model identity; effort was not saved.", NoticeLevel::Error);
            return;
        }
        if result.active {
            self.remember_effort(result.current.clone());
        }
        let label = result.current.clone().unwrap_or_else(|| "auto".into());
        let paths = self.paths.clone();
        let saved = tokio::task::spawn_blocking(move ||
            save_effort(&paths, &result.provider_id, &result.model, result.current.as_deref())
        ).await;
        let status = match saved {
            Ok(Ok(())) => format!("{model}: effort {label} saved"),
            Ok(Err(error)) => {
                let status = format!("{model}: effort {label} is session-only; could not save: {error}");
                self.notice(status.clone(), NoticeLevel::Warning);
                status
            }
            Err(error) => {
                let status = format!("{model}: effort {label} is session-only; save failed: {error}");
                self.notice(status.clone(), NoticeLevel::Warning);
                status
            }
        };
        self.reload_browser_with_status(status).await;
    }

    fn remember_effort(&mut self, current: Option<String>) {
        let label = current.unwrap_or_else(|| "auto".into());
        self.session_effort = Some(label.clone());
        self.state.effort = Some(label);
        self.dirty = true;
    }

    pub(super) async fn refresh_effort(&mut self) {
        match self.fetch::<messages::ReasoningCapabilityResult>(
            method::REASONING_CAPABILITY, Some(serde_json::json!({})),
        ).await {
            Ok(capability) => self.remember_effort(capability.current),
            Err(error) => {
                self.session_effort = None;
                self.state.effort = None;
                self.notice(format!("Could not read reasoning effort: {error}"), NoticeLevel::Warning);
            }
        }
    }

    pub(super) async fn apply_set_effort(
        &mut self, effort: String, persist: bool, for_model: (String, String),
    ) {
        let result = match self.fetch::<messages::SetEffortResult>(
            method::SET_EFFORT,
            Some(serde_json::json!({
                "effort": effort,
                "expectedProvider": for_model.0,
                "expectedModel": for_model.1,
            })),
        ).await {
            Ok(result) => result,
            Err(error) => {
                self.notice(format!("Could not set effort: {error}"), NoticeLevel::Error);
                return;
            }
        };
        if !result.ok {
            self.notice(format!("Effort not applied: {}",
                result.note.as_deref().unwrap_or("engine rejected the request")), NoticeLevel::Warning);
            return;
        }
        self.remember_effort(result.current.clone());
        if self.surfaces.top().is_some_and(|surface| surface.as_any().is::<EffortPickerSurface>()) {
            self.surfaces.pop();
        }
        let label = result.current.as_deref().unwrap_or("auto").to_owned();
        if let Some(note) = result.note.filter(|note| !note.is_empty()) {
            self.notice(note, NoticeLevel::Info);
        }
        if !persist {
            self.notice(format!("Effort set to {label} for this session."), NoticeLevel::Info);
            return;
        }
        let paths = self.paths.clone();
        let saved = tokio::task::spawn_blocking(move ||
            save_effort(&paths, &for_model.0, &for_model.1, result.current.as_deref())
        ).await;
        match saved {
            Ok(Ok(())) => self.notice(format!("Effort set to {label} and saved."), NoticeLevel::Info),
            Ok(Err(error)) => self.notice(
                format!("Effort set to {label} for this session; could not save: {error}"),
                NoticeLevel::Warning,
            ),
            Err(error) => self.notice(
                format!("Effort set to {label} for this session; save failed: {error}"),
                NoticeLevel::Warning,
            ),
        }
    }

    pub(super) async fn open_effort_picker(&mut self, explicit: Option<String>) {
        self.open_effort_for_model(explicit, None).await;
    }

    pub(super) async fn open_effort_for_model(&mut self, explicit: Option<String>, expected_model: Option<&str>) {
        let capability = match self.fetch::<messages::ReasoningCapabilityResult>(
            method::REASONING_CAPABILITY, Some(serde_json::json!({})),
        ).await {
            Ok(capability) => capability,
            Err(error) => {
                self.notice(format!("Could not load effort controls: {error}"), NoticeLevel::Error);
                return;
            }
        };
        let Some(for_model) = identity(&capability) else {
            self.notice("The engine did not identify the model for effort controls.", NoticeLevel::Error);
            return;
        };
        if expected_model.is_some_and(|expected| expected != for_model.1) {
            self.notice("The selected model was not activated; effort was not changed.", NoticeLevel::Warning);
            return;
        }
        self.remember_effort(capability.current.clone());
        if let Some(level) = explicit {
            self.apply_set_effort(level, true, for_model).await;
            return;
        }
        let supported = if capability.indeterminate {
            PickerCapability::Indeterminate
        } else if capability.supported {
            PickerCapability::Levels(capability.levels)
        } else {
            PickerCapability::Unsupported
        };
        let picker = EffortPickerSurface::new(
            capability.current.as_deref(), supported, capability.supports_auto, for_model,
        );
        if !self.surfaces.push(Box::new(picker)) {
            self.notice("Answer the open prompt first.", NoticeLevel::Warning);
        }
        self.dirty = true;
    }

    pub async fn apply_cli_effort(&mut self, level: &str) -> anyhow::Result<()> {
        let result = self.fetch::<messages::SetEffortResult>(
            method::SET_EFFORT, Some(serde_json::json!({ "effort": level })),
        ).await.map_err(|error| anyhow::anyhow!("could not apply --effort: {error}"))?;
        if !result.ok {
            anyhow::bail!("--effort {level} was not applied: {}",
                result.note.as_deref().unwrap_or("unsupported level"));
        }
        self.remember_effort(result.current);
        if let Some(note) = result.note.filter(|note| !note.is_empty()) {
            self.notice(note, NoticeLevel::Info);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn effort_identity_uses_canonical_model_not_display_name() {
        let capability: messages::ReasoningCapabilityResult = serde_json::from_value(serde_json::json!({
            "providerId": "github-copilot", "model": "claude-opus-5", "current": "xhigh",
        })).unwrap();
        assert_eq!(identity(&capability), Some(("github-copilot".into(), "claude-opus-5".into())));
        assert!(identity(&messages::ReasoningCapabilityResult::default()).is_none());
    }

    #[test]
    fn effort_saving_preserves_other_settings_and_auto_clears_only_this_model() {
        let dir = tempfile::tempdir().unwrap();
        let paths = Paths { user_root: dir.path().into(), project_root: dir.path().into() };
        let mut settings = Settings::empty_at(paths.settings());
        settings.set_effort_for("p", "other", Some("low"));
        settings.save().unwrap();
        save_effort(&paths, "p", "model", Some("xhigh")).unwrap();
        assert_eq!(Settings::load(&paths).unwrap().effort_for("p", "model"), Some("xhigh"));
        save_effort(&paths, "p", "model", None).unwrap();
        let saved = Settings::load(&paths).unwrap();
        assert_eq!(saved.effort_for("p", "model"), None);
        assert_eq!(saved.effort_for("p", "other"), Some("low"));
    }

    #[test]
    fn effort_save_never_overwrites_corrupt_settings() {
        let dir = tempfile::tempdir().unwrap();
        let paths = Paths { user_root: dir.path().into(), project_root: dir.path().into() };
        std::fs::write(paths.settings(), "{broken").unwrap();
        assert!(save_effort(&paths, "p", "m", Some("high")).is_err());
        assert_eq!(std::fs::read_to_string(paths.settings()).unwrap(), "{broken");
    }
}
