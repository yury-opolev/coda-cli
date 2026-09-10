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
                    Ok(result) => {
                        if let Some(provider) = &result.provider_id {
                            self.connected_provider = Some(provider.clone());
                        }
                        rows::models(&result.models, result.model.as_deref(), &result.source)
                    }
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
            // MCP: which source is authoritative depends on whose machine
            // holds the files. When this client started the core, the local
            // file *is* what the engine reads and the editor must show
            // exactly that, unresolved secret references included. When it
            // did not, the engine's own read-only inventory is the only
            // truthful answer — and it is rendered read-only rather than
            // silently disabled.
            BrowserKind::Mcp => {
                if self.access_mode.allows_local_maintenance() {
                    match config::load_mcp_servers(&self.paths) {
                        Ok(servers) => rows::mcp(&servers),
                        Err(error) => {
                            self.notice(
                                format!("Could not read MCP configuration: {error}"),
                                NoticeLevel::Error,
                            );
                            return None;
                        }
                    }
                } else {
                    if !self.require_engine("The MCP browser") {
                        return None;
                    }
                    match self.bounded(crate::api::mcp_list(&self.connection)).await {
                        Ok(result) => rows::mcp_from_engine(&result),
                        Err(error) => {
                            self.notice(
                                format!("Could not read the engine's MCP servers: {error}"),
                                NoticeLevel::Error,
                            );
                            return None;
                        }
                    }
                }
            }
            // Tasks are engine state, but the runtime persists a log per task
            // and reports outcomes over the event stream.
            BrowserKind::Tasks => {
                let logs = config::list_task_logs(&self.paths, self.state.session_id.as_deref());
                rows::tasks(&logs, &self.task_outcomes)
            }
            // Sessions come from the engine's own read-only listing, not from
            // a directory this front-end reads: the engine is the only thing
            // that knows where its transcripts live and what shape they are.
            BrowserKind::Sessions => {
                if !self.require_engine("The sessions browser") {
                    return None;
                }
                let summaries =
                    match self.bounded(crate::api::list_sessions(&self.connection, None)).await {
                        Ok(result) => result.sessions,
                        Err(error) => {
                            self.notice(
                                format!("Could not load sessions: {error}"),
                                NoticeLevel::Error,
                            );
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
                RowActions::new()
                    .on_activate(|id| A::SwitchModel(id.to_string()))
                    .on_horizontal(|id, direction| A::AdjustModelEffort { model: id.into(), direction })
                    .on_key('e', |id| A::OpenEffortPickerForModel(id.to_string()))
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
        self.reload_browser_with_status("reloaded".into()).await;
    }

    pub(super) async fn reload_browser_with_status(&mut self, status: String) {
        let Some(kind) = self.browser_kind() else {
            return;
        };
        let previous = self.browser().cloned();

        // Fetch before retiring. If the engine hiccups the old browser stays
        // exactly as it was, rather than vanishing and losing the user's place.
        let Some(mut browser) = self.build_browser(kind).await else {
            return;
        };
        if let Some(previous) = previous {
            browser.restore_navigation_from(&previous);
        }
        browser.set_status(status);

        self.retire_browser_surface();
        self.surfaces
            .push(Box::new(Self::browser_surface(kind, browser)));
        self.dirty = true;
    }

    /// Switches the active model.
    ///
    /// `session/setModel` is a real engine method, and the engine rebuilds its
    /// agent from the current model on every turn, so the switch takes effect
    /// on the next one with the running conversation kept. Nothing is
    /// restarted: writing `settings.json` and bouncing the process was a
    /// workaround for a call that did not exist, it cost the session, and it
    /// failed outright before the session had been written to disk.
    ///
    /// The setting is then persisted **only** where this client owns the file
    /// the engine reads. On an [`AccessMode::ApiOnly`] session the write is
    /// refused before it happens and the notice says so, because the engine
    /// reads its own host's settings and a durable change here would be a
    /// change nothing ever reads.
    ///
    /// [`AccessMode::ApiOnly`]: crate::local::AccessMode::ApiOnly
    pub(super) async fn switch_model(&mut self, model: &str) {
        // The picker's rows outlive the connection that filled them: a list
        // read before a sign-out is still on screen afterwards, and choosing
        // from it must not push a change at an engine that is gone.
        if !self.require_engine("Switching the model") {
            return;
        }
        // Ask the engine directly; see the doc comment for why nothing is
        // restarted.
        let result = self
            .ask::<serde_json::Value>(
                method::SET_MODEL,
                Some(serde_json::json!({ "model": model })),
            )
            .await;
        let value = match result {
            Ok(value) => value,
            Err(error) => {
                // The change may or may not have been made: an engine that
                // did not answer is not an engine that refused. Re-read
                // rather than claim either.
                self.needs_resync = true;
                return self.notice(
                    format!("Could not switch the model: {error}"),
                    NoticeLevel::Error,
                );
            }
        };
        match value.get("ok").and_then(serde_json::Value::as_bool) {
            Some(true) => {}
            Some(false) => {
                let reason = value.get("note").or_else(|| value.get("error"))
                    .and_then(serde_json::Value::as_str)
                    .filter(|text| !text.is_empty())
                    .unwrap_or("the engine did not accept the requested model");
                return self.notice(format!("Model change refused: {reason}"), NoticeLevel::Warning);
            }
            None => {
                self.needs_resync = true;
                return self.notice(
                    "Model change outcome is unknown: the engine did not confirm it. Nothing was saved.",
                    NoticeLevel::Warning,
                );
            }
        }

        // Persisted too, so the choice survives the next start — but only
        // where this client owns the file the engine reads. An engine this
        // client did not start reads its own host's settings, so writing them
        // here would report a durable change that never happens.
        if !self.owns_engine_settings() {
            self.close_browser();
            let note = self.not_saved_remotely();
            self.notice(format!("Model set to {model} for this session.{note}"), NoticeLevel::Info);
            self.load_models().await;
            return;
        }

        let saved = self.save_model_preference(model).await;

        // The session change already happened, so the screen must reflect it
        // whatever the file did. Returning early here left the browser open
        // on a list that no longer described the session, and the header
        // naming the previous model.
        self.close_browser();
        self.notice(format!("Model set to {model}."), NoticeLevel::Info);
        if let super::settings::Saved::Failed(error) = saved {
            self.notice(
                format!("The model was not saved for the next start: {error}"),
                NoticeLevel::Warning,
            );
        }
        // Ask the engine what is now active: it reports the display name and
        // context limit, so the status line does not degrade to the raw id.
        self.load_models().await;
    }

    async fn save_model_preference(&mut self, model: &str) -> super::settings::Saved {
        use super::settings::Saved;

        let paths = self.paths.clone();
        let provider = match self.connected_provider.clone() {
            Some(provider) => Some(provider),
            None => match tokio::task::spawn_blocking(move || Settings::load(&paths)).await {
                Ok(Ok(settings)) => settings.default_provider().map(str::to_string),
                Ok(Err(error)) => return Saved::Failed(format!("Could not read settings: {error}")),
                Err(_) => return Saved::Failed("Settings read was interrupted.".into()),
            },
        };
        let Some(provider) = provider else {
            return Saved::Failed(
                "The engine has not reported a provider, and no default provider is configured.".into(),
            );
        };
        let model = model.to_string();
        self.persist_engine_default(move |settings| settings.set_model_for(&provider, &model))
            .await
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
