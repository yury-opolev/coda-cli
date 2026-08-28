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
use futures_lite::StreamExt;
use serde_json::Value;
use tokio::sync::mpsc;
use tokio::sync::oneshot;

use crate::commands::{self, Scope};
use crate::composer::{Completion, Composer};
use crate::draw;
use crate::keymap::{self, Action, Focus, KeyContext};
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
}

impl App {
    /// Connects to an engine and completes the handshake.
    pub async fn connect(
        command: EngineCommand,
        theme: Theme,
    ) -> Result<(Self, Engine, mpsc::UnboundedReceiver<Inbound>)> {
        let (engine, inbound) = Engine::spawn(command).context("failed to start the engine")?;
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
        };

        Ok((app, engine, inbound))
    }

    /// Runs until the user quits or the engine disconnects.
    pub async fn run(
        mut self,
        guard: &mut TerminalGuard,
        mut inbound: mpsc::UnboundedReceiver<Inbound>,
    ) -> Result<()> {
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

                // A turn finished.
                Some(outcome) = turn_rx.recv() => self.on_turn_finished(outcome),
            }

            // Poll the outstanding turn without blocking the loop.
            if let Some(receiver) = &mut self.turn {
                if let Ok(result) = receiver.try_recv() {
                    self.turn = None;
                    let _ = turn_tx.send(TurnOutcome {
                        result: result.map_err(ClientError::Rpc),
                    });
                }
            }

            if self.state.should_quit {
                break;
            }
            if self.dirty {
                self.redraw(guard)?;
            }
        }

        Ok(())
    }

    // -- Engine -------------------------------------------------------------

    fn on_inbound(&mut self, message: Inbound) {
        match message {
            Inbound::Notification { method, params } => {
                let event = Event::parse(&method, params.as_ref());
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

        let action = keymap::resolve(key, self.key_context());
        self.dirty = true;

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

            Action::Interrupt => self.interrupt(),
            Action::Quit => self.state.should_quit = true,
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

    /// Answers an open prompt.
    fn on_prompt_key(&mut self, key: KeyEvent) {
        let Some(prompt) = self.state.prompt.clone() else {
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

        let id = response
            .ok()
            .and_then(|value| serde_json::from_value::<messages::SteerResult>(value).ok())
            .filter(|result| result.ok)
            .and_then(|result| result.message_id);

        self.apply(UiEvent::Queued { text, id });
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

        guard.terminal().draw(|frame| {
            draw::draw(frame, state, composer, viewport, rows, theme);
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

