//! Opening browsers and performing the actions their rows raise.
//!
//! One module because every one of these has the same shape: fetch from the
//! engine or the filesystem, build a `Browser`, then act on a row by id. The
//! per-kind matches below are what makes adding a browser a five-place edit;
//! they are what the `RowActions` change removes.

use coda_client::ClientError;
use coda_proto::messages::{self, method};

use super::App;
use crate::browsers as rows;
use crate::config::{PluginState, Settings};
use crate::state::UiEvent;
use crate::config;
use crate::overlay::Browser;
use crate::surface::browser::{BrowserKind, BrowserSurface};
use crate::transcript::NoticeLevel;

impl App {
    /// The open browser, read from the stack.
    ///
    /// Read through rather than kept as a field: a second copy would have to
    /// be held in step with the stack, and the two disagreeing is exactly the
    /// class of bug this abstraction removes.
    pub(super) fn browser(&self) -> Option<&Browser> {
        self.surfaces
            .top()
            .and_then(|s| s.as_any().downcast_ref::<BrowserSurface>())
            .map(BrowserSurface::browser)
    }

    pub(super) fn browser_kind(&self) -> Option<BrowserKind> {
        self.surfaces
            .top()
            .and_then(|s| s.as_any().downcast_ref::<BrowserSurface>())
            .map(BrowserSurface::kind)
    }

    /// Sends the reply to an engine prompt and records the outcome.
    /// Removes an open browser surface.
    pub(super) fn retire_browser_surface(&mut self) {
        while self
            .surfaces
            .top()
            .is_some_and(|s| s.as_any().is::<BrowserSurface>())
        {
            self.surfaces.pop();
        }
    }

    pub(super) fn close_browser(&mut self) {
        self.retire_browser_surface();
    }

    /// Fetches a browser's data and builds it, without opening it.
    ///
    /// Separate from opening so a reload can fetch first and only replace the
    /// open browser once it has something to replace it with. Retiring the old
    /// surface before a fallible fetch makes a transient engine error close the
    /// browser and lose the user's place -- and reload is exactly when a flaky
    /// engine is most likely.
    pub(super) async fn build_browser(&mut self, kind: BrowserKind) -> Option<Browser> {
        let browser = match kind {
            BrowserKind::Models => {
                match self
                    .fetch::<messages::ModelsResult>(
                        method::MODELS,
                        Some(serde_json::json!({ "refresh": false })),
                    )
                    .await
                {
                    Ok(result) => rows::models(
                        &result.models,
                        self.state.model.as_deref(),
                        &result.source,
                    ),
                    Err(error) => {
                        self.browser_failed("models", error);
                        return None;
                    }
                }
            }
            BrowserKind::Schedules => {
                match self
                    .fetch::<messages::ScheduleListResult>(
                        method::SCHEDULE_LIST,
                        Some(serde_json::json!({})),
                    )
                    .await
                {
                    Ok(result) => rows::schedules(&result.schedules),
                    Err(error) => {
                        self.browser_failed("schedules", error);
                        return None;
                    }
                }
            }
            BrowserKind::Skills => {
                match self
                    .fetch::<messages::SkillsListResult>(
                        method::SKILLS_LIST,
                        Some(serde_json::json!({})),
                    )
                    .await
                {
                    Ok(result) => rows::skills(&result.skills),
                    Err(error) => {
                        self.browser_failed("skills", error);
                        return None;
                    }
                }
            }
            BrowserKind::Plugins => {
                match self
                    .fetch::<messages::PluginsListResult>(
                        method::PLUGINS_LIST,
                        Some(serde_json::json!({})),
                    )
                    .await
                {
                    Ok(result) => rows::plugins(&result.plugins),
                    Err(error) => {
                        self.browser_failed("plugins", error);
                        return None;
                    }
                }
            }
            BrowserKind::Hooks => {
                match self
                    .fetch::<messages::HooksListResult>(
                        method::HOOKS_LIST,
                        Some(serde_json::json!({})),
                    )
                    .await
                {
                    Ok(result) => rows::hooks(&result.hooks),
                    Err(error) => {
                        self.browser_failed("hooks", error);
                        return None;
                    }
                }
            }
            // MCP configuration lives in local JSON, so it needs no engine call.
            BrowserKind::Mcp => match config::load_mcp_servers(&self.paths) {
                Ok(servers) => rows::mcp(&servers),
                Err(error) => {
                    self.notice(
                        format!("Could not read MCP configuration: {error}"),
                        NoticeLevel::Error,
                    );
                    return None;
                }
            },
            // Tasks are engine state, but the runtime persists a log per task
            // and reports outcomes over the event stream.
            BrowserKind::Tasks => {
                let logs = config::list_task_logs(&self.paths, self.state.session_id.as_deref());
                rows::tasks(&logs, &self.task_outcomes)
            }
            // Sessions are read from disk; there is no engine RPC for listing them.
            BrowserKind::Sessions => {
                let project_root = self.paths.project_root.clone();
                let summaries = match tokio::task::spawn_blocking(move || {
                    coda_agent::SessionTranscriptStore::new(&project_root).list()
                })
                .await
                {
                    Ok(list) => list,
                    Err(_) => {
                        self.notice("Could not load sessions.", NoticeLevel::Error);
                        return None;
                    }
                };
                if summaries.is_empty() {
                    self.notice(
                        "No sessions found. Start a conversation to create one.",
                        NoticeLevel::Info,
                    );
                    return None;
                }
                rows::sessions(&summaries)
            }
        };

        Some(browser)
    }

