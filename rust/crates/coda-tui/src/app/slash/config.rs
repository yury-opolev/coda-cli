//! Configuration commands: model, provider, permissions, headers, output
//! style, logging.
//!
//! These change how the agent behaves rather than what the conversation is.


use super::super::App;
use crate::commands;
use crate::config;
use crate::transcript::NoticeLevel;

impl App {
    /// `/output-style [<style>]` — show or set the response style persona.
    pub(super) async fn cmd_output_style(&mut self, invocation: &commands::Invocation) {
        let arg = invocation.first().map(str::to_string);
        let paths = self.paths.clone();

        let result = tokio::task::spawn_blocking(move || -> Result<String, config::ConfigError> {
            let mut settings = config::Settings::load(&paths)?;

            if let Some(ref style_name) = arg {
                if !coda_agent::BuiltInOutputStyles::is_known(Some(style_name)) {
                    let names: Vec<&str> =
                        coda_agent::BuiltInOutputStyles::all().iter().map(|s| s.name).collect();
                    return Ok(format!(
                        "Unknown style '{style_name}'. Available: {}",
                        names.join(", ")
                    ));
                }
                settings.set_output_style(style_name);
                settings.save()?;
                return Ok(format!(
                    "Output style set to {style_name}. Restart the engine to apply."
                ));
            }

            let current = settings.output_style().unwrap_or("default");
            let mut out = format!("Current style: {current}\n");
            for s in coda_agent::BuiltInOutputStyles::all() {
                let marker = if s.name.eq_ignore_ascii_case(current) { " (active)" } else { "" };
                out.push_str(&format!("  {}{marker} — {}\n", s.name, s.description));
            }
            Ok(out.trim_end().to_string())
        })
        .await;

        match result {
            Ok(Ok(text)) => self.output(text),
            Ok(Err(e)) => self.notice(format!("Settings error: {e}"), NoticeLevel::Error),
            Err(_) => self.notice("Settings read was interrupted.", NoticeLevel::Error),
        }
    }

    /// `/permissions [<mode>]` — show or set the tool-permission mode.
    pub(super) async fn cmd_permissions(&mut self, invocation: &commands::Invocation) {
        let Some(raw) = invocation.first().map(str::to_string) else {
            // No argument: report the mode without changing anything.
            let paths = self.paths.clone();
            let current = tokio::task::spawn_blocking(move || {
                config::Settings::load(&paths)
                    .ok()
                    .and_then(|s| s.permission_mode().map(str::to_string))
                    .unwrap_or_else(|| "default".to_string())
            })
            .await
            .unwrap_or_else(|_| "default".to_string());

            self.output(format!(
                "Permission mode: {current}\nModes: default (ask), acceptEdits (auto-edit), plan (read-only), bypass (yolo: allow all)"
            ));
            return;
        };

        let Some(canonical) = parse_permission_mode(&raw) else {
            self.output(format!(
                "Unknown mode '{raw}'. Use: default | acceptEdits | plan | bypass"
            ));
            return;
        };

        if self.apply_permission_mode(canonical).await {
            let note = if canonical == "bypass" {
                " — tools now run without asking"
            } else {
                ""
            };
            self.output(format!("Permission mode set to {canonical}{note}."));
        }
    }

    /// `/yolo` — grant bypass-permissions mode. Explicit, loud, and impossible to miss.
    pub(super) async fn cmd_yolo(&mut self) {
        if !self.apply_permission_mode("bypass").await {
            return;
        }
        // The warning appears first in the transcript to make the state
        // change impossible to overlook before the confirmation notice.
        self.notice(
            "⚠  YOLO mode: tools will run without asking for permission.",
            NoticeLevel::Warning,
        );
        self.notice(
            "In effect now. Use /permissions default to revert.",
            NoticeLevel::Info,
        );
    }

