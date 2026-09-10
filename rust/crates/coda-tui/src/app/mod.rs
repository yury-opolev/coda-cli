//! The application loop.
//!
//! Terminal input and engine notifications arrive on independent channels and
//! are funnelled into the same reducer, so ordering between a keystroke and a
//! streamed token is explicit. Rendering happens once per iteration, only when
//! something actually changed.

use std::time::Duration;

use anyhow::{Context, Result};
use coda_client::{ClientError, Connection, Engine, EngineCommand, Inbound};
use coda_proto::messages::{self, method};
use coda_render::{RenderLine, Theme};
use crossterm::event::{
    Event as TerminalEvent, EventStream, KeyCode, KeyEvent, KeyEventKind,
};
use futures::future::OptionFuture;
use futures_lite::StreamExt;
use serde_json::Value;
use tokio::sync::mpsc;
use tokio::sync::oneshot;

mod auth;
mod browsers;
mod slash;
mod clipboard;
mod effort;
mod engine;
mod identity;
mod image;
mod queue;
mod serve;
mod settings;
mod startup_cli;

use crate::config::{self, Paths};
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

/// How long the loop waits before running a re-read it owes but has no
/// schedule for.
///
/// Short enough that a stale screen corrects itself immediately, long enough
/// that a read which keeps re-owing one cannot become a spin: each wake costs
/// one settle, and a settle that fails arms a real backoff instead.
const SETTLE_WAKE: Duration = Duration::from_millis(50);

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
    /// The event fence: what this client has already seen (§2.5).
    pub(crate) view: crate::api::ServeView,
    /// Outstanding permission/question/plan decisions, from the raw
    /// round-trip and from discovery alike.
    pub(crate) pending: crate::api::requests::PendingInteractions,
    /// Set when the engine's authoritative state must be re-read.
    pub(crate) needs_resync: bool,
    /// Set when the conversation itself must be rebuilt from the engine.
    pub(crate) needs_rehydrate: bool,
    /// Retry schedule for `session/getState`.
    pub(crate) resync_recovery: serve::Recovery,
    /// Retry schedule for `session/getHistory`.
    pub(crate) rehydrate_recovery: serve::Recovery,
    /// Whether this client may maintain the engine-adjacent files itself.
    pub(crate) access_mode: crate::local::AccessMode,
    /// How long the UI waits for one engine read before treating the silence
    /// as a failure. A field rather than a constant so the bound is visible
    /// where the loop is, and drivable in a test without sleeping through it.
    pub(crate) metadata_timeout: Duration,
    /// The engine's own configuration catalogue, when it has been read.
    pub(crate) config_catalog: Option<coda_proto::config::ConfigDescribeResult>,
    /// How long a deliberate stop waits for the engine to go away before it
    /// is killed.
    ///
    /// A field rather than a constant because an authentication transition
    /// depends on this bound being *reached*: the credential is only safe to
    /// delete once the process using it is gone, and a test proving that
    /// ordering must not have to sleep through the production grace period.
    pub(crate) shutdown_grace: Duration,
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
    /// Images staged by `/image`/clipboard paste; identified by marker, not position.
    staged_images: Vec<image::StagedImage>,
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
    /// The effort level currently applied to the session.
    ///
    /// `None` means auto / not set. Updated when the picker confirms a choice.
    /// Used to pre-select the picker at the setting already in effect rather
    /// than defaulting to "high" every time.
    session_effort: Option<String>,
    /// The engine's own diagnostic log path, as reported by its
    /// `initialize` response (`telemetryLogPath`). `None` when the engine
    /// has no healthy diagnostic destination — surfaced by `/log`, not
    /// silently treated as "logging is off".
    engine_log_path: Option<String>,
    /// Where the header's session id is drawn; shared by drawing and hit-testing.
    header_id_rect: Option<ratatui::layout::Rect>,
    /// Whether the header's session id is selected (all-or-nothing).
    header_id_selected: bool,
    /// The engine process this application owns — the one it was handed at
    /// launch, and thereafter whichever replacement superseded it.
    ///
    /// Unified deliberately. While only restarts were owned here, an
    /// authentication transition could not stop the *original* engine at all:
    /// it belonged to `main`, so the credential it was using was deleted while
    /// its process was still running and spending it.
    owned_engine: Option<Engine>,
    /// Whether an engine is believed to be answering.
    ///
    /// Cleared by a deliberate disconnection so the loop stops reading a
    /// closed channel and stops polling for a recovery that cannot happen,
    /// while every local command keeps working.
    engine_connected: bool,
    /// The running authentication flow, if any.
    auth: auth::AuthState,
    /// How a login opens this profile.
    auth_port: crate::local::auth::AuthPort,
}


