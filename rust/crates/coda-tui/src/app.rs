//! The application loop.
//!
//! Terminal input and engine notifications arrive on independent channels and
//! are funnelled into the same reducer, so ordering between a keystroke and a
//! streamed token is explicit. Rendering happens once per iteration, only when
//! something actually changed.

use std::time::Duration;

use anyhow::{Context, Result};
use coda_client::{ClientError, Connection, Engine, EngineCommand, Inbound, Responder};
use coda_proto::messages::{self, method, server_method};
use coda_proto::Event;
use coda_render::{RenderLine, Theme};
use crossterm::event::{Event as TerminalEvent, EventStream, KeyCode, KeyEvent, MouseEventKind};
use futures::future::OptionFuture;
use futures_lite::StreamExt;
use serde_json::Value;
use tokio::sync::mpsc;
use tokio::sync::oneshot;

use crate::browsers::{self, TaskOutcome};
use crate::config::{self, Paths, PluginState, Settings};
use crate::commands::{self, Scope};
use crate::composer::{Completion, Composer};
use crate::draw;
use crate::keymap::{self, Action, Focus, KeyContext};
use crate::overlay::{Browser, Intent};
use crate::state::{PendingPrompt, UiEvent, UiState};
use crate::terminal::TerminalGuard;
use crate::transcript::NoticeLevel;
use crate::viewport::Viewport;

/// How long a turn may take before we stop waiting on shutdown.
const SHUTDOWN_GRACE: Duration = Duration::from_secs(5);
/// Rows scrolled per mouse wheel notch.
const WHEEL_ROWS: usize = 3;

/// Outcome of an in-flight `session/prompt`.
struct TurnOutcome {
    result: Result<Value, ClientError>,
}

/// The running application.
pub struct App {
    state: UiState,
    composer: Composer,
    viewport: Viewport,
    theme: Theme,
    connection: Connection,
    /// Cached rendered rows, invalidated whenever state or width changes.
    rows: Vec<RenderLine>,
    /// Width the cached rows were laid out for.
    laid_out_width: usize,
    dirty: bool,
    /// Set while a `session/prompt` is outstanding.
    turn: Option<oneshot::Receiver<Result<Value, coda_proto::ResponseError>>>,
    /// The responder for a prompt the user has not answered yet.
    pending_responder: Option<Responder>,
    /// A two-press chord armed by the previous keystroke, and when.
    armed: Option<(keymap::Chord, std::time::Instant)>,
    /// The open browser overlay, if any.
    browser: Option<Browser>,
    /// Which browser is open, so reload and actions know what to do.
    browser_kind: Option<BrowserKind>,
    /// Local Coda file locations for this session.
    paths: Paths,
    /// Outcomes reported by `event/taskCompleted`, keyed by task id.
    task_outcomes: std::collections::BTreeMap<String, TaskOutcome>,
    /// How the engine was launched, so it can be restarted in place.
    engine_command: EngineCommand,
    /// A freshly started engine waiting for the run loop to swap it in.
    restarted: Option<(Engine, mpsc::UnboundedReceiver<Inbound>)>,
}

/// Which overlay is on screen.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum BrowserKind {
    Models,
    Schedules,
    Skills,
    Plugins,
    Hooks,
    Mcp,
    Tasks,
}

impl App {
    /// Connects to an engine and completes the handshake.
    pub async fn connect(
        command: EngineCommand,
        theme: Theme,
    ) -> Result<(Self, Engine, mpsc::UnboundedReceiver<Inbound>)> {
        let (engine, inbound) = Engine::spawn(command.clone()).context("failed to start the engine")?;
        let connection = engine.connection();

        let params = serde_json::to_value(messages::InitializeParams::new("coda-tui"))?;
        let result = connection
            .request(method::INITIALIZE, Some(params))
            .await
            .context("the engine rejected the handshake")?;
        let initialized: messages::InitializeResult = serde_json::from_value(result)
            .context("the engine returned an unexpected initialize result")?;

        let mut state = UiState::new();
        state.apply(UiEvent::Connected {
            session_id: initialized.session_id,
        });

        let project_root = command
            .working_dir
            .clone()
            .unwrap_or_else(|| std::env::current_dir().unwrap_or_default());

        let app = Self {
            state,
            composer: Composer::new(),
            viewport: Viewport::new(),
            theme,
            connection,
            rows: Vec::new(),
            laid_out_width: 0,
            dirty: true,
            turn: None,
            pending_responder: None,
            armed: None,
            browser: None,
            browser_kind: None,
            paths: Paths::new(project_root),
            task_outcomes: std::collections::BTreeMap::new(),
            engine_command: command,
            restarted: None,
        };

        Ok((app, engine, inbound))
    }