    /// Opens a browser, fetching its data from the engine.
    pub(super) async fn open_browser(&mut self, kind: BrowserKind) {
        if let Some(browser) = self.build_browser(kind).await {
            self.surfaces
                .push(Box::new(BrowserSurface::new(kind, browser)));
            self.dirty = true;
        }
    }

    pub(super) fn browser_failed(&mut self, what: &str, error: ClientError) {
        self.notice(
            format!("Could not load {what}: {error}"),
            NoticeLevel::Error,
        );
    }

    pub(super) async fn reload_browser(&mut self) {
        let Some(kind) = self.browser_kind() else {
            return;
        };
        let selected = self
            .browser()
            .and_then(|b| b.selected_id().map(str::to_string));

        // Fetch before retiring. If the engine hiccups the old browser stays
        // exactly as it was, rather than vanishing and losing the user's place.
        let Some(mut browser) = self.build_browser(kind).await else {
            return;
        };
        if let Some(id) = selected {
            browser.select_by_id(&id);
        }
        browser.set_status("reloaded");

        self.retire_browser_surface();
        self.surfaces
            .push(Box::new(BrowserSurface::new(kind, browser)));
        self.dirty = true;
    }

    /// Handles Enter on a row.
    pub(super) async fn activate_browser_row(&mut self, id: &str) {
        match self.browser_kind() {
            Some(BrowserKind::Models) => self.switch_model(id).await,
            Some(BrowserKind::Sessions) => self.resume_to_session(id.to_string()).await,
            _ => self.dirty = true,
        }
    }

    /// Switches the active model.
    ///
    /// `coda serve` exposes no model-switch method, but the model is read from
    /// `~/.coda/settings.json` at engine start. Writing the setting and
    /// restarting the engine against the same session id therefore performs a
    /// real switch, with the conversation preserved.
    pub(super) async fn switch_model(&mut self, model: &str) {
        // Read the configured provider (blocking I/O wrapped in spawn_blocking so
        // it cannot block the async runtime).
        let paths = self.paths.clone();
        let provider = match tokio::task::spawn_blocking(move || Settings::load(&paths)).await {
            Ok(Ok(settings)) => settings.default_provider().map(str::to_string),
            Ok(Err(error)) => {
                return self.notice(
                    format!("Could not read settings: {error}"),
                    NoticeLevel::Error,
                )
            }
            Err(_) => return self.notice("Settings read was interrupted.", NoticeLevel::Error),
        };

        let Some(provider) = provider else {
            return self.notice(
                "No default provider is configured; run `coda setup` first.",
                NoticeLevel::Warning,
            );
        };

        // Write the new model choice (also blocking I/O).
        let paths = self.paths.clone();
        let model_str = model.to_string();
        let write_result = tokio::task::spawn_blocking(move || -> Result<(), config::ConfigError> {
            let mut settings = Settings::load(&paths)?;
            settings.set_model_for(&provider, &model_str);
            settings.save()
        })
        .await;

        match write_result {
            Ok(Ok(())) => {}
            Ok(Err(error)) => {
                return self.notice(
                    format!("Could not save the model: {error}"),
                    NoticeLevel::Error,
                )
            }
            Err(_) => return self.notice("Settings write was interrupted.", NoticeLevel::Error),
        }

        self.close_browser();
        self.apply(UiEvent::ModelChanged {
            id: model.to_string(),
            context_limit: None,
        });
        self.notice(
            format!("Model set to {model}. Restarting the engine…"),
            NoticeLevel::Info,
        );
        self.restart_engine().await;
    }