impl App {
    /// Runs until the user quits or the engine disconnects.
    /// Runs the UI loop, returning the summary the caller prints on exit.
    ///
    /// The summary is produced here rather than by the caller because the loop
    /// consumes `self`: usage and session id are only final once it returns.
    ///
    /// **Every** way out of the loop — a quit, a disconnect, a terminal error
    /// — goes through [`Self::finish`], which is the only place this session
    /// is torn down. It is written this way on purpose: the teardown used to
    /// live in a method with no callers at all, so in production the engine's
    /// outstanding requests were left to be cancelled by `Drop`, which cannot
    /// tell a live request from one whose engine has been replaced.
    ///
    /// `engine` is the process the caller started for this session. It is
    /// handed over rather than kept, because a credential change has to stop
    /// and *await* it before writing: a caller holding it could only be told
    /// about the change afterwards, by which time its child has already
    /// outlived the credential it was using.
    pub async fn run(
        mut self,
        guard: &mut TerminalGuard,
        inbound: mpsc::UnboundedReceiver<Inbound>,
        engine: Engine,
        started_at: std::time::Instant,
    ) -> Result<crate::branding::ExitSummary> {
        // One owner for the original and for every replacement after it.
        self.owned_engine = Some(engine);
        let outcome = self.event_loop(guard, inbound).await;
        // `finish` is the whole teardown, engines included: the session is
        // closed out over the connection first, and only then do the
        // processes this loop owns go away.
        self.finish(outcome, started_at).await
    }

    /// Tears the session down and reports what the run produced.
    ///
    /// The single exit for every path through [`Self::run`], including the
    /// failing ones: the engine's outstanding requests are answered or
    /// discarded *before* anything is dropped, then every process this
    /// application owns is asked to stop and awaited, and only then is the
    /// run's own outcome propagated. Bound to that order rather than to
    /// `?`-propagation, so a failing run still stops its engines instead of
    /// leaking them.
    pub(crate) async fn finish(
        &mut self,
        outcome: Result<()>,
        started_at: std::time::Instant,
    ) -> Result<crate::branding::ExitSummary> {
        self.close_out().await;
        self.stop_owned_engines(SHUTDOWN_GRACE).await;
        outcome?;
        Ok(self.exit_summary(started_at.elapsed()))
    }

    async fn event_loop(
        &mut self,
        guard: &mut TerminalGuard,
        mut inbound: mpsc::UnboundedReceiver<Inbound>,
    ) -> Result<()> {
        let mut terminal_events = EventStream::new();
        let (turn_tx, mut turn_rx) = mpsc::unbounded_channel::<TurnOutcome>();
        let mut auth_events = self.auth.take_events();

        // The screen first, before anything off this machine is awaited. The
        // model list is a round-trip and the preflight opens a credential
        // store — both bounded, neither instant — and doing them ahead of the
        // first draw left the terminal blank for as long as they took, with
        // no banner, no composer and nothing to say why.
        self.redraw(guard)?;
        self.load_models().await;
        // Says nothing at all when this profile already has a credential, and
        // nothing ever for an engine this client did not start.
        self.preflight().await;
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
                //
                // Guarded: after a deliberate disconnection the channel is
                // closed and `recv` answers `None` immediately and forever,
                // which would spin the loop at full speed while reporting a
                // disconnection the user asked for.
                message = inbound.recv(), if self.engine_connected => match message {
                    Some(message) => self.on_inbound(message),
                    None => {
                        self.set_engine_connected(false);
                        self.notice("The engine disconnected.", NoticeLevel::Error);
                        self.redraw(guard)?;
                        break;
                    }
                },

                // An authentication task has something to report.
                Some(event) = auth_events.recv() => self.on_auth_event(event).await,

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
                let previous = std::mem::replace(&mut self.owned_engine, Some(engine));
                inbound = next_inbound;
                if let Some(previous) = previous {
                    let _ = previous.shutdown(SHUTDOWN_GRACE).await;
                }
            }