    /// Runs until the user quits or the engine disconnects.
    pub async fn run(
        mut self,
        guard: &mut TerminalGuard,
        mut inbound: mpsc::UnboundedReceiver<Inbound>,
    ) -> Result<()> {
        // Holds an engine this loop started itself, so it can be shut down
        // when superseded by another restart.
        let mut owned_engine: Option<Engine> = None;
        let mut terminal_events = EventStream::new();
        let (turn_tx, mut turn_rx) = mpsc::unbounded_channel::<TurnOutcome>();

        self.load_models().await;
        self.redraw(guard)?;

        loop {
            tokio::select! {
                // Engine notifications and server-initiated requests.
                message = inbound.recv() => match message {
                    Some(message) => self.on_inbound(message),
                    None => {
                        self.notice("The engine disconnected.", NoticeLevel::Error);
                        self.redraw(guard)?;
                        break;
                    }
                },

                // Terminal input.
                event = terminal_events.next() => match event {
                    Some(Ok(event)) => self.on_terminal(event, guard).await?,
                    Some(Err(error)) => return Err(error).context("terminal input failed"),
                    None => break,
                },

                // A turn finished (delivered via the mpsc relay from the turn branch below).
                Some(outcome) = turn_rx.recv() => self.on_turn_finished(outcome),

                // The outstanding session/prompt oneshot resolves.
                //
                // OptionFuture is Pending when self.turn is None, so this branch
                // only competes when a turn is actually in flight.  Without this
                // branch the oneshot would only be polled via try_recv() AFTER
                // another branch woke the loop, which could leave the TUI stuck
                // in the "working" state if the engine responded but no other
                // event arrived.
                Some(result) = OptionFuture::from(self.turn.as_mut()) => {
                    self.turn = None;
                    // result: Result<Result<Value,ResponseError>, RecvError>.
                    // Flatten into Result<Value, ClientError>.
                    let outcome = result
                        .map_err(|_| ClientError::ConnectionClosed)
                        .and_then(|r| r.map_err(ClientError::Rpc));
                    let _ = turn_tx.send(TurnOutcome { result: outcome });
                }
            }

            // A restart stages a new engine; swap it in between iterations so
            // the inbound stream is never replaced mid-await.
            if let Some((engine, next_inbound)) = self.restarted.take() {
                let previous = std::mem::replace(&mut owned_engine, Some(engine));
                inbound = next_inbound;
                if let Some(previous) = previous {
                    let _ = previous.shutdown(SHUTDOWN_GRACE).await;
                }
            }

            if self.state.should_quit {
                break;
            }
            if self.dirty {
                self.redraw(guard)?;
            }
        }

        if let Some(engine) = owned_engine {
            let _ = engine.shutdown(SHUTDOWN_GRACE).await;
        }
        Ok(())
    }

    // -- Engine -------------------------------------------------------------

    fn on_inbound(&mut self, message: Inbound) {
        match message {
            Inbound::Notification { method, params } => {
                let event = Event::parse(&method, params.as_ref());
                if let Event::TaskCompleted {
                    task_id,
                    status,
                    description,
                    report,
                } = &event
                {
                    self.task_outcomes.insert(
                        task_id.clone(),
                        TaskOutcome {
                            status: status.clone(),
                            description: description.clone(),
                            report: report.clone(),
                        },
                    );
                }
                self.apply(UiEvent::Engine(event));
            }
            Inbound::Request {
                method,
                params,
                responder,
            } => self.on_server_request(&method, params, responder),
        }
    }