    /// Toggles the selected row where the change can actually be persisted.
    pub(super) async fn toggle_browser_row(&mut self, id: &str) {
        match self.browser_kind() {
            Some(BrowserKind::Plugins) => {
                // Plugin state lives in a JSON file; read and write are blocking
                // I/O that must not block the async runtime.
                let paths = self.paths.clone();
                let id_owned = id.to_string();
                let result = tokio::task::spawn_blocking(move || -> Result<bool, config::ConfigError> {
                    let mut state = PluginState::load(&paths)?;
                    let enabled = state.is_disabled(&id_owned); // toggling to this
                    state.set_enabled(&id_owned, enabled);
                    state.save()?;
                    Ok(enabled)
                })
                .await;

                match result {
                    Ok(Ok(enabled)) => {
                        let word = if enabled { "Enabled" } else { "Disabled" };
                        self.notice(
                            format!("{word} plugin {id}. Restart the engine to apply."),
                            NoticeLevel::Info,
                        );
                        self.reload_browser().await;
                    }
                    Ok(Err(error)) => self.notice(
                        format!("Could not update plugin state: {error}"),
                        NoticeLevel::Error,
                    ),
                    Err(_) => self.notice("Plugin state write was interrupted.", NoticeLevel::Error),
                }
            }
            Some(BrowserKind::Mcp) => {
                // Load + mutate MCP config; both are blocking.
                let paths = self.paths.clone();
                let id_owned = id.to_string();
                let enable_result = tokio::task::spawn_blocking(move || {
                    let enabled = config::load_mcp_servers(&paths)
                        .ok()
                        .and_then(|servers| {
                            servers.iter().find(|s| s.name == id_owned).map(|s| !s.enabled)
                        })
                        .unwrap_or(false);
                    config::set_mcp_enabled(&paths, &id_owned, enabled).map(|ok| (ok, enabled))
                })
                .await;

                match enable_result {
                    Ok(Ok((true, _))) => {
                        self.notice(
                            format!("Updated MCP server {id}. Restart the engine to apply."),
                            NoticeLevel::Info,
                        );
                        self.reload_browser().await;
                    }
                    Ok(Ok((false, _))) => self.notice(
                        format!("MCP server {id} is not defined in a local .mcp.json."),
                        NoticeLevel::Warning,
                    ),
                    Ok(Err(error)) => self.notice(
                        format!("Could not update MCP configuration: {error}"),
                        NoticeLevel::Error,
                    ),
                    Err(_) => self.notice("MCP config write was interrupted.", NoticeLevel::Error),
                }
            }
            Some(BrowserKind::Skills) => self.notice(
                "Skills are frontmatter-driven; edit the SKILL.md file to change them.",
                NoticeLevel::Info,
            ),
            _ => self.dirty = true,
        }
    }

    pub(super) async fn delete_browser_row(&mut self, id: &str) {
        if self.browser_kind() != Some(BrowserKind::Schedules) {
            return;
        }
        match self
            .connection
            .request(
                method::SCHEDULE_DELETE,
                Some(serde_json::json!({ "id": id })),
            )
            .await
        {
            Ok(_) => {
                self.notice(format!("Deleted schedule {id}."), NoticeLevel::Info);
                self.reload_browser().await;
            }
            Err(error) => self.notice(
                format!("Could not delete {id}: {error}"),
                NoticeLevel::Error,
            ),
        }
    }

