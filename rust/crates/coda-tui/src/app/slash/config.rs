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
    ///
    /// The list of styles comes from `config/describe`, which is the engine's
    /// own catalogue for the key. Before this, the TUI enumerated
    /// `coda_agent::BuiltInOutputStyles` in-process, which meant the terminal
    /// front-end had to link the agent runtime to name a handful of strings —
    /// and an engine on another machine could offer a different set with no
    /// way to find out.
    pub(super) async fn cmd_output_style(&mut self, invocation: &commands::Invocation) {
        let arg = invocation.first().map(str::to_string);
        let paths = self.paths.clone();

        let described = match self.described_output_styles().await {
            Some(styles) => styles,
            None => {
                self.notice(
                    "The engine does not describe output styles, so this build cannot \
                     validate one.",
                    NoticeLevel::Warning,
                );
                return;
            }
        };

        if let Some(style_name) = arg {
            if !described.iter().any(|s| s.value.eq_ignore_ascii_case(&style_name)) {
                let names: Vec<&str> = described.iter().map(|s| s.value.as_str()).collect();
                self.output(format!(
                    "Unknown style '{style_name}'. Available: {}",
                    names.join(", ")
                ));
                return;
            }
            // `config/describe` reports this key as `clientLocal` and not
            // mutable: the engine does not apply an output style at all. The
            // value is this client's own, so it is saved in every mode — but
            // the old "restart the engine to apply" promised a behaviour
            // change that no restart produces.
            let saved = self
                .persist_client_local({
                    let style = style_name.clone();
                    move |settings| settings.set_output_style(&style)
                })
                .await;
            match saved {
                super::super::settings::Saved::Ok => self.output(format!(
                    "Output style set to {style_name}, in this client's own settings. \
                     The engine reports that it does not apply an output style, so \
                     restarting it changes nothing."
                )),
                super::super::settings::Saved::Failed(error) => {
                    self.notice(format!("Settings error: {error}"), NoticeLevel::Error)
                }
                // Client-local settings are never refused.
                super::super::settings::Saved::Refused => {}
            }
            return;
        }

        let result = tokio::task::spawn_blocking(move || -> Result<String, config::ConfigError> {
            let settings = config::Settings::load(&paths)?;
            let current = settings.output_style().unwrap_or("default");
            let mut out = format!("Current style: {current}\n");
            for s in &described {
                let marker = if s.value.eq_ignore_ascii_case(current) { " (active)" } else { "" };
                let description = s.description.clone().unwrap_or_default();
                out.push_str(&format!("  {}{marker} — {}\n", s.value, description));
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

    /// The output styles the engine publishes, read once and cached.
    async fn described_output_styles(
        &mut self,
    ) -> Option<Vec<coda_proto::config::AllowedValue>> {
        let catalog = self.described_config().await?;
        crate::api::allowed_values(catalog, "outputStyle").map(<[_]>::to_vec)
    }

    /// The permission mode the engine reports for the next check.
    ///
    /// `config/describe` publishes it as an engine-owned session value, which
    /// is the only authority for what this session is actually doing — and a
    /// *value*, not a description, so it is read fresh every time. Serving it
    /// from the cache reported the mode from before a `/yolo`, with the
    /// engine's authority behind a number the engine had already changed.
    async fn described_permission_mode(&mut self) -> Option<String> {
        let catalog = self.described_config_now().await?;
        catalog
            .entries
            .iter()
            .find(|entry| entry.key == "permissionMode")
            .and_then(|entry| entry.value.as_ref())
            .and_then(|value| value.as_str())
            .map(str::to_string)
    }

    /// The engine's own configuration catalogue, read once and cached.
    ///
    /// Safe to cache only for what a catalogue *is*: which keys exist, who
    /// owns them, what they accept. The cache is dropped whenever the engine
    /// says its configuration moved (`event/configChanged`), whenever the
    /// conversation is replaced, and whenever the engine itself is — see
    /// [`Self::forget_described_config`]. Live values are read through
    /// [`Self::described_config_now`] instead.
    async fn described_config(&mut self) -> Option<&coda_proto::config::ConfigDescribeResult> {
        if self.config_catalog.is_none() {
            self.config_catalog =
                self.bounded(crate::api::config_describe(&self.connection)).await.ok();
        }
        self.config_catalog.as_ref()
    }

    /// The catalogue as it is *now*, refreshing the cache.
    ///
    /// For reads of a live value, where a cached answer is not a stale
    /// description but a wrong fact.
    async fn described_config_now(
        &mut self,
    ) -> Option<&coda_proto::config::ConfigDescribeResult> {
        match self.bounded(crate::api::config_describe(&self.connection)).await {
            Ok(fresh) => self.config_catalog = Some(fresh),
            // Keep whatever is cached rather than losing the menus too: the
            // caller reports the missing value itself.
            Err(_) => return None,
        }
        self.config_catalog.as_ref()
    }

    /// `/permissions [<mode>]` — show or set the tool-permission mode.
    pub(super) async fn cmd_permissions(&mut self, invocation: &commands::Invocation) {
        let Some(raw) = invocation.first().map(str::to_string) else {
            // No argument: report the mode the *engine* is actually using.
            // `settings.json` is only a startup default — and on an API-only
            // session it is not even this engine's startup default — so
            // reading it here described a file rather than the session.
            let described = self.described_permission_mode().await;
            let current = match described {
                Some(mode) => mode,
                None => {
                    self.notice(
                        "The engine did not report its permission mode.",
                        NoticeLevel::Warning,
                    );
                    return;
                }
            };
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

    /// `/provider [<id>]` — show what is connected, or connect something else.
    ///
    /// With an argument this is `/login` by another name: choosing a provider
    /// is not a settings edit that happens to need a restart, it is a
    /// credential decision that stops the engine, commits, and starts a fresh
    /// one told explicitly which account to use. Writing `defaultProvider` and
    /// restarting — which is what this did — changed nothing about *which
    /// credential* the engine could find, so a switch to an account with no
    /// stored credential silently came back on the old one.
    pub(super) async fn cmd_provider(&mut self, invocation: &commands::Invocation) {
        if let Some(requested) = invocation.first() {
            self.cmd_login(Some(requested)).await;
            return;
        }

        // No argument — report, from two sources that must not be conflated:
        //   1. what the engine says it connected with, which is a fact about
        //      the running session and not a claim about what is stored;
        //   2. what this machine actually holds, read without refreshing
        //      anything — and only when this machine is the engine's host.
        let session_provider = self.connected_provider.clone();
        let mut out = match session_provider.as_deref() {
            Some(provider) => format!(
                "Session provider: {provider} (reported by the engine; authentication state \
                 unverified)\n"
            ),
            None if !self.engine_connected => {
                "Session provider: none — this session is disconnected.\n".to_owned()
            }
            None => "Session provider: (none — the engine has not reported one)\n".to_owned(),
        };

        if let Some(message) = crate::local::auth::refusal(self.access_mode, "Account maintenance")
        {
            out.push_str(&message);
            self.output(out);
            return;
        }

        // The stored half is read on a task: opening a credential store is a
        // keychain round-trip and a settings file, and awaiting either on the
        // loop is a terminal that stops drawing. The session half above is
        // already known, so it is carried into the answer rather than read
        // again when it arrives.
        self.report_accounts(out);
    }

    /// `/headers [--set <name> <value> | --remove <name>]` — manage custom HTTP headers.
    ///
    /// Engine-owned throughout: the headers are read by the engine's own HTTP
    /// client from the engine host's settings, so an API-only session neither
    /// shows nor edits this machine's copy — showing it would describe another
    /// host's configuration as this session's.
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

        // Before any filesystem access, and for the read as much as the write.
        if !self.allow_engine_settings("Custom HTTP header configuration") {
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
    ///
    /// Two different things wear this name, and they are reported separately
    /// because only one of them is this client's:
    ///
    /// - the **front-end's own** essential diagnostics (path, verbosity,
    ///   health) plus the engine's reported log path — client-local, always
    ///   available, in every mode;
    /// - the **legacy telemetry settings** in `settings.json`, which the
    ///   *engine* reads at its own startup on its own host. An API-only
    ///   session neither reads nor writes those.
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

        // Real frontend diagnostic status and the engine's own reported log
        // path — computed here, on this task, so the ambient
        // `coda_diagnostics` context (a `tokio::task_local`) is still
        // visible; it would not be inside `spawn_blocking`'s separate task.
        let diagnostics_report = self.diagnostics_status_report();

        // The client's own diagnostics are always reportable; the engine's
        // telemetry settings are not this client's to read or write when the
        // engine runs elsewhere. Gated before any filesystem access.
        if !self.owns_engine_settings() {
            match op {
                LogOp::Show => self.output(format!(
                    "{diagnostics_report}\n\
                     Engine telemetry settings are managed on the engine host: this \
                     client did not start this engine, so the values on this machine \
                     are not the ones it read."
                )),
                _ => {
                    self.allow_engine_settings("Engine telemetry configuration");
                }
            }
            return;
        }

        let result = tokio::task::spawn_blocking(move || -> Result<String, config::ConfigError> {
            let mut settings = config::Settings::load(&paths)?;
            match op {
                LogOp::Show => {
                    let dir = settings
                        .log_directory_override()
                        .map(str::to_string)
                        .unwrap_or_else(|| log_dir.display().to_string());
                    Ok(format!(
                        "{diagnostics_report}\n\
                         Legacy telemetry settings (do not affect essential diagnostics above):\n\
                         Telemetry: {}\nLog level:  {}\nStderr:     {}\nLog dir:    {}\nChanges apply to the next session.",
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
                    Ok("Legacy telemetry disabled (applies to the next session). \
                        Essential operational diagnostics are unaffected — use \
                        `/log` to see their current path/health."
                        .to_string())
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

    /// The actual discoverable diagnostic state: this frontend's own writer
    /// (path/mode/verbosity/health) plus the engine's reported log path —
    /// never the legacy, currently-unused telemetry settings, which do not
    /// control it.
    fn diagnostics_status_report(&self) -> String {
        let status = coda_diagnostics::current().map(|ctx| ctx.logger().status());
        crate::diagnostics::status_report(status.as_ref(), self.engine_log_path.as_deref())
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