    /// `/provider [<id>]` — show the configured provider or switch to a different one.
    pub(super) async fn cmd_provider(&mut self, invocation: &commands::Invocation) {
        let arg = invocation.first().map(str::to_string);
        let paths = self.paths.clone();

        if let Some(new_provider) = arg {
            // Write the new provider and restart so the engine picks it up.
            let success_msg = format!("Provider set to {new_provider}. Restarting engine…");
            let write_result = tokio::task::spawn_blocking(move || -> Result<(), config::ConfigError> {
                let mut settings = config::Settings::load(&paths)?;
                settings.set_default_provider(&new_provider);
                settings.save()
            })
            .await;

            match write_result {
                Ok(Ok(())) => {
                    self.notice(success_msg, NoticeLevel::Info);
                    self.restart_engine().await;
                }
                Ok(Err(e)) => self.notice(format!("Could not save provider: {e}"), NoticeLevel::Error),
                Err(_) => self.notice("Settings write was interrupted.", NoticeLevel::Error),
            }
            return;
        }

        // No argument — display the configured provider and any model-by-provider entries.
        let read_result = tokio::task::spawn_blocking(move || config::Settings::load(&paths)).await;
        match read_result {
            Ok(Ok(settings)) => {
                let provider = settings.default_provider().unwrap_or("(none)");
                let mut out = format!("Active provider: {provider}\n");
                let providers_seen: Vec<String> = settings
                    .raw()
                    .get("modelByProvider")
                    .and_then(|m| m.as_object())
                    .map(|obj| obj.keys().cloned().collect())
                    .unwrap_or_default();
                if !providers_seen.is_empty() {
                    out.push_str("Configured providers:");
                    for p in &providers_seen {
                        let mark = if p == provider { " (active)" } else { "" };
                        out.push_str(&format!("\n  {p}{mark}"));
                    }
                } else {
                    out.push_str("Use /provider <id> to switch (e.g. github-copilot, claude-ai).");
                }
                self.output(out);
            }
            Ok(Err(e)) => self.notice(format!("Could not read settings: {e}"), NoticeLevel::Error),
            Err(_) => self.notice("Settings read was interrupted.", NoticeLevel::Error),
        }
    }

    /// `/headers [--set <name> <value> | --remove <name>]` — manage custom HTTP headers.
    pub(super) async fn cmd_headers(&mut self, invocation: &commands::Invocation) {
        let words = invocation.words();
        let paths = self.paths.clone();

        // Collect the operation before spawning so we don't capture `words` (non-Send).
        enum HeaderOp {
            Show,
            Set(String, String),
            Remove(String),
            BadUsage,
        }

        let op = match words.as_slice() {
            [] => HeaderOp::Show,
            ["--set", name, value] => HeaderOp::Set((*name).to_string(), (*value).to_string()),
            ["--remove", name] => HeaderOp::Remove((*name).to_string()),
            _ => HeaderOp::BadUsage,
        };

        if matches!(op, HeaderOp::BadUsage) {
            self.notice(
                "Usage: /headers | /headers --set <name> <value> | /headers --remove <name>",
                NoticeLevel::Warning,
            );
            return;
        }

        let result = tokio::task::spawn_blocking(move || -> Result<String, config::ConfigError> {
            let mut settings = config::Settings::load(&paths)?;
            match op {
                HeaderOp::Show => {
                    let headers = settings.custom_headers();
                    if headers.is_empty() {
                        return Ok("No custom headers configured.\nAuth headers are managed by the engine.".to_string());
                    }
                    let mut out = String::from("Custom headers:\n");
                    for (k, v) in &headers {
                        out.push_str(&format!("  {k}: {v}\n"));
                    }
                    out.push_str("Auth headers are managed by the engine.");
                    Ok(out.trim_end().to_string())
                }
                HeaderOp::Set(name, value) => {
                    settings.set_custom_header(&name, &value);
                    settings.save()?;
                    Ok(format!("Custom header set: {name}: {value}"))
                }
                HeaderOp::Remove(name) => {
                    settings.remove_custom_header(&name);
                    settings.save()?;
                    Ok(format!("Custom header removed: {name}"))
                }
                HeaderOp::BadUsage => unreachable!(),
            }
        })
        .await;

        match result {
            Ok(Ok(text)) => self.output(text),
            Ok(Err(e)) => self.notice(format!("Settings error: {e}"), NoticeLevel::Error),
            Err(_) => self.notice("Settings operation was interrupted.", NoticeLevel::Error),
        }
    }

