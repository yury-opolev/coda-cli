//! The application loop.
//!
//! Terminal input and engine notifications arrive on independent channels and
//! are funnelled into the same reducer, so ordering between a keystroke and a
//! streamed token is explicit. Rendering happens once per iteration, only when
//! something actually changed.

use std::time::Duration;

use anyhow::{Context, Result};
use coda_client::{ClientError, Connection, Engine, EngineCommand, Inbound, Responder};
use coda_proto::messages::{self, method};
use coda_proto::Event;
use coda_render::{RenderLine, Theme};
use crossterm::event::{
    Event as TerminalEvent, EventStream, KeyCode, KeyEvent, KeyEventKind,
};
use futures::future::OptionFuture;
use futures_lite::StreamExt;
use serde_json::Value;
use tokio::sync::mpsc;
use tokio::sync::oneshot;

mod browsers;
mod slash;
mod clipboard;
mod engine;

use crate::config::{self, Paths, Settings};
use crate::commands;
use crate::composer::{Completion, Composer};
use crate::draw;
use crate::keymap::{self, Action, Focus, KeyContext};
use crate::overlay::Intent;
use crate::surface::browser::BrowserKind;
use crate::state::{UiEvent, UiState};
use crate::terminal::TerminalGuard;
use crate::transcript::NoticeLevel;
use crate::viewport::{Viewport, ViewportAnchor};

/// How long a turn may take before we stop waiting on shutdown.
const SHUTDOWN_GRACE: Duration = Duration::from_secs(5);
/// Rows scrolled per mouse wheel notch.
pub(crate) const WHEEL_ROWS: usize = 3;

/// What a pointer gesture asks the clipboard to do.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum PointerAction {
    Copy,
    Paste,
}


/// Minimum interval between streaming (non-critical) frames — 30 FPS.
///
/// Matches C# `UiActor.MinStreamingFrameIntervalMs = 33`.
const MIN_STREAMING_FRAME_MS: u64 = 33;

/// How long each frame of the working indicator is held.
///
/// Slower than the frame cap, so the spinner costs at most one extra redraw
/// per interval rather than driving the loop at full rate while idle-but-busy.
const SPINNER_FRAME_MS: u64 = 110;

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
    /// When the working indicator last advanced a frame.
    spinner_at: Option<std::time::Instant>,
    /// The provider the engine connected with, as it reports it.
    ///
    /// Not the same as `defaultProvider` in settings: the engine uses whatever
    /// credential it actually found. A model preference saved under the wrong
    /// one is written where the engine will never read it, so the choice
    /// silently reverts on the next start.
    connected_provider: Option<String>,
    /// Whether a left-drag selection is genuinely in progress.
    ///
    /// A drag only continues a selection that was explicitly begun. Without
    /// this, a drag starting on a row that consumed the press — a fold header —
    /// updated a selection whose anchor was still at its default, selecting
    /// from the top of the transcript.
    dragging: bool,
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
    /// Open surfaces, topmost last. Owns key routing while non-empty.
    surfaces: crate::surface::stack::SurfaceStack,
    /// Local Coda file locations for this session.
    paths: Paths,
    /// Outcomes reported by `event/taskCompleted`, keyed by task id.
    task_outcomes: std::collections::BTreeMap<String, crate::browsers::TaskOutcome>,
    /// How the engine was launched, so it can be restarted in place.
    engine_command: EngineCommand,
    /// A freshly started engine waiting for the run loop to swap it in.
    restarted: Option<(Engine, mpsc::UnboundedReceiver<Inbound>)>,
    /// Images staged by `/image` to be sent with the next user turn.
    staged_images: Vec<messages::WireImage>,
    /// The active drag-selection over the transcript, if any.
    selection: crate::selection::TranscriptSelection,
    /// Screen row where the transcript area starts, captured at draw time.
    ///
    /// Mouse coordinates are screen-relative, so translating a click into a
    /// transcript row needs to know where the transcript begins and how far it
    /// is scrolled. Recording it during the draw keeps the two in step rather
    /// than duplicating the layout arithmetic here.
    transcript_origin: (u16, u16),
    /// Screen cell of the composer's first text column, for click-to-caret.
    composer_origin: (u16, u16),
}


impl App {


    /// Connects to an engine and completes the handshake.
    pub async fn connect(
        command: EngineCommand,
        theme: Theme,
    ) -> Result<(Self, Engine, mpsc::UnboundedReceiver<Inbound>)> {
        Self::connect_to_session(command, theme, None).await
    }