    fn on_server_request(&mut self, method: &str, params: Option<Value>, responder: Responder) {
        let params = params.unwrap_or(Value::Null);

        let prompt = match method {
            server_method::PERMISSION => serde_json::from_value::<messages::PermissionRequest>(
                params,
            )
            .ok()
            .map(|request| PendingPrompt::Permission {
                tool: request.tool_name,
                preview: request.input_preview,
            }),
            server_method::QUESTION => serde_json::from_value::<messages::QuestionRequest>(params)
                .ok()
                .map(|request| PendingPrompt::Question {
                    question: request.question,
                    options: request.options,
                    multi_select: request.multi_select,
                    allow_free_text: request.allow_free_text,
                }),
            server_method::PLAN_APPROVAL => serde_json::from_value::<
                messages::PlanApprovalRequest,
            >(params)
            .ok()
            .map(|request| PendingPrompt::PlanApproval { plan: request.plan }),
            _ => None,
        };

        match prompt {
            Some(prompt) => {
                // Only one prompt can be outstanding; a second would leave the
                // first unanswered and hang the turn.
                if let Some(previous) = self.pending_responder.take() {
                    previous.fail(
                        coda_proto::error_codes::INTERNAL_ERROR,
                        "superseded by another request",
                    );
                }
                self.pending_responder = Some(responder);
                self.apply(UiEvent::PromptRequested(prompt));
            }
            None => responder.fail(
                coda_proto::error_codes::METHOD_NOT_FOUND,
                format!("{method} is not supported by this client"),
            ),
        }
    }

    fn on_turn_finished(&mut self, outcome: TurnOutcome) {
        match outcome.result {
            Ok(value) => {
                let result: messages::PromptResult =
                    serde_json::from_value(value).unwrap_or_default();
                self.apply(UiEvent::TurnFinished {
                    interrupted: result.interrupted,
                    error: result.error,
                });
            }
            Err(error) => self.apply(UiEvent::TurnFinished {
                interrupted: false,
                error: Some(error.to_string()),
            }),
        }
    }

    /// Fetches the model list so the status bar can name the active model.
    async fn load_models(&mut self) {
        let Ok(value) = self
            .connection
            .request(method::MODELS, Some(serde_json::json!({ "refresh": false })))
            .await
        else {
            return;
        };
        let Ok(result) = serde_json::from_value::<messages::ModelsResult>(value) else {
            return;
        };
        if let Some(model) = result.models.first() {
            self.apply(UiEvent::ModelChanged {
                id: model.label().to_string(),
                context_limit: model.context_limit,
            });
        }
    }

    // -- Terminal input -----------------------------------------------------

    async fn on_terminal(
        &mut self,
        event: TerminalEvent,
        guard: &mut TerminalGuard,
    ) -> Result<()> {
        match event {
            TerminalEvent::Key(key) => self.on_key(key).await,
            TerminalEvent::Paste(text) => {
                self.composer.insert(&text);
                self.dirty = true;
            }
            TerminalEvent::Resize(..) => {
                // Force a relayout; cached rows were wrapped for the old width.
                self.laid_out_width = 0;
                self.dirty = true;
                guard.terminal().autoresize()?;
            }
            TerminalEvent::Mouse(mouse) => match mouse.kind {
                MouseEventKind::ScrollUp => {
                    self.viewport.scroll_up(WHEEL_ROWS);
                    self.dirty = true;
                }
                MouseEventKind::ScrollDown => {
                    self.viewport.scroll_down(WHEEL_ROWS);
                    self.dirty = true;
                }
                _ => {}
            },
            TerminalEvent::FocusGained | TerminalEvent::FocusLost => {}
        }
        Ok(())
    }