    /// `/log [<level> | stderr on|off | off]` — show or change telemetry logging.
    pub(super) async fn cmd_log(&mut self, invocation: &commands::Invocation) {
        let words = invocation.words();
        let paths = self.paths.clone();

        enum LogOp {
            Show,
            SetLevel(String),
            Disable,
            Stderr(bool),
            BadUsage,
        }

        let op = match words.as_slice() {
            [] => LogOp::Show,
            ["off"] => LogOp::Disable,
            ["stderr", "on"] => LogOp::Stderr(true),
            ["stderr", "off"] => LogOp::Stderr(false),
            ["stderr", ..] => LogOp::BadUsage,
            [level] => {
                let lc = level.to_lowercase();
                if ["trace", "debug", "info", "warn", "error"].contains(&lc.as_str()) {
                    LogOp::SetLevel(lc)
                } else {
                    LogOp::BadUsage
                }
            }
            _ => LogOp::BadUsage,
        };

        if matches!(op, LogOp::BadUsage) {
            self.notice(
                "Usage: /log | /log <level> | /log off | /log stderr on|off  (levels: trace debug info warn error)",
                NoticeLevel::Warning,
            );
            return;
        }

        let log_dir = self.paths.logs();

        let result = tokio::task::spawn_blocking(move || -> Result<String, config::ConfigError> {
            let mut settings = config::Settings::load(&paths)?;
            match op {
                LogOp::Show => {
                    let dir = settings
                        .log_directory_override()
                        .map(str::to_string)
                        .unwrap_or_else(|| log_dir.display().to_string());
                    Ok(format!(
                        "Telemetry: {}\nLog level:  {}\nStderr:     {}\nLog dir:    {}\nChanges apply to the next session.",
                        if settings.log_enabled() { "enabled" } else { "disabled" },
                        settings.log_level(),
                        if settings.log_to_stderr() { "on" } else { "off" },
                        dir
                    ))
                }
                LogOp::SetLevel(level) => {
                    let stderr = settings.log_to_stderr();
                    settings.set_telemetry(true, &level, stderr);
                    let dir = settings
                        .log_directory_override()
                        .map(str::to_string)
                        .unwrap_or_else(|| log_dir.display().to_string());
                    settings.save()?;
                    Ok(format!(
                        "Telemetry enabled at {level}. Logs: {dir}. Applies to the next session."
                    ))
                }
                LogOp::Disable => {
                    let level = settings.log_level().to_string();
                    let stderr = settings.log_to_stderr();
                    settings.set_telemetry(false, &level, stderr);
                    settings.save()?;
                    Ok("Telemetry disabled. Applies to the next session.".to_string())
                }
                LogOp::Stderr(on) => {
                    let enabled = settings.log_enabled();
                    let level = settings.log_level().to_string();
                    settings.set_telemetry(enabled, &level, on);
                    settings.save()?;
                    Ok(format!(
                        "Stderr logging: {}. Applies to the next session.",
                        if on { "on" } else { "off" }
                    ))
                }
                LogOp::BadUsage => unreachable!(),
            }
        })
        .await;

        match result {
            Ok(Ok(text)) => self.output(text),
            Ok(Err(e)) => self.notice(format!("Settings error: {e}"), NoticeLevel::Error),
            Err(_) => self.notice("Settings operation was interrupted.", NoticeLevel::Error),
        }
    }
}

/// Parses a permission-mode name into its canonical form, case-insensitively.
///
/// Accepts the same set of aliases as the C# `PermissionsCommand.TryParseMode`.
fn parse_permission_mode(value: &str) -> Option<&'static str> {
    match value.to_lowercase().as_str() {
        "default" => Some("default"),
        "acceptedits" | "accept-edits" | "edits" => Some("acceptEdits"),
        "plan" => Some("plan"),
        "bypass" | "bypasspermissions" | "yolo" => Some("bypass"),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_permission_mode_accepts_canonical_names() {
        assert_eq!(parse_permission_mode("default"), Some("default"));
        assert_eq!(parse_permission_mode("acceptEdits"), Some("acceptEdits"));
        assert_eq!(parse_permission_mode("plan"), Some("plan"));
        assert_eq!(parse_permission_mode("bypass"), Some("bypass"));
    }

    #[test]
    fn parse_permission_mode_accepts_aliases() {
        assert_eq!(parse_permission_mode("edits"), Some("acceptEdits"));
        assert_eq!(parse_permission_mode("accept-edits"), Some("acceptEdits"));
        assert_eq!(parse_permission_mode("yolo"), Some("bypass"));
        assert_eq!(parse_permission_mode("bypassPermissions"), Some("bypass"));
    }

    #[test]
    fn parse_permission_mode_is_case_insensitive() {
        assert_eq!(parse_permission_mode("DEFAULT"), Some("default"));
        assert_eq!(parse_permission_mode("BYPASS"), Some("bypass"));
        assert_eq!(parse_permission_mode("YOLO"), Some("bypass"));
    }

    #[test]
    fn parse_permission_mode_rejects_unknown_names() {
        assert_eq!(parse_permission_mode("admin"), None);
        assert_eq!(parse_permission_mode(""), None);
    }
}

