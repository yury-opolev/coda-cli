//! Slash commands.
//!
//! Dispatch lives here; the handlers live in siblings grouped by subject, so a
//! change to session handling does not scroll past every configuration command
//! on its way. Before this split all nineteen sat in the event loop's file.

mod config;
mod plugins;
mod session;

use coda_proto::messages::{self, method};
use serde_json::Value;

use super::App;
use crate::commands::{self, Scope};
use crate::state::UiEvent;
use crate::surface::browser::BrowserKind;
use crate::transcript::NoticeLevel;

impl App {
    pub(super) async fn run_command(&mut self, invocation: commands::Invocation) {
        let Some(spec) = commands::lookup(&invocation.name) else {
            self.notice(
                format!("Unknown command: /{}. Try /help.", invocation.name),
                NoticeLevel::Warning,
            );
            return;
        };

        match spec.name {
            "help" => self.output(commands::help(invocation.first())),
            "clear" => {
                self.staged_images.clear();
                self.apply(UiEvent::Cleared);
            }
            "exit" => self.state.should_quit = true,
            "interrupt" => self.interrupt(),
            "theme" => self.set_theme(invocation.first()),
            "tools" => match commands::parse_display_mode(invocation.first()) {
                Ok(mode) => {
                    self.apply(UiEvent::DisplayModeChanged(mode));
                    self.notice(
                        format!("Tool display: {}", mode.as_str()),
                        NoticeLevel::Info,
                    );
                }
                Err(error) => self.notice(error, NoticeLevel::Warning),
            },
            "status" => self.output(self.status_text()),
            "context" => self.output(self.context_text()),
            "cost" => self.output(self.cost_text()),
            // Bare invocations open a browser; arguments keep the text path.
            "model" | "models" if invocation.args.is_empty() => {
                self.open_browser(BrowserKind::Models).await
            }
            "schedule" if invocation.args.is_empty() => {
                self.open_browser(BrowserKind::Schedules).await
            }
            "skills" => self.open_browser(BrowserKind::Skills).await,
            "plugins" => self.open_browser(BrowserKind::Plugins).await,
            "hooks" => self.open_browser(BrowserKind::Hooks).await,
            "settings" => self.open_settings_form(),
            "mcp" if invocation.args.is_empty() => self.open_browser(BrowserKind::Mcp).await,
            "tasks" => self.open_browser(BrowserKind::Tasks).await,
            "version" => self.output(format!(
                "coda-tui {} (Rust front-end)",
                env!("CARGO_PKG_VERSION")
            )),
            "doctor" => self.output(self.doctor_text()),
            "init" => self.cmd_init().await,
            "memory" => self.cmd_memory(),
            "output-style" => self.cmd_output_style(&invocation).await,
            "permissions" => self.cmd_permissions(&invocation).await,
            "yolo" => self.cmd_yolo().await,
            "provider" => self.cmd_provider(&invocation).await,
            "headers" => self.cmd_headers(&invocation).await,
            "log" => self.cmd_log(&invocation).await,
            "marketplace" => self.cmd_marketplace(&invocation).await,
            "plugin" => self.cmd_plugin(&invocation).await,
            "skill" => self.cmd_skill(&invocation).await,
            "export" => self.cmd_export(&invocation).await,
            "diff" => self.cmd_diff().await,
            "image" => self.cmd_image(&invocation).await,
            "setup" => self.cmd_setup(),
            "compact" => self.cmd_compact().await,
            "resume" => self.cmd_resume(&invocation).await,
            "fork" => self.cmd_fork().await,
            "rewind" => self.cmd_rewind(&invocation).await,
            _ if spec.scope == Scope::Engine => self.run_engine_command(spec, invocation).await,
            _ => self.notice(
                format!("/{} is not implemented yet.", spec.name),
                NoticeLevel::Warning,
            ),
        }
    }

    pub(super) async fn run_engine_command(
        &mut self,
        spec: &commands::CommandSpec,
        invocation: commands::Invocation,
    ) {
        let (rpc_method, params) = match spec.name {
            "models" => (
                method::MODELS,
                Some(serde_json::json!({
                    "refresh": invocation.words().contains(&"--refresh")
                })),
            ),
            "model" => match invocation.first() {
                Some(_) => {
                    // Switching models is not exposed over serve; report rather
                    // than silently doing nothing.
                    self.notice(
                        "Switching models is not available over `coda serve`; start the engine with the model you want.",
                        NoticeLevel::Warning,
                    );
                    return;
                }
                None => (method::MODELS, Some(serde_json::json!({ "refresh": false }))),
            },
            "effort" => (
                method::SET_EFFORT,
                Some(serde_json::json!({ "effort": invocation.first() })),
            ),
            "goal" => (
                method::SET_GOAL,
                Some(serde_json::json!({
                    "goal": (!invocation.args.is_empty()).then_some(invocation.args.clone())
                })),
            ),
            "history" => (method::HISTORY, Some(serde_json::json!({}))),
            "schedules" => (method::SCHEDULE_LIST, Some(serde_json::json!({}))),
            "skills" => (method::SKILLS_LIST, Some(serde_json::json!({}))),
            "plugins" => (method::PLUGINS_LIST, Some(serde_json::json!({}))),
            "hooks" => (method::HOOKS_LIST, Some(serde_json::json!({}))),
            other => {
                self.notice(format!("/{other} is not wired up."), NoticeLevel::Warning);
                return;
            }
        };

        match self.connection.request(rpc_method, params).await {
            Ok(value) => self.output(format_result(spec.name, &value)),
            Err(error) => self.notice(
                format!("/{} failed: {error}", spec.name),
                NoticeLevel::Error,
            ),
        }
    }
}