    /// Connects to an engine, resuming `session_id` when one is given.
    ///
    /// Resuming is part of the handshake rather than something done afterwards
    /// because the engine seeds its history from the stored transcript while
    /// initialising; asking later would leave the first turn without it.
    pub async fn connect_to_session(
        command: EngineCommand,
        theme: Theme,
        session_id: Option<String>,
    ) -> Result<(Self, Engine, mpsc::UnboundedReceiver<Inbound>)> {
        let (engine, inbound) = Engine::spawn(command.clone()).context("failed to start the engine")?;
        let connection = engine.connection();

        let mut init = messages::InitializeParams::new("coda-tui");
        init.session_id = session_id;
        let params = serde_json::to_value(init)?;
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
            spinner_at: None,
            connected_provider: None,
            dragging: false,
            detached_anchor: None,
            turn: None,
            pending_responder: None,
            armed: None,
            surfaces: Default::default(),
            paths: Paths::new(project_root),
            task_outcomes: std::collections::BTreeMap::new(),
            engine_command: command,
            restarted: None,
            staged_images: Vec::new(),
            selection: crate::selection::TranscriptSelection::new(),
            transcript_origin: (0, 0),
            composer_origin: (0, 0),
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
            self.tick_spinner();
            self.maybe_redraw(guard)?;
            self.arm_spinner_wakeup();
        }

