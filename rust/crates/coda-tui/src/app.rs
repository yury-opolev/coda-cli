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
use crossterm::event::{
    Event as TerminalEvent, EventStream, KeyCode, KeyEvent, KeyEventKind, MouseEventKind,
};
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
use crate::viewport::{Viewport, ViewportAnchor};

/// How long a turn may take before we stop waiting on shutdown.
const SHUTDOWN_GRACE: Duration = Duration::from_secs(5);
/// Rows scrolled per mouse wheel notch.
const WHEEL_ROWS: usize = 3;
/// Minimum interval between streaming (non-critical) frames — 30 FPS.
///
/// Matches C# `UiActor.MinStreamingFrameIntervalMs = 33`.
const MIN_STREAMING_FRAME_MS: u64 = 33;

/// Outcome of an in-flight `session/prompt`.
struct TurnOutcome {
    result: Result<Value, ClientError>,
}

/// The running application.
/// What the startup banner needs before the UI takes over the terminal.
#[derive(Debug, Clone, Default)]
pub struct SessionSnapshot {
    pub provider: Option<String>,
    pub model: Option<String>,
}

pub struct App {
    state: UiState,    composer: Composer,
    viewport: Viewport,
    theme: Theme,
    connection: Connection,
    /// Cached rendered rows, invalidated whenever state or width changes.
    rows: Vec<RenderLine>,
    /// Per-block start-row table, parallel to `state.transcript.blocks()`.
    ///
    /// `block_starts[i]` is the index into `rows` where block `i` begins.  A
    /// sentinel entry equal to `rows.len()` is appended.  Rebuilt together
    /// with `rows` whenever the layout is invalidated.
    block_starts: Vec<usize>,
    /// Width the cached rows were laid out for.
    laid_out_width: usize,
    dirty: bool,
    /// Set when a critical event arrived since the last frame.  Critical frames
    /// are not throttled.  Matches C# `UiActor.IsCritical`.
    critical_dirty: bool,
    /// When `Some`, a non-critical frame is deferred until this instant.
    frame_deadline: Option<tokio::time::Instant>,
    /// When the most recent frame was drawn (wall-clock monotonic).
    last_frame_at: Option<std::time::Instant>,
    /// Stable position anchor captured when the viewport detaches.
    ///
    /// Resolved to a new global row on every reflow (width change) so the
    /// user's reading position stays stable while the model is typing.
    detached_anchor: Option<ViewportAnchor>,
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
    /// Images staged by `/image` to be sent with the next user turn.
    staged_images: Vec<messages::WireImage>,
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
    Sessions,
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
            block_starts: Vec::new(),
            laid_out_width: 0,
            dirty: true,
            critical_dirty: false,
            frame_deadline: None,
            last_frame_at: None,
            detached_anchor: None,
            turn: None,
            pending_responder: None,
            armed: None,
            browser: None,
            browser_kind: None,
            paths: Paths::new(project_root),
            task_outcomes: std::collections::BTreeMap::new(),
            engine_command: command,
            restarted: None,
            staged_images: Vec::new(),
        };

        Ok((app, engine, inbound))
    }

    /// Runs until the user quits or the engine disconnects.
    /// Runs the UI loop, returning the summary the caller prints on exit.
    ///
    /// The summary is produced here rather than by the caller because the loop
    /// consumes `self`: usage and session id are only final once it returns.
    pub async fn run(
        mut self,
        guard: &mut TerminalGuard,
        mut inbound: mpsc::UnboundedReceiver<Inbound>,
        started_at: std::time::Instant,
    ) -> Result<crate::branding::ExitSummary> {
        // Holds an engine this loop started itself, so it can be shut down
        // when superseded by another restart.
        let mut owned_engine: Option<Engine> = None;
        let mut terminal_events = EventStream::new();
        let (turn_tx, mut turn_rx) = mpsc::unbounded_channel::<TurnOutcome>();

        self.load_models().await;
        // Show the setup wizard welcome on first run.
        self.check_first_run();
        self.redraw(guard)?;

        loop {
            // Compute the frame deadline for the timer branch.  When no
            // deferred frame is pending, a far-future deadline is used so the
            // branch competes but never wins before being explicitly armed.
            let frame_deadline = self.frame_deadline.unwrap_or_else(|| {
                tokio::time::Instant::now() + Duration::from_secs(86400)
            });

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

                // A deferred streaming frame is due.
                _ = tokio::time::sleep_until(frame_deadline), if self.frame_deadline.is_some() => {
                    self.frame_deadline = None;
                    // Fall through to the redraw logic below.
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
            self.maybe_redraw(guard)?;
        }

        if let Some(engine) = owned_engine {
            let _ = engine.shutdown(SHUTDOWN_GRACE).await;
        }
        Ok(self.exit_summary(started_at.elapsed()))
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
            // Windows reports key *release* (and repeat) events as well as
            // presses. Acting on a release is wrong for every binding, but it
            // is actively broken for the two-press chords: the release of the
            // first Ctrl+C disarms the chord immediately, so the second press
            // only ever re-arms and the app can never be exited from the
            // keyboard. Repeats are kept so held keys still autorepeat.
            TerminalEvent::Key(key)
                if matches!(key.kind, KeyEventKind::Press | KeyEventKind::Repeat) =>
            {
                self.on_key(key).await
            }
            TerminalEvent::Key(_) => {}
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
                    self.capture_anchor_if_following();
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

            Action::ScrollUp => {
                self.capture_anchor_if_following();
                self.viewport.scroll_up(1);
            }
            Action::ScrollDown => self.viewport.scroll_down(1),
            Action::PageUp => {
                self.capture_anchor_if_following();
                self.viewport.page_up();
            }
            Action::PageDown => self.viewport.page_down(),
            Action::ScrollTop => {
                self.capture_anchor_if_following();
                self.viewport.scroll_to_top();
            }
            Action::ScrollBottom => {
                self.detached_anchor = None;
                self.viewport.scroll_to_bottom();
            }

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
            Action::Copy => self.copy_to_clipboard(),
            Action::Paste | Action::Confirm | Action::None => self.dirty = false,
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
                        return self.notice("Could not load sessions.", NoticeLevel::Error)
                    }
                };
                if summaries.is_empty() {
                    return self.notice(
                        "No sessions found. Start a conversation to create one.",
                        NoticeLevel::Info,
                    );
                }
                browsers::sessions(&summaries)
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
        // A prompt needs text, staged images, or both.
        let has_content = !text.trim().is_empty() || !self.staged_images.is_empty();
        if !has_content {
            return;
        }

        if let Some(invocation) = commands::parse(&text) {
            // Commands are dispatched without consuming staged images; the images
            // remain for the next real user turn.
            self.run_command(invocation).await;
            return;
        }

        // A message typed mid-turn is steered into the running turn rather
        // than dropped or forced to wait for it to finish.
        if self.state.is_busy() {
            self.steer(text).await;
            return;
        }

        // Use the text as the displayed turn label; blank text with images
        // still needs something in the transcript.
        let display = if text.is_empty() {
            "[image]".to_string()
        } else {
            text.clone()
        };
        self.apply(UiEvent::Submitted { text: display });

        let params = serde_json::to_value(messages::PromptParams {
            text: if text.is_empty() { None } else { Some(text) },
            images: std::mem::take(&mut self.staged_images),
        })
        .unwrap_or_default();
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

    /// What the startup banner needs to know before the UI takes the terminal.
    ///
    /// Provider and model come from settings rather than the engine, because
    /// the banner is printed before the first turn and there is nothing to ask
    /// yet — and because naming the wrong provider is exactly the mistake the
    /// banner exists to prevent.
    pub fn session_snapshot(&self) -> SessionSnapshot {
        let settings = Settings::load(&self.paths).ok();
        let provider = settings
            .as_ref()
            .and_then(|s| s.default_provider().map(str::to_owned));
        let model = self.state.model.clone().or_else(|| {
            let settings = settings.as_ref()?;
            let provider = provider.as_deref()?;
            settings.model_for(provider).map(str::to_owned)
        });
        SessionSnapshot { provider, model }
    }

    /// Builds the exit summary from the session's final state.
    pub fn exit_summary(&self, duration: std::time::Duration) -> crate::branding::ExitSummary {
        let snapshot = self.session_snapshot();
        let settings = Settings::load(&self.paths).ok();
        let effort = match (&snapshot.provider, &snapshot.model) {
            (Some(p), Some(m)) => settings
                .as_ref()
                .and_then(|s| s.effort_for(p, m).map(str::to_owned)),
            _ => None,
        };

        crate::branding::ExitSummary {
            duration,
            message_count: self.state.transcript.blocks().len(),
            provider_id: snapshot.provider.unwrap_or_else(|| "—".into()),
            model: snapshot.model.unwrap_or_else(|| "—".into()),
            effort,
            input_tokens: self.state.usage.input_tokens.max(0) as u64,
            output_tokens: self.state.usage.output_tokens.max(0) as u64,
            session_id: self.state.session_id.clone(),
            working_directory: self.paths.project_root.to_string_lossy().into_owned(),
        }
    }

    fn key_context(&self) -> KeyContext {        let (line, _) = self.composer.cursor_position();
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
        if is_critical_event(&event) {
            self.critical_dirty = true;
        } else {
            self.dirty = true;
        }
        self.state.apply(event);
        self.laid_out_width = 0;
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

    /// Redraws immediately if warranted, or schedules a deferred frame.
    ///
    /// Critical events bypass the 30 FPS throttle (C# `UiActor.IsCritical`).
    /// Streaming events defer to the deadline so a burst of deltas at >30 FPS
    /// does not cause 60+ redraws per second, while the deferred timer
    /// guarantees the last frame is drawn even if no further events arrive.
    fn maybe_redraw(&mut self, guard: &mut TerminalGuard) -> Result<()> {
        let has_work = self.dirty || self.critical_dirty;
        if !has_work {
            return Ok(());
        }

        if self.critical_dirty {
            self.critical_dirty = false;
            self.dirty = false;
            self.frame_deadline = None;
            return self.redraw(guard);
        }

        // Non-critical: honour the 30 FPS cap.
        let min_interval = Duration::from_millis(MIN_STREAMING_FRAME_MS);
        let elapsed = self
            .last_frame_at
            .map(|t| t.elapsed())
            .unwrap_or(min_interval);

        if elapsed >= min_interval {
            self.dirty = false;
            self.frame_deadline = None;
            self.redraw(guard)
        } else {
            // Defer: arm the timer for the remaining slice.
            if self.frame_deadline.is_none() {
                let remaining = min_interval - elapsed;
                self.frame_deadline = Some(tokio::time::Instant::now() + remaining);
            }
            Ok(())
        }
    }

    fn redraw(&mut self, guard: &mut TerminalGuard) -> Result<()> {
        let size = guard.terminal().size()?;
        let regions = draw::layout(
            ratatui::layout::Rect::new(0, 0, size.width, size.height),
            self.composer.line_count(),
            self.viewport.is_scrollable(),
        );
        let width = regions.transcript.width as usize;
        let height = regions.transcript.height as usize;

        if width != self.laid_out_width {
            let was_following = self.viewport.is_following();
            let (rows, starts) = self
                .state
                .transcript
                .render_with_block_starts(width, self.state.display_mode);
            self.rows = rows;
            self.block_starts = starts;
            self.laid_out_width = width;

            // Anchor-aware reflow: restore the detached position rather than
            // clamping, so the user's reading position survives a resize or
            // streaming growth above the viewport.
            if was_following {
                self.viewport.update(self.rows.len(), height);
            } else {
                let anchor_row = self.detached_anchor.and_then(|a| {
                    self.block_starts
                        .get(a.block_index)
                        .map(|&s| s + a.row_within_block)
                });
                self.viewport
                    .update_with_anchor(self.rows.len(), height, anchor_row.unwrap_or(0));
            }
        } else {
            self.viewport.update(self.rows.len(), height);
        }

        // Compose the pin row when the user prompt has scrolled out of view.
        let pin_text = self.compose_pin(width);

        let state = &self.state;
        let composer = &self.composer;
        let viewport = &self.viewport;
        let rows = &self.rows;
        let theme = &self.theme;
        let browser = self.browser.as_ref();
        let pin = pin_text.as_deref();

        guard.terminal().draw(|frame| {
            draw::draw_with_pin(frame, state, composer, viewport, rows, theme, browser, pin);
        })?;

        self.last_frame_at = Some(std::time::Instant::now());
        self.dirty = false;
        self.critical_dirty = false;
        Ok(())
    }

    /// Computes the pin text for the current frame, or `None` when the pin
    /// should not be shown.
    fn compose_pin(&self, width: usize) -> Option<String> {
        if !self.state.is_busy() {
            return None;
        }
        // Find the last non-pending user block.
        let (block_idx, user_text) = self
            .state
            .transcript
            .blocks()
            .iter()
            .enumerate()
            .rev()
            .find_map(|(i, b)| {
                if let crate::transcript::Block::User { text, pending: false, .. } = b {
                    Some((i, text.as_str()))
                } else {
                    None
                }
            })?;

        let block_start = self.block_starts.get(block_idx).copied()?;
        let block_end = self.block_starts.get(block_idx + 1).copied().unwrap_or(block_start);

        if !crate::pin::should_show(
            true,
            Some(block_start),
            block_end,
            self.viewport.offset(),
            self.viewport.height(),
        ) {
            return None;
        }

        crate::pin::compose(user_text, width)
    }

    /// Captures a viewport anchor from the current offset if the viewport is
    /// currently following (about to be detached by a scroll).
    fn capture_anchor_if_following(&mut self) {
        if !self.viewport.is_following() {
            return;
        }
        self.detached_anchor = self.compute_anchor();
    }

    /// Computes a `ViewportAnchor` from the current viewport offset and block layout.
    fn compute_anchor(&self) -> Option<ViewportAnchor> {
        if self.block_starts.is_empty() {
            return None;
        }
        let offset = self.viewport.offset();
        // Binary search: find the last block whose start <= offset.
        let i = self.block_starts.partition_point(|&s| s <= offset);
        let block_index = i.saturating_sub(1);
        let row_within_block = offset.saturating_sub(
            self.block_starts.get(block_index).copied().unwrap_or(0),
        );
        Some(ViewportAnchor {
            block_index,
            row_within_block,
        })
    }

    /// Copies the visible transcript to the clipboard (Ctrl+Y).
    ///
    /// If nothing is visible, the call is a no-op.  A failure to access the
    /// clipboard (e.g. no display server) is reported as a notice rather than
    /// crashing the application.
    fn copy_to_clipboard(&mut self) {
        let text = crate::selection::copy_visible_text(&self.rows, self.viewport.visible_range());
        if text.is_empty() {
            self.dirty = false;
            return;
        }
        match arboard::Clipboard::new().and_then(|mut c| c.set_text(&text)) {
            Ok(()) => {
                self.notice("Copied transcript to clipboard.", NoticeLevel::Info);
            }
            Err(err) => {
                self.notice(
                    format!("Could not access the clipboard: {err}"),
                    NoticeLevel::Warning,
                );
            }
        }
    }

    /// Checks for a first run and surfaces the setup wizard notice.
    fn check_first_run(&mut self) {
        if crate::setup::is_first_run(&self.paths) {
            self.notice(crate::setup::WELCOME_TEXT, NoticeLevel::Info);
        }
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

    /// `/setup` — re-run the setup wizard.
    fn cmd_setup(&mut self) {
        use crate::setup;
        let providers = setup::provider_selection_prompt();
        self.output(format!(
            "Setup wizard\n\
             \n\
             {providers}\n\
             \n\
             Choose a provider and run /login <id> to authenticate.\n\
             \n\
             {}",
            "[SEAM: OAuth login handoff requires coda-auth login RPCs, \
             being added by another agent.  Once available, /login will \
             complete the authentication flow interactively.]"
        ));
    }

    // -- Slash command handlers (local / config scope) -----------------------

    /// Submits a programmatic prompt to the engine on behalf of a command.
    ///
    /// Used by `/init` and `/skill` to inject model-directed work into the
    /// running session without touching the composer.
    async fn submit_programmatic(&mut self, text: String) {
        if self.state.is_busy() {
            self.notice("A turn is already running; try again when ready.", NoticeLevel::Warning);
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

    /// `/init` — ask the agent to generate a CLAUDE.md for this project.
    async fn cmd_init(&mut self) {
        let claude_md = self.paths.project_root.join("CLAUDE.md");
        if claude_md.exists() {
            self.notice(
                "CLAUDE.md already exists; not overwriting. Use /memory to view it.",
                NoticeLevel::Warning,
            );
            return;
        }
        let prompt = concat!(
            "Analyze this codebase and write a concise CLAUDE.md that captures: ",
            "the project purpose, key architecture decisions, important conventions, ",
            "build/test commands, and any gotchas worth knowing. ",
            "Write ONLY the raw Markdown content to CLAUDE.md using the write_file tool — ",
            "no additional commentary, no code fences around the file content."
        );
        self.notice("Sending analysis request to agent…", NoticeLevel::Info);
        self.submit_programmatic(prompt.to_string()).await;
    }

    /// `/memory` — display CLAUDE.md if it exists.
    fn cmd_memory(&mut self) {
        let claude_md = self.paths.project_root.join("CLAUDE.md");
        self.output(format!("CLAUDE.md path: {}", claude_md.display()));
        match std::fs::read_to_string(&claude_md) {
            Ok(contents) => self.output(contents),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => self.notice(
                "CLAUDE.md not found. Run /init to generate one for this project.",
                NoticeLevel::Warning,
            ),
            Err(e) => self.notice(format!("Could not read CLAUDE.md: {e}"), NoticeLevel::Error),
        }
    }

    /// `/output-style [<style>]` — show or set the response style persona.
    async fn cmd_output_style(&mut self, invocation: &commands::Invocation) {
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
    async fn cmd_permissions(&mut self, invocation: &commands::Invocation) {
        let arg = invocation.first().map(str::to_string);
        let paths = self.paths.clone();

        let result = tokio::task::spawn_blocking(move || -> Result<String, config::ConfigError> {
            let mut settings = config::Settings::load(&paths)?;

            if let Some(ref raw) = arg {
                match parse_permission_mode(raw) {
                    Some(canonical) => {
                        settings.set_permission_mode(canonical);
                        settings.save()?;
                        let note = if canonical == "bypass" {
                            " (YOLO — tools run without asking)"
                        } else {
                            ""
                        };
                        Ok(format!("Permission mode set to {canonical}{note}. Restart the engine to apply."))
                    }
                    None => Ok(format!(
                        "Unknown mode '{raw}'. Use: default | acceptEdits | plan | bypass"
                    )),
                }
            } else {
                let current = settings.permission_mode().unwrap_or("default");
                Ok(format!(
                    "Permission mode: {current}\nModes: default (ask), acceptEdits (auto-edit), plan (read-only), bypass (yolo: allow all)"
                ))
            }
        })
        .await;

        match result {
            Ok(Ok(text)) => self.output(text),
            Ok(Err(e)) => self.notice(format!("Settings error: {e}"), NoticeLevel::Error),
            Err(_) => self.notice("Settings read was interrupted.", NoticeLevel::Error),
        }
    }

    /// `/yolo` — grant bypass-permissions mode. Explicit, loud, and impossible to miss.
    async fn cmd_yolo(&mut self) {
        let paths = self.paths.clone();
        let result = tokio::task::spawn_blocking(move || -> Result<(), config::ConfigError> {
            let mut settings = config::Settings::load(&paths)?;
            settings.set_permission_mode("bypass");
            settings.save()
        })
        .await;

        match result {
            Ok(Ok(())) => {
                // The warning appears first in the transcript to make the state
                // change impossible to overlook before the confirmation notice.
                self.notice(
                    "⚠  YOLO mode: tools will run without asking for permission.",
                    NoticeLevel::Warning,
                );
                self.notice(
                    "Restart the engine to apply. Use /permissions default to revert.",
                    NoticeLevel::Info,
                );
            }
            Ok(Err(e)) => self.notice(format!("Could not save setting: {e}"), NoticeLevel::Error),
            Err(_) => self.notice("Settings write was interrupted.", NoticeLevel::Error),
        }
    }

    /// `/provider [<id>]` — show the configured provider or switch to a different one.
    async fn cmd_provider(&mut self, invocation: &commands::Invocation) {
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
    async fn cmd_headers(&mut self, invocation: &commands::Invocation) {
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
    async fn cmd_log(&mut self, invocation: &commands::Invocation) {
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

    /// `/marketplace [list | add <source> | remove <name>]`.
    async fn cmd_marketplace(&mut self, invocation: &commands::Invocation) {
        let words = invocation.words();
        let paths = self.paths.clone();

        enum MktOp {
            List,
            Add(String),
            Remove(String),
            Unsupported(String),
            BadUsage,
        }

        let op = match words.as_slice() {
            [] | ["list"] => MktOp::List,
            ["add", source] => MktOp::Add((*source).to_string()),
            ["remove", name] => MktOp::Remove((*name).to_string()),
            [sub, ..] if ["browse", "install", "search", "refresh"].contains(sub) => {
                MktOp::Unsupported((*sub).to_string())
            }
            _ => MktOp::BadUsage,
        };

        if let MktOp::Unsupported(sub) = op {
            self.notice(
                format!("/{sub} is not yet available in the Rust front-end. Use the C# coda tool."),
                NoticeLevel::Warning,
            );
            return;
        }

        if matches!(op, MktOp::BadUsage) {
            self.notice(
                "Usage: /marketplace [list | add <source> | remove <name>]",
                NoticeLevel::Warning,
            );
            return;
        }

        let result = tokio::task::spawn_blocking(move || -> Result<String, config::ConfigError> {
            let mut settings = config::Settings::load(&paths)?;
            match op {
                MktOp::List => {
                    let markets = settings.marketplaces();
                    if markets.is_empty() {
                        return Ok("No marketplaces configured. Use /marketplace add <source> to register one.".to_string());
                    }
                    let mut out = String::from("Marketplaces\n");
                    for (name, source) in &markets {
                        out.push_str(&format!("  {name}  {source}\n"));
                    }
                    Ok(out.trim_end().to_string())
                }
                MktOp::Add(source) => {
                    // Derive a name from the last URL segment or filename.
                    let name = marketplace_name_from_source(&source);
                    settings.add_marketplace(&name, &source);
                    settings.save()?;
                    Ok(format!("Registered marketplace '{name}' ({source})."))
                }
                MktOp::Remove(name) => {
                    if settings.remove_marketplace(&name) {
                        settings.save()?;
                        Ok(format!("Removed marketplace '{name}'."))
                    } else {
                        Ok(format!("No marketplace named '{name}'."))
                    }
                }
                _ => unreachable!(),
            }
        })
        .await;

        match result {
            Ok(Ok(text)) => self.output(text),
            Ok(Err(e)) => self.notice(format!("Settings error: {e}"), NoticeLevel::Error),
            Err(_) => self.notice("Settings operation was interrupted.", NoticeLevel::Error),
        }
    }

    /// `/plugin [list | info <name> | enable <name> | disable <name>]`.
    async fn cmd_plugin(&mut self, invocation: &commands::Invocation) {
        let words = invocation.words();

        enum PluginOp {
            List,
            Info(String),
            SetEnabled(String, bool),
            Unsupported(String),
            BadUsage,
        }

        let op = match words.as_slice() {
            [] | ["list"] => PluginOp::List,
            ["info", name] => PluginOp::Info((*name).to_string()),
            ["enable", name] => PluginOp::SetEnabled((*name).to_string(), true),
            ["disable", name] => PluginOp::SetEnabled((*name).to_string(), false),
            [sub, ..] if ["install", "remove", "update", "prune", "approve", "validate", "new"].contains(sub) => {
                PluginOp::Unsupported((*sub).to_string())
            }
            _ => PluginOp::BadUsage,
        };

        if let PluginOp::Unsupported(sub) = op {
            self.notice(
                format!("plugin {sub} is not yet available in the Rust front-end. Use the C# coda tool or /plugins browser."),
                NoticeLevel::Warning,
            );
            return;
        }

        match op {
            PluginOp::List => {
                // Delegate to the engine for the definitive plugin list.
                match self
                    .fetch::<messages::PluginsListResult>(
                        method::PLUGINS_LIST,
                        Some(serde_json::json!({})),
                    )
                    .await
                {
                    Ok(result) => {
                        let text = if result.plugins.is_empty() {
                            "No plugins installed.".to_string()
                        } else {
                            let mut out = String::from("Plugins\n");
                            for p in &result.plugins {
                                out.push_str(&format!(
                                    "  {} {}\n",
                                    p.name,
                                    p.version.as_deref().unwrap_or("")
                                ));
                            }
                            out.trim_end().to_string()
                        };
                        self.output(text);
                    }
                    Err(e) => self.notice(format!("Could not list plugins: {e}"), NoticeLevel::Error),
                }
            }
            PluginOp::Info(name) => {
                match self
                    .fetch::<messages::PluginsListResult>(
                        method::PLUGINS_LIST,
                        Some(serde_json::json!({})),
                    )
                    .await
                {
                    Ok(result) => {
                        if let Some(plugin) = result.plugins.iter().find(|p| p.name.eq_ignore_ascii_case(&name)) {
                            let version = plugin.version.as_deref().unwrap_or("(unknown)");
                            self.output(format!("Plugin: {}\nVersion: {version}", plugin.name));
                        } else {
                            self.notice(format!("Plugin '{name}' not found."), NoticeLevel::Warning);
                        }
                    }
                    Err(e) => self.notice(format!("Could not fetch plugins: {e}"), NoticeLevel::Error),
                }
            }
            PluginOp::SetEnabled(name, enabled) => {
                let paths = self.paths.clone();
                let result = tokio::task::spawn_blocking(move || -> Result<(), config::ConfigError> {
                    let mut state = config::PluginState::load(&paths)?;
                    state.set_enabled(&name, enabled);
                    state.save()
                })
                .await;

                match result {
                    Ok(Ok(())) => {
                        let word = if enabled { "Enabled" } else { "Disabled" };
                        self.notice(format!("{word} plugin. Restart the engine to apply."), NoticeLevel::Info);
                    }
                    Ok(Err(e)) => self.notice(format!("Could not update plugin state: {e}"), NoticeLevel::Error),
                    Err(_) => self.notice("Plugin state write was interrupted.", NoticeLevel::Error),
                }
            }
            PluginOp::BadUsage => self.notice(
                "Usage: /plugin [list | info <name> | enable <name> | disable <name>]",
                NoticeLevel::Warning,
            ),
            _ => {}
        }
    }

    /// `/skill [<name> [args...]]` — list skills or run one by name.
    async fn cmd_skill(&mut self, invocation: &commands::Invocation) {
        let words = invocation.words();

        if words.is_empty() {
            // No arguments: list available skills via the engine.
            match self
                .fetch::<messages::SkillsListResult>(
                    method::SKILLS_LIST,
                    Some(serde_json::json!({})),
                )
                .await
            {
                    Ok(result) => {
                        let text = if result.skills.is_empty() {
                            "No skills available.".to_string()
                        } else {
                            let mut out = String::from("Skills\n");
                            for s in &result.skills {
                                let mark = if s.enabled { "*" } else { " " };
                                out.push_str(&format!(
                                    "  {mark} {}  {}\n",
                                    s.name,
                                    s.description.as_deref().unwrap_or("")
                                ));
                            }
                            out.trim_end().to_string()
                        };
                        self.output(text);
                    }
                    Err(e) => self.notice(format!("Could not list skills: {e}"), NoticeLevel::Error),
                }
                return;
            }

        let name = words[0];
        let args: Vec<&str> = words[1..].to_vec();

        // Look up the SKILL.md in project-local and user-scoped dirs.
        let body = find_local_skill_body(&self.paths, name, &args);
        match body {
            Some(text) => {
                self.notice(format!("Running skill '{name}'…"), NoticeLevel::Info);
                self.submit_programmatic(text).await;
            }
            None => {
                // Report what IS available to help the user.
                let available = list_local_skill_names(&self.paths);
                let list = if available.is_empty() {
                    "(none found locally)".to_string()
                } else {
                    available.join(", ")
                };
                self.notice(
                    format!("Skill '{name}' not found. Available locally: {list}"),
                    NoticeLevel::Warning,
                );
            }
        }
    }

    /// `/export [<path>]` — write the current conversation to a Markdown file.
    async fn cmd_export(&mut self, invocation: &commands::Invocation) {
        let arg = invocation.first().map(str::to_string);
        let project_root = self.paths.project_root.clone();

        // Capture the transcript before spawning; the transcript is !Send.
        let markdown = build_markdown_export(self.state.transcript.blocks());

        if markdown.trim().is_empty() {
            self.notice("Nothing to export yet.", NoticeLevel::Info);
            return;
        }

        let result = tokio::task::spawn_blocking(move || -> std::io::Result<std::path::PathBuf> {
            let path = match arg {
                Some(ref provided) if !provided.is_empty() => {
                    let p = std::path::Path::new(provided);
                    if p.is_absolute() {
                        p.to_path_buf()
                    } else {
                        project_root.join(p)
                    }
                }
                _ => {
                    use time::OffsetDateTime;
                    let now = OffsetDateTime::now_utc();
                    let ts = format!(
                        "{}{:02}{:02}-{:02}{:02}{:02}",
                        now.year(),
                        u8::from(now.month()),
                        now.day(),
                        now.hour(),
                        now.minute(),
                        now.second()
                    );
                    project_root.join(format!("coda-conversation-{ts}.md"))
                }
            };

            if let Some(parent) = path.parent() {
                std::fs::create_dir_all(parent)?;
            }
            std::fs::write(&path, markdown.as_bytes())?;
            Ok(path)
        })
        .await;

        match result {
            Ok(Ok(path)) => self.notice(format!("Conversation exported to {}", path.display()), NoticeLevel::Info),
            Ok(Err(e)) => self.notice(format!("Export failed: {e}"), NoticeLevel::Error),
            Err(_) => self.notice("Export was interrupted.", NoticeLevel::Error),
        }
    }

    /// `/diff` — run `git diff` and render the result with syntax colouring.
    async fn cmd_diff(&mut self) {
        let cwd = self.paths.project_root.clone();
        let output = tokio::process::Command::new("git")
            .arg("diff")
            .current_dir(&cwd)
            .output()
            .await;

        match output {
            Ok(out) => {
                let stderr = String::from_utf8_lossy(&out.stderr);
                if !out.status.success() || stderr.contains("not a git repository") {
                    let msg = if stderr.trim().is_empty() {
                        "git exited with a non-zero status. Is this directory a git repository?"
                            .to_string()
                    } else {
                        coda_render::text::sanitize(stderr.trim())
                    };
                    self.notice(msg, NoticeLevel::Error);
                    return;
                }
                let stdout = String::from_utf8_lossy(&out.stdout).into_owned();
                if stdout.trim().is_empty() {
                    self.notice("No uncommitted changes.", NoticeLevel::Info);
                } else {
                    // Sanitize before storage: strips ANSI escapes from coloured git output.
                    let sanitized = coda_render::text::sanitize(&stdout);
                    self.apply(UiEvent::DiffOutput { text: sanitized });
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                self.notice(
                    "git not found. Make sure git is installed and on your PATH.",
                    NoticeLevel::Warning,
                );
            }
            Err(e) => self.notice(format!("Could not run git: {e}"), NoticeLevel::Error),
        }
    }

    /// `/image <path>` — base64-encode an image and stage it for the next turn.
    ///
    /// Maximum size is 5 MB. Accepted formats: .png .jpg/.jpeg .gif .webp.
    async fn cmd_image(&mut self, invocation: &commands::Invocation) {
        let Some(path_str) = invocation.first() else {
            self.notice(
                "Usage: /image <path>  — attaches an image to the next turn.",
                NoticeLevel::Warning,
            );
            return;
        };

        let path = std::path::PathBuf::from(path_str);
        let file_name = path
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("image")
            .to_string();

        let result = tokio::task::spawn_blocking(move || -> Result<(String, Vec<u8>), String> {
            let ext = path.extension().and_then(|e| e.to_str()).unwrap_or("");
            let media_type = image_media_type(ext).ok_or_else(|| {
                format!(
                    "File type not supported: '.{ext}'. Supported: .png, .jpg, .jpeg, .gif, .webp"
                )
            })?;

            if !path.exists() {
                return Err(format!("File not found: {}", path.display()));
            }

            let metadata = std::fs::metadata(&path)
                .map_err(|e| format!("Could not read file: {e}"))?;
            const MAX_BYTES: u64 = 5 * 1024 * 1024;
            if metadata.len() > MAX_BYTES {
                let size_mb = metadata.len() as f64 / (1024.0 * 1024.0);
                return Err(format!(
                    "File too large ({size_mb:.1} MB). Maximum size is 5 MB."
                ));
            }

            let bytes = std::fs::read(&path).map_err(|e| format!("Could not read image: {e}"))?;
            Ok((media_type.to_string(), bytes))
        })
        .await;

        match result {
            Ok(Ok((media_type, bytes))) => {
                let label = self.staged_images.len() + 1;
                self.staged_images.push(messages::WireImage {
                    media_type,
                    base64: base64_encode(&bytes),
                });
                // Insert the label token into the composer so the user can see
                // where the attachment sits in the composed message.
                let token = format!("[Image {label}]");
                if !self.composer.is_empty() {
                    self.composer.insert(" ");
                }
                self.composer.insert(&token);

                let size_kb = bytes.len() as f64 / 1024.0;
                self.notice(
                    format!(
                        "Attached {file_name} as {token} ({size_kb:.1} KB). It will be sent with your next message."
                    ),
                    NoticeLevel::Info,
                );
            }
            Ok(Err(msg)) => self.notice(msg, NoticeLevel::Error),
            Err(_) => self.notice("Could not read the image file.", NoticeLevel::Error),
        }
    }

    // -- Session management commands -----------------------------------------

    /// `/compact` — ask the engine to summarise the conversation.
    ///
    /// Mirrors C# `CompactCommand`: empty history → "Nothing to compact yet.";
    /// success → "Conversation compacted (N messages kept).";
    /// summariser error → warning with detail.
    async fn cmd_compact(&mut self) {
        let result = self
            .fetch::<messages::CompactResult>(
                method::COMPACT,
                Some(serde_json::json!({})),
            )
            .await;

        match result {
            Ok(r) if r.messages_before == 0 => {
                self.notice("Nothing to compact yet.", NoticeLevel::Info);
            }
            Ok(r) if r.error.is_some() => {
                let detail = r.error.unwrap_or_default();
                self.notice(
                    format!("Compaction warning: {detail}"),
                    NoticeLevel::Warning,
                );
            }
            Ok(r) => {
                self.notice(
                    format!(
                        "Conversation compacted ({} messages kept).",
                        r.messages_after
                    ),
                    NoticeLevel::Info,
                );
            }
            Err(e) => self.notice(format!("Compaction failed: {e}"), NoticeLevel::Error),
        }
    }

    /// `/resume [<id>]` — list or resume a past session.
    ///
    /// No arg → open the sessions browser picker (mirrors C#
    /// `ResumeCommand.HandleNoArgsAsync`). A positive integer N → use the
    /// N-th newest session (1-based). Any other string → treat as a literal
    /// session id.
    async fn cmd_resume(&mut self, invocation: &commands::Invocation) {
        let arg = invocation.first().map(str::to_string);

        if let Some(arg) = arg {
            let session_id = self.resolve_resume_target(&arg).await;
            self.resume_to_session(session_id).await;
        } else {
            self.open_browser(BrowserKind::Sessions).await;
        }
    }

    /// Resolves a `/resume` argument to a session id.
    ///
    /// A bare positive integer selects the N-th newest session (1-based); any
    /// other string is returned as-is. Mirrors C# `ResolveTargetIdAsync`.
    async fn resolve_resume_target(&self, arg: &str) -> String {
        if let Ok(n) = arg.parse::<usize>() {
            if n >= 1 {
                let project_root = self.paths.project_root.clone();
                if let Ok(summaries) = tokio::task::spawn_blocking(move || {
                    coda_agent::SessionTranscriptStore::new(&project_root).list()
                })
                .await
                {
                    if n <= summaries.len() {
                        return summaries[n - 1].id.clone();
                    }
                }
            }
        }
        arg.to_string()
    }

    /// Restarts the engine loading `session_id` from disk, clearing the
    /// current transcript.
    ///
    /// Pre-checks that the session exists on disk so the user gets a clear
    /// "not found" message instead of an engine handshake error.  Mirrors C#
    /// `ResumeCommand.ResumeSessionAsync`.
    async fn resume_to_session(&mut self, session_id: String) {
        // Pre-check: verify the session exists and get its message count.
        let project_root = self.paths.project_root.clone();
        let sid = session_id.clone();
        let summary = match tokio::task::spawn_blocking(move || {
            coda_agent::SessionTranscriptStore::new(&project_root)
                .list()
                .into_iter()
                .find(|s| s.id == sid)
        })
        .await
        {
            Ok(s) => s,
            Err(_) => {
                self.notice("Could not read session store.", NoticeLevel::Error);
                return;
            }
        };

        let Some(summary) = summary else {
            let escaped = coda_render::text::sanitize(&session_id);
            self.notice(
                format!("Session '{escaped}' not found."),
                NoticeLevel::Warning,
            );
            return;
        };
        let count = summary.message_count;

        self.close_browser();
        // Clear the current transcript — we are switching sessions.
        self.apply(UiEvent::Cleared);

        // Restart with the target session id; initialize loads the history.
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
        let params =
            serde_json::to_value(messages::InitializeParams::new("coda-tui").resume(&session_id))
                .unwrap_or_default();

        match connection.request(method::INITIALIZE, Some(params)).await {
            Ok(value) => {
                let initialized: messages::InitializeResult =
                    serde_json::from_value(value).unwrap_or(messages::InitializeResult {
                        protocol_version: coda_proto::PROTOCOL_VERSION.to_string(),
                        session_id: session_id.clone(),
                        server_info: "coda".into(),
                        telemetry_log_path: None,
                    });

                self.connection = connection;
                self.restarted = Some((engine, inbound));

                let actual_id = if initialized.session_id.is_empty() {
                    session_id.clone()
                } else {
                    initialized.session_id.clone()
                };
                self.state.session_id = Some(actual_id.clone());

                let escaped = coda_render::text::sanitize(&actual_id);
                self.notice(
                    format!("Resumed session {escaped} ({count} messages)."),
                    NoticeLevel::Info,
                );
            }
            Err(error) => {
                self.notice(
                    format!("The restarted engine rejected the handshake: {error}"),
                    NoticeLevel::Error,
                );
            }
        }
    }

    /// `/fork` — branch the live conversation into a new session.
    ///
    /// Calls `session/fork`; the engine persists the current history under a
    /// fresh id and switches to it.  Mirrors C# `ForkCommand`.
    async fn cmd_fork(&mut self) {
        match self
            .connection
            .request("session/fork", Some(serde_json::json!({})))
            .await
        {
            Ok(value) => {
                if let Some(new_id) = value.get("newSessionId").and_then(Value::as_str) {
                    let new_id = new_id.to_string();
                    let escaped = coda_render::text::sanitize(&new_id);
                    // Reflect the engine's new session id in the TUI state.
                    self.state.session_id = Some(new_id);
                    self.notice(
                        format!("Forked into a new session {escaped} (original frozen)."),
                        NoticeLevel::Info,
                    );
                } else {
                    self.notice("Fork completed (session ID unknown).", NoticeLevel::Info);
                }
            }
            Err(e) => self.notice(format!("Fork failed: {e}"), NoticeLevel::Error),
        }
    }

    /// `/rewind [<n>]` — remove the last N user exchanges from the conversation.
    ///
    /// Default n = 1.  Mirrors C# `RewindCommand`: validates n, calls
    /// `session/rewind`, then reports how many exchanges were removed.
    async fn cmd_rewind(&mut self, invocation: &commands::Invocation) {
        let n = match parse_rewind_n(invocation.first()) {
            Ok(n) => n,
            Err(msg) => {
                self.notice(msg, NoticeLevel::Warning);
                return;
            }
        };

        match self
            .connection
            .request("session/rewind", Some(serde_json::json!({ "n": n })))
            .await
        {
            Ok(value) => {
                let removed =
                    value.get("removed").and_then(Value::as_u64).unwrap_or(0) as usize;
                let remaining =
                    value.get("remaining").and_then(Value::as_u64).unwrap_or(0) as usize;
                if removed == 0 {
                    self.notice("Nothing to rewind.", NoticeLevel::Info);
                } else {
                    self.notice(
                        format!(
                            "Rewound {removed} exchange(s). {remaining} message(s) remain."
                        ),
                        NoticeLevel::Info,
                    );
                }
            }
            Err(e) => self.notice(format!("Rewind failed: {e}"), NoticeLevel::Error),
        }
    }
}

/// Returns the MIME type for a supported image extension, case-insensitively.
fn image_media_type(extension: &str) -> Option<&'static str> {
    match extension.to_lowercase().as_str() {
        "png" => Some("image/png"),
        "jpg" | "jpeg" => Some("image/jpeg"),
        "gif" => Some("image/gif"),
        "webp" => Some("image/webp"),
        _ => None,
    }
}

/// Parses the `n` argument of `/rewind`, returning `Ok(n)` for a valid
/// positive integer or `Err` with a usage hint.
///
/// Mirrors C# `RewindCommand`: defaults to 1 when absent; rejects zero and
/// non-integers with the same usage message.
fn parse_rewind_n(arg: Option<&str>) -> Result<u32, &'static str> {
    match arg {
        None => Ok(1),
        Some(s) => s
            .parse::<u32>()
            .ok()
            .filter(|&v| v >= 1)
            .ok_or("Usage: /rewind [n] where n is a positive integer."),
    }
}

/// Encodes bytes as standard (RFC 4648) base64 with `=` padding.
fn base64_encode(data: &[u8]) -> String {
    const TABLE: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity((data.len() + 2) / 3 * 4);
    for chunk in data.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = chunk.get(1).copied().unwrap_or(0) as u32;
        let b2 = chunk.get(2).copied().unwrap_or(0) as u32;
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(TABLE[((n >> 18) & 0x3F) as usize] as char);
        out.push(TABLE[((n >> 12) & 0x3F) as usize] as char);
        out.push(if chunk.len() > 1 { TABLE[((n >> 6) & 0x3F) as usize] as char } else { '=' });
        out.push(if chunk.len() > 2 { TABLE[(n & 0x3F) as usize] as char } else { '=' });
    }
    out
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

/// Derives a marketplace name from a source URL or path.
fn marketplace_name_from_source(source: &str) -> String {
    // Use the last non-empty path segment, stripping common extensions.
    source
        .trim_end_matches('/')
        .rsplit('/')
        .find(|s| !s.is_empty())
        .unwrap_or(source)
        .trim_end_matches(".json")
        .trim_end_matches(".git")
        .to_string()
}

/// Reads a skill body from local SKILL.md files, binding positional args.
///
/// Searches project-scoped then user-scoped skill directories. Returns the
/// bound body, or `None` when no matching skill is found.
fn find_local_skill_body(paths: &config::Paths, name: &str, args: &[&str]) -> Option<String> {
    for dir in [paths.skills_project(), paths.skills_user()] {
        let skill_md = dir.join(name).join("SKILL.md");
        if let Ok(body) = std::fs::read_to_string(&skill_md) {
            return Some(bind_skill_args(&body, args));
        }
    }
    None
}

/// Lists the names of locally available skills.
fn list_local_skill_names(paths: &config::Paths) -> Vec<String> {
    let mut names = Vec::new();
    for dir in [paths.skills_project(), paths.skills_user()] {
        let Ok(entries) = std::fs::read_dir(&dir) else {
            continue;
        };
        for entry in entries.flatten() {
            if entry.path().join("SKILL.md").is_file() {
                if let Some(n) = entry.file_name().to_str() {
                    if !names.iter().any(|e: &String| e.eq_ignore_ascii_case(n)) {
                        names.push(n.to_string());
                    }
                }
            }
        }
    }
    names.sort();
    names
}

/// Substitutes skill argument placeholders in a single pass, preventing
/// re-expansion of substituted values.
///
/// Rules (matching C# `SkillArgumentBinder`):
/// - `$$`         → literal `$`
/// - `$ARGUMENTS` → all positional args joined by a single space (case-sensitive)
/// - `$N` (N ≥ 1) → the N-th positional arg, or empty if out of range
/// - `$identifier` → empty (named args from frontmatter; treated as unknown
///                   here because the Rust TUI does not parse frontmatter)
/// - bare `$`     → kept as-is
pub fn bind_skill_args(body: &str, args: &[&str]) -> String {
    let mut out = String::with_capacity(body.len());
    let mut chars = body.char_indices().peekable();

    while let Some((_, c)) = chars.next() {
        if c != '$' {
            out.push(c);
            continue;
        }

        match chars.peek() {
            Some((_, '$')) => {
                // $$ → literal $
                chars.next();
                out.push('$');
            }
            Some((_, d)) if d.is_ascii_digit() => {
                let d = *d;
                let mut num_str = String::new();
                num_str.push(d);
                chars.next();
                while let Some(&(_, nd)) = chars.peek() {
                    if nd.is_ascii_digit() {
                        num_str.push(nd);
                        chars.next();
                    } else {
                        break;
                    }
                }
                let n: usize = num_str.parse().unwrap_or(0);
                if n >= 1 && n <= args.len() {
                    out.push_str(args[n - 1]);
                }
                // $0 or out-of-range → push nothing (renders as empty)
            }
            Some((_, d)) if d.is_alphabetic() || *d == '_' => {
                let mut name = String::new();
                while let Some(&(_, nc)) = chars.peek() {
                    if nc.is_alphanumeric() || nc == '_' {
                        name.push(nc);
                        chars.next();
                    } else {
                        break;
                    }
                }
                if name == "ARGUMENTS" {
                    out.push_str(&args.join(" "));
                }
                // Any other named identifier → empty (unknown; no frontmatter)
            }
            _ => {
                // Bare `$` not followed by a recognisable pattern → keep.
                out.push('$');
            }
        }
    }

    out
}

/// Renders transcript blocks as a Markdown document for `/export`.
pub fn build_markdown_export(blocks: &[crate::transcript::Block]) -> String {
    use crate::transcript::Block;
    let mut out = String::from("# Coda Conversation Export\n\n");
    for block in blocks {
        match block {
            Block::User { text, .. } => {
                out.push_str("## User\n\n");
                out.push_str(text);
                out.push_str("\n\n");
            }
            Block::Assistant { text, .. } => {
                out.push_str("## Assistant\n\n");
                out.push_str(text);
                out.push_str("\n\n");
            }
            Block::Tools { activity, .. } => {
                for call in &activity.calls {
                    out.push_str(&format!("- tool call: {}\n", call.name));
                    if let Some(result) = &call.result {
                        out.push_str(&format!("- tool result: {}\n", result.chars().take(200).collect::<String>()));
                    }
                }
                if !activity.calls.is_empty() {
                    out.push('\n');
                }
            }
            Block::Diff { raw } => {
                out.push_str("```diff\n");
                out.push_str(raw);
                out.push_str("```\n\n");
            }
            // Skip non-content blocks in the export.
            Block::Notice { .. }
            | Block::Permission { .. }
            | Block::Question { .. }
            | Block::CommandOutput { .. }
            | Block::Thinking { .. }
            | Block::SessionBoundary { .. } => {}
        }
    }
    out
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

/// Returns `true` for events that bypass the 30 FPS streaming throttle.
///
/// Mirrors `UiActor.IsCritical` in C#: turn boundaries, errors, prompts,
/// session lifecycle, and mode changes all get immediate frames.
fn is_critical_event(event: &UiEvent) -> bool {
    use coda_proto::Event;
    match event {
        UiEvent::TurnFinished { .. }
        | UiEvent::Connected { .. }
        | UiEvent::PromptRequested(_)
        | UiEvent::PromptAnswered { .. }
        | UiEvent::Notice { .. }
        | UiEvent::Cleared
        | UiEvent::ModelChanged { .. }
        | UiEvent::DisplayModeChanged(_)
        | UiEvent::Submitted { .. }
        | UiEvent::Queued { .. }
        | UiEvent::InterruptRequested => true,
        UiEvent::Engine(inner) => match inner {
            Event::TurnComplete { .. }
            | Event::Error { .. }
            | Event::LimitReached { .. }
            | Event::AssistantTextComplete
            | Event::ThinkingComplete { .. }
            | Event::PermissionDecided { .. }
            | Event::SteeringDelivered { .. } => true,
            // Streaming events: subject to throttle.
            Event::AssistantText { .. }
            | Event::Thinking { .. }
            | Event::ToolProgress { .. }
            | Event::Usage { .. }
            | Event::StreamProgress { .. } => false,
            _ => true,
        },
        // The buffering activation seam is rare and user-visible.
        UiEvent::EnableAssistantBuffering => true,
        UiEvent::CommandOutput { .. } | UiEvent::DiffOutput { .. } => true,
    }
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

    #[test]
    fn base64_encodes_rfc_4648_test_vectors() {
        // RFC 4648 §10 test vectors.
        assert_eq!(base64_encode(b""), "");
        assert_eq!(base64_encode(b"f"), "Zg==");
        assert_eq!(base64_encode(b"fo"), "Zm8=");
        assert_eq!(base64_encode(b"foo"), "Zm9v");
        assert_eq!(base64_encode(b"foob"), "Zm9vYg==");
        assert_eq!(base64_encode(b"fooba"), "Zm9vYmE=");
        assert_eq!(base64_encode(b"foobar"), "Zm9vYmFy");
    }

    #[test]
    fn base64_encodes_a_longer_string() {
        assert_eq!(base64_encode(b"Hello, World!"), "SGVsbG8sIFdvcmxkIQ==");
    }

    #[test]
    fn image_media_type_maps_supported_extensions() {
        assert_eq!(image_media_type("png"), Some("image/png"));
        assert_eq!(image_media_type("jpg"), Some("image/jpeg"));
        assert_eq!(image_media_type("jpeg"), Some("image/jpeg"));
        assert_eq!(image_media_type("gif"), Some("image/gif"));
        assert_eq!(image_media_type("webp"), Some("image/webp"));
    }

    #[test]
    fn image_media_type_is_case_insensitive() {
        assert_eq!(image_media_type("PNG"), Some("image/png"));
        assert_eq!(image_media_type("JPG"), Some("image/jpeg"));
        assert_eq!(image_media_type("WEBP"), Some("image/webp"));
    }

    #[test]
    fn image_media_type_rejects_unsupported_extensions() {
        assert_eq!(image_media_type("bmp"), None);
        assert_eq!(image_media_type("svg"), None);
        assert_eq!(image_media_type("tiff"), None);
        assert_eq!(image_media_type(""), None);
    }

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

    #[test]
    fn marketplace_name_strips_extension_and_uses_last_segment() {
        assert_eq!(marketplace_name_from_source("https://example.com/plugins.json"), "plugins");
        assert_eq!(marketplace_name_from_source("https://example.com/my-marketplace"), "my-marketplace");
        assert_eq!(marketplace_name_from_source("git@github.com:org/repo.git"), "repo");
        assert_eq!(marketplace_name_from_source("/local/path/plugins/"), "plugins");
    }

    #[test]
    fn bind_skill_args_substitutes_positional_placeholders() {
        let body = "Translate to $1: $ARGUMENTS";
        let result = bind_skill_args(body, &["French", "Hello world"]);
        assert_eq!(result, "Translate to French: French Hello world");
    }

    #[test]
    fn bind_skill_args_handles_missing_args_gracefully() {
        // Out-of-range positionals render as empty (not left as literal `$3`).
        let result = bind_skill_args("Do $1 and $3", &["first"]);
        assert_eq!(result, "Do first and ");
    }

    #[test]
    fn bind_skill_args_double_dollar_produces_a_literal_dollar() {
        assert_eq!(bind_skill_args("Cost: $$10", &[]), "Cost: $10");
    }

    #[test]
    fn bind_skill_args_double_dollar_is_not_re_expanded() {
        // $$ → $, then $1 on the next pass must NOT be expanded.
        assert_eq!(bind_skill_args("$$1", &["ignored"]), "$1");
    }

    #[test]
    fn bind_skill_args_substituted_value_is_not_re_expanded() {
        // The value "$ARGUMENTS" inserted for $1 must not trigger a second pass.
        assert_eq!(bind_skill_args("$1", &["$ARGUMENTS"]), "$ARGUMENTS");
    }

    #[test]
    fn bind_skill_args_positional_zero_renders_empty() {
        assert_eq!(bind_skill_args("$0", &["a"]), "");
    }

    #[test]
    fn bind_skill_args_unknown_identifier_renders_empty() {
        // $nonexistent is not $ARGUMENTS and not positional → empty.
        assert_eq!(bind_skill_args("$nonexistent", &["val"]), "");
    }

    #[test]
    fn bind_skill_args_arguments_is_case_sensitive() {
        // $arguments (lowercase) is NOT the special $ARGUMENTS token → empty.
        assert_eq!(bind_skill_args("$arguments", &["val"]), "");
    }

    #[test]
    fn build_markdown_export_includes_user_and_assistant_turns() {
        use crate::transcript::Block;
        let blocks = vec![
            Block::User {
                text: "Hello".to_string(),
                timestamp: "09:41".to_string(),
                pending: false,
                queue_id: None,
            },
            Block::Assistant {
                text: "World".to_string(),
                complete: true,
            },
        ];
        let md = build_markdown_export(&blocks);
        assert!(md.contains("## User"));
        assert!(md.contains("Hello"));
        assert!(md.contains("## Assistant"));
        assert!(md.contains("World"));
    }

    #[test]
    fn build_markdown_export_is_empty_for_no_content_blocks() {
        use crate::transcript::Block;
        // Notice blocks are not exported.
        let blocks = vec![Block::Notice {
            text: "internal notice".to_string(),
            level: crate::transcript::NoticeLevel::Info,
        }];
        let md = build_markdown_export(&blocks);
        // Only the header line; no turns.
        assert!(!md.contains("## User"));
        assert!(!md.contains("## Assistant"));
    }

    // -- /rewind argument parsing -----------------------------------------------

    #[test]
    fn rewind_n_defaults_to_one_when_no_arg() {
        assert_eq!(parse_rewind_n(None), Ok(1));
    }

    #[test]
    fn rewind_n_parses_a_valid_positive_integer() {
        assert_eq!(parse_rewind_n(Some("3")), Ok(3));
        assert_eq!(parse_rewind_n(Some("1")), Ok(1));
        assert_eq!(parse_rewind_n(Some("100")), Ok(100));
    }

    #[test]
    fn rewind_n_rejects_zero() {
        assert!(parse_rewind_n(Some("0")).is_err());
    }

    #[test]
    fn rewind_n_rejects_non_integer() {
        assert!(parse_rewind_n(Some("abc")).is_err());
        assert!(parse_rewind_n(Some("1.5")).is_err());
        assert!(parse_rewind_n(Some("")).is_err());
    }

    #[test]
    fn rewind_n_rejects_negative_integers() {
        // u32 parse rejects negative strings.
        assert!(parse_rewind_n(Some("-1")).is_err());
        assert!(parse_rewind_n(Some("-100")).is_err());
    }

    #[test]
    fn rewind_n_error_message_matches_c_sharp() {
        let err = parse_rewind_n(Some("0")).unwrap_err();
        assert!(
            err.contains("positive integer"),
            "error must mention 'positive integer', got: {err}"
        );
    }
}

