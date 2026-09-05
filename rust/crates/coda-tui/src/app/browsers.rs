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
use crate::surface::browser::{BrowserKind, BrowserSurface, RowActions};
use crate::surface::SurfaceAction;
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

    /// The row actions for a browser, declared beside the browser itself.
    ///
    /// One place per browser rather than a share of five `BrowserKind`
    /// matches. A browser with no entry here still works — its rows fall
    /// through to the host as before — so this is additive rather than a
    /// cliff.
    fn row_actions(kind: BrowserKind) -> RowActions {
        use SurfaceAction as A;
        match kind {
            BrowserKind::Models => {
                RowActions::new().on_activate(|id| A::SwitchModel(id.to_string()))
            }
            BrowserKind::Sessions => {
                RowActions::new().on_activate(|id| A::ResumeSession(id.to_string()))
            }
            BrowserKind::Plugins => RowActions::new()
                .on_toggle(|id| A::TogglePlugin(id.to_string()))
                .on_key('u', |id| A::UpdatePlugin(id.to_string())),
            BrowserKind::Mcp => RowActions::new()
                .on_toggle(|id| A::ToggleMcp(id.to_string()))
                .on_bare_key('n', || A::NewMcpServer)
                .on_key('e', |id| A::EditMcpServer(id.to_string()))
                .on_key('d', |id| A::DeleteMcpServer(id.to_string())),
            BrowserKind::Schedules => RowActions::new()
                .on_key('d', |id| A::DeleteSchedule(id.to_string()))
                .on_bare_key('n', || A::ExplainScheduleCreation),
            BrowserKind::Skills => {
                RowActions::new().on_toggle(|_| A::ExplainSkillToggle)
            }
            // Hooks and tasks are read-only; Enter opens their detail view,
            // which the browser handles without troubling the host.
            BrowserKind::Hooks | BrowserKind::Tasks => RowActions::new(),
        }
    }

    /// Wraps a built browser in its surface, actions attached.
    ///
    /// The only place a `BrowserSurface` is constructed, enforced by test.
    /// Attaching the actions is what makes a browser's keys do anything, and
    /// it is invisible when missed: the browser draws correctly and every key
    /// quietly does nothing. Reload used to construct its own and was exactly
    /// that bug.
    fn browser_surface(kind: BrowserKind, browser: Browser) -> BrowserSurface {
        BrowserSurface::new(kind, browser).with_actions(Self::row_actions(kind))
    }

    /// Opens a browser, fetching its data from the engine.
    pub(super) async fn open_browser(&mut self, kind: BrowserKind) {
        if let Some(browser) = self.build_browser(kind).await {
            self.surfaces
                .push(Box::new(Self::browser_surface(kind, browser)));
            self.dirty = true;
        }
    }

    /// Enables or disables an installed plugin.
    pub(super) async fn toggle_plugin(&mut self, id: &str) {
        // Plugin state lives in a JSON file; read and write are blocking I/O
        // that must not block the async runtime.
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

    /// Enables or disables a configured MCP server.
    pub(super) async fn toggle_mcp(&mut self, id: &str) {
        // Load and mutate MCP config; both are blocking.
        let paths = self.paths.clone();
        let id_owned = id.to_string();
        let enable_result = tokio::task::spawn_blocking(move || {
            let enabled = config::load_mcp_servers(&paths)
                .ok()
                .and_then(|servers| {
                    servers
                        .iter()
                        .find(|s| s.name == id_owned)
                        .map(|s| !s.enabled)
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

    /// Removes a scheduled task.
    pub(super) async fn delete_schedule(&mut self, id: &str) {
        match self
            .fetch::<messages::OkResult>(
                method::SCHEDULE_DELETE,
                Some(serde_json::json!({ "id": id })),
            )
            .await
        {
            Ok(result) if result.ok => {
                self.notice(format!("Deleted schedule {id}."), NoticeLevel::Info);
                self.reload_browser().await;
            }
            Ok(_) => self.notice(
                format!("Schedule {id} was already gone."),
                NoticeLevel::Warning,
            ),
            Err(error) => self.notice(
                format!("Could not delete schedule: {error}"),
                NoticeLevel::Error,
            ),
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
            .push(Box::new(Self::browser_surface(kind, browser)));
        self.dirty = true;
    }


    /// Switches the active model.
    ///
    /// `coda serve` exposes no model-switch method, but the model is read from
    /// `~/.coda/settings.json` at engine start. Writing the setting and
    /// restarting the engine against the same session id therefore performs a
    /// real switch, with the conversation preserved.
    pub(super) async fn switch_model(&mut self, model: &str) {
        // The provider the engine actually connected with, when it has said.
        // Falling back to `defaultProvider` is a guess: settings can nominate
        // a provider whose credential is not present, in which case the engine
        // connects with a different one and reads a different key. Writing to
        // the nominated one then saves the choice where nothing reads it, and
        // the model silently reverts on the next start.
        let connected = self.connected_provider.clone();
        let paths = self.paths.clone();
        let provider = match connected {
            Some(provider) => Some(provider),
            None => match tokio::task::spawn_blocking(move || Settings::load(&paths)).await {
                Ok(Ok(settings)) => settings.default_provider().map(str::to_string),
                Ok(Err(error)) => {
                    return self.notice(
                        format!("Could not read settings: {error}"),
                        NoticeLevel::Error,
                    )
                }
                Err(_) => return self.notice("Settings read was interrupted.", NoticeLevel::Error),
            },
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
        // Immediate feedback uses the id, because that is all this code has.
        self.apply(UiEvent::ModelChanged {
            id: model.to_string(),
            context_limit: None,
        });
        self.notice(
            format!("Model set to {model}. Restarting the engine…"),
            NoticeLevel::Info,
        );
        self.restart_engine().await;
        // Then ask the engine, which reports the active model along with its
        // display name and context limit. Without this the status line kept
        // the raw id — "claude-opus-5" where it had read "Claude Opus 5" —
        // for the rest of the session, and lost the context limit with it.
        self.load_models().await;
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