    async fn on_key(&mut self, key: KeyEvent) {
        // A prompt takes the keyboard until it is answered.
        if self.state.prompt.is_some() {
            self.on_prompt_key(key);
            return;
        }

        // An open overlay owns the keyboard next.
        if self.browser.is_some() {
            self.on_browser_key(key).await;
            return;
        }

        let action = keymap::resolve(key, self.key_context());
        self.dirty = true;

        // Any other keystroke disarms, so a chord only ever fires on two
        // consecutive presses of the same key.
        if !matches!(action, Action::Arm(_)) {
            self.armed = None;
        }

        match action {
            Action::Insert(c) => {
                self.composer.insert_char(c);
                self.refresh_completions();
            }
            Action::Newline => self.composer.insert_newline(),
            Action::Submit => self.submit().await,

            Action::Backspace => {
                self.composer.backspace();
                self.refresh_completions();
            }
            Action::Delete => {
                self.composer.delete();
            }
            Action::DeleteWordBack => {
                self.composer.delete_word_back();
            }
            Action::DeleteToLineStart => {
                self.composer.delete_to_line_start();
            }
            Action::DeleteToLineEnd => {
                self.composer.delete_to_line_end();
            }

            Action::MoveLeft => {
                self.composer.move_left();
            }
            Action::MoveRight => {
                self.composer.move_right();
            }
            Action::MoveUp => {
                self.composer.move_up();
            }
            Action::MoveDown => {
                self.composer.move_down();
            }
            Action::MoveWordLeft => {
                self.composer.move_word_left();
            }
            Action::MoveWordRight => {
                self.composer.move_word_right();
            }
            Action::MoveLineStart => {
                self.composer.move_line_start();
            }
            Action::MoveLineEnd => {
                self.composer.move_line_end();
            }

            Action::HistoryPrevious => {
                self.composer.history_previous();
            }
            Action::HistoryNext => {
                self.composer.history_next();
            }

            Action::CompletionRequest => self.refresh_completions(),
            Action::CompletionNext => self.composer.completion_next(),
            Action::CompletionPrevious => self.composer.completion_previous(),
            Action::CompletionAccept => {
                self.composer.accept_completion();
            }
            Action::CompletionCancel => self.composer.clear_completions(),

            Action::ScrollUp => self.viewport.scroll_up(1),
            Action::ScrollDown => self.viewport.scroll_down(1),
            Action::PageUp => self.viewport.page_up(),
            Action::PageDown => self.viewport.page_down(),
            Action::ScrollTop => self.viewport.scroll_to_top(),
            Action::ScrollBottom => self.viewport.scroll_to_bottom(),

            Action::Interrupt => {
                self.armed = None;
                self.interrupt();
            }
            Action::Arm(chord) => {
                self.armed = Some((chord, std::time::Instant::now()));
                let hint = match chord {
                    keymap::Chord::Exit if self.state.is_busy() => {
                        "Press Ctrl+C again to stop the turn."
                    }
                    keymap::Chord::Exit => "Press Ctrl+C again to exit.",
                    keymap::Chord::Interrupt => "Press Esc again to stop the turn.",
                };
                self.notice(hint, NoticeLevel::Info);
            }
            Action::Quit => self.state.should_quit = true,
            Action::Repaint => {
                // Force a relayout without touching the transcript.
                self.laid_out_width = 0;
            }
            Action::Cancel => {
                if self.composer.completion().is_active() {
                    self.composer.clear_completions();
                } else {
                    self.composer.clear();
                }
            }
            Action::ClearTranscript => self.apply(UiEvent::Cleared),
            Action::Copy | Action::Paste | Action::Confirm | Action::None => self.dirty = false,
        }
    }

    /// Routes a key to the open overlay and performs whatever it asks for.
    async fn on_browser_key(&mut self, key: KeyEvent) {
        let Some(browser) = self.browser.as_mut() else {
            return;
        };
        let intent = browser.handle(key);
        self.dirty = true;

        match intent {
            Intent::Redraw => {}
            Intent::Ignored => self.dirty = false,
            Intent::Close => self.close_browser(),
            Intent::Reload => self.reload_browser().await,
            Intent::Activate(id) => self.activate_browser_row(&id).await,
            Intent::Toggle(id) => self.toggle_browser_row(&id).await,
            Intent::Delete(id) => self.delete_browser_row(&id).await,
            Intent::Key(c, id) => self.browser_key_action(c, id).await,
        }
    }

    fn close_browser(&mut self) {
        self.browser = None;
        self.browser_kind = None;
    }