/// Formats an engine response for display.
pub(super) fn format_result(command: &str, value: &Value) -> String {
    match command {
        "models" | "model" => match serde_json::from_value::<messages::ModelsResult>(value.clone())
        {
            Ok(result) => {
                let mut out = format!("Models ({})\n", result.source);
                for model in &result.models {
                    match model.context_limit {
                        Some(limit) => {
                            out.push_str(&format!("  {}  ({limit} tokens)\n", model.label()))
                        }
                        None => out.push_str(&format!("  {}\n", model.label())),
                    }
                }
                out.trim_end().to_string()
            }
            Err(_) => pretty(value),
        },
        "skills" => match serde_json::from_value::<messages::SkillsListResult>(value.clone()) {
            Ok(result) if !result.skills.is_empty() => {
                let mut out = String::from("Skills\n");
                for skill in &result.skills {
                    let mark = if skill.enabled { "*" } else { " " };
                    out.push_str(&format!(
                        "  {mark} {}  {}\n",
                        skill.name,
                        skill.description.as_deref().unwrap_or("")
                    ));
                }
                out.trim_end().to_string()
            }
            Ok(_) => "No skills available.".to_string(),
            Err(_) => pretty(value),
        },
        "plugins" => match serde_json::from_value::<messages::PluginsListResult>(value.clone()) {
            Ok(result) if !result.plugins.is_empty() => {
                let mut out = String::from("Plugins\n");
                for plugin in &result.plugins {
                    out.push_str(&format!(
                        "  {} {}\n",
                        plugin.name,
                        plugin.version.as_deref().unwrap_or("")
                    ));
                }
                out.trim_end().to_string()
            }
            Ok(_) => "No plugins installed.".to_string(),
            Err(_) => pretty(value),
        },
        "history" => match serde_json::from_value::<messages::HistoryResult>(value.clone()) {
            Ok(result) => format!("{} messages in history.", result.messages.len()),
            Err(_) => pretty(value),
        },
        "schedules" => {
            match serde_json::from_value::<messages::ScheduleListResult>(value.clone()) {
                Ok(result) if !result.schedules.is_empty() => {
                    let mut out = String::from("Schedules\n");
                    for task in &result.schedules {
                        out.push_str(&format!(
                            "  {}  {}  {}\n",
                            task.id, task.rule, task.state
                        ));
                    }
                    out.trim_end().to_string()
                }
                Ok(_) => "No schedules.".to_string(),
                Err(_) => pretty(value),
            }
        }
        _ => pretty(value),
    }
}

pub(super) fn pretty(value: &Value) -> String {
    serde_json::to_string_pretty(value).unwrap_or_else(|_| value.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn formats_a_model_list() {
        let value = json!({
            "source": "live",
            "models": [
                { "id": "a", "displayName": "Model A", "contextLimit": 200000 },
                { "id": "b" }
            ]
        });
        let text = format_result("models", &value);
        assert!(text.contains("Models (live)"));
        assert!(text.contains("Model A  (200000 tokens)"));
        assert!(text.contains("  b"));
    }

    #[test]
    fn formats_an_empty_skill_list() {
        let text = format_result("skills", &json!({ "skills": [] }));
        assert_eq!(text, "No skills available.");
    }

    #[test]
    fn formats_a_skill_list_marking_enabled_entries() {
        let value = json!({
            "skills": [
                { "name": "pdf", "description": "PDF tools", "enabled": true },
                { "name": "xlsx", "description": "Spreadsheets", "enabled": false }
            ]
        });
        let text = format_result("skills", &value);
        assert!(text.contains("* pdf  PDF tools"));
        assert!(text.contains("  xlsx  Spreadsheets"));
    }

    #[test]
    fn formats_a_history_count() {
        let value = json!({ "messages": [{ "role": "user", "content": "a" }] });
        assert_eq!(format_result("history", &value), "1 messages in history.");
    }

    #[test]
    fn formats_an_empty_schedule_list() {
        assert_eq!(
            format_result("schedules", &json!({ "schedules": [] })),
            "No schedules."
        );
    }

    #[test]
    fn falls_back_to_pretty_json_for_unknown_commands() {
        let text = format_result("whatever", &json!({ "a": 1 }));
        assert!(text.contains("\"a\""));
    }

    #[test]
    fn falls_back_to_pretty_json_when_the_shape_is_unexpected() {
        // Optional fields all have defaults, so an object still parses; only a
        // structurally wrong payload falls through to the raw view.
        let text = format_result("models", &json!(["not", "an", "object"]));
        assert!(text.contains("not"), "got {text:?}");
    }

    #[test]
    fn tolerates_a_result_missing_its_optional_fields() {
        let text = format_result("models", &json!({ "models": [{ "id": "a" }] }));
        assert!(text.contains("  a"), "got {text:?}");
    }
}