        if let Some(engine) = owned_engine {
            let _ = engine.shutdown(SHUTDOWN_GRACE).await;
        }
        Ok(self.exit_summary(started_at.elapsed()))
    }

    // -- Engine -------------------------------------------------------------



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
    ///
    /// The engine reports which model is active; the list is only how it is
    /// labelled. Taking the first entry instead named whatever the provider
    /// happened to return first, so the status bar could disagree with the
    /// engine and switching a model looked as though it had not been saved.
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
        // Remembered so a model switch is saved under the provider the engine
        // connected with, rather than the one settings nominate.
        if let Some(provider) = result.provider_id.clone() {
            self.connected_provider = Some(provider);
        }
        if let Some(label) = result.active_label() {
            let context_limit = result.active_context_limit();
            self.apply(UiEvent::ModelChanged {
                id: label.to_string(),
                context_limit,
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
            TerminalEvent::Mouse(mouse) => {
                if let Some(action) = self.decide_pointer_action(mouse) {
                    match action {
                        PointerAction::Copy => self.copy_selection_via_pointer(),
                        PointerAction::Paste => self.paste_from_pointer(),
                    }
                }
            }
            TerminalEvent::FocusGained | TerminalEvent::FocusLost => {}
        }
        Ok(())
    }


    async fn on_key(&mut self, key: KeyEvent) {
        // Open surfaces own the keyboard, topmost first. A key the top surface
        // declines falls through to the global keymap below, so opening a
        // surface never disables Ctrl+C. An engine prompt is a surface too,
        // and its Exclusive modality is what keeps it on top rather than the
        // ordering of the branches here.
        if !self.surfaces.is_empty() {
            use crate::surface::stack::StackOutcome;
            match self.surfaces.handle_key(key) {
                StackOutcome::Handled => {
                    self.dirty = true;
                    return;
                }
                StackOutcome::Action(action) => {
                    self.dirty = true;
                    self.apply_surface_action(action).await;
                    return;
                }
                StackOutcome::Ignored => {}
            }
        }

        // Ctrl+C copies when there is a selection, matching the Windows console
        // and every terminal emulator. The selection is cleared either way, so
        // a second Ctrl+C still exits — leaving it set would trap the user in a
        // session they cannot quit with the key that normally quits it.
        if key.code == KeyCode::Char('c')
            && key.modifiers.contains(crossterm::event::KeyModifiers::CONTROL)
            && self.selection.has_selection()
        {
            self.copy_selection_via_pointer();
            self.selection.clear();
            self.armed = None;
            self.dirty = true;
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
                // Fills the input and stops. Accepting never runs: the point
                // is to see what you are about to run, and add arguments.
                self.composer.accept_completion();
            }
            Action::CompletionSubmit => {
                // Runs whatever is highlighted. A candidate is highlighted
                // from the moment the popup opens, so acting on anything else
                // would contradict what is on screen — and it is what makes
                // typing a prefix and pressing Enter run the command rather
                // than submitting an unknown one.
                self.composer.accept_completion();
                self.composer.clear_completions();
                self.submit().await;
            }
            Action::CompletionCancel => self.composer.clear_completions(),

            Action::ScrollUp => {
                self.viewport.scroll_up(1);
                self.remember_position();
            }
            Action::ScrollDown => {
                self.viewport.scroll_down(1);
                self.remember_position();
            }
            Action::PageUp => {
                self.viewport.page_up();
                self.remember_position();
            }
            Action::PageDown => {
                self.viewport.page_down();
                self.remember_position();
            }
            Action::ScrollTop => {
                self.viewport.scroll_to_top();
                self.remember_position();
            }
            Action::ScrollBottom => {
                self.viewport.scroll_to_bottom();
                self.remember_position();
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


















    /// Answers an open prompt.

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

    /// Seeds the transcript with the startup banner.
    ///
    /// The banner belongs in the transcript, not on the raw console: written
    /// before the alternate screen it is hidden the moment the screen is
    /// entered, so the user never sees it at all. In the transcript it scrolls
    /// and can be selected like any other content.
    pub fn push_banner(&mut self, working_directory: &str) {
        let session = self.session_snapshot();
        self.state.transcript.push(crate::transcript::Block::Banner {
            wordmark: crate::branding::wordmark_lines(),
            details: crate::branding::startup_detail_lines(
                working_directory,
                session.provider.as_deref(),
                session.model.as_deref(),
            ),
        });
        self.dirty = true;
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



    fn key_context(&self) -> KeyContext {
        let (line, _) = self.composer.cursor_position();
        KeyContext {
            // An open surface takes focus away from the composer. Without
            // this, any key the surface declines is resolved as composer
            // editing and typed into a composer the user cannot see: letters
            // inserted, Backspace deleting, Up loading a past submission —
            // all behind a modal, and submitted on close.
            //
            // A prompt is a surface too, so it needs no separate branch; a
            // second condition reading `state.prompt` would be a second source
            // of truth for the same question.
            focus: if !self.surfaces.is_empty() {
                Focus::Surface
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

        // Keep the prompt surface in lockstep with the reducer. The engine can
        // clear a prompt without it being answered — a turn ending or being
        // interrupted does exactly that — and an Exclusive surface left behind
        // would be undismissable, wedging the interface permanently.
        if self.state.prompt.is_none() {
            self.retire_prompt_surface();
        }
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
    /// Advances the working indicator when its frame is due.
    ///
    /// Time-driven rather than event-driven: the indicator has to keep moving
    /// through the long silences between engine events, which is exactly when
    /// the user most needs telling that anything is still happening.
    fn tick_spinner(&mut self) {
        if !self.state.activity.is_animated() {
            // Reset, so the next turn starts at the first frame rather than
            // wherever the last one stopped.
            self.state.spinner = 0;
            self.spinner_at = None;
            return;
        }

        let due = self
            .spinner_at
            .is_none_or(|at| at.elapsed() >= Duration::from_millis(SPINNER_FRAME_MS));
        if due {
            self.state.spinner = self.state.spinner.wrapping_add(1);
            self.spinner_at = Some(std::time::Instant::now());
            self.dirty = true;
        }
    }

    /// Wakes the loop when the next indicator frame is due.
    ///
    /// Without this the loop blocks in `select!` until an event arrives, and
    /// the indicator freezes during precisely the long waits it exists to
    /// cover. Never displaces an already-armed deadline, which is a redraw
    /// falling due sooner.
    fn arm_spinner_wakeup(&mut self) {
        if !self.state.activity.is_animated() || self.frame_deadline.is_some() {
            return;
        }
        self.frame_deadline =
            Some(tokio::time::Instant::now() + Duration::from_millis(SPINNER_FRAME_MS));
    }

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

        // Where the composer's first text cell sits, so a click can be turned
        // into a caret position. Captured from the same layout that is about
        // to be drawn, rather than recomputed in the event handler where it
        // would silently drift the first time the chrome changed.
        self.composer_origin = (
            regions.composer.x + draw::COMPOSER_TEXT_COLUMN,
            regions.composer.y + 1,
        );

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
        
        let pin = pin_text.as_deref();
        let selection = self.selection.has_selection().then_some(&self.selection);
        let surfaces = &self.surfaces;

        // The transcript origin is captured from the draw so mouse-to-row
        // translation always matches the layout that was actually rendered.
        let mut origin = self.transcript_origin;
        // Hidden for the duration of the write. Ratatui shows the cursor after
        // painting, at whatever position the frame asked for, but never hides
        // it beforehand — so while cells are being written the hardware cursor
        // is dragged across the screen by the writes and the terminal blinks
        // it wherever it happens to be. Visible as flicker away from the
        // caret, and worse now the working indicator forces a frame every
        // 110ms. Whatever sets a cursor position — the composer, or a focused
        // field in a surface — shows it again at the end of the frame.
        guard.terminal().hide_cursor()?;
        guard.terminal().draw(|frame| {
            origin = draw::draw_with_pin(
                frame, state, composer, viewport, rows, theme, pin, selection,
            );
            // Surfaces draw last and bottom-up, so a detail sits over its list
            // and the whole stack sits over the shell. Rendered as a second
            // pass rather than a tenth parameter on draw_with_pin.
            for rendered in surfaces.render(frame.area(), theme) {
                draw::draw_surface(frame, &rendered, theme);
            }
        })?;
        self.transcript_origin = origin;

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
    /// Shows a passing message on the line above the composer.
    ///
    /// For facts worth saying once and not worth keeping. The transcript is a
    /// record of the conversation; "copied 412 characters" is not part of it,
    /// and putting it there pushed the conversation up the screen to say so.
    pub(super) fn hint(&mut self, text: impl Into<String>) {
        self.state.hint = Some(text.into());
        self.dirty = true;
    }

    /// Records where the user is now, so a reflow can put them back.
    ///
    /// Recomputed after *every* scroll, not only when the viewport first
    /// detaches. An anchor captured once describes where the user was at that
    /// moment, so every later scroll was undone by the next arriving event —
    /// the transcript jumped back to wherever they had first scrolled away
    /// from, which reads as the panel moving on its own.
    fn remember_position(&mut self) {
        self.detached_anchor = if self.viewport.is_following() {
            None
        } else {
            self.compute_anchor()
        };
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
    /// Opens the settings surface, seeded from the settings on disk.
    fn open_settings_form(&mut self) {
        if !self
            .surfaces
            .push(Box::new(crate::surface::settings::SettingsSurface::open(
                &self.paths,
            )))
        {
            // Refused by an exclusive surface. Say so, rather than appearing
            // to do nothing.
            self.notice(
                "Answer the open prompt first.",
                NoticeLevel::Warning,
            );
        }
        self.dirty = true;
    }

    /// Performs the work a surface asked for.
    ///
    /// The only bridge from a surface to the engine and the filesystem;
    /// surfaces themselves do no I/O, which is what makes them testable
    /// without either.
    async fn apply_surface_action(&mut self, action: crate::surface::SurfaceAction) {
        use crate::surface::SurfaceAction;

        match action {
            SurfaceAction::SaveSettings => {
                // Take the surface first: whether the save succeeds or fails,
                // the modal closes, so a broken settings file cannot trap the
                // user in a form they can only escape by killing the process.
                let Some(surface) = self.surfaces.pop() else {
                    return;
                };
                let Some(settings_surface) = surface
                    .as_any()
                    .downcast_ref::<crate::surface::settings::SettingsSurface>()
                else {
                    // Some other surface emitted SaveSettings. Put it back
                    // rather than closing it and discarding the edit: losing
                    // both the modal and the save with no message is the worst
                    // of the available outcomes.
                    self.surfaces.push(surface);
                    self.notice(
                        "Could not save: unexpected surface.",
                        NoticeLevel::Error,
                    );
                    return;
                };
                let mut settings = crate::config::Settings::load(&self.paths)
                    .unwrap_or_else(|_| crate::config::Settings::empty_at(self.paths.settings()));
                match settings_surface.apply(&mut settings) {
                    Ok(()) => self.notice(
                        "Settings saved. Some changes apply on restart.",
                        NoticeLevel::Info,
                    ),
                    Err(err) => self.notice(
                        format!("Could not save settings: {err}"),
                        NoticeLevel::Error,
                    ),
                }
            }
            SurfaceAction::SaveMcpServer => {
                let Some(surface) = self.surfaces.pop() else {
                    return;
                };
                let Some(editor) = surface
                    .as_any()
                    .downcast_ref::<crate::surface::mcp_editor::McpEditorSurface>()
                else {
                    self.surfaces.push(surface);
                    self.notice("Could not save: unexpected surface.", NoticeLevel::Error);
                    return;
                };

                let draft = editor.draft();
                // A rename leaves the old entry behind unless it is retired,
                // and the loader would then serve two servers under one name.
                let renamed_from = editor
                    .original_name()
                    .filter(|old| *old != draft.name)
                    .map(str::to_string);

                let paths = self.paths.clone();
                let name = draft.name.clone();
                let saved = tokio::task::spawn_blocking(move || {
                    if let Some(old) = renamed_from {
                        config::delete_mcp_server(&paths, &old)?;
                    }
                    config::save_mcp_server(&paths, &draft)
                })
                .await;

                match saved {
                    Ok(Ok(())) => {
                        self.notice(
                            format!("Saved MCP server '{name}'. Restart the engine to connect."),
                            NoticeLevel::Info,
                        );
                        // Reflect the change in the list underneath, if it is
                        // still open.
                        if self.browser_kind() == Some(BrowserKind::Mcp) {
                            self.reload_browser().await;
                        }
                    }
                    Ok(Err(err)) => {
                        self.notice(format!("Could not save: {err}"), NoticeLevel::Error)
                    }
                    Err(_) => self.notice("The save was interrupted.", NoticeLevel::Error),
                }
            }
            SurfaceAction::Browser { kind: _, intent } => match intent {
                Intent::Reload => self.reload_browser().await,
                // Anything a browser did not claim for itself. Enter with no
                // configured action opens a detail view, which the browser
                // handled before reaching here.
                Intent::Activate(_)
                | Intent::Toggle(_)
                | Intent::Delete(_)
                | Intent::Key(_, _)
                | Intent::Redraw
                | Intent::Ignored
                | Intent::Close => self.dirty = true,
            },

            // ── Browser row actions ────────────────────────────────────────
            //
            // Flat, and named after the work. Reaching one of these means the
            // browser that raised it declared it at construction, so there is
            // no kind to look up and nothing to forget.
            SurfaceAction::SwitchModel(id) => self.switch_model(&id).await,
            SurfaceAction::ResumeSession(id) => self.resume_to_session(id).await,
            SurfaceAction::TogglePlugin(id) => self.toggle_plugin(&id).await,
            SurfaceAction::UpdatePlugin(id) => self.update_plugin(&id).await,
            SurfaceAction::ToggleMcp(id) => self.toggle_mcp(&id).await,
            SurfaceAction::DeleteSchedule(id) => self.delete_schedule(&id).await,
            SurfaceAction::NewMcpServer => {
                self.surfaces.push(Box::new(
                    crate::surface::mcp_editor::McpEditorSurface::creating(),
                ));
                self.dirty = true;
            }
            SurfaceAction::EditMcpServer(id) => self.edit_mcp_server(Some(id)).await,
            SurfaceAction::DeleteMcpServer(id) => self.delete_mcp_server(Some(id)).await,
            SurfaceAction::ExplainScheduleCreation => self.notice(
                "Creating a schedule needs arguments; use /schedule from the composer.",
                NoticeLevel::Info,
            ),
            SurfaceAction::ExplainSkillToggle => self.notice(
                "Skills are frontmatter-driven; edit the SKILL.md file to change them.",
                NoticeLevel::Info,
            ),
            SurfaceAction::AnswerPrompt { allowed, answer } => {
                self.surfaces.pop();
                self.answer_prompt(allowed, answer);
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















    // -- Session management commands -----------------------------------------






}












/// Returns `true` for events that bypass the 30 FPS streaming throttle.
///
/// Mirrors `UiActor.IsCritical` in C#: turn boundaries, errors, prompts,
/// session lifecycle, and mode changes all get immediate frames.
fn is_critical_event(event: &UiEvent) -> bool {
        match event {
        UiEvent::TurnFinished { .. }
        | UiEvent::Connected { .. }
        | UiEvent::PromptRequested(_)
        | UiEvent::PromptAnswered { .. }
        | UiEvent::Notice { .. }
        | UiEvent::Cleared
        | UiEvent::ModelChanged { .. }
        | UiEvent::DisplayModeChanged(_)
        // A fold is a direct response to a click, so it must repaint at once
        // rather than waiting for the streaming throttle: an idle session
        // produces no further frames to carry it.
        | UiEvent::ThinkingFoldToggled { .. }
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