    /// Opens a browser, fetching its data from the engine.
    async fn open_browser(&mut self, kind: BrowserKind) {
        let browser = match kind {
            BrowserKind::Models => {
                match self
                    .fetch::<messages::ModelsResult>(
                        method::MODELS,
                        Some(serde_json::json!({ "refresh": false })),
                    )
                    .await
                {
                    Ok(result) => browsers::models(
                        &result.models,
                        self.state.model.as_deref(),
                        &result.source,
                    ),
                    Err(error) => return self.browser_failed("models", error),
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
                    Ok(result) => browsers::schedules(&result.schedules),
                    Err(error) => return self.browser_failed("schedules", error),
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
                    Ok(result) => browsers::skills(&result.skills),
                    Err(error) => return self.browser_failed("skills", error),
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
                    Ok(result) => browsers::plugins(&result.plugins),
                    Err(error) => return self.browser_failed("plugins", error),
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
                    Ok(result) => browsers::hooks(&result.hooks),
                    Err(error) => return self.browser_failed("hooks", error),
                }
            }
            // MCP configuration lives in local JSON, so it needs no engine call.
            BrowserKind::Mcp => match config::load_mcp_servers(&self.paths) {
                Ok(servers) => browsers::mcp(&servers),
                Err(error) => {
                    return self.notice(
                        format!("Could not read MCP configuration: {error}"),
                        NoticeLevel::Error,
                    )
                }
            },
            // Tasks are engine state, but the runtime persists a log per task
            // and reports outcomes over the event stream.
            BrowserKind::Tasks => {
                let logs = config::list_task_logs(&self.paths, self.state.session_id.as_deref());
                browsers::tasks(&logs, &self.task_outcomes)
            }
        };

        self.browser = Some(browser);
        self.browser_kind = Some(kind);
        self.dirty = true;
    }

    fn browser_failed(&mut self, what: &str, error: ClientError) {
        self.notice(
            format!("Could not load {what}: {error}"),
            NoticeLevel::Error,
        );
    }

    async fn reload_browser(&mut self) {
        if let Some(kind) = self.browser_kind {
            // Rebuilding preserves the selected row by id.
            let selected = self
                .browser
                .as_ref()
                .and_then(|b| b.selected_id().map(str::to_string));
            self.open_browser(kind).await;
            if let (Some(browser), Some(_)) = (self.browser.as_mut(), selected) {
                browser.set_status("reloaded");
            }
        }
    }

    /// Handles Enter on a row.
    async fn activate_browser_row(&mut self, id: &str) {
        match self.browser_kind {
            Some(BrowserKind::Models) => self.switch_model(id).await,
            _ => self.dirty = true,
        }
    }