            if self.state.should_quit {
                break;
            }
            // Whatever the event fence asked for: a snapshot after a gap or a
            // reset, a rehydration after the conversation was replaced. Done
            // here, between iterations, so no RPC is awaited while an inbound
            // frame is half-processed.
            self.settle_with_engine().await;
            self.tick_spinner();
            if self.state.tick_thinking(std::time::Instant::now()) {
                self.laid_out_width = 0;
                self.dirty = true;
            }
            self.dirty |= self.state.hints.prune(std::time::Instant::now());
            self.maybe_redraw(guard)?;
            self.arm_spinner_wakeup();
        }

        Ok(())
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
        if !self.engine_connected {
            return;
        }
        // Bounded like every other read the loop awaits: this one runs at
        // startup and after a model switch, and an unbounded await here would
        // freeze the UI before it had drawn a single frame.
        let Ok(value) = self
            .bounded(
                self.connection
                    .request(method::MODELS, Some(serde_json::json!({ "refresh": false }))),
            )
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
            let price = result.active_price();
            self.apply(UiEvent::ModelChanged {
                id: label.to_string(),
                context_limit,
            });
            // Carried on the state so the renderer can show a running cost
            // without reaching for a catalogue of its own.
            self.state.usage.price_per_million = price;
        }
        self.refresh_effort().await;
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
            && (self.selection.has_selection() || self.header_id_selected)
        {
            self.copy_selection_via_pointer();
            self.selection.clear();
            self.header_id_selected = false;
            self.armed = None;
            self.state.hints.clear_chord();
            self.dirty = true;
            return;
        }

        let action = keymap::resolve(key, self.key_context());
        self.dirty = true;

        // Any other keystroke disarms, so a chord only ever fires on two
        // consecutive presses of the same key.
        if !matches!(action, Action::Arm(_)) {
            self.armed = None;
            self.state.hints.clear_chord();
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
                if self.recall_pending_into_composer().await {
                    return;
                }
                if self.recall_unsent_into_composer() {
                    return;
                }
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
                let hint_text = match chord {
                    keymap::Chord::Exit if self.state.is_busy() => {
                        "Press Ctrl+C again to stop the turn."
                    }
                    keymap::Chord::Exit => "Press Ctrl+C again to exit.",
                    keymap::Chord::Interrupt => "Press Esc again to stop the turn.",
                };
                self.state.hints.push_chord(
                    hint_text,
                    keymap::CHORD_WINDOW,
                    std::time::Instant::now(),
                );
                self.dirty = true;
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
                    self.staged_images.clear();
                }
            }
            Action::ClearTranscript => self.apply(UiEvent::Cleared),
            Action::Copy => self.copy_to_clipboard(),
            Action::Paste => self.paste_image_from_clipboard(),
            Action::Confirm | Action::None => self.dirty = false,
        }
    }


















    /// Answers an open prompt.

    // -- Actions ------------------------------------------------------------

    async fn submit(&mut self) {
        let text = self.composer.take_submission();
        if text.trim().is_empty() {
            self.staged_images.clear();
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
        let images = image::images_for_draft(&self.staged_images, &text);
        if !self.engine_connected {
            // The draft is put back, never sent and never queued for an
            // automatic resend: reconnecting is a decision, and so is sending.
            self.composer.set_text(text);
            self.notice(
                "Not connected to an engine, so nothing was sent. Your message is still here. \
                 Run /provider or /login to connect.",
                NoticeLevel::Warning,
            );
            return;
        }
        if self.state.is_busy() {
            if !images.is_empty() {
                self.composer.set_text(text);
                self.notice("Images cannot be steered into a running turn. Send this draft after it finishes.", NoticeLevel::Warning);
                return;
            }
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
        let params = match serde_json::to_value(messages::PromptParams {
            text: Some(text.clone()),
            images,
        }) {
            Ok(params) => params,
            Err(error) => {
                self.composer.set_text(text);
                self.notice(format!("Could not prepare prompt: {error}"), NoticeLevel::Error);
                return;
            }
        };
        match self.connection.send_request(method::PROMPT, Some(params)) {
            Ok(receiver) => {
                self.staged_images.clear();
                self.apply(UiEvent::Submitted { text: display });
                self.turn = Some(receiver);
            }
            Err(error) => {
                self.composer.set_text(text);
                self.notice(format!("Could not send prompt; draft retained: {error}"), NoticeLevel::Error);
            }
        }
    }

    fn interrupt(&mut self) {
        if !self.engine_connected {
            return;
        }
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
        if crate::state::is_critical_event(&event) {
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
            // Not animated is not the same as not busy: keep the pinned row's clock advancing while waiting.
            self.dirty |= self.state.is_busy();
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

    /// Wakes the loop when the next indicator frame or hint expiry is due.
    ///
    /// Without this the loop blocks in `select!` until an event arrives, and
    /// the indicator freezes during precisely the long waits it exists to
    /// cover. Never displaces an already-armed deadline, which is a redraw
    /// falling due sooner.
    ///
    /// Also arms a wakeup for the next hint expiry so transient messages
    /// disappear on time even when no other events are arriving.
    /// Arms a timer wakeup for whatever the loop owes itself next: the
    /// spinner, a hint expiry, or a backed-off re-read of the engine's state.
    fn arm_spinner_wakeup(&mut self) {
        if self.frame_deadline.is_some() {
            return;
        }
        let now = std::time::Instant::now();

        let mut deadline: Option<std::time::Instant> = None;

        // Wakeup while busy, animated or not: the pinned row's elapsed clock
        // must advance through a silent wait too.
        if self.state.is_busy() {
            let spinner_due = now + Duration::from_millis(SPINNER_FRAME_MS);
            deadline = Some(match deadline {
                Some(d) => d.min(spinner_due),
                None => spinner_due,
            });
        }

        // Hint expiry wakeup.
        if let Some(expiry) = self.state.hints.next_expiry(now) {
            deadline = Some(match deadline {
                Some(d) => d.min(expiry),
                None => expiry,
            });
        }

        // A re-read this client owes itself. The loop only settles when
        // something wakes it, and the two ways a re-read comes to be owed
        // both leave nothing else that would: a *successful* snapshot clears
        // the retry schedule and can still owe another read (a hole in the
        // replayed buffer, a config or steering change it carried), and a
        // failing engine sends no events at all. Either way an idle screen
        // would sit on state it already knows is stale until the user typed.
        //
        // Gated by each read's own schedule, so a failing engine is retried
        // at the backoff rather than at the speed of this wakeup.
        let owed = [
            (self.needs_resync || self.view.needs_snapshot(), self.resync_recovery.next_attempt()),
            (self.needs_rehydrate, self.rehydrate_recovery.next_attempt()),
        ];
        for due in owed
            .into_iter()
            .filter(|(owed, _)| *owed)
            .map(|(_, scheduled)| scheduled.unwrap_or(now + SETTLE_WAKE))
        {
            deadline = Some(match deadline {
                Some(d) => d.min(due),
                None => due,
            });
        }

        if let Some(d) = deadline {
            self.frame_deadline = Some(tokio::time::Instant::from_std(d));
        }
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
        let regions = draw::layout_with_pending(
            ratatui::layout::Rect::new(0, 0, size.width, size.height),
            self.composer.line_count(),
            self.viewport.is_scrollable(),
            self.state.is_busy(),
            self.state.queued.len() + self.state.unsent.len(),
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
        // Same rect drawing will use, so a click can never target stale layout.
        self.header_id_rect = regions.header.and_then(|h| draw::header_id_rect(h, &self.state));

        if width != self.laid_out_width {
            let was_following = self.viewport.is_following();
            let style = crate::transcript::TranscriptStyle::Cards;
            let (rows, starts) =
                self.state.transcript.render_with_block_starts_styled(width, self.state.display_mode, style);
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
                frame, state, composer, viewport, rows, theme, pin, selection, self.header_id_selected, std::time::Instant::now(),
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
        self.state.hints.push_transient(text, std::time::Instant::now());
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
                // The form edits two owners' values. Appearance is always
                // this client's; the permission mode and telemetry are read
                // by the engine at *its* startup, so an API-only session
                // saves only its own half and says which half that was.
                settings_surface.apply_client_local(&mut settings);
                let engine_owned = self.owns_engine_settings();
                if engine_owned {
                    settings_surface.apply_engine_owned(&mut settings);
                }
                match settings.save() {
                    Ok(()) if engine_owned => self.notice(
                        "Settings saved. Some changes apply on restart.",
                        NoticeLevel::Info,
                    ),
                    Ok(()) => self.notice(
                        "Appearance settings saved. The permission mode and telemetry \
                         settings belong to the engine host, which this client did not \
                         start, so they were left alone.",
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
                let original = editor.original().cloned();

                if !self.allow_local_maintenance("MCP server configuration") {
                    return;
                }

                let paths = self.paths.clone();
                let name = draft.name.clone();
                let saved = tokio::task::spawn_blocking(move || {
                    config::save_mcp_server(&paths, &draft, original.as_ref())
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
            SurfaceAction::TogglePlugin(id) => {
                if self.allow_local_maintenance("Plugin management") {
                    self.toggle_plugin(&id).await
                }
            }
            SurfaceAction::UpdatePlugin(id) => {
                if self.allow_local_maintenance("Plugin management") {
                    self.update_plugin(&id).await
                }
            }
            SurfaceAction::ToggleMcp(id) => {
                if self.allow_local_maintenance("MCP server configuration") {
                    self.toggle_mcp(&id).await
                }
            }
            SurfaceAction::DeleteSchedule(id) => self.delete_schedule(&id).await,
            SurfaceAction::NewMcpServer => {
                if self.allow_local_maintenance("MCP server configuration") {
                    self.surfaces.push(Box::new(
                        crate::surface::mcp_editor::McpEditorSurface::creating(),
                    ));
                    self.dirty = true;
                }
            }
            SurfaceAction::EditMcpServer(id) => {
                if self.allow_local_maintenance("MCP server configuration") {
                    self.edit_mcp_server(Some(id)).await
                }
            }
            SurfaceAction::DeleteMcpServer(id) => {
                if self.allow_local_maintenance("MCP server configuration") {
                    self.delete_mcp_server(Some(id)).await
                }
            }
            SurfaceAction::ExplainScheduleCreation => self.notice(
                "Creating a schedule needs arguments; use /schedule from the composer.",
                NoticeLevel::Info,
            ),
            SurfaceAction::ExplainSkillToggle => self.notice(
                "Skills are frontmatter-driven; edit the SKILL.md file to change them.",
                NoticeLevel::Info,
            ),
            SurfaceAction::SubmitAuthChoice => self.start_prepared_login(),
            SurfaceAction::CancelAuth => self.cancel_auth(),
            SurfaceAction::AnswerPrompt { allowed, answer } => {
                self.surfaces.pop();
                self.answer_prompt(allowed, answer).await;
            }
            SurfaceAction::SetEffort { effort, persist, for_model } => {
                self.apply_set_effort(effort, persist, for_model).await;
            }
            SurfaceAction::OpenEffortPicker => {
                // Close the browser first so the picker opens cleanly above it.
                self.retire_browser_surface();
                self.open_effort_picker(None).await;
            }
            SurfaceAction::OpenEffortPickerForModel(model) => {
                // Switch to the row's model first (intentional, visible), then
                // open the picker for it. Switching closes the browser.
                self.switch_model(&model).await;
                self.open_effort_for_model(None, Some(&model)).await;
            }
            SurfaceAction::AdjustModelEffort { model, direction } => {
                self.adjust_model_effort(model, direction).await;
            }
        }
    }

    /// Submits a programmatic prompt to the engine on behalf of a command.
    ///
    /// Used by `/init` and `/skill` to inject model-directed work into the
    /// running session without touching the composer.
    async fn submit_programmatic(&mut self, text: String) {
        if !self.engine_connected {
            self.notice(
                "Not connected to an engine; nothing was sent. Run /provider to connect.",
                NoticeLevel::Warning,
            );
            return;
        }
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