    pub(super) async fn browser_key_action(&mut self, key: char, id: Option<String>) {
        match (self.browser_kind(), key) {
            (Some(BrowserKind::Schedules), 'd') => {
                if let Some(id) = id {
                    self.delete_browser_row(&id).await;
                }
            }
            (Some(BrowserKind::Schedules), 'n') => self.notice(
                "Creating a schedule needs arguments; use /schedule from the composer.",
                NoticeLevel::Info,
            ),
            (Some(BrowserKind::Plugins), 'u') => match id {
                Some(id) => self.update_plugin(&id).await,
                None => self.dirty = false,
            },
            // The MCP list is where servers are managed, so the editor opens
            // from here rather than from a command with a dozen arguments.
            (Some(BrowserKind::Mcp), 'n') => {
                self.surfaces.push(Box::new(
                    crate::surface::mcp_editor::McpEditorSurface::creating(),
                ));
                self.dirty = true;
            }
            (Some(BrowserKind::Mcp), 'e') => self.edit_mcp_server(id).await,
            (Some(BrowserKind::Mcp), 'd') => self.delete_mcp_server(id).await,
            _ => self.dirty = false,
        }
    }

    /// Opens the editor on the selected MCP server.
    pub(super) async fn edit_mcp_server(&mut self, id: Option<String>) {
        let Some(name) = id else {
            self.dirty = false;
            return;
        };
        let paths = self.paths.clone();
        let found = tokio::task::spawn_blocking(move || {
            config::load_mcp_servers(&paths)
                .ok()
                .and_then(|servers| servers.into_iter().find(|s| s.name == name))
        })
        .await
        .ok()
        .flatten();

        match found {
            Some(server) => {
                self.surfaces.push(Box::new(
                    crate::surface::mcp_editor::McpEditorSurface::editing(
                        config::McpDraft::from_server(&server),
                    ),
                ));
                self.dirty = true;
            }
            None => self.notice("That server is no longer defined.", NoticeLevel::Warning),
        }
    }

    /// Removes the selected MCP server.
    pub(super) async fn delete_mcp_server(&mut self, id: Option<String>) {
        let Some(name) = id else {
            self.dirty = false;
            return;
        };
        let paths = self.paths.clone();
        let target = name.clone();
        let removed =
            tokio::task::spawn_blocking(move || config::delete_mcp_server(&paths, &target)).await;

        match removed {
            Ok(Ok(true)) => {
                self.notice(format!("Removed MCP server '{name}'."), NoticeLevel::Info);
                self.reload_browser().await;
            }
            Ok(Ok(false)) => {
                self.notice("That server is no longer defined.", NoticeLevel::Warning)
            }
            Ok(Err(err)) => self.notice(format!("Could not remove: {err}"), NoticeLevel::Error),
            Err(_) => self.notice("The removal was interrupted.", NoticeLevel::Error),
        }
    }

    /// Updates a git-installed plugin by pulling in its directory.
    ///
    /// The engine has no update RPC, but plugins live in known directories, so
    /// the update is a plain `git pull` the front-end can run itself.
    pub(super) async fn update_plugin(&mut self, name: &str) {
        let candidates = [
            self.paths.project_root.join(".coda").join("plugins").join(name),
            self.paths.user_root.join("plugins").join(name),
        ];

        let Some(directory) = candidates.into_iter().find(|p| p.join(".git").is_dir()) else {
            return self.notice(
                format!("{name} is not a git-installed plugin, so there is nothing to update."),
                NoticeLevel::Warning,
            );
        };

        let output = tokio::process::Command::new("git")
            .arg("pull")
            .arg("--ff-only")
            .current_dir(&directory)
            .output()
            .await;

        match output {
            Ok(output) if output.status.success() => {
                let summary = String::from_utf8_lossy(&output.stdout);
                self.notice(
                    format!("Updated {name}: {}", summary.trim()),
                    NoticeLevel::Info,
                );
                self.reload_browser().await;
            }
            Ok(output) => self.notice(
                format!(
                    "Could not update {name}: {}",
                    String::from_utf8_lossy(&output.stderr).trim()
                ),
                NoticeLevel::Error,
            ),
            Err(error) => self.notice(
                format!("Could not run git: {error}"),
                NoticeLevel::Error,
            ),
        }
    }
}