    /// Switches the active model.
    ///
    /// `coda serve` exposes no model-switch method, but the model is read from
    /// `~/.coda/settings.json` at engine start. Writing the setting and
    /// restarting the engine against the same session id therefore performs a
    /// real switch, with the conversation preserved.
    async fn switch_model(&mut self, model: &str) {
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
    async fn toggle_browser_row(&mut self, id: &str) {
        match self.browser_kind {
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

    /// Restarts the engine in place, resuming the current session.
    ///
    /// Settings are read once at engine start, so anything that changes them
    /// only takes effect across a restart. `initialize` accepts a session id,
    /// so the conversation survives.
    async fn restart_engine(&mut self) {
        let session_id = self.state.session_id.clone();

        let (engine, inbound) = match Engine::spawn(self.engine_command.clone()) {
            Ok(pair) => pair,
            Err(error) => {
                return self.notice(
                    format!("Could not restart the engine: {error}"),
                    NoticeLevel::Error,
                )
            }
        };

        let connection = engine.connection();
        let mut params = messages::InitializeParams::new("coda-tui");
        if let Some(session_id) = session_id {
            params = params.resume(session_id);
        }

        let params = serde_json::to_value(params).unwrap_or_default();
        match connection.request(method::INITIALIZE, Some(params)).await {
            Ok(value) => {
                let initialized: messages::InitializeResult =
                    serde_json::from_value(value).unwrap_or(messages::InitializeResult {
                        protocol_version: coda_proto::PROTOCOL_VERSION.to_string(),
                        session_id: String::new(),
                        server_info: "coda".into(),
                        telemetry_log_path: None,
                    });

                self.connection = connection;
                self.restarted = Some((engine, inbound));
                if !initialized.session_id.is_empty() {
                    self.state.session_id = Some(initialized.session_id);
                }
                self.notice("Engine restarted.", NoticeLevel::Info);
            }
            Err(error) => self.notice(
                format!("The restarted engine rejected the handshake: {error}"),
                NoticeLevel::Error,
            ),
        }
    }

    async fn delete_browser_row(&mut self, id: &str) {
        if self.browser_kind != Some(BrowserKind::Schedules) {
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

    async fn browser_key_action(&mut self, key: char, id: Option<String>) {
        match (self.browser_kind, key) {
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
            _ => self.dirty = false,
        }
    }

    /// Updates a git-installed plugin by pulling in its directory.
    ///
    /// The engine has no update RPC, but plugins live in known directories, so
    /// the update is a plain `git pull` the front-end can run itself.
    async fn update_plugin(&mut self, name: &str) {
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

    /// Issues a request and deserialises its result.
    async fn fetch<T: serde::de::DeserializeOwned>(
        &self,
        rpc_method: &str,
        params: Option<Value>,
    ) -> Result<T, ClientError> {
        let value = self.connection.request(rpc_method, params).await?;
        serde_json::from_value(value).map_err(ClientError::Serde)
    }

    /// Answers an open prompt.
    fn on_prompt_key(&mut self, key: KeyEvent) {        let Some(prompt) = self.state.prompt.clone() else {
            return;
        };
        self.dirty = true;

        let (allowed, answer) = match (&prompt, key.code) {
            // Yes/no prompts.
            (PendingPrompt::Permission { .. } | PendingPrompt::PlanApproval { .. }, code) => {
                match code {
                    KeyCode::Char('y') | KeyCode::Char('Y') | KeyCode::Enter => (true, None),
                    KeyCode::Char('n') | KeyCode::Char('N') | KeyCode::Esc => (false, None),
                    _ => return,
                }
            }
            // Numbered choices.
            (PendingPrompt::Question { options, .. }, KeyCode::Char(c))
                if c.is_ascii_digit() =>
            {
                let index = c.to_digit(10).unwrap_or(0) as usize;
                match index.checked_sub(1).and_then(|i| options.get(i)) {
                    Some(option) => (true, Some(option.clone())),
                    None => return,
                }
            }
            (PendingPrompt::Question { options, .. }, KeyCode::Enter) => {
                (true, options.first().cloned())
            }
            (PendingPrompt::Question { .. }, KeyCode::Esc) => (false, None),
            _ => return,
        };

        if let Some(responder) = self.pending_responder.take() {
            let reply = match &prompt {
                PendingPrompt::Permission { .. } => {
                    serde_json::to_value(messages::PermissionResponse { allow: allowed })
                }
                PendingPrompt::PlanApproval { .. } => {
                    serde_json::to_value(messages::PlanApprovalResponse { approve: allowed })
                }
                PendingPrompt::Question { .. } => {
                    serde_json::to_value(messages::QuestionResponse {
                        answer: answer.clone().unwrap_or_default(),
                    })
                }
            };
            match reply {
                Ok(value) => responder.respond(value),
                Err(error) => responder.fail(
                    coda_proto::error_codes::INTERNAL_ERROR,
                    error.to_string(),
                ),
            }
        }

        self.apply(UiEvent::PromptAnswered { allowed, answer });
    }

    // -- Actions ------------------------------------------------------------

    async fn submit(&mut self) {
        let text = self.composer.take_submission();
        if text.trim().is_empty() {
            return;
        }

        if let Some(invocation) = commands::parse(&text) {
            self.run_command(invocation).await;
            return;
        }

        // A message typed mid-turn is steered into the running turn rather
        // than dropped or forced to wait for it to finish.
        if self.state.is_busy() {
            self.steer(text).await;
            return;
        }

        self.apply(UiEvent::Submitted { text: text.clone() });

        let params = serde_json::to_value(messages::PromptParams::text(text)).unwrap_or_default();
        match self.connection.send_request(method::PROMPT, Some(params)) {
            Ok(receiver) => self.turn = Some(receiver),
            Err(error) => self.apply(UiEvent::TurnFinished {
                interrupted: false,
                error: Some(error.to_string()),
            }),
        }
    }

    async fn steer(&mut self, text: String) {
        let params = serde_json::to_value(messages::SteerParams { text: text.clone() }).ok();
        let response = self.connection.request(method::STEER, params).await;

        let result = response
            .ok()
            .and_then(|value| serde_json::from_value::<messages::SteerResult>(value).ok());

        match result {
            Some(r) if r.ok => {
                self.apply(UiEvent::Queued { text, id: r.message_id });
            }
            Some(_) => {
                // Engine accepted the call but rejected the steer (ok:false).
                // Showing the message as "queued" would be misleading because it
                // will never be delivered; surface a warning instead.
                self.notice(
                    "The engine could not queue the message right now.",
                    NoticeLevel::Warning,
                );
            }
            None => {
                // RPC error or unexpected response shape — message not delivered.
                self.notice(
                    "Failed to deliver the steering message to the engine.",
                    NoticeLevel::Error,
                );
            }
        }
    }

    fn interrupt(&mut self) {
        self.apply(UiEvent::InterruptRequested);
        if let Err(error) = self
            .connection
            .notify(method::INTERRUPT, Some(serde_json::json!({})))
        {
            self.notice(format!("Interrupt failed: {error}"), NoticeLevel::Error);
        }
    }

    async fn run_command(&mut self, invocation: commands::Invocation) {
        let Some(spec) = commands::lookup(&invocation.name) else {
            self.notice(
                format!("Unknown command: /{}. Try /help.", invocation.name),
                NoticeLevel::Warning,
            );
            return;
        };

        match spec.name {
            "help" => self.output(commands::help(invocation.first())),
            "clear" => self.apply(UiEvent::Cleared),
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
            "mcp" if invocation.args.is_empty() => self.open_browser(BrowserKind::Mcp).await,
            "tasks" => self.open_browser(BrowserKind::Tasks).await,
            "version" => self.output(format!(
                "coda-tui {} (Rust front-end)",
                env!("CARGO_PKG_VERSION")
            )),
            "doctor" => self.output(self.doctor_text()),
            _ if spec.scope == Scope::Engine => self.run_engine_command(spec, invocation).await,
            _ => self.notice(
                format!("/{} is not implemented yet.", spec.name),
                NoticeLevel::Warning,
            ),
        }
    }

    async fn run_engine_command(
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

    fn set_theme(&mut self, name: Option<&str>) {
        match name {
            None => self.output(format!(
                "Theme: {}\nAvailable: {}",
                self.theme.name,
                Theme::names().join(", ")
            )),
            Some(name) => match Theme::by_name(name) {
                Some(theme) => {
                    let depth = self.theme.depth();
                    self.theme = theme.with_depth(depth);
                    self.notice(format!("Theme: {}", self.theme.name), NoticeLevel::Info);
                }
                None => self.notice(
                    format!(
                        "Unknown theme: {name}. Available: {}",
                        Theme::names().join(", ")
                    ),
                    NoticeLevel::Warning,
                ),
            },
        }
    }

    fn status_text(&self) -> String {
        let mut out = String::from("Session\n");
        out.push_str(&format!(
            "  id       {}\n",
            self.state.session_id.as_deref().unwrap_or("(none)")
        ));
        out.push_str(&format!(
            "  model    {}\n",
            self.state.model.as_deref().unwrap_or("(unknown)")
        ));
        out.push_str(&format!("  activity {}\n", self.state.activity.label()));
        out.push_str(&format!("  theme    {}\n", self.theme.name));
        out.push_str(&format!(
            "  tools    {}",
            self.state.display_mode.as_str()
        ));
        out
    }

    fn context_text(&self) -> String {
        let usage = self.state.usage;
        let mut out = String::from("Context usage\n");
        out.push_str(&format!("  input   {} tokens\n", usage.input_tokens));
        out.push_str(&format!("  output  {} tokens\n", usage.output_tokens));
        match usage.percent_used() {
            Some(percent) => out.push_str(&format!(
                "  window  {percent}% of {} tokens",
                usage.context_limit
            )),
            None => out.push_str("  window  (unknown)"),
        }
        out
    }

    fn cost_text(&self) -> String {
        let usage = self.state.usage;
        format!(
            "Token usage\n  input   {}\n  output  {}\n  total   {}",
            usage.input_tokens,
            usage.output_tokens,
            usage.input_tokens + usage.output_tokens
        )
    }

    fn doctor_text(&self) -> String {
        let mut out = String::from("Diagnostics\n");
        out.push_str(&format!(
            "  front-end   coda-tui {}\n",
            env!("CARGO_PKG_VERSION")
        ));
        out.push_str(&format!(
            "  session     {}\n",
            self.state.session_id.as_deref().unwrap_or("(none)")
        ));
        out.push_str(&format!(
            "  connected   {}\n",
            if self.connection.is_closed() {
                "no"
            } else {
                "yes"
            }
        ));
        out.push_str(&format!(
            "  cwd         {}\n",
            std::env::current_dir()
                .map(|p| p.display().to_string())
                .unwrap_or_else(|_| "(unknown)".into())
        ));
        out.push_str(&format!("  commands    {}", commands::COMMANDS.len()));
        out
    }

    // -- Helpers ------------------------------------------------------------

    fn key_context(&self) -> KeyContext {
        let (line, _) = self.composer.cursor_position();
        KeyContext {
            focus: if self.state.prompt.is_some() {
                Focus::Overlay
            } else if self.composer.completion().is_active() {
                Focus::Completion
            } else {
                Focus::Composer
            },
            busy: self.state.is_busy(),
            composer_empty: self.composer.is_empty(),
            on_first_line: line == 0,
            on_last_line: line + 1 >= self.composer.line_count(),
            // An armed chord expires, so a press now and a press a minute later
            // are two separate first presses rather than a confirmation.
            armed: self
                .armed
                .filter(|(_, at)| at.elapsed() < keymap::CHORD_WINDOW)
                .map(|(chord, _)| chord),
        }
    }

    /// Recomputes the completion popup for the token under the cursor.
    fn refresh_completions(&mut self) {
        let Some((token, range)) = self.composer.completion_context() else {
            self.composer.clear_completions();
            return;
        };

        if !token.starts_with('/') {
            self.composer.clear_completions();
            return;
        }

        let candidates: Vec<Completion> = commands::complete(&token)
            .into_iter()
            .map(|spec| {
                Completion::new(format!("/{}", spec.name), Some(spec.summary.to_string()))
            })
            .collect();

        if candidates.is_empty() {
            self.composer.clear_completions();
        } else {
            self.composer.set_completions(candidates, range);
        }
    }

    fn apply(&mut self, event: UiEvent) {
        self.state.apply(event);
        self.laid_out_width = 0;
        self.dirty = true;
    }

    fn notice(&mut self, text: impl Into<String>, level: NoticeLevel) {
        self.apply(UiEvent::Notice {
            text: text.into(),
            level,
        });
    }

    fn output(&mut self, text: impl Into<String>) {
        self.apply(UiEvent::CommandOutput { text: text.into() });
    }

    fn redraw(&mut self, guard: &mut TerminalGuard) -> Result<()> {
        let size = guard.terminal().size()?;
        let regions = draw::layout(
            ratatui::layout::Rect::new(0, 0, size.width, size.height),
            self.composer.line_count(),
            self.viewport.is_scrollable(),
        );
        let width = regions.transcript.width as usize;

        if width != self.laid_out_width {
            self.rows = self.state.transcript.render(width, self.state.display_mode);
            self.laid_out_width = width;
        }
        self.viewport
            .update(self.rows.len(), regions.transcript.height as usize);

        let state = &self.state;
        let composer = &self.composer;
        let viewport = &self.viewport;
        let rows = &self.rows;
        let theme = &self.theme;
        let browser = self.browser.as_ref();

        guard.terminal().draw(|frame| {
            draw::draw(frame, state, composer, viewport, rows, theme, browser);
        })?;

        self.dirty = false;
        Ok(())
    }

    /// Closes the engine down cleanly.
    pub async fn shutdown(self, engine: Engine) {
        if let Some(responder) = self.pending_responder {
            responder.fail(
                coda_proto::error_codes::REQUEST_CANCELLED,
                "the client is shutting down",
            );
        }
        let _ = self
            .connection
            .request(method::SHUTDOWN, Some(serde_json::json!({})))
            .await;
        let _ = engine.shutdown(SHUTDOWN_GRACE).await;
    }
}

/// Formats an engine response for display.
fn format_result(command: &str, value: &Value) -> String {
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

fn pretty(value: &Value) -> String {
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

