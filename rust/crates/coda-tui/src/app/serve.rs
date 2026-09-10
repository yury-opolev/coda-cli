//! The App's side of the public serve API: fencing, resync, hydration.
//!
//! `app/engine.rs` is the wire seam (a notification arrives, a request goes
//! out). This is the *contract* seam: which inbound frames may be applied,
//! when the client has to go back and ask for the truth, and how an engine's
//! answer becomes the conversation on screen.
//!
//! Kept out of `app/mod.rs` deliberately — that file is the event loop and is
//! held to a line ceiling by `tests/conventions.rs`. "How this client stays in
//! step with the engine" is its own responsibility, not more of the loop's.

use anyhow::Result;
use coda_client::{Engine, EngineCommand, Inbound};
use coda_proto::Event;
use coda_render::Theme;
use tokio::sync::mpsc;

use super::App;
use crate::api::{self, requests::Delivery, view::Frame, Reception, StateFrame};
use crate::composer::Composer;
use crate::config::Paths;
use crate::state::{ExternalResolution, UiEvent, UiState};
use crate::transcript::NoticeLevel;
use crate::viewport::Viewport;

/// The delay before the first retry of a failed engine read.
const RETRY_BASE: std::time::Duration = std::time::Duration::from_millis(250);
/// The longest this client will ever wait between retries.
///
/// A cap rather than a give-up: an engine that is briefly overloaded comes
/// back, and a client that stopped asking would sit on stale state for ever
/// with nothing to make it look again.
const RETRY_CAP: std::time::Duration = std::time::Duration::from_secs(30);
/// How many consecutive failures are reported before the client stops
/// repeating itself. The retries continue; the notices do not.
const REPORTED_FAILURES: u32 = 2;

/// How long the UI waits for an engine read before treating the silence as a
/// failure.
///
/// Generous: these reads are answered from memory under short locks, so a
/// wait this long means something is genuinely wrong rather than merely slow.
/// It exists because the alternative is unbounded — a connected peer that
/// never answers freezes the whole event loop, and with it the keyboard, the
/// redraw and the ability to quit.
pub(crate) const DEFAULT_METADATA_TIMEOUT: std::time::Duration =
    std::time::Duration::from_secs(20);

/// How many committed entries the client asks for when rebuilding a
/// conversation, before the engine's own advertised maximum is applied.
const HISTORY_TAIL: i64 = 200;

/// Why an engine read produced no answer.
///
/// The three are deliberately distinct: a refusal is the engine's own typed
/// verdict (and may name a fence that moved), a transport failure is the
/// connection breaking, and silence is a peer that is still there and simply
/// did not reply. Only the first can ever be reported as "the engine said no".
#[derive(Debug)]
pub(crate) enum ReadFailure {
    Refused(coda_proto::ResponseError),
    Transport(coda_client::ClientError),
    Silent(std::time::Duration),
}

impl ReadFailure {
    /// The engine's own error code, when it answered at all.
    pub(crate) fn code(&self) -> Option<i64> {
        match self {
            ReadFailure::Refused(error) => Some(error.code),
            _ => None,
        }
    }

    /// Whether the engine answered "your fence is stale" — which is a reason
    /// to re-read the fence, never to ask again with the same one.
    fn fence_moved(&self) -> bool {
        use coda_proto::messages::error_code::{HISTORY_FENCE_MOVED, STALE_EPOCH};
        /// `session/getHistory` refuses a read from another process with this.
        const INSTANCE_CHANGED: i64 = -32010;
        matches!(
            self.code(),
            Some(STALE_EPOCH) | Some(HISTORY_FENCE_MOVED) | Some(INSTANCE_CHANGED)
        )
    }
}

impl std::fmt::Display for ReadFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ReadFailure::Refused(error) => write!(f, "{}", error.message),
            ReadFailure::Transport(error) => write!(f, "{error}"),
            ReadFailure::Silent(after) => {
                write!(f, "the engine did not answer within {}s", after.as_secs().max(1))
            }
        }
    }
}

/// Bounded, backed-off retry of one engine read.
///
/// `settle_with_engine` runs once per loop iteration, so an unconditional
/// retry turns a single broken RPC into a request per inbound frame and per
/// keystroke — the engine is hammered exactly when it is least able to answer,
/// and the terminal's own input loop is the thing paying for it. The schedule
/// here is the whole of the answer: attempts are spaced by an exponential
/// backoff capped at [`RETRY_CAP`], and the user is told the first couple of
/// times rather than once per attempt.
#[derive(Debug, Default)]
pub(crate) struct Recovery {
    failures: u32,
    /// The earliest instant another attempt may be made.
    next_attempt: Option<std::time::Instant>,
}

impl Recovery {
    /// Whether an attempt may be made now.
    pub(crate) fn ready(&self, now: std::time::Instant) -> bool {
        self.next_attempt.is_none_or(|at| now >= at)
    }

    /// Records a failure and schedules the next attempt. Returns whether the
    /// user should be told about this one.
    pub(crate) fn on_failure(&mut self, now: std::time::Instant) -> bool {
        self.failures = self.failures.saturating_add(1);
        let backoff = RETRY_BASE
            .checked_mul(1u32 << self.failures.min(16).saturating_sub(1))
            .unwrap_or(RETRY_CAP)
            .min(RETRY_CAP);
        self.next_attempt = Some(now + backoff);
        self.failures <= REPORTED_FAILURES
    }

    /// The read succeeded: the schedule starts over.
    pub(crate) fn on_success(&mut self) {
        self.failures = 0;
        self.next_attempt = None;
    }

    /// When the next attempt is due, if one is owed.
    ///
    /// The event loop arms a wakeup for this: it only settles when something
    /// wakes it, and an engine that fails a read while sending no events
    /// wakes nothing.
    pub(crate) fn next_attempt(&self) -> Option<std::time::Instant> {
        self.next_attempt
    }

    /// How many consecutive failures this read has had. Diagnostics and
    /// tests: a client that never recovers should be visible as such.
    pub(crate) fn failures(&self) -> u32 {
        self.failures
    }
}

/// The tail added to a failure notice once this client stops repeating
/// itself: silence would otherwise read as "it recovered".
fn quiet_from_now_on(failures: u32) -> &'static str {
    if failures >= REPORTED_FAILURES {
        " Retries continue, more slowly, and are not reported again."
    } else {
        ""
    }
}

impl App {
    /// Awaits one engine read within the UI's bound.
    ///
    /// Every read the event loop awaits goes through here. Without a bound a
    /// peer that is connected but silent stops the loop outright: no
    /// keystroke is read, no frame is drawn and the session cannot even be
    /// quit — and none of the existing retry machinery ever engages, because
    /// nothing ever returns to engage it.
    pub(crate) async fn bounded<T>(
        &self,
        read: impl std::future::Future<Output = Result<T, coda_client::ClientError>>,
    ) -> Result<T, ReadFailure> {
        match tokio::time::timeout(self.metadata_timeout, read).await {
            Err(_) => Err(ReadFailure::Silent(self.metadata_timeout)),
            Ok(Ok(value)) => Ok(value),
            Ok(Err(coda_client::ClientError::Rpc(error))) => Err(ReadFailure::Refused(error)),
            Ok(Err(other)) => Err(ReadFailure::Transport(other)),
        }
    }

    /// Routes one inbound notification through the event fence.
    ///
    /// Returns the frames the reducer should see, in order. A frame the
    /// snapshot we hold already covers is dropped here rather than applied
    /// twice, and a genuine gap arms a resync instead of being papered over.
    pub(in crate::app) fn accept(&mut self, method: String, params: Option<serde_json::Value>) -> Vec<Frame> {
        let frame = Frame::new(method, params);
        match self.view.receive(frame.clone()) {
            Reception::Apply | Reception::Unfenced => vec![frame],
            // Held: the pending snapshot decides whether they are new.
            Reception::Buffered => Vec::new(),
            Reception::Gap => {
                // A missing `seq` is a missing piece of the conversation, and
                // a snapshot does not carry conversation content: re-reading
                // state alone would refresh the metadata and leave the
                // transcript quietly short of a tool result. Say so, and
                // rebuild what the engine says the conversation is.
                self.on_events_lost("Some engine events did not arrive");
                Vec::new()
            }
            Reception::Overflowed => {
                // Our own bound, not the engine's fault: the events arrived
                // and this client could not hold them while a resync it had
                // asked for failed to complete. Blaming the engine here would
                // send anyone reading the message to the wrong place.
                self.on_events_lost("This client could not hold any more engine events");
                Vec::new()
            }
            Reception::InstanceChanged => {
                // A different process: its seqs, its request handles and its
                // conversation are all its own.
                self.on_engine_replaced();
                Vec::new()
            }
            Reception::Duplicate => Vec::new(),
        }
    }

    /// Drops the cached `config/describe` answer.
    ///
    /// Called wherever the engine's configuration may have moved underneath
    /// the cache: a published change, a replaced conversation, a replaced
    /// engine. A catalogue is cheap to re-read and expensive to be wrong
    /// about — every menu of allowed values is built from it.
    pub(crate) fn forget_described_config(&mut self) {
        self.config_catalog = None;
    }

    /// Events were genuinely missed: re-read the engine's state *and* rebuild
    /// the conversation, and tell the user that is what happened.
    fn on_events_lost(&mut self, what: &str) {
        self.notice(
            format!("{what}; re-reading the engine's state and conversation."),
            NoticeLevel::Warning,
        );
        self.needs_resync = true;
        self.needs_rehydrate = true;
    }

    /// The engine behind this connection was replaced.
    ///
    /// Every handle the previous process minted is meaningless to the new
    /// one, so the outstanding decisions are retired here rather than being
    /// answered against an engine that never asked. They are *discarded*, not
    /// declined: a numeric request id from the old process addresses nothing
    /// in the new one, and over a proxy that kept the connection it would
    /// address whatever now happens to hold that id.
    fn on_engine_replaced(&mut self) {
        let instance = self.view.engine_instance_id().map(str::to_string);
        let on_screen = self.pending.current().map(|entry| entry.key());
        let retired = self.pending.retire_other_instances(instance.as_deref());
        // Driven by *which* decision was retired, not by how many. A count
        // said only that something went, so a replacement that retired a
        // queued decision while the operator was reading one raised by the
        // process that is still current took the wrong modal down and told
        // the reducer a turn had stopped waiting when it had not. Retired,
        // not answered: nothing was decided, here or anywhere, so the
        // transcript records no decision — a `PromptAnswered` here wrote a
        // denial the operator never gave for a request nobody can answer any
        // more.
        let outcome = crate::api::requests::Reconciliation { opened: Vec::new(), retired };
        if self.close_retired_prompt(on_screen.as_deref(), &outcome) {
            // Whatever the current process is still waiting for takes the
            // screen next rather than being left queued behind a modal that
            // has gone.
            self.open_current_prompt();
        }
        self.notice(
            "The engine was replaced; re-reading its state and conversation.",
            NoticeLevel::Warning,
        );
        self.needs_resync = true;
        self.needs_rehydrate = true;
    }

    /// Applies one accepted frame: state metadata, or ordinary content.
    ///
    /// A `stateEvents` frame carries no conversation content, so handling it
    /// alongside the legacy content stream cannot double anything. The one
    /// place the two describe the same moment — a turn ending — is exactly
    /// where they say different things: the legacy frame reports the last
    /// output, the state frame reports the fence and the released slot.
    pub(in crate::app) fn dispatch_frame(&mut self, frame: Frame) {
        if let Some(state) = api::view::parse_state_frame(&frame.method, &frame.params) {
            self.apply_state_frame(state);
            return;
        }
        let event = Event::parse(&frame.method, Some(&frame.params));
        // Metadata first, and unconditionally: a task's outcome is a side
        // table the browsers read, not conversation content, so it must
        // survive even when the content itself is already on screen.
        self.record_task_outcome(&event);
        if self.view.content_is_reflected(frame.seq) {
            match event {
                Event::AssistantText { .. }
                | Event::AssistantTextComplete
                | Event::Thinking { .. }
                | Event::ThinkingComplete { .. }
                | Event::ToolCall { .. }
                | Event::ToolProgress { .. }
                | Event::ToolResult { .. }
                | Event::TurnComplete { .. }
                | Event::ResponseRewritten { .. } => return,
                Event::SteeringDelivered { message_ids } => {
                    self.apply(UiEvent::SteeringDeliveryReflected { message_ids });
                    return;
                }
                // History does not contain usage, errors, limits or hook/task
                // notices. Their event cursor is not a content-coverage claim.
                _ => {}
            }
        }
        self.apply(UiEvent::Engine(event));
    }

    fn apply_state_frame(&mut self, state: StateFrame) {
        match state {
            StateFrame::Activity { phase, .. } => self.apply(UiEvent::CoreActivity(phase)),
            StateFrame::Lifecycle { lifecycle, .. } => {
                self.apply(UiEvent::CoreLifecycle(lifecycle))
            }
            StateFrame::TurnEnded { history_epoch, history_length, .. } => {
                // The fence only. The turn's own completion is reported by
                // `event/turnComplete`, which the reducer already handles;
                // acting on both would end the turn twice.
                //
                // The epoch is not merely recorded: a turn that ended on a
                // *new* epoch replaced the conversation (a compaction runs at
                // exactly this boundary), and a client that only noted the
                // number kept showing the history that no longer exists.
                if self.view.on_history_reset(history_epoch, history_length) {
                    self.needs_rehydrate = true;
                    self.needs_resync = true;
                }
            }
            StateFrame::ConfigChanged { .. } => {
                // The authoritative values live in the snapshot, which is
                // cheap to re-read and cannot disagree with itself. Acting on
                // half a config here is how `active` and `next` drift apart.
                self.forget_described_config();
                self.needs_resync = true;
            }
            StateFrame::Steering(_) => self.needs_resync = true,
            StateFrame::SessionChanged { session_id, history_epoch, history_length, .. } => {
                // fork / rewind / compact / resume: the conversation itself
                // was replaced, so the transcript is rebuilt rather than
                // patched.
                self.state.session_id = Some(session_id);
                self.header_id_selected = false;
                self.selection.clear();
                // A different conversation can be running a different
                // configuration; the catalogue describing the old one is not
                // evidence about this one.
                self.forget_described_config();
                if self.view.on_history_reset(history_epoch, history_length) {
                    self.needs_rehydrate = true;
                }
                self.needs_resync = true;
            }
            StateFrame::RequestPending { request, requests, at } => {
                let instance = self.view.engine_instance_id().map(str::to_string);
                let on_screen = self.pending.current().map(|entry| entry.key());
                let announced = self.pending.on_discovered(&request, instance.clone(), at);
                // The list is announced with the frame and can carry
                // decisions this client has never seen — a background
                // subagent's, one raised while it was reconnecting. Throwing
                // the reconciliation's own answer away meant those stayed
                // outstanding with nothing on screen for them.
                //
                // It is also the engine's state as of this frame's own `seq`,
                // so it can retire what the engine's ordering places before
                // it — and only that. An announcement is broadcast on the
                // engine's schedule rather than built for a read this client
                // issued, so no local receipt can be placed relative to it.
                let outcome = self.pending.reconcile(
                    &requests,
                    instance,
                    at.map_or(
                        crate::api::requests::Authority::Unsequenced,
                        crate::api::requests::Authority::Announced,
                    ),
                );
                let closed = self.close_retired_prompt(on_screen.as_deref(), &outcome);
                if announced || !outcome.opened.is_empty() || closed {
                    self.open_current_prompt();
                }
            }
            StateFrame::RequestResolved { request_id, kind, outcome, .. } => {
                // Only the decision actually on screen may be taken down, and
                // only with the verdict the engine published. Dispatching a
                // fabricated `allowed: false` here made every approval given
                // on another client — or over the API — appear as a refusal,
                // complete with a denial written into the transcript.
                let on_screen = self.pending.position(&request_id) == Some(0);
                if self.pending.on_resolved(&request_id) && on_screen {
                    self.retire_prompt_surface();
                    self.apply(UiEvent::PromptResolved(ExternalResolution::Outcome {
                        kind,
                        outcome,
                    }));
                    self.open_current_prompt();
                }
            }
            StateFrame::EventsDropped { from_cursor, to_cursor } => {
                self.notice(
                    format!(
                        "The engine dropped events {from_cursor}-{to_cursor}; \
                         re-reading its state."
                    ),
                    NoticeLevel::Warning,
                );
                self.needs_resync = true;
                self.needs_rehydrate = true;
            }
        }
    }

    /// Reads the engine's authoritative state and replays whatever the
    /// snapshot did not already cover.
    ///
    /// This is the only place a snapshot is fetched: on connect, on an actual
    /// gap and after an engine-owned reset. Never per token — the streaming
    /// path is the event stream, and a round-trip per delta would make the
    /// UI's latency a function of engine load.
    ///
    /// `now` is the instant the retry schedule is measured against, supplied
    /// by the caller so a test can drive the backoff deterministically rather
    /// than sleeping through it.
    pub(in crate::app) async fn resync_at(&mut self, now: std::time::Instant) {
        // Taken immediately before the read is enqueued, and carried with
        // *that* read rather than re-read when the answer lands: see
        // `resync_paired_at`.
        let received_before = self.pending.raw_watermark();
        self.resync_paired_at(received_before, now).await;
    }

    /// [`resync_at`](Self::resync_at) with the read's watermark supplied by
    /// the caller.
    ///
    /// Split out because the pairing is the load-bearing part and is
    /// otherwise invisible: `received_before` must be the value taken before
    /// *this* read went out, never the registry's latest. Re-reading it here
    /// would fold in every raw request that arrived while the engine was
    /// building the answer, and the snapshot would then retire decisions
    /// raised after it was built — the exact failure the watermark exists to
    /// prevent. A caller (and a test) that supplies it explicitly can prove
    /// the pairing rather than assume it.
    pub(in crate::app) async fn resync_paired_at(
        &mut self,
        received_before: u64,
        now: std::time::Instant,
    ) {
        self.needs_resync = false;
        self.view.begin_resync();
        let snapshot = match self.bounded(api::get_state(&self.connection)).await {
            Ok(snapshot) => snapshot,
            Err(error) => {
                // Release the gate before anything else. While it is held
                // every inbound frame is buffered, so a read that failed and
                // left it set froze the screen mid-turn and grew the buffer
                // without end. The cursor is still valid, so whatever was
                // held and continues from it is applied now; a hole in the
                // held run is reported rather than spliced over.
                let replay = self.view.abort_resync();
                for frame in replay.frames {
                    self.dispatch_frame(frame);
                }
                if let Some((from, to)) = replay.gap {
                    self.on_events_lost(&format!("The engine's events {from}-{to} never arrived"));
                }
                if self.resync_recovery.on_failure(now) {
                    self.notice(
                        format!(
                            "Could not read the engine's state (attempt {}): {error}{}",
                            self.resync_recovery.failures(),
                            quiet_from_now_on(self.resync_recovery.failures()),
                        ),
                        NoticeLevel::Warning,
                    );
                }
                return;
            }
        };
        self.resync_recovery.on_success();
        let replay = self.view.apply_snapshot(&snapshot);

        let instance = Some(snapshot.engine_instance_id.clone());
        // Which decision the operator is actually looking at, captured before
        // the registry changes underneath it.
        let on_screen = self.pending.current().map(|entry| entry.key());
        // The engine's own answer to "what are you still waiting for?", at
        // the point in its event stream the snapshot was taken. A handle only
        // means anything to the process that minted it; and a request this
        // list does not name is gone *if the list was late enough to know
        // about it* — by the engine's ordering where this client has it, and
        // otherwise by the raw frame having been in hand before this very
        // read was issued.
        let outcome = self.pending.reconcile(
            &snapshot.requests,
            instance,
            crate::api::requests::Authority::Snapshot {
                cursor: snapshot.cursor,
                received_before,
            },
        );
        self.apply(UiEvent::Snapshot(Box::new(snapshot)));
        // The screen as well as the registry. Retiring the entry alone left
        // the modal up over a decision nothing was waiting for: the operator
        // answered it, this client sent a bare numeric id into a registry
        // that had reused it, and the transcript recorded a decision that
        // reached no tool call.
        let closed = self.close_retired_prompt(on_screen.as_deref(), &outcome);
        for frame in replay.frames {
            self.dispatch_frame(frame);
        }
        if let Some((from, to)) = replay.gap {
            // The buffer itself had a hole: the events between these two
            // never arrived and the snapshot did not cover them either.
            // Nothing after the hole was applied, so what is on screen is the
            // conversation up to the hole — which is exactly why it has to be
            // re-read rather than continued.
            self.on_events_lost(&format!("The engine's events {from}-{to} never arrived"));
        }
        if !outcome.opened.is_empty() || closed {
            self.open_current_prompt();
        }
    }

    /// Takes down the decision on screen when a reconciliation retired it.
    ///
    /// Retired is not answered, and must never read as though it were: the
    /// modal comes down, the turn stops waiting, and the transcript records
    /// **no** decision — nobody made one, here or anywhere. Saying so out
    /// loud matters too, because a modal that simply vanishes looks like a
    /// dropped keystroke.
    ///
    /// Returns whether the screen changed, so the caller knows to show
    /// whatever is next in the queue.
    fn close_retired_prompt(
        &mut self,
        on_screen: Option<&str>,
        outcome: &crate::api::requests::Reconciliation,
    ) -> bool {
        // Only what is actually showing. `pending.current()` names the next
        // decision in the queue whether or not a modal was ever opened for
        // it, and resolving one that was never on screen would take the turn
        // out of its wait on the strength of nothing.
        if self.state.prompt.is_none() {
            return false;
        }
        if !on_screen.is_some_and(|key| outcome.retired_key(key)) {
            return false;
        }
        self.retire_prompt_surface();
        self.apply(UiEvent::PromptResolved(ExternalResolution::Retired));
        self.notice(
            "The engine is no longer waiting for that decision, so it was taken down. \
             Nothing was sent and nothing was decided here.",
            NoticeLevel::Warning,
        );
        true
    }

    /// Rebuilds the visible conversation from the engine's rich history.
    ///
    /// Called after connect, resume, fork and rewind. Without it a resumed
    /// session showed a "restored N messages" notice above an empty screen —
    /// technically honest, and useless.
    ///
    /// `now` is the instant the retry schedule is measured against, supplied
    /// by the caller so a test can drive the backoff deterministically.
    pub(in crate::app) async fn rehydrate_at(&mut self, now: std::time::Instant) {
        self.needs_rehydrate = false;
        let window = self.history_window();
        let history = match self.bounded(api::get_history(&self.connection, &window)).await {
            Ok(history) => history,
            Err(error) => {
                // The need survives the failure. Dropping it left the
                // transcript permanently short of the conversation the engine
                // holds, with nothing that would ever ask again — and
                // retrying it on the next loop iteration would spin against a
                // failing engine while the user is trying to type.
                self.needs_rehydrate = true;
                if error.fence_moved() {
                    // The engine refused the *fence*, not the read: the
                    // conversation, its length or the process itself moved
                    // under us. Asking again with the same numbers cannot
                    // ever succeed, so the snapshot is re-read first.
                    self.needs_resync = true;
                }
                if self.rehydrate_recovery.on_failure(now) {
                    self.notice(
                        format!(
                            "Could not read the conversation (attempt {}): {error}{}",
                            self.rehydrate_recovery.failures(),
                            quiet_from_now_on(self.rehydrate_recovery.failures()),
                        ),
                        NoticeLevel::Warning,
                    );
                }
                return;
            }
        };

        // The read is fenced on the epoch we asked for, so a *different*
        // epoch coming back means the conversation was replaced while the
        // request was in flight. Rendering it against the fence we hold would
        // show one conversation and count another.
        if let (Some(asked), Some(served)) = (window.history_epoch, history.history_epoch) {
            if asked != served {
                self.view.on_history_reset(served, history.history_length);
                self.needs_rehydrate = true;
                self.needs_resync = true;
                // Not a failure of the engine's, but it is still an attempt
                // that produced nothing: without the backoff, a session being
                // compacted repeatedly would re-read on every iteration.
                self.rehydrate_recovery.on_failure(now);
                return;
            }
        }

        // The window was computed from a length this client already held, and
        // the answer reports the real one. A window that did not reach the end
        // was aimed at the wrong place: re-aim it rather than showing an
        // interior page as though it were the end of the conversation.
        if history.truncated && history.total_known > 0 {
            self.view.on_history_reset(
                history.history_epoch.unwrap_or_else(|| self.view.history_epoch()),
                history.history_length,
            );
            self.needs_rehydrate = true;
            self.rehydrate_recovery.on_failure(now);
            return;
        }
        self.rehydrate_recovery.on_success();
        // The read is exact at its own cursor, and the events up to it are
        // already inside what it returned. Their *content* must not be
        // applied a second time when the loop drains the inbound queue; their
        // metadata still must.
        if let Some(cursor) = history.cursor {
            self.view.note_history_read(cursor);
        }

        let hydrated = api::history::hydrate(
            &history.entries,
            history.live_entries.as_deref().unwrap_or(&[]),
            history.live_truncated.unwrap_or(false),
        );
        let mut notices = hydrated.notices;
        let omitted = history.total_known - history.entries.len() as i64;
        if omitted > 0 {
            // True by construction now: the window is the tail, so what is
            // missing is genuinely the earlier part of the conversation.
            notices.push(format!(
                "Showing the most recent {} of {} messages; the earlier {omitted} are not \
                 loaded.",
                history.entries.len(),
                history.total_known
            ));
        }
        if hydrated.blocks.is_empty() && !self.state.has_conversation() {
            // Nothing on either side to replace. The reducer would keep this
            // client's own blocks anyway, so this is only about not
            // announcing a rebuild that rebuilds nothing.
            for notice in notices {
                self.notice(notice, NoticeLevel::Warning);
            }
            return;
        }
        // An engine that reports an empty conversation is still the authority
        // on what the conversation is: after a rewind to the start, or a
        // session that was replaced, returning early here left the previous
        // transcript on screen as if it were still real. The reducer replaces
        // the conversation *around* the banner, the launch notices and any
        // slash-command output, which are this client's own.
        //
        // A selection is a pair of row positions into the transcript that is
        // about to be rebuilt, so it is dropped here rather than only on
        // `sessionChanged`: a gap, a compaction and a reconnect move the rows
        // just as thoroughly, and a selection kept across one copies whatever
        // text now sits at those coordinates.
        self.selection.clear();
        self.apply(UiEvent::Rehydrated { blocks: hydrated.blocks, notices });
    }

    /// The window of the conversation this client asks for.
    ///
    /// The **newest** page, not the oldest: `session/getHistory` defaults
    /// `sinceIndex` to `0`, so a read that leaves it out is served the first
    /// hundred messages of the conversation — which a resumed session then
    /// showed while announcing them as the most recent.
    ///
    /// Every fence the engine offers is sent. They are what makes a window
    /// computed from a length this client already held safe: if the
    /// conversation, its length or the process moved in between, the read is
    /// refused with a typed error rather than answered with a page from a
    /// different conversation.
    fn history_window(&self) -> api::HistoryWindow {
        let fenced = self.view.is_fenced();
        let limit = match self.view.max_history_page() {
            Some(max) => HISTORY_TAIL.min(max).max(1),
            // Not advertised yet: ask for the client's own page size and let
            // the engine clamp it. The answer reports the real total, which
            // re-aims the window if this one missed the end.
            None => HISTORY_TAIL,
        };
        let length = self.view.history_length();
        api::HistoryWindow {
            engine_instance_id: fenced
                .then(|| self.view.engine_instance_id().map(str::to_string))
                .flatten(),
            history_epoch: fenced.then(|| self.view.history_epoch()),
            expected_history_length: fenced.then_some(length),
            since_index: Some(api::HistoryWindow::tail_start(length, limit)),
            limit: Some(limit),
        }
    }

    /// Runs whatever the fence has asked for. Called once per loop iteration.
    pub(in crate::app) async fn settle_with_engine(&mut self) {
        self.settle_with_engine_at(std::time::Instant::now()).await
    }

    /// [`Self::settle_with_engine`] against a caller-supplied instant.
    ///
    /// Each read is gated by its own retry schedule, so a broken engine costs
    /// a bounded number of requests rather than one per loop iteration — and
    /// the loop keeps servicing the keyboard while the engine is unwell.
    pub(in crate::app) async fn settle_with_engine_at(&mut self, now: std::time::Instant) {
        // A deliberate disconnection is not an outage to recover from: the
        // engine is gone because the user asked for it to be, and retrying a
        // snapshot against a closed connection would be a poll that can only
        // ever fail.
        if !self.engine_connected {
            return;
        }
        if (self.needs_resync || self.view.needs_snapshot()) && self.resync_recovery.ready(now) {
            self.resync_at(now).await;
        }
        if self.needs_rehydrate && self.rehydrate_recovery.ready(now) {
            self.rehydrate_at(now).await;
        }
    }

    /// Shows the oldest outstanding decision, if one is not already on screen.
    pub(in crate::app) fn open_current_prompt(&mut self) {
        if self.state.prompt.is_some() {
            return;
        }
        let Some(prompt) = self.pending.current().map(|entry| entry.prompt.clone()) else {
            return;
        };
        self.surfaces
            .push(Box::new(crate::surface::prompt::PromptSurface::new(prompt.clone())));
        self.apply(UiEvent::PromptRequested(prompt));
    }

    /// Delivers the answer to the decision currently on screen.
    ///
    /// Whichever way this client learned about the request, exactly one
    /// resolution is sent: the raw round-trip when we own it (which is the
    /// engine's own exactly-once path and needs no second call), otherwise
    /// the out-of-band RPC. The responder is never simply dropped while a
    /// resolution is in flight — dropping one *answers* it, with a decline.
    pub(in crate::app) async fn deliver_answer(
        &mut self,
        prompt: &crate::state::PendingPrompt,
        allowed: bool,
        answer: Option<&str>,
    ) {
        match self.pending.take_current(self.view.engine_instance_id()) {
            Delivery::Raw(responder) => {
                match super::engine::prompt_response(prompt, allowed, answer) {
                    Ok(value) => responder.respond(value),
                    Err(error) => responder.fail(error.code, error.message),
                }
            }
            Delivery::Rpc { request_id } => {
                let outcome = super::engine::rpc_outcome(prompt, allowed, answer);
                let result = match outcome {
                    Some(outcome) => {
                        self.bounded(api::resolve_request(
                            &self.connection,
                            &request_id,
                            outcome,
                        ))
                        .await
                    }
                    // A declined question is not an empty answer: the
                    // fail-closed default is what "no answer" means, and the
                    // engine records it as declined rather than malformed.
                    None => {
                        self.bounded(api::cancel_request(
                            &self.connection,
                            &request_id,
                            "declined",
                        ))
                        .await
                    }
                };
                match result {
                    Ok(_) => {}
                    Err(error @ ReadFailure::Silent(_)) => {
                        // The decision was sent and never acknowledged. That
                        // is an *unknown* outcome: re-sending could answer a
                        // request the engine already resolved, and calling it
                        // a refusal would invent a decision the operator
                        // never made. The engine's own list settles it.
                        self.notice(
                            format!(
                                "The engine did not acknowledge the answer ({error}); this \
                                 client does not know whether it was applied. Re-reading \
                                 what the engine is still waiting for."
                            ),
                            NoticeLevel::Warning,
                        );
                        self.needs_resync = true;
                    }
                    Err(error) => self.notice(
                        format!("The engine did not accept the answer: {error}"),
                        NoticeLevel::Warning,
                    ),
                }
            }
            Delivery::Stale => self.notice(
                "That request was raised by an engine process that is no longer \
                 connected, so the answer was not sent anywhere.",
                NoticeLevel::Warning,
            ),
            Delivery::None => {}
        }
    }

    /// Closes this session out over the connection, before anything is
    /// dropped.
    ///
    /// Two things happen here and the order is the point.
    ///
    /// First, every decision raised by the engine we are still talking to is
    /// declined explicitly: an error reply is the wire signal the engine reads
    /// as "the operator refused", so a tool call blocked on a human who has
    /// closed the terminal fails closed now instead of waiting for the
    /// engine's own end-of-connection sweep. A question aborts as `noAnswer` —
    /// no answer is invented for it. Anything this client cannot place on the
    /// current process is discarded rather than answered, because a numeric
    /// request id from a replaced engine addresses whatever *its replacement*
    /// now has under that number. Leaving either case to `Responder`'s `Drop`
    /// would send a cancellation for both, indiscriminately, in field-
    /// declaration order.
    ///
    /// Then the engine is asked to stop, so it can close its MCP servers and
    /// flush the session rather than being killed after a grace period.
    /// Bounded, because a client that hangs on exit is worse than one that
    /// gives up on a graceful stop.
    pub(crate) async fn close_out(&mut self) {
        // The authentication flow first: a preparation has written nothing and
        // is dropped, but a commit in flight is awaited, because a transaction
        // abandoned half-written would leave a profile this process then
        // reported as unchanged.
        self.close_auth_out().await;
        if !self.engine_connected {
            // Already stopped and awaited by whatever disconnected. Asking a
            // closed connection to shut down would only wait out a timeout on
            // the way to the exit.
            return;
        }
        let instance = self.view.engine_instance_id().map(str::to_string);
        self.pending.decline_live(instance.as_deref());
        let _ = tokio::time::timeout(
            super::SHUTDOWN_GRACE,
            self.connection.request(coda_proto::messages::method::SHUTDOWN, Some(serde_json::json!({}))),
        )
        .await;
    }

    /// Stops every engine process this application owns, and waits for each.
    ///
    /// Both of them: the one in service, and one a restart staged that the
    /// loop never swapped in. An event loop that exits — with an error, or on
    /// a quit typed at exactly the wrong moment — between "the replacement is
    /// up" and "the replacement is ours" still started that child, and
    /// leaving it to `Engine`'s `Drop` *kills* it: no stdin close, no MCP
    /// servers shut down, no session flushed. Each is asked to stop over its
    /// own connection and awaited, which is also what fails the requests
    /// still outstanding on that exact instance rather than on whichever
    /// connection happens to be current.
    ///
    /// Called after [`close_out`](Self::close_out), never before: the session
    /// is answered and asked to stop over the connection first.
    pub(crate) async fn stop_owned_engines(&mut self, grace: std::time::Duration) {
        let staged = self.restarted.take().map(|(engine, _)| engine);
        let owned = self.owned_engine.take();
        for engine in [staged, owned].into_iter().flatten() {
            let _ = engine.shutdown(grace).await;
        }
    }

    /// Whether this client may maintain the engine-adjacent files itself.
    ///
    /// Returns `false` — and says why — **before** any filesystem write is
    /// attempted, so an API-only session never edits local files that the
    /// engine it is talking to will never read. The gate is this client's own
    /// knowledge of how it launched; no engine claim can open it.
    pub(in crate::app) fn allow_local_maintenance(&mut self, what: &str) -> bool {
        if self.access_mode.allows_local_maintenance() {
            return true;
        }
        self.notice(crate::local::unsupported_remotely(what), NoticeLevel::Warning);
        false
    }

    /// Notes an inbound message's task outcome, which the browsers surface.
    fn record_task_outcome(&mut self, event: &Event) {
        if let Event::TaskCompleted { task_id, status, description, report } = event {
            self.task_outcomes.insert(
                task_id.clone(),
                crate::browsers::TaskOutcome {
                    status: status.clone(),
                    description: description.clone(),
                    report: report.clone(),
                },
            );
        }
    }

    /// One inbound message from the engine.
    pub(in crate::app) fn on_inbound(&mut self, message: Inbound) {
        match message {
            Inbound::Notification { method, params } => {
                for frame in self.accept(method, params) {
                    self.dispatch_frame(frame);
                }
            }
            Inbound::Request { method, params, responder } => {
                self.on_server_request(&method, params, responder)
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Construction
// ---------------------------------------------------------------------------
//
// The constructors live here rather than in `app/mod.rs` because starting up
// *is* the bootstrap contract: spawn the core, discover sessions read-only,
// `initialize`, fork if asked, then seed the event fence from the handshake.
// `app/mod.rs` stays the event loop.

impl App {
    /// How this client reaches its engine, and therefore whether the files
    /// the engine reads are files this client owns.
    ///
    /// Decided at construction from how this process launched, never from
    /// anything the engine says, and readable so a caller (or a test) can
    /// assert which contract a session is actually running under.
    pub fn access_mode(&self) -> crate::local::AccessMode {
        self.access_mode
    }

    /// The state the UI draws from.
    pub fn state(&self) -> &UiState {
        &self.state
    }

    /// Connects to an engine and completes the handshake.
    ///
    /// `access_mode` is the caller's own statement of how it launched this
    /// engine. There is no default: the difference between "this process
    /// started its own core in this workspace" and "the user pointed us at
    /// somebody else's binary or proxy" is exactly what decides whether local
    /// files may be written, and a constructor that guessed it would make the
    /// safe answer depend on which constructor a call site happened to pick.
    pub async fn connect(
        command: EngineCommand,
        theme: Theme,
        access_mode: crate::local::AccessMode,
    ) -> Result<(Self, Engine, mpsc::UnboundedReceiver<Inbound>)> {
        Self::connect_to_session(command, theme, None, access_mode).await
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
        access_mode: crate::local::AccessMode,
    ) -> Result<(Self, Engine, mpsc::UnboundedReceiver<Inbound>)> {
        let intent = match session_id {
            Some(id) => coda_boot::SessionIntent::Resume(id),
            None => coda_boot::SessionIntent::New,
        };
        Self::boot(command, theme, &intent, access_mode).await
    }

    /// Starts a core and becomes its client, following the public bootstrap
    /// order: spawn, read-only discovery, `initialize`, then fork if asked.
    ///
    /// No transcript file is read and no `.coda/sessions` path is built: the
    /// engine is the only thing that knows where its sessions live, which is
    /// what makes this front-end an ordinary API client rather than a
    /// privileged one.
    pub async fn boot(
        command: EngineCommand,
        theme: Theme,
        intent: &coda_boot::SessionIntent,
        access_mode: crate::local::AccessMode,
    ) -> Result<(Self, Engine, mpsc::UnboundedReceiver<Inbound>)> {
        let booted = crate::api::boot::boot(command.clone(), intent, "coda-tui")
            .await
            .map_err(anyhow::Error::new)?;
        let crate::api::boot::Booted {
            engine,
            inbound,
            connection,
            initialize: initialized,
            session_id,
            forked_from,
            notices,
        } = booted;

        if let Some(ctx) = coda_diagnostics::current() {
            crate::diagnostics::record_engine_log_path(
                &ctx.with_session(session_id.clone()),
                initialized.telemetry_log_path.as_deref(),
            );
        }

        let mut app =
            Self::attach(connection, &initialized, session_id, theme, access_mode, command);

        if let Some(source) = forked_from {
            let escaped = coda_render::text::sanitize(&source);
            app.notice(
                format!("Forked from session {escaped}; the original is untouched."),
                NoticeLevel::Info,
            );
        }
        for notice in notices {
            app.notice(notice, NoticeLevel::Info);
        }

        Ok((app, engine, inbound))
    }

    /// Builds the application over a connection that is already established
    /// and handshaken.
    ///
    /// Separate from [`Self::boot`] because "who started the engine" and "how
    /// this client talks to it" are different questions: an embedder that
    /// already holds a connection — an external orchestrator, a proxy — is a
    /// legitimate client, and it must state its own `access_mode` exactly as
    /// a spawning caller does.
    pub fn attach(
        connection: coda_client::Connection,
        initialized: &coda_proto::messages::InitializeResult,
        session_id: String,
        theme: Theme,
        access_mode: crate::local::AccessMode,
        command: EngineCommand,
    ) -> Self {
        let mut view = crate::api::ServeView::new();
        view.on_initialize(initialized);

        let mut state = UiState::new();
        state.apply(UiEvent::Connected { session_id });

        let project_root = command
            .working_dir
            .clone()
            .unwrap_or_else(|| std::env::current_dir().unwrap_or_default());

        Self {
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
            view,
            pending: Default::default(),
            needs_resync: true,
            needs_rehydrate: true,
            resync_recovery: Default::default(),
            rehydrate_recovery: Default::default(),
            access_mode,
            metadata_timeout: DEFAULT_METADATA_TIMEOUT,
            config_catalog: None,
            shutdown_grace: super::SHUTDOWN_GRACE,
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
            session_effort: None,
            engine_log_path: initialized.telemetry_log_path.clone(),
            header_id_rect: None,
            header_id_selected: false,
            owned_engine: None,
            engine_connected: true,
            auth: super::auth::AuthState::new(),
            auth_port: crate::local::auth::AuthPort::profile(),
        }
    }
}


// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------
//
// These drive a real `App` over a real `Connection`, against an in-process
// engine that answers the documented methods. Nothing about the contract seam
// is re-implemented for the test: the fence, the resync and the hydration
// under test are the ones the terminal runs.

#[cfg(test)]
pub(in crate::app) mod tests {
    use super::*;
    use crate::state::PendingPrompt;
    use crate::transcript::Block;
    use coda_client::Responder;
    use coda_proto::messages::{InitializeResult, CONTRACT_VERSION};
    use coda_proto::{encode_frame, FrameDecoder, RequestId};
    use serde_json::{json, Value};
    use std::sync::{Arc, Mutex};

    /// Everything a test needs to keep alive while it drives an `App`.
    pub(in crate::app) struct Harness {
        pub(in crate::app) app: App,
        /// Methods the engine was actually asked for, in order.
        pub(in crate::app) calls: Arc<Mutex<Vec<String>>>,
        /// Every frame the client wrote, in order — responses included, so a
        /// test can assert what was answered *and* in which order relative to
        /// the requests around it.
        frames: Arc<Mutex<Vec<Value>>>,
        /// Frames the fake engine pushes at the client: server-initiated
        /// requests and notifications.
        push: mpsc::UnboundedSender<Value>,
        inbound: mpsc::UnboundedReceiver<Inbound>,
        _tasks: coda_client::ConnectionTasks,
    }

    impl Harness {
        /// Sends one frame from the engine to the client.
        pub(in crate::app) fn push(&self, frame: Value) {
            self.push.send(frame).expect("the fake engine is still running");
        }

        /// The next inbound message, exactly as the event loop receives it.
        pub(in crate::app) async fn next_inbound(&mut self) -> Inbound {
            tokio::time::timeout(std::time::Duration::from_secs(5), self.inbound.recv())
                .await
                .expect("the client received the engine's frame")
                .expect("the connection is still open")
        }

        /// Every frame the client has written so far.
        pub(in crate::app) fn frames(&self) -> Vec<Value> {
            self.frames.lock().expect("frames poisoned").clone()
        }

        /// Lets the connection's writer task drain before the frames are read.
        pub(in crate::app) async fn settle_wire(&self) {
            tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        }
    }

    fn initialize(instance: &str, cursor: i64) -> InitializeResult {
        InitializeResult {
            protocol_version: "1".into(),
            session_id: "s1".into(),
            server_info: "in-process".into(),
            telemetry_log_path: None,
            contract_version: Some(CONTRACT_VERSION.into()),
            engine_instance_id: Some(instance.into()),
            event_cursor: Some(cursor),
            capabilities: None,
        }
    }

    /// Builds an `App` wired to an in-process engine that answers each
    /// request with `answer(method, params)`.
    pub(in crate::app) fn app_with(
        access_mode: crate::local::AccessMode,
        answer: impl Fn(&str, &Value) -> Value + Send + 'static,
    ) -> Harness {
        app_answering(access_mode, move |method, params| Ok(answer(method, params)))
    }

    /// Builds an `App` whose engine may also *refuse* a request.
    ///
    /// A refusal is a real JSON-RPC error frame, not a sentinel result: the
    /// recovery under test is the one that runs when `session/getState`
    /// genuinely fails, and a fake failure would only prove the fake works.
    pub(in crate::app) fn app_answering(
        access_mode: crate::local::AccessMode,
        answer: impl Fn(&str, &Value) -> Result<Value, (i64, String)> + Send + 'static,
    ) -> Harness {
        app_pump(access_mode, move |method, params| Some(answer(method, params)))
    }

    /// Builds an `App` over a fake engine that may answer, refuse, or stay
    /// silent (`None`) — a live connection with a peer that never replies.
    fn app_pump(
        access_mode: crate::local::AccessMode,
        answer: impl Fn(&str, &Value) -> Option<Result<Value, (i64, String)>> + Send + 'static,
    ) -> Harness {
        let (client_side, server_side) = tokio::io::duplex(256 * 1024);
        let (client_read, client_write) = tokio::io::split(client_side);
        let (connection, inbound, tasks) = coda_client::connect(client_read, client_write);

        let calls = Arc::new(Mutex::new(Vec::new()));
        let recorder = Arc::clone(&calls);
        let frames = Arc::new(Mutex::new(Vec::new()));
        let frame_recorder = Arc::clone(&frames);
        let (push, mut push_rx) = mpsc::unbounded_channel::<Value>();
        tokio::spawn(async move {
            use tokio::io::{AsyncReadExt, AsyncWriteExt};
            let (mut read, mut write) = tokio::io::split(server_side);
            let mut decoder = FrameDecoder::new();
            let mut buffer = [0u8; 8192];
            loop {
                let count = tokio::select! {
                    // The engine speaks first sometimes: a permission
                    // request, a notification. Without this the fake could
                    // only ever answer, and the teardown path — which is all
                    // about answering the engine's *own* requests — could not
                    // be driven at all.
                    outbound = push_rx.recv() => {
                        let Some(frame) = outbound else { return };
                        let bytes = serde_json::to_vec(&frame).expect("frame");
                        if write.write_all(&encode_frame(&bytes)).await.is_err() {
                            return;
                        }
                        continue;
                    }
                    count = read.read(&mut buffer) => match count {
                        Ok(0) | Err(_) => return,
                        Ok(count) => count,
                    },
                };
                decoder.feed(&buffer[..count]);
                while let Ok(Some(frame)) = decoder.next_frame() {
                    let Ok(request) = serde_json::from_slice::<Value>(&frame) else { continue };
                    frame_recorder.lock().expect("frames poisoned").push(request.clone());
                    // No method: this is the client answering something the
                    // engine asked. Recorded, never answered back.
                    let Some(method) = request["method"].as_str() else { continue };
                    recorder.lock().expect("calls poisoned").push(method.to_string());
                    let Some(outcome) = answer(method, &request["params"]) else {
                        // Received and deliberately never answered.
                        continue;
                    };
                    let response = match outcome {
                        Ok(result) => json!({ "jsonrpc": "2.0", "id": request["id"], "result": result }),
                        Err((code, message)) => json!({
                            "jsonrpc": "2.0",
                            "id": request["id"],
                            "error": { "code": code, "message": message },
                        }),
                    };
                    let bytes = serde_json::to_vec(&response).expect("response");
                    if write.write_all(&encode_frame(&bytes)).await.is_err() {
                        return;
                    }
                }
            }
        });

        let app = App::attach(
            connection,
            &initialize("e1", 0),
            "s1".into(),
            Theme::default(),
            access_mode,
            EngineCommand::new("engine-that-is-never-spawned"),
        );
        Harness { app, calls, frames, push, inbound, _tasks: tasks }
    }

    fn text_frame(instance: &str, seq: i64) -> (String, Option<Value>) {
        (
            "event/assistantText".to_string(),
            Some(json!({ "delta": "x", "seq": seq, "engineInstanceId": instance })),
        )
    }

    pub(in crate::app) fn notices(app: &App) -> Vec<String> {
        app.state
            .transcript
            .blocks()
            .iter()
            .filter_map(|block| match block {
                Block::Notice { text, .. } => Some(text.clone()),
                _ => None,
            })
            .collect()
    }

    /// Everything the client printed as ordinary command output.
    pub(in crate::app) fn outputs(app: &App) -> Vec<String> {
        app.state
            .transcript
            .blocks()
            .iter()
            .filter_map(|block| match block {
                Block::CommandOutput { text } => Some(text.clone()),
                _ => None,
            })
            .collect()
    }

    fn responder(id: i64) -> (Responder, mpsc::UnboundedReceiver<Vec<u8>>) {
        let (tx, rx) = mpsc::unbounded_channel();
        (Responder::new(RequestId::Number(id), tx), rx)
    }

    fn emitted(rx: &mut mpsc::UnboundedReceiver<Vec<u8>>) -> Option<Value> {
        let frame = rx.try_recv().ok()?;
        let mut decoder = FrameDecoder::new();
        decoder.feed(&frame);
        let bytes = decoder.next_frame().ok()??;
        serde_json::from_slice(&bytes).ok()
    }

    #[tokio::test]
    async fn a_gap_in_the_event_stream_rebuilds_the_conversation_and_says_so() {
        // A snapshot carries no conversation content, so re-reading state
        // alone refreshes the metadata and leaves the transcript quietly
        // short of whatever the missing events carried.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        app.needs_rehydrate = false;
        app.needs_resync = false;

        let (method, params) = text_frame("e1", 1);
        assert_eq!(app.accept(method, params).len(), 1, "an in-order frame applies");

        // 2..=8 never arrived.
        let (method, params) = text_frame("e1", 9);
        assert!(app.accept(method, params).is_empty(), "the frame after a gap is held");

        assert!(app.needs_rehydrate, "a gap must rebuild the conversation, not just the metadata");
        assert!(app.needs_resync || app.view.needs_snapshot());
        let notices = notices(app);
        assert!(
            notices.iter().any(|n| n.contains("did not arrive")),
            "losing events must be visible, not silent: {notices:?}"
        );
    }

    #[tokio::test]
    async fn a_failed_state_read_releases_the_gate_so_streaming_continues() {
        // The gate is only justified while a snapshot is actually coming. A
        // read that failed and left it set buffered every later frame for the
        // rest of the session: the screen froze mid-turn and the buffer grew
        // without end.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => Err((-32000, "the engine is busy".into())),
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;
        app.needs_rehydrate = false;
        app.needs_resync = true;
        let start = std::time::Instant::now();

        app.settle_with_engine_at(start).await;

        assert!(!app.view.is_resyncing(), "a failed read must not hold the stream hostage");
        assert!(app.view.needs_snapshot(), "the state read is still owed");
        let notices = notices(app);
        assert!(
            notices.iter().any(|n| n.contains("Could not read the engine's state")),
            "the failure must be visible: {notices:?}"
        );

        // The stream keeps flowing, in order, with the cursor intact.
        let (method, params) = text_frame("e1", 1);
        assert_eq!(app.accept(method, params).len(), 1, "streaming stopped after a failed read");
        assert_eq!(app.view.cursor(), 1);
        assert_eq!(app.view.buffered(), 0);
    }

    #[tokio::test]
    async fn frames_held_across_a_failed_state_read_are_released_in_order() {
        // What was held while the read was in flight is not lost: it
        // continues from the cursor this client still holds.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => Err((-32000, "no".into())),
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;
        app.needs_rehydrate = false;
        app.view.begin_resync();
        for seq in 1..=2 {
            let (method, params) = text_frame("e1", seq);
            assert!(app.accept(method, params).is_empty(), "held while the read is in flight");
        }
        app.needs_resync = true;

        app.settle_with_engine_at(std::time::Instant::now()).await;

        let assistant: String = app
            .state
            .transcript
            .blocks()
            .iter()
            .filter_map(|block| match block {
                Block::Assistant { text, .. } => Some(text.clone()),
                _ => None,
            })
            .collect();
        assert_eq!(assistant, "xx", "the held frames were dropped instead of replayed");
        assert_eq!(app.view.cursor(), 2);
    }

    #[tokio::test]
    async fn repeated_state_read_failures_back_off_rather_than_retrying_per_frame() {
        // `settle_with_engine` runs once per loop iteration, so an
        // unconditional retry turns one broken RPC into a request per inbound
        // frame and per keystroke.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => Err((-32000, "no".into())),
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;
        app.needs_rehydrate = false;
        app.needs_resync = true;
        let start = std::time::Instant::now();

        // Fifty loop iterations in the same instant: the engine must not see
        // fifty reads.
        for _ in 0..50 {
            app.settle_with_engine_at(start).await;
        }
        let attempts = harness
            .calls
            .lock()
            .expect("calls")
            .iter()
            .filter(|m| *m == "session/getState")
            .count();
        assert_eq!(attempts, 1, "a failed read was retried on every iteration: {attempts}");
        assert_eq!(
            harness.app.resync_recovery.failures(),
            1,
            "one attempt, one recorded failure — the schedule must not be advanced by \
             iterations that never asked"
        );

        // Time passing is what allows the next attempt.
        harness
            .app
            .settle_with_engine_at(start + std::time::Duration::from_secs(60))
            .await;
        let attempts = harness
            .calls
            .lock()
            .expect("calls")
            .iter()
            .filter(|m| *m == "session/getState")
            .count();
        assert_eq!(attempts, 2, "the client gave up on ever recovering");
    }

    #[tokio::test]
    async fn a_failed_conversation_read_keeps_the_need_and_retries_later() {
        // Dropping `needs_rehydrate` on failure left the transcript
        // permanently short of the conversation the engine holds, with
        // nothing that would ever ask again.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getHistory" => Err((-32000, "no".into())),
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;
        app.needs_resync = false;
        app.needs_rehydrate = true;
        let start = std::time::Instant::now();

        for _ in 0..20 {
            app.settle_with_engine_at(start).await;
        }
        assert!(app.needs_rehydrate, "the conversation still has to be read");
        let attempts = harness
            .calls
            .lock()
            .expect("calls")
            .iter()
            .filter(|m| *m == "session/getHistory")
            .count();
        assert_eq!(attempts, 1, "a failed read spun on every iteration: {attempts}");

        harness
            .app
            .settle_with_engine_at(start + std::time::Duration::from_secs(60))
            .await;
        let attempts = harness
            .calls
            .lock()
            .expect("calls")
            .iter()
            .filter(|m| *m == "session/getHistory")
            .count();
        assert_eq!(attempts, 2, "the retry never came");
    }

    #[tokio::test]
    async fn a_full_hold_buffer_is_reported_as_this_clients_own_bound() {
        // The events did arrive; this client could not hold them. Saying they
        // never arrived would blame the engine for a local limit — and saying
        // nothing at all would show a short conversation as though it were
        // whole.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        app.needs_rehydrate = false;
        app.needs_resync = false;
        app.view.begin_resync();
        for seq in 1..=(crate::api::view::MAX_BUFFERED_FRAMES as i64 + 1) {
            let (method, params) = text_frame("e1", seq);
            assert!(app.accept(method, params).is_empty());
        }

        assert!(app.view.buffered() <= crate::api::view::MAX_BUFFERED_FRAMES);
        assert!(app.needs_rehydrate, "a discarded run must be re-read, not assumed");
        let notices = notices(app);
        assert!(
            notices.iter().any(|n| n.contains("could not hold")),
            "the client must own its own limit: {notices:?}"
        );
    }

    fn permission_dto(handle: &str) -> coda_proto::state::PendingRequestDto {
        use coda_proto::state::{PendingRequestDto, PendingRequestKind};
        PendingRequestDto {
            request_id: handle.into(),
            kind: PendingRequestKind::Permission,
            issued_at: "now".into(),
            turn_id: None,
            call_id: None,
            display: json!({ "toolName": "run_command", "inputPreview": "ls" }),
            fail_closed_default: PendingRequestKind::Permission.fail_closed_default().into(),
        }
    }

    fn question_dto(handle: &str) -> coda_proto::state::PendingRequestDto {
        use coda_proto::state::{PendingRequestDto, PendingRequestKind};
        PendingRequestDto {
            request_id: handle.into(),
            kind: PendingRequestKind::Question,
            issued_at: "now".into(),
            turn_id: None,
            call_id: None,
            display: json!({ "question": "Which?", "options": ["a", "b"] }),
            fail_closed_default: PendingRequestKind::Question.fail_closed_default().into(),
        }
    }

    /// Opens a discovered request as the decision on screen.
    fn discovered(app: &mut App, dto: &coda_proto::state::PendingRequestDto) {
        assert!(app.pending.on_discovered(dto, Some("e1".into()), Some(1)));
        app.open_current_prompt();
        assert!(app.state.prompt.is_some(), "the decision must be on screen to begin with");
    }

    #[tokio::test]
    async fn a_permission_allowed_on_another_client_is_not_shown_as_a_denial() {
        // The engine publishes the verdict it applied. Dispatching a
        // fabricated `allowed: false` turned every external approval into a
        // refusal on this screen — and a `Denied` block to match.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        let dto = permission_dto("req-e1-1");
        discovered(app, &dto);

        app.apply_state_frame(StateFrame::RequestResolved {
            request_id: "req-e1-1".into(),
            kind: Some(coda_proto::state::PendingRequestKind::Permission),
            outcome: Some("allowed".into()),
            requests: Vec::new(),
        });

        assert!(app.state.prompt.is_none(), "the modal must come down");
        assert!(app.pending.is_empty());
        assert!(
            !app.state.transcript.blocks().iter().any(|b| matches!(
                b,
                Block::Permission { decision: crate::transcript::PermissionDecision::Denied, .. }
            )),
            "a denial was fabricated: {:?}",
            app.state.transcript.blocks()
        );
        let notices = notices(app);
        assert!(
            notices.iter().any(|n| n.contains("allowed")),
            "the engine's verdict must reach the screen: {notices:?}"
        );
    }

    #[tokio::test]
    async fn a_question_cancelled_by_the_engine_reports_no_answer_rather_than_one() {
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        let dto = question_dto("req-e1-7");
        discovered(app, &dto);

        app.apply_state_frame(StateFrame::RequestResolved {
            request_id: "req-e1-7".into(),
            kind: Some(coda_proto::state::PendingRequestKind::Question),
            outcome: Some("noAnswer.cancelled".into()),
            requests: Vec::new(),
        });

        assert!(
            !app.state
                .transcript
                .blocks()
                .iter()
                .any(|b| matches!(b, Block::Question { answer: Some(_), .. })),
            "an answer was invented: {:?}",
            app.state.transcript.blocks()
        );
        let notices = notices(app);
        assert!(notices.iter().any(|n| n.contains("no answer")), "{notices:?}");
    }

    #[tokio::test]
    async fn resolving_a_queued_request_leaves_the_decision_on_screen_alone() {
        // Only the decision actually showing may be taken down. A background
        // subagent's request resolving elsewhere must not close the modal the
        // operator is reading.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        let first = permission_dto("req-e1-1");
        let second = question_dto("req-e1-2");
        discovered(app, &first);
        assert!(app.pending.on_discovered(&second, Some("e1".into()), Some(2)));

        app.apply_state_frame(StateFrame::RequestResolved {
            request_id: "req-e1-2".into(),
            kind: Some(coda_proto::state::PendingRequestKind::Question),
            outcome: Some("answered".into()),
            requests: Vec::new(),
        });

        assert!(app.state.prompt.is_some(), "the on-screen decision was closed by another's outcome");
        assert_eq!(app.pending.len(), 1);
        assert!(
            app.state.transcript.blocks().is_empty(),
            "a queued request's outcome must write nothing about the open decision: {:?}",
            app.state.transcript.blocks()
        );
    }

    #[tokio::test]
    async fn answering_here_still_records_the_operators_own_decision() {
        // The regression guard for the fix above: a local answer is still a
        // decision this terminal made, and still renders as one.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({ "ok": true }));
        let app = &mut harness.app;
        let dto = permission_dto("req-e1-1");
        discovered(app, &dto);

        app.answer_prompt(true, None).await;

        assert!(matches!(
            app.state.transcript.blocks().last(),
            Some(Block::Permission { decision: crate::transcript::PermissionDecision::Allowed, .. })
        ), "{:?}", app.state.transcript.blocks());
        assert!(app.pending.is_empty());

        // And the engine's own echo of that resolution adds nothing: this
        // client already removed the entry when it answered.
        let before = app.state.transcript.len();
        app.apply_state_frame(StateFrame::RequestResolved {
            request_id: "req-e1-1".into(),
            kind: Some(coda_proto::state::PendingRequestKind::Permission),
            outcome: Some("allowed".into()),
            requests: Vec::new(),
        });
        assert_eq!(app.state.transcript.len(), before, "the resolution was reported twice");
    }

    #[tokio::test]
    async fn a_re_read_owed_by_a_successful_snapshot_still_wakes_the_loop() {
        // A successful `session/getState` clears the retry schedule, so a
        // re-read owed by what that snapshot *contained* — a config change, a
        // steering update, a hole in the replayed buffer — had nothing left
        // to wake the loop with. On an idle screen the client then sat on
        // state it already knew was stale until the user happened to type.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        app.needs_resync = false;
        app.needs_rehydrate = false;
        // Exactly what a successful read leaves behind: nothing owed, no
        // schedule.
        app.resync_recovery.on_success();
        app.rehydrate_recovery.on_success();
        app.frame_deadline = None;

        app.apply_state_frame(StateFrame::ConfigChanged { active: None, next: None });
        assert!(app.needs_resync, "a config change owes a re-read to begin with");

        app.arm_spinner_wakeup();

        let deadline = app.frame_deadline.expect("an owed re-read must wake the loop");
        assert!(
            deadline
                <= tokio::time::Instant::from_std(
                    std::time::Instant::now() + std::time::Duration::from_secs(1)
                ),
            "the wakeup must be near-term, not a far-off frame"
        );
    }

    #[tokio::test]
    async fn an_owed_conversation_read_wakes_the_loop_too() {
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        app.needs_resync = false;
        app.needs_rehydrate = true;
        app.resync_recovery.on_success();
        app.rehydrate_recovery.on_success();
        app.frame_deadline = None;

        app.arm_spinner_wakeup();

        assert!(app.frame_deadline.is_some(), "an owed rehydration must wake the loop");
    }

    #[tokio::test]
    async fn an_owed_re_read_waits_for_its_backoff_rather_than_spinning() {
        // The wake must be *gated* by the schedule: arming it for "now" while
        // a backoff is running would turn a failing engine into a busy loop
        // driven by this client's own wakeups.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => Err((-32000, "no".into())),
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;
        app.needs_rehydrate = false;
        app.needs_resync = true;

        app.settle_with_engine_at(std::time::Instant::now()).await;
        let due = app.resync_recovery.next_attempt().expect("a failed read schedules a retry");
        app.frame_deadline = None;

        app.arm_spinner_wakeup();

        assert_eq!(
            app.frame_deadline,
            Some(tokio::time::Instant::from_std(due)),
            "the wake must be the scheduled retry, not an immediate one"
        );
    }

    #[tokio::test]
    async fn a_pending_retry_wakes_the_loop_even_when_the_engine_has_gone_quiet() {
        // The event loop only runs `settle_with_engine` when something wakes
        // it. An engine that answers reads with an error but sends no events
        // wakes nothing, so a backed-off retry that waited for the next
        // inbound frame or keystroke would simply never happen.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => Err((-32000, "no".into())),
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;
        app.needs_rehydrate = false;
        app.needs_resync = true;
        app.frame_deadline = None;

        let now = std::time::Instant::now();
        app.settle_with_engine_at(now).await;
        app.arm_spinner_wakeup();

        let deadline = app.frame_deadline.expect("a pending retry must arm a wakeup");
        assert!(
            deadline <= tokio::time::Instant::from_std(now + std::time::Duration::from_secs(1)),
            "the wakeup must be at the retry, not at some far-off frame"
        );
    }

    // -- Session teardown ----------------------------------------------------
    //
    // These drive the production exit path — the same `finish` every way out
    // of `run` goes through — over a real connection, and read the frames the
    // client actually wrote. `tests/conventions.rs` holds `run` to having no
    // other exit, because the previous shape had exactly this teardown
    // written down and never called.

    /// A `request/permission` frame, as the engine sends it.
    fn permission_request(id: i64, handle: &str) -> Value {
        json!({
            "jsonrpc": "2.0",
            "id": id,
            "method": "request/permission",
            "params": {
                "requestId": handle,
                "toolName": "run_command",
                "inputPreview": "ls",
            },
        })
    }

    fn response_for(frames: &[Value], id: i64) -> Option<&Value> {
        frames
            .iter()
            .find(|frame| frame.get("method").is_none() && frame["id"].as_i64() == Some(id))
    }

    fn position_of_request(frames: &[Value], method: &str) -> Option<usize> {
        frames.iter().position(|frame| frame["method"].as_str() == Some(method))
    }

    /// Every frame the client wrote that was an *answer* rather than a
    /// request: a reply to a reverse request, or a cancellation.
    ///
    /// The fenced recovery cases assert this is empty. Retiring a decision is
    /// not answering it, and the whole point of discarding a responder rather
    /// than dropping one is that nothing goes out — a cancellation addressed
    /// to an id the engine has since reused resolves somebody else's request.
    fn client_answers(frames: &[Value]) -> Vec<&Value> {
        frames.iter().filter(|frame| frame.get("method").is_none()).collect()
    }

    #[tokio::test]
    async fn closing_the_session_talks_to_a_live_engine_and_leaves_it_talking() {
        // The graceful half of the teardown, and the thing a fail-fast
        // connection must not break. `close_out` says goodbye *over the
        // connection*: the outstanding decisions are declined and
        // `session/shutdown` is a real round-trip that the engine answers.
        // None of that is possible if the connection is closed when the stop
        // is sent — the request would be refused before it reached the wire,
        // and the engine would be killed after a grace period instead of
        // flushing its session and closing its MCP servers.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "shutdown" => Ok(json!({ "ok": true })),
                _ => Ok(json!({ "ok": true })),
            }
        });

        within("the graceful close-out", harness.app.close_out()).await;
        harness.settle_wire().await;

        assert!(
            position_of_request(&harness.frames(), "shutdown").is_some(),
            "the engine was never asked to stop: {:?}",
            harness.frames()
        );
        // The round-trip completed against a connection that is *still open*.
        // Closing at the point the stop is sent would have failed the request
        // before it was written, and this is what tells the two apart.
        assert!(
            !harness.app.connection.is_closed(),
            "the connection was closed while the engine was still being talked to"
        );
        let answered = within(
            "a round-trip after the goodbye",
            harness.app.connection.request("session/getState", None),
        )
        .await;
        assert!(answered.is_ok(), "the closing window was not usable: {answered:?}");

        // And a decision the engine raises inside that window is still
        // answerable: the reply is written, not dropped on the floor.
        harness.push(permission_request(51, "req-e1-51"));
        let message = harness.next_inbound().await;
        harness.app.on_inbound(message);
        harness.app.answer_prompt(true, None).await;
        harness.settle_wire().await;

        let frames = harness.frames();
        let reply = response_for(&frames, 51)
            .unwrap_or_else(|| panic!("a reply inside the closing window never left: {frames:?}"));
        assert_eq!(reply["result"]["allow"], true, "{reply}");

        // Only now do the processes go away.
        within(
            "stopping the engines",
            harness.app.stop_owned_engines(std::time::Duration::from_millis(250)),
        )
        .await;
    }

    #[tokio::test]
    async fn closing_the_session_declines_the_live_engines_request_before_it_stops() {
        // The engine is blocked on a human who has just closed the terminal.
        // Leaving the responder to `Drop` would still cancel it, but only as
        // a side effect of teardown order — and `Drop` cannot tell a live
        // request from one whose engine has been replaced.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "shutdown" => Ok(json!({ "ok": true })),
                _ => Ok(json!({})),
            }
        });

        harness.push(permission_request(41, "req-e1-1"));
        let message = harness.next_inbound().await;
        harness.app.on_inbound(message);
        assert_eq!(harness.app.pending.len(), 1, "the request is outstanding to begin with");

        let summary = harness
            .app
            .finish(Ok(()), std::time::Instant::now())
            .await
            .expect("a clean exit still produces a summary");
        assert_eq!(summary.session_id.as_deref(), Some("s1"));
        harness.settle_wire().await;

        let frames = harness.frames();
        let reply = response_for(&frames, 41).unwrap_or_else(|| {
            panic!("the live request was never answered: {frames:?}")
        });
        assert_eq!(
            reply["error"]["code"].as_i64(),
            Some(coda_proto::error_codes::REQUEST_CANCELLED),
            "an explicit decline is an error reply, which the engine reads as a refusal: {reply}"
        );

        // Order matters: the engine must have its answer before it is asked
        // to stop, or the stop races the decision it is still blocked on.
        let decline_at = frames
            .iter()
            .position(|frame| frame.get("method").is_none() && frame["id"].as_i64() == Some(41))
            .expect("the decline was recorded");
        let shutdown_at =
            position_of_request(&frames, "shutdown").expect("the engine was asked to stop");
        assert!(decline_at < shutdown_at, "the stop overtook the decline: {frames:?}");
    }

    #[tokio::test]
    async fn closing_the_session_sends_nothing_for_a_request_it_cannot_place() {
        // A responder whose engine this client cannot identify must not be
        // answered at all: over a connection that outlived its engine — a
        // proxy that kept the socket — a numeric id addresses whatever the
        // *new* process now has under that number. `Drop` would send one
        // anyway, which is why the responder is discarded explicitly first.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "shutdown" => Ok(json!({ "ok": true })),
                _ => Ok(json!({})),
            }
        });

        harness.push(permission_request(42, "req-unknown-1"));
        let message = harness.next_inbound().await;
        let Inbound::Request { responder, .. } = message else {
            panic!("expected a server-initiated request");
        };
        // Registered without an instance, which is what a legacy connection —
        // or a frame that arrived before the handshake named one — leaves
        // behind: the origin cannot be compared, so it cannot be answered.
        harness.app.pending.on_server_request(
            Some("req-unknown-1".into()),
            None,
            PendingPrompt::Permission { tool: "run_command".into(), preview: "ls".into() },
            responder,
        );

        harness.app.finish(Ok(()), std::time::Instant::now()).await.expect("summary");
        harness.settle_wire().await;
        assert!(
            response_for(&harness.frames(), 42).is_none(),
            "a request that cannot be placed was answered anyway: {:?}",
            harness.frames()
        );

        // And it stays unanswered when the app itself goes away: the
        // responder was discarded, so `Drop` has nothing left to cancel.
        let Harness { app, frames, .. } = harness;
        drop(app);
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
        let written = frames.lock().expect("frames poisoned").clone();
        assert!(
            response_for(&written, 42).is_none(),
            "dropping the app cancelled a request it could not place: {written:?}"
        );
    }

    #[tokio::test]
    async fn a_failed_run_tears_the_session_down_before_reporting_the_failure() {
        // The error path is the one that used to skip teardown entirely: an
        // early `?` dropped the app, and with it every responder, in whatever
        // order the fields happened to be declared.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "shutdown" => Ok(json!({ "ok": true })),
                _ => Ok(json!({})),
            }
        });

        harness.push(permission_request(43, "req-e1-9"));
        let message = harness.next_inbound().await;
        harness.app.on_inbound(message);

        let error = harness
            .app
            .finish(Err(anyhow::anyhow!("terminal input failed")), std::time::Instant::now())
            .await
            .expect_err("the failure is still reported");
        assert!(error.to_string().contains("terminal input failed"));
        harness.settle_wire().await;

        let frames = harness.frames();
        assert!(
            response_for(&frames, 43).is_some(),
            "a failing run left the engine waiting: {frames:?}"
        );
        assert!(
            position_of_request(&frames, "shutdown").is_some(),
            "a failing run never asked the engine to stop: {frames:?}"
        );
    }

    #[tokio::test]
    async fn a_failed_resume_leaves_the_conversation_it_could_not_replace() {
        // The transcript was cleared *before* the new engine was started, so
        // a bad session id — or an engine that would not spawn — wiped a
        // perfectly healthy conversation and left the user with an error
        // message and nothing else.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        app.apply(UiEvent::Submitted { text: "keep me".into() });
        app.apply(UiEvent::Engine(coda_proto::Event::AssistantText { delta: "and me".into() }));
        let before = app.state.transcript.len();

        // The harness's engine command names a binary that does not exist, so
        // this is the real failure path: `boot` cannot start it.
        app.resume_to_session("no-such-session".into()).await;

        assert!(
            app.state.transcript.blocks().iter().any(
                |b| matches!(b, Block::User { text, .. } if text == "keep me")
            ),
            "a failed resume wiped the conversation: {:?}",
            app.state.transcript.blocks()
        );
        assert!(
            app.state.transcript.len() > before,
            "the failure must still be reported: {:?}",
            app.state.transcript.blocks()
        );
        let notices = notices(app);
        assert!(!notices.is_empty(), "the failure must be visible: {notices:?}");
    }

    #[tokio::test]
    async fn a_second_outstanding_request_still_reaches_the_screen() {
        // `reconcile` reports which requests it discovered from the list, and
        // that list was thrown away: a frame whose own `request` this client
        // already knew therefore surfaced nothing, even when the same frame
        // announced a *second* decision nobody had seen.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        let known = permission_dto("req-e1-1");
        let fresh = question_dto("req-e1-2");
        assert!(app.pending.on_discovered(&known, Some("e1".into()), Some(1)));
        assert!(app.state.prompt.is_none(), "nothing is on screen to begin with");

        app.apply_state_frame(StateFrame::RequestPending {
            request: Box::new(known.clone()),
            requests: vec![known.clone(), fresh.clone()],
            at: Some(2),
        });

        assert_eq!(app.pending.len(), 2, "both are outstanding");
        assert!(
            app.state.prompt.is_some(),
            "two decisions are outstanding and neither reached the screen"
        );
        // The oldest is the one to answer first.
        assert_eq!(app.pending.current().map(|e| e.key()), Some("req-e1-1".to_string()));
    }

    #[tokio::test]
    async fn a_permission_mode_the_engine_never_answered_is_not_reported_as_refused() {
        // A transport failure and "the engine said no" are different facts.
        // Collapsing them told the user the mode was unchanged when the
        // engine may well have changed it and failed to say so.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/setPermissionMode" => Err((-32000, "the engine is busy".into())),
                _ => Ok(json!({ "ok": true })),
            }
        });

        let applied = harness.app.apply_permission_mode("plan").await;

        assert!(!applied, "an unconfirmed change must not be reported as applied");
        let notices = notices(&harness.app);
        assert!(
            notices.iter().any(|n| n.contains("the engine is busy")),
            "the engine's own error must be shown: {notices:?}"
        );
        assert!(
            !notices.iter().any(|n| n.contains("it is unchanged")),
            "an unanswered request is an unknown outcome, not a refusal: {notices:?}"
        );
    }

    #[tokio::test]
    async fn a_permission_mode_the_engine_refused_says_it_is_unchanged() {
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/setPermissionMode" => json!({ "ok": false }),
                _ => json!({ "ok": true }),
            }
        });

        let applied = harness.app.apply_permission_mode("plan").await;

        assert!(!applied);
        let notices = notices(&harness.app);
        assert!(
            notices.iter().any(|n| n.contains("unchanged")),
            "an explicit refusal is a known outcome and says so: {notices:?}"
        );
    }

    // -- The conversation read ------------------------------------------------
    //
    // These drive a fake engine that honours `sinceIndex`, `limit` and the
    // fences exactly as the real one does, so what is asserted is the request
    // this client actually sends and the conversation it actually shows.

    /// A `session/getState` answer, built from the real DTO so a field this
    /// client depends on cannot quietly stop being sent.
    fn snapshot_value(cursor: i64, history_length: i64, max_history_page: i64) -> Value {
        use coda_proto::state::*;
        let snapshot = StateSnapshot {
            contract_version: CONTRACT_VERSION.into(),
            engine_instance_id: "e1".into(),
            session_id: "s1".into(),
            workspace_path: "/w".into(),
            cursor,
            history_epoch: 0,
            history_length,
            lifecycle: EngineLifecycle::Ready,
            initialized: true,
            last_turn_outcome: None,
            turn: None,
            steering: SteeringQueueState::default(),
            tools: ToolsState::default(),
            requests: Vec::new(),
            config: EffectiveConfig {
                active: None,
                next: ActiveConfig {
                    provider_id: None,
                    model: "m".into(),
                    effort: None,
                    effort_is_auto: true,
                    permission_mode: "default".into(),
                    system_prompt_source: "default".into(),
                },
                differing: Vec::new(),
            },
            usage: UsageState::default(),
            limits: Limits {
                ring_envelopes: 2048,
                ring_bytes: 4 << 20,
                live_bytes_cap: 262_144,
                outcomes_retained: 64,
                history_block_bytes_cap: 65_536,
                max_history_page,
                max_session_page: 200,
            },
            capabilities: Default::default(),
        };
        serde_json::to_value(snapshot).expect("snapshot")
    }

    /// A committed conversation of `total` alternating messages, paged the way
    /// `coda-serve` pages it: `sinceIndex` clamped into range, `limit`
    /// clamped to the advertised maximum, fences refused rather than ignored.
    fn history_page(params: &Value, total: i64, cursor: i64, max_page: i64) -> Result<Value, (i64, String)> {
        if let Some(expected) = params.get("expectedHistoryLength").and_then(Value::as_i64) {
            if expected != total {
                return Err((
                    coda_proto::messages::error_code::HISTORY_FENCE_MOVED,
                    format!("history moved: expected {expected}, have {total}"),
                ));
            }
        }
        if let Some(epoch) = params.get("historyEpoch").and_then(Value::as_i64) {
            if epoch != 0 {
                return Err((
                    coda_proto::messages::error_code::STALE_EPOCH,
                    format!("stale epoch {epoch}"),
                ));
            }
        }
        if let Some(instance) = params.get("engineInstanceId").and_then(Value::as_str) {
            if instance != "e1" {
                return Err((-32010, "this engine is instance e1".into()));
            }
        }
        let since = params.get("sinceIndex").and_then(Value::as_i64).unwrap_or(0).clamp(0, total);
        let limit = params.get("limit").and_then(Value::as_i64).unwrap_or(100).clamp(1, max_page);
        let end = (since + limit).min(total);
        let entries: Vec<Value> = (since..end)
            .map(|index| {
                let assistant = index % 2 == 1;
                json!({
                    "index": index,
                    "role": if assistant { "assistant" } else { "user" },
                    "entryKind": if assistant { "assistant" } else { "userPrompt" },
                    "blocks": [{ "kind": "text", "text": format!("message {index}") }],
                })
            })
            .collect();
        Ok(json!({
            "sessionId": "s1",
            "engineInstanceId": "e1",
            "isLiveSession": true,
            "historyEpoch": 0,
            "cursor": cursor,
            "historyLength": total,
            "entries": entries,
            "nextIndex": end,
            "totalKnown": total,
            "truncated": end < total,
        }))
    }

    fn user_texts(app: &App) -> Vec<String> {
        app.state
            .transcript
            .blocks()
            .iter()
            .filter_map(|block| match block {
                Block::User { text, .. } => Some(text.clone()),
                Block::Assistant { text, .. } => Some(text.clone()),
                _ => None,
            })
            .collect()
    }

    fn requests_for<'a>(frames: &'a [Value], method: &str) -> Vec<&'a Value> {
        frames
            .iter()
            .filter(|frame| frame["method"].as_str() == Some(method))
            .map(|frame| &frame["params"])
            .collect()
    }

    #[tokio::test]
    async fn a_long_conversation_is_rebuilt_from_its_newest_page_not_its_oldest() {
        // `sinceIndex` was never sent, so the engine served entries 0..100 —
        // the *oldest* hundred — and the client announced them as "the most
        // recent". A resumed session then showed its opening messages with
        // the last thing that happened nowhere on screen.
        const TOTAL: i64 = 250;
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, params| {
            match method {
                "session/getState" => Ok(snapshot_value(9, TOTAL, 500)),
                "session/getHistory" => history_page(params, TOTAL, 9, 500),
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;

        app.settle_with_engine_at(std::time::Instant::now()).await;

        let shown = user_texts(app);
        assert!(
            shown.iter().any(|t| t == &format!("message {}", TOTAL - 1)),
            "the newest message is not on screen: {:?}",
            shown.last()
        );
        assert!(
            !shown.iter().any(|t| t == "message 0"),
            "the oldest message was shown as though it were recent"
        );
        assert_eq!(
            shown.last().map(String::as_str),
            Some(format!("message {}", TOTAL - 1).as_str()),
            "the newest message must be last"
        );

        let notices = notices(app);
        assert!(
            notices.iter().any(|n| n.contains("most recent") && n.contains("250")),
            "an incomplete conversation must say what is missing: {notices:?}"
        );

        // The request itself: an explicit tail window, fenced on everything
        // the engine offers to fence on.
        harness.settle_wire().await;
        let frames = harness.frames();
        let reads = requests_for(&frames, "session/getHistory");
        let asked = reads.last().expect("the conversation was read");
        let since = asked["sinceIndex"].as_i64().expect("an explicit sinceIndex");
        let limit = asked["limit"].as_i64().expect("an explicit limit");
        assert_eq!(since, TOTAL - limit, "the window must be the tail: {asked}");
        assert_eq!(asked["historyEpoch"].as_i64(), Some(0), "{asked}");
        assert_eq!(asked["expectedHistoryLength"].as_i64(), Some(TOTAL), "{asked}");
        assert_eq!(asked["engineInstanceId"].as_str(), Some("e1"), "{asked}");
    }

    #[tokio::test]
    async fn the_page_size_never_exceeds_what_the_engine_advertises() {
        const TOTAL: i64 = 400;
        const MAX_PAGE: i64 = 40;
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, params| {
            match method {
                "session/getState" => Ok(snapshot_value(3, TOTAL, MAX_PAGE)),
                "session/getHistory" => history_page(params, TOTAL, 3, MAX_PAGE),
                _ => Ok(json!({})),
            }
        });

        harness.app.settle_with_engine_at(std::time::Instant::now()).await;
        harness.settle_wire().await;

        let frames = harness.frames();
        let asked = requests_for(&frames, "session/getHistory");
        let asked = asked.last().expect("the conversation was read");
        assert!(
            asked["limit"].as_i64().is_some_and(|l| l <= MAX_PAGE),
            "the client asked for more than the engine advertises: {asked}"
        );
        assert_eq!(
            asked["sinceIndex"].as_i64(),
            Some(TOTAL - asked["limit"].as_i64().unwrap()),
            "still the tail, at the smaller size: {asked}"
        );
        assert!(
            user_texts(&harness.app).iter().any(|t| t == &format!("message {}", TOTAL - 1)),
            "the newest message must still be on screen"
        );
    }

    #[tokio::test]
    async fn a_stale_length_costs_one_more_read_rather_than_the_oldest_page() {
        // The window is computed from the length the snapshot reported, and a
        // turn can commit between the two reads. The engine refuses the stale
        // fence; the client must go back for the truth and re-aim, never
        // render whatever page came back.
        const TOTAL: i64 = 300;
        let committed = std::sync::Arc::new(std::sync::Mutex::new(120i64));
        let engine_committed = std::sync::Arc::clone(&committed);
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, move |method, params| {
            match method {
                "session/getState" => {
                    let mut length = engine_committed.lock().expect("length poisoned");
                    let reported = *length;
                    // A turn commits the moment that snapshot is taken.
                    *length = TOTAL;
                    Ok(snapshot_value(4, reported, 500))
                }
                "session/getHistory" => history_page(params, TOTAL, 4, 500),
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;

        let start = std::time::Instant::now();
        app.settle_with_engine_at(start).await;
        assert!(app.needs_resync, "a moved fence must send the client back to the snapshot");
        // The refusal is a reason to re-read, not to give up.
        app.settle_with_engine_at(start + std::time::Duration::from_secs(30)).await;

        let shown = user_texts(app);
        assert!(
            shown.iter().any(|t| t == &format!("message {}", TOTAL - 1)),
            "the newest message never arrived: {:?}",
            shown.last()
        );
        assert!(!shown.iter().any(|t| t == "message 0"), "{shown:?}");
    }

    #[tokio::test]
    async fn an_engine_that_names_no_instance_still_ends_up_at_the_newest_page() {
        // A legacy engine stamps no envelopes, so there is no length to
        // compute a window from and no fence to send. The answer's own
        // `totalKnown` is then the only way to find the end — and the client
        // must use it rather than settle for the oldest page it was served.
        const TOTAL: i64 = 260;
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, params| {
            match method {
                "session/getHistory" => history_page(params, TOTAL, -1, 500),
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;
        // No instance: exactly what `initialize` from a legacy build leaves.
        app.view = crate::api::ServeView::new();
        app.needs_resync = false;
        app.needs_rehydrate = true;

        let start = std::time::Instant::now();
        app.rehydrate_at(start).await;
        assert!(app.needs_rehydrate, "an interior page is not the conversation's end");
        app.rehydrate_at(start + std::time::Duration::from_secs(30)).await;

        let shown = user_texts(app);
        assert!(
            shown.iter().any(|t| t == &format!("message {}", TOTAL - 1)),
            "the newest message never arrived: {:?}",
            shown.last()
        );
    }

    #[tokio::test]
    async fn a_stale_epoch_is_re_fenced_rather_than_retried_for_ever() {
        // Asking again with the same rejected epoch is a loop that cannot
        // terminate: the epoch only moves when the client re-reads state.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getHistory" => Err((
                    coda_proto::messages::error_code::STALE_EPOCH,
                    "the conversation was replaced".into(),
                )),
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;
        app.needs_resync = false;
        app.needs_rehydrate = true;
        app.view.on_history_reset(7, 3);

        app.rehydrate_at(std::time::Instant::now()).await;

        assert!(app.needs_resync, "a stale epoch must send the client back to the snapshot");
        assert!(app.needs_rehydrate, "and the conversation still has to be read");
    }

    #[tokio::test]
    async fn a_tail_that_starts_mid_turn_says_which_results_have_no_call() {
        // The tail can begin between a tool call and its result. Attaching
        // the orphan to something nearby would be a lie; dropping it silently
        // would be a different one.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => Ok(snapshot_value(1, 2, 500)),
                "session/getHistory" => Ok(json!({
                    "sessionId": "s1",
                    "engineInstanceId": "e1",
                    "isLiveSession": true,
                    "historyEpoch": 0,
                    "cursor": 1,
                    "historyLength": 2,
                    // The call that produced this result is before the window.
                    "entries": [{
                        "index": 8,
                        "role": "user",
                        "entryKind": "toolResults",
                        "blocks": [{
                            "kind": "toolResult",
                            "callId": "toolu_9",
                            "isError": false,
                            "content": "the file contents",
                        }],
                    }],
                    "nextIndex": 9,
                    "totalKnown": 9,
                    "truncated": false,
                })),
                _ => Ok(json!({})),
            }
        });

        harness.app.settle_with_engine_at(std::time::Instant::now()).await;

        let notices = notices(&harness.app);
        assert!(
            notices.iter().any(|n| n.contains("no matching call")),
            "an orphaned result must be stated: {notices:?}"
        );
    }

    #[tokio::test]
    async fn content_the_conversation_read_already_covers_is_not_applied_twice() {
        // The read is exact at its own `cursor`, and events up to it are
        // already in what it returned. Applying them again — they are still
        // sitting in the inbound queue — printed the same reply twice. The
        // *state* frames in that range carry no conversation content and must
        // still be applied, or the queue, the config and the outstanding
        // requests silently regress.
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => Ok(snapshot_value(0, 0, 500)),
                "session/getHistory" => Ok(json!({
                    "sessionId": "s1",
                    "engineInstanceId": "e1",
                    "isLiveSession": true,
                    "historyEpoch": 0,
                    // Exact at seq 2: both frames below are already in here.
                    "cursor": 2,
                    "historyLength": 1,
                    "entries": [{
                        "index": 0,
                        "role": "assistant",
                        "entryKind": "assistant",
                        "blocks": [{ "kind": "text", "text": "the only answer" }],
                    }],
                    "nextIndex": 1,
                    "totalKnown": 1,
                    "truncated": false,
                }),
                ),
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;

        app.settle_with_engine_at(std::time::Instant::now()).await;
        assert_eq!(
            user_texts(app),
            ["the only answer"],
            "the conversation must be what the engine says it is"
        );

        // Now the events the engine had already folded into that read arrive:
        // the loop applies whatever `accept` hands back.
        for frame in app.accept(
            "event/assistantText".into(),
            Some(json!({ "delta": "the only answer", "seq": 1, "engineInstanceId": "e1" })),
        ) {
            app.dispatch_frame(frame);
        }
        for frame in app.accept(
            "event/activity".into(),
            Some(json!({
                "turnId": "t1",
                "phase": "runningTools",
                "seq": 2,
                "engineInstanceId": "e1",
            })),
        ) {
            app.dispatch_frame(frame);
        }

        assert_eq!(
            user_texts(app),
            ["the only answer"],
            "the reply was rendered twice: once from history and once from its own event"
        );
        assert_eq!(
            app.state.activity,
            crate::state::Activity::Working,
            "the state frame in the same range must still have been applied"
        );

        // Anything after the read's cursor is new and is applied normally.
        for frame in app.accept(
            "event/assistantText".into(),
            Some(json!({ "delta": " and more", "seq": 3, "engineInstanceId": "e1" })),
        ) {
            app.dispatch_frame(frame);
        }
        assert_eq!(user_texts(app), ["the only answer", " and more"]);
    }

    #[tokio::test]
    async fn history_fence_preserves_notices_and_usage_not_present_in_history() {
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        app.view.note_history_read(10);
        for (method, params) in [
            ("event/error", json!({ "seq": 1, "message": "provider unavailable" })),
            ("event/limitReached", json!({ "seq": 2, "kind": "tokens", "message": "budget exhausted" })),
            ("event/usage", json!({ "seq": 3, "inputTokens": 17, "outputTokens": 9 })),
            ("event/taskCompleted", json!({ "seq": 4, "taskId": "task-1", "description": "background work", "status": "completed" })),
        ] {
            app.dispatch_frame(Frame::new(method, Some(params)));
        }
        let visible = notices(app);
        assert!(visible.iter().any(|text| text.contains("provider unavailable")));
        assert!(visible.iter().any(|text| text.contains("budget exhausted")));
        assert!(visible.iter().any(|text| text.contains("background work")));
        assert_eq!(app.state.usage.input_tokens, 17);
        assert_eq!(app.state.usage.output_tokens, 9);
    }

    #[tokio::test]
    async fn history_fence_acknowledges_delivery_without_duplicating_user_text() {
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        app.apply(UiEvent::Queued { text: "already delivered".into(), id: Some("queued-1".into()) });
        app.apply(UiEvent::Rehydrated { blocks: vec![Block::User {
            text: "already delivered".into(), timestamp: String::new(),
            pending: false, queue_id: None,
        }], notices: Vec::new() });
        app.view.note_history_read(10);
        app.dispatch_frame(Frame::new("event/steeringDelivered", Some(json!({
            "seq": 5, "messageIds": ["queued-1"],
        }))));
        assert!(app.state.queued.is_empty(), "known delivery must not remain pending");
        assert_eq!(user_texts(app), ["already delivered"]);
    }

    // -- A peer that never answers ------------------------------------------

    /// Builds an `App` whose engine may answer, refuse, or say nothing at all.
    ///
    /// `None` means the request is received and simply never answered — a
    /// live connection with a silent peer, which is the case an unbounded
    /// `await` turns into a frozen terminal.
    pub(in crate::app) fn app_with_pump(
        access_mode: crate::local::AccessMode,
        answer: impl Fn(&str, &Value) -> Option<Result<Value, (i64, String)>> + Send + 'static,
    ) -> Harness {
        app_pump(access_mode, answer)
    }

    #[tokio::test]
    async fn a_silent_engine_does_not_freeze_the_loop_and_still_retries() {
        // The connection is open, the engine is receiving, and it never
        // answers. An unbounded await here stops the event loop dead: no
        // keystrokes, no redraw, no quit, and no retry either.
        let mut harness = app_with_pump(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => None,
                _ => Some(Ok(json!({}))),
            }
        });
        let app = &mut harness.app;
        app.metadata_timeout = std::time::Duration::from_millis(50);
        app.needs_rehydrate = false;
        app.needs_resync = true;

        let start = std::time::Instant::now();
        tokio::time::timeout(
            std::time::Duration::from_secs(5),
            app.settle_with_engine_at(start),
        )
        .await
        .expect("a silent engine must not hold the loop for ever");

        assert!(!app.view.is_resyncing(), "the fence must not be left held");
        assert!(
            app.resync_recovery.next_attempt().is_some(),
            "a timeout must engage the same retry a refusal does"
        );
        let notices = notices(app);
        assert!(
            notices.iter().any(|n| n.contains("did not answer")),
            "silence must be reported as silence: {notices:?}"
        );
    }

    #[tokio::test]
    async fn a_silent_conversation_read_keeps_the_need_and_backs_off() {
        let mut harness = app_with_pump(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getHistory" => None,
                _ => Some(Ok(json!({}))),
            }
        });
        let app = &mut harness.app;
        app.metadata_timeout = std::time::Duration::from_millis(50);
        app.needs_resync = false;
        app.needs_rehydrate = true;

        tokio::time::timeout(
            std::time::Duration::from_secs(5),
            app.settle_with_engine_at(std::time::Instant::now()),
        )
        .await
        .expect("a silent engine must not hold the loop for ever");

        assert!(app.needs_rehydrate, "the conversation still has to be read");
        assert!(app.rehydrate_recovery.next_attempt().is_some(), "and it is scheduled");
    }

    #[tokio::test]
    async fn an_answer_the_engine_never_acknowledged_is_reported_as_unknown() {
        // The decision was sent. Silence afterwards means this client does
        // not know whether it landed — which is neither "denied" nor a reason
        // to send it again. The engine's own report is what settles it, so
        // the outstanding list is re-read.
        let mut harness = app_with_pump(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/resolveRequest" => None,
                _ => Some(Ok(json!({}))),
            }
        });
        let app = &mut harness.app;
        app.metadata_timeout = std::time::Duration::from_millis(50);
        app.needs_resync = false;
        let dto = permission_dto("req-e1-1");
        discovered(app, &dto);

        tokio::time::timeout(
            std::time::Duration::from_secs(5),
            app.answer_prompt(true, None),
        )
        .await
        .expect("answering must not hold the loop for ever");

        harness.settle_wire().await;
        let sent = harness
            .calls
            .lock()
            .expect("calls")
            .iter()
            .filter(|m| *m == "session/resolveRequest")
            .count();
        assert_eq!(sent, 1, "an unacknowledged decision must never be sent twice");

        let notices = notices(&harness.app);
        assert!(
            notices.iter().any(|n| n.contains("not know") || n.contains("unknown")),
            "the uncertainty must be stated plainly: {notices:?}"
        );
        assert!(
            !notices.iter().any(|n| n.contains("declined") || n.contains("denied")),
            "silence must not be reported as a decision: {notices:?}"
        );
        assert!(
            harness.app.needs_resync,
            "the outstanding requests must be re-read from the engine's own list"
        );
    }

    #[tokio::test]
    async fn a_long_conversation_read_while_the_engine_is_talking_shows_each_thing_once() {
        // The realistic case, end to end: a resumed session with far more
        // than one page of history, and the engine streaming into it while
        // the read is in flight. The tail must be what is shown, the streamed
        // reply must appear exactly once, and the state frame that travelled
        // with it must still have been applied.
        const TOTAL: i64 = 240;
        const READ_CURSOR: i64 = 12;
        let mut harness = app_answering(crate::local::AccessMode::TrustedLocal, |method, params| {
            match method {
                "session/getState" => Ok(snapshot_value(10, TOTAL, 500)),
                "session/getHistory" => {
                    let mut page = history_page(params, TOTAL, READ_CURSOR, 500)?;
                    // The turn that is still running, as the engine projects
                    // it: the read is exact at `cursor`, so this is the same
                    // content the events below carry.
                    page["liveEntries"] = json!([{
                        "index": TOTAL,
                        "role": "assistant",
                        "entryKind": "assistant",
                        "blocks": [{ "kind": "text", "text": "still typing" }],
                    }]);
                    page["liveTruncated"] = json!(false);
                    Ok(page)
                }
                _ => Ok(json!({})),
            }
        });
        let app = &mut harness.app;

        // The engine streams while the client is reading: these frames are
        // queued by the transport and handed to the loop afterwards.
        let streamed = [
            (
                "event/assistantText".to_string(),
                json!({ "delta": "still typing", "seq": 11, "engineInstanceId": "e1" }),
            ),
            (
                "event/activity".to_string(),
                json!({
                    "turnId": "t1",
                    "phase": "runningTools",
                    "seq": 12,
                    "engineInstanceId": "e1",
                }),
            ),
        ];

        app.settle_with_engine_at(std::time::Instant::now()).await;
        for (method, params) in streamed {
            for frame in app.accept(method, Some(params)) {
                app.dispatch_frame(frame);
            }
        }

        let shown = user_texts(app);
        assert_eq!(
            shown.iter().filter(|t| *t == "still typing").count(),
            1,
            "the running turn was rendered twice: {shown:?}"
        );
        assert!(
            shown.iter().any(|t| t == &format!("message {}", TOTAL - 1)),
            "the newest committed message is missing: {shown:?}"
        );
        assert!(!shown.iter().any(|t| t == "message 0"), "the oldest page was shown: {shown:?}");
        assert_eq!(
            app.state.activity,
            crate::state::Activity::Working,
            "the state frame that arrived with it must still have been applied"
        );

        // And the next thing the engine says is applied normally.
        for frame in app.accept(
            "event/assistantText".into(),
            Some(json!({ "delta": " some more", "seq": 13, "engineInstanceId": "e1" })),
        ) {
            app.dispatch_frame(frame);
        }
        assert!(
            user_texts(app).iter().any(|t| t.contains("some more")),
            "streaming stopped after the read"
        );
    }

    #[tokio::test]
    async fn a_replaced_engine_retires_the_decision_its_predecessor_raised() {
        // The old process's request id addresses nothing in the new one. Over
        // a proxy that kept the connection it would address whatever now
        // holds that number, so nothing may be sent for it.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        let (responder, mut rx) = responder(11);
        app.pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );
        assert_eq!(app.pending.len(), 1);

        let (method, params) = text_frame("e2", 1);
        app.accept(method, params);

        assert!(app.pending.is_empty(), "the previous engine's decision was retired");
        assert!(emitted(&mut rx).is_none(), "and nothing was sent for it");
        assert!(app.needs_rehydrate, "a new process means a new conversation to read");
        assert!(app.state.prompt.is_none(), "the modal for it must not stay on screen");
        assert!(
            !app.state.transcript.blocks().iter().any(|b| matches!(
                b,
                Block::Permission { decision: crate::transcript::PermissionDecision::Denied, .. }
            )),
            "a retirement is not a decision, and must not be written down as one: {:?}",
            app.state.transcript.blocks()
        );
    }

    #[tokio::test]
    async fn a_turn_that_ends_on_a_new_history_epoch_rebuilds_the_conversation() {
        // Compaction happens at exactly this boundary: the turn ends and the
        // conversation it ran against is replaced.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        app.needs_rehydrate = false;

        app.apply_state_frame(StateFrame::TurnEnded {
            turn_id: "t1".into(),
            history_epoch: 4,
            history_length: 2,
        });
        assert!(app.needs_rehydrate, "the epoch moved and the transcript was kept anyway");

        app.needs_rehydrate = false;
        app.apply_state_frame(StateFrame::TurnEnded {
            turn_id: "t2".into(),
            history_epoch: 4,
            history_length: 3,
        });
        assert!(!app.needs_rehydrate, "the same epoch is not a reason to re-read everything");
    }

    #[tokio::test]
    async fn rebuilding_the_conversation_drops_a_selection_that_indexed_the_old_rows() {
        // A selection is a pair of *row* positions. Rebuilding the transcript
        // moves every row, so a selection kept across it copies whatever text
        // now happens to sit at those coordinates — which is how a copy comes
        // back with somebody else's message in it. Clearing it only on
        // `sessionChanged` missed the gap, compaction and reconnect paths,
        // which rebuild just as thoroughly.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getHistory" => json!({
                    "sessionId": "s1",
                    "engineInstanceId": "e1",
                    "isLiveSession": true,
                    "historyEpoch": 0,
                    "cursor": 0,
                    "historyLength": 1,
                    "entries": [
                        { "index": 0, "role": "assistant", "entryKind": "assistant",
                          "blocks": [{ "kind": "text", "text": "rebuilt" }] },
                    ],
                    "nextIndex": 1,
                    "totalKnown": 1,
                    "truncated": false,
                }),
                _ => json!({}),
            }
        });
        let app = &mut harness.app;
        app.apply(UiEvent::Submitted { text: "something to select".into() });
        app.selection.begin(crate::selection::SelectionPos { row: 0, col: 0 });
        assert!(
            app.selection.update(crate::selection::SelectionPos { row: 3, col: 4 }),
            "a selection must exist to begin with"
        );

        app.rehydrate_at(std::time::Instant::now()).await;

        assert!(
            !app.selection.has_selection(),
            "a selection survived a rebuild of the rows it points at"
        );
    }

    #[tokio::test]
    async fn a_nonempty_rehydration_replaces_the_conversation_around_this_clients_blocks() {
        // The reviewer's case: a resume, a gap or a compaction hands back a
        // real conversation, and the banner, the fork notice and the `/help`
        // output that were on screen must still be there afterwards.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getHistory" => json!({
                    "sessionId": "s1",
                    "engineInstanceId": "e1",
                    "isLiveSession": true,
                    "historyEpoch": 0,
                    "cursor": 0,
                    "historyLength": 2,
                    "entries": [
                        { "index": 0, "role": "user", "entryKind": "userPrompt",
                          "blocks": [{ "kind": "text", "text": "from history" }] },
                        { "index": 1, "role": "assistant", "entryKind": "assistant",
                          "blocks": [{ "kind": "text", "text": "answered before" }] },
                    ],
                    "nextIndex": 2,
                    "totalKnown": 2,
                    "truncated": false,
                }),
                _ => json!({}),
            }
        });
        let app = &mut harness.app;
        app.push_banner("/w");
        app.notice("Forked from session abc; the original is untouched.", NoticeLevel::Info);
        app.apply(UiEvent::CommandOutput { text: "Permission mode: plan".into() });
        app.apply(UiEvent::Submitted { text: "stale local echo".into() });

        app.rehydrate_at(std::time::Instant::now()).await;

        let blocks = app.state.transcript.blocks();
        assert!(matches!(blocks.first(), Some(Block::Banner { .. })), "{blocks:?}");
        assert!(
            blocks.iter().any(|b| matches!(b, Block::Notice { text, .. } if text.contains("Forked"))),
            "the launch notice was lost on rehydration: {blocks:?}"
        );
        assert!(
            blocks.iter().any(|b| matches!(b, Block::CommandOutput { .. })),
            "command output was lost on rehydration: {blocks:?}"
        );
        assert!(
            blocks.iter().any(|b| matches!(b, Block::User { text, .. } if text == "from history")),
            "the engine's conversation was not applied: {blocks:?}"
        );
        assert!(
            !blocks.iter().any(|b| matches!(b, Block::User { text, .. } if text.contains("stale"))),
            "the replaced conversation is still on screen: {blocks:?}"
        );
    }

    #[tokio::test]
    async fn an_empty_conversation_from_the_engine_replaces_the_one_on_screen() {
        // A rewind to the very start, or a session replaced under us: the
        // engine says there is no conversation, and the screen must agree.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getHistory" => json!({
                    "sessionId": "s1",
                    "engineInstanceId": "e1",
                    "isLiveSession": true,
                    "historyEpoch": 0,
                    "cursor": 0,
                    "historyLength": 0,
                    "entries": [],
                    "nextIndex": 0,
                    "totalKnown": 0,
                    "truncated": false,
                }),
                _ => json!({}),
            }
        });
        let app = &mut harness.app;
        app.apply(UiEvent::Submitted { text: "from the old conversation".into() });
        assert!(app.state.has_conversation());

        app.rehydrate_at(std::time::Instant::now()).await;

        assert!(
            !app.state.has_conversation(),
            "a stale conversation was left on screen: {:?}",
            app.state.transcript.blocks()
        );
        assert_eq!(harness.calls.lock().expect("calls").as_slice(), ["session/getHistory"]);
    }

    #[tokio::test]
    async fn an_empty_conversation_does_not_wipe_this_clients_own_banner() {
        // The banner and the launch notices are this client's, not a
        // projection of the engine's history. A fresh session has nothing to
        // replace, and replacing it anyway made the banner vanish.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getHistory" => json!({
                    "sessionId": "s1",
                    "engineInstanceId": "e1",
                    "isLiveSession": true,
                    "historyEpoch": 0,
                    "cursor": 0,
                    "historyLength": 0,
                    "entries": [],
                    "nextIndex": 0,
                    "totalKnown": 0,
                    "truncated": false,
                }),
                _ => json!({}),
            }
        });
        let app = &mut harness.app;
        app.notice("Forked from session abc; the original is untouched.", NoticeLevel::Info);
        let before = app.state.transcript.len();

        app.rehydrate_at(std::time::Instant::now()).await;

        assert_eq!(app.state.transcript.len(), before, "the launch notice was thrown away");
    }

    // -- The engine's own list, and the decision on screen ---------------------

    #[tokio::test]
    async fn a_snapshot_that_no_longer_lists_the_open_decision_takes_it_off_the_screen() {
        // The stale-decision bug. `event/requestResolved` never arrived — a
        // dropped ring, an overflowed hold, a resync in between — so the
        // permission dialog stayed up over a decision the engine had already
        // finished with. Answering it sent a bare numeric id into a registry
        // that had reused it, and the transcript recorded a decision that
        // reached nothing at all.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                // The authoritative answer, taken at cursor 9: nothing
                // outstanding.
                "session/getState" => snapshot_value(9, 0, 500),
                _ => json!({}),
            }
        });
        let app = &mut harness.app;
        let (responder, mut rx) = responder(42);
        app.pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "run_command".into(), preview: "ls".into() },
            responder,
        );
        // The announcement the engine wrote before the raw frame, at a seq
        // the snapshot below is late enough to know about.
        app.pending.on_discovered(&permission_dto("req-e1-1"), Some("e1".into()), Some(4));
        app.open_current_prompt();
        assert!(app.state.prompt.is_some(), "the decision is on screen to begin with");

        tokio::time::timeout(NO_HANG, app.resync_at(std::time::Instant::now()))
            .await
            .expect("the resync never returned");

        assert!(app.pending.is_empty(), "the engine is not waiting for it any more");
        assert!(app.state.prompt.is_none(), "a decision nothing is waiting for stayed on screen");
        assert!(
            !app.surfaces
                .top()
                .is_some_and(|s| s.as_any().is::<crate::surface::prompt::PromptSurface>()),
            "the modal is still up"
        );
        assert!(
            emitted(&mut rx).is_none(),
            "a retired request must be discarded, never answered: its id has been reused"
        );
        assert!(
            !app.state.transcript.blocks().iter().any(|b| matches!(
                b,
                Block::Permission { .. } | Block::Question { .. }
            )),
            "a decision nobody made was written into the transcript: {:?}",
            app.state.transcript.blocks()
        );
        let notices = notices(app);
        assert!(
            notices.iter().any(|n| n.contains("no longer waiting")),
            "a modal that simply vanishes reads as a dropped keystroke: {notices:?}"
        );
        harness.settle_wire().await;
        assert!(
            client_answers(&harness.frames()).is_empty(),
            "a retired decision was answered or cancelled on the wire: {:?}",
            harness.frames()
        );
    }

    #[tokio::test]
    async fn a_request_raised_around_the_snapshot_is_not_retired_by_it() {
        // The other side of the fence. The engine raised a decision while
        // this client was reading state; the snapshot it gets back was
        // sampled before that request existed, so its silence about it is not
        // evidence. Discarding on the strength of it would abandon a tool
        // call the engine is blocked on.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => snapshot_value(9, 0, 500),
                _ => json!({}),
            }
        });
        // Written by the engine while the state read is in flight: the
        // announcement carries a seq *after* the snapshot's cursor, and the
        // raw frame that follows it bypasses the fence entirely.
        harness.app.pending.on_discovered(
            &permission_dto("req-e1-2"),
            Some("e1".into()),
            Some(12),
        );
        harness.push(json!({
            "jsonrpc": "2.0",
            "id": 77,
            "method": "request/permission",
            "params": {
                "requestId": "req-e1-2",
                "engineInstanceId": "e1",
                "toolName": "run_command",
                "inputPreview": "ls",
            },
        }));

        tokio::time::timeout(NO_HANG, harness.app.resync_at(std::time::Instant::now()))
            .await
            .expect("the resync never returned");
        let message = harness.next_inbound().await;
        harness.app.on_inbound(message);

        assert_eq!(
            harness.app.pending.keys(),
            ["req-e1-2"],
            "a request raised after the read was asked for was discarded by it"
        );
        assert!(harness.app.state.prompt.is_some(), "and it never reached the screen");
        harness.settle_wire().await;
        assert!(
            client_answers(&harness.frames()).is_empty(),
            "a live decision was answered or cancelled by the reconciliation: {:?}",
            harness.frames()
        );
    }

    #[tokio::test]
    async fn a_replaced_engine_retires_the_decision_on_screen_without_answering_it() {
        // A different process behind the same connection: every handle the
        // previous one minted addresses nothing here, so the modal comes down
        // and *nothing goes out*. A cancellation would be delivered to the
        // new engine and resolve whichever of its requests happens to hold
        // that number.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        let (responder, mut rx) = responder(7);
        app.pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "run_command".into(), preview: "ls".into() },
            responder,
        );
        app.open_current_prompt();
        assert!(app.state.prompt.is_some(), "the decision is on screen to begin with");

        let applied = app.accept(
            "event/assistantText".to_string(),
            Some(json!({ "delta": "x", "seq": 1, "engineInstanceId": "e2" })),
        );

        assert!(applied.is_empty(), "a replaced engine's first frame is not content to apply");
        assert!(app.pending.is_empty());
        assert!(app.state.prompt.is_none(), "the modal outlived the process that raised it");
        assert!(
            emitted(&mut rx).is_none(),
            "a replaced engine's request must be discarded, never cancelled: its id has been reused"
        );
        assert!(
            !app.state.transcript.blocks().iter().any(|b| matches!(
                b,
                Block::Permission { .. } | Block::Question { .. }
            )),
            "a decision nobody made was written into the transcript: {:?}",
            app.state.transcript.blocks()
        );
    }

    #[tokio::test]
    async fn a_replaced_engine_takes_down_only_the_decision_it_actually_retired() {
        // Driven by *which* entry went, not by how many. Acting on the count
        // alone took down whatever modal happened to be up — here one raised
        // over a legacy connection that names no instance, which nothing has
        // said anything about — and told the reducer a turn had stopped
        // waiting when it had not.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        let (legacy, mut legacy_rx) = responder(1);
        app.pending.on_server_request(
            Some("req-legacy".into()),
            None,
            PendingPrompt::Permission { tool: "run_command".into(), preview: "ls".into() },
            legacy,
        );
        let (old, mut old_rx) = responder(2);
        app.pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Question {
                question: "Which?".into(),
                options: vec!["a".into()],
                multi_select: false,
                allow_free_text: true,
            },
            old,
        );
        app.open_current_prompt();

        app.accept(
            "event/assistantText".to_string(),
            Some(json!({ "delta": "x", "seq": 1, "engineInstanceId": "e2" })),
        );

        assert_eq!(app.pending.keys(), ["req-legacy"], "the wrong entry was retired");
        assert!(
            matches!(app.state.prompt, Some(PendingPrompt::Permission { .. })),
            "the modal the operator was reading was closed by another entry's retirement: {:?}",
            app.state.prompt
        );
        assert!(emitted(&mut old_rx).is_none(), "the replaced engine's request was answered");
        assert!(emitted(&mut legacy_rx).is_none(), "the open decision was answered for the user");
    }

    #[tokio::test]
    async fn a_raw_decision_whose_announcement_and_resolution_were_lost_is_retired_by_a_later_read() {
        // Case A, and the completeness gap this closes. The raw `request/*`
        // arrived; the `event/requestPending` that carries its `seq` and the
        // `event/requestResolved` that would have ended it were both lost —
        // the ordinary consequence of a dropped ring or an overflowed hold.
        // The engine's ordering therefore says nothing about this decision at
        // all, and under engine evidence alone the modal stayed up for ever.
        //
        // The read this client issues afterwards is still conclusive: the
        // engine registers a request before it writes it, and a fresh
        // `session/getState` is built when it arrives rather than served from
        // a cache, so a frame already in hand when the read went out was
        // registered before the answer was built.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => snapshot_value(9, 0, 500),
                _ => json!({}),
            }
        });
        harness.push(permission_request(61, "req-e1-61"));
        let message = harness.next_inbound().await;
        harness.app.on_inbound(message);
        assert!(harness.app.state.prompt.is_some(), "the decision is on screen to begin with");
        let before = harness.app.state.transcript.len();

        tokio::time::timeout(NO_HANG, harness.app.resync_at(std::time::Instant::now()))
            .await
            .expect("the resync never returned");

        assert!(harness.app.pending.is_empty(), "the raw-only decision stayed outstanding");
        assert!(harness.app.state.prompt.is_none(), "the modal outlived the request");
        assert!(
            !harness
                .app
                .surfaces
                .top()
                .is_some_and(|s| s.as_any().is::<crate::surface::prompt::PromptSurface>()),
            "the prompt surface is still up"
        );
        assert!(
            !harness.app.state.transcript.blocks()[before..].iter().any(|b| matches!(
                b,
                Block::Permission { .. } | Block::Question { .. }
            )),
            "a decision nobody made was written into the transcript: {:?}",
            harness.app.state.transcript.blocks()
        );
        let notices = notices(&harness.app);
        assert!(
            notices.iter().any(|n| n.contains("no longer waiting")),
            "a modal that simply vanishes reads as a dropped keystroke: {notices:?}"
        );
        harness.settle_wire().await;
        assert!(
            client_answers(&harness.frames()).is_empty(),
            "a retired decision was answered or cancelled on the wire: {:?}",
            harness.frames()
        );
    }

    #[tokio::test]
    async fn a_raw_decision_that_arrived_while_the_read_was_in_flight_survives_it() {
        // Case B, the other side of the same evidence. The frame was still
        // unread in the channel when the read went out, so the engine may
        // well have registered it after building the answer: its silence
        // proves nothing, and retiring on it would abandon a tool call the
        // engine is blocked on.
        //
        // The watermark is taken before the read is issued and carried with
        // *that* read. Re-reading it when the answer lands — "the latest" —
        // would fold in exactly the frames this is meant to exclude, which is
        // why the pairing is passed in here rather than assumed.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => snapshot_value(9, 0, 500),
                _ => json!({}),
            }
        });
        let received_before = harness.app.pending.raw_watermark();

        // The engine raises a decision while that read is in flight; this
        // client takes delivery of it before the answer comes back.
        harness.push(permission_request(62, "req-e1-62"));
        let message = harness.next_inbound().await;
        harness.app.on_inbound(message);

        tokio::time::timeout(
            NO_HANG,
            harness.app.resync_paired_at(received_before, std::time::Instant::now()),
        )
        .await
        .expect("the resync never returned");

        assert_eq!(
            harness.app.pending.keys(),
            ["req-e1-62"],
            "a decision raised while the read was in flight was discarded by it"
        );
        assert!(harness.app.state.prompt.is_some(), "and the modal was taken down with it");
        harness.settle_wire().await;
        assert!(
            client_answers(&harness.frames()).is_empty(),
            "something was answered or cancelled for a live decision: {:?}",
            harness.frames()
        );

        // The next read is issued after the frame was in hand, so it is the
        // one entitled to settle it — and it does.
        tokio::time::timeout(NO_HANG, harness.app.resync_at(std::time::Instant::now()))
            .await
            .expect("the second resync never returned");

        assert!(harness.app.pending.is_empty(), "the decision was never settled");
        assert!(harness.app.state.prompt.is_none(), "the modal stayed up");
        harness.settle_wire().await;
        assert!(
            client_answers(&harness.frames()).is_empty(),
            "the retirement answered or cancelled it after all: {:?}",
            harness.frames()
        );
    }

    /// A snapshot that lists exactly these permission handles as outstanding.
    fn snapshot_listing(handles: &[&str]) -> Value {
        let mut value = snapshot_value(9, 0, 500);
        let listed: Vec<_> = handles.iter().map(|handle| permission_dto(handle)).collect();
        value["requests"] = serde_json::to_value(listed).expect("requests");
        value
    }

    #[tokio::test]
    async fn a_snapshot_that_drops_the_open_decision_shows_the_one_behind_it() {
        // Taking the stale modal down is only half of it: the decision the
        // engine *is* still waiting for has to reach the screen, or the
        // operator is left looking at nothing while a tool call blocks.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => snapshot_listing(&["req-e1-2"]),
                _ => json!({}),
            }
        });
        let app = &mut harness.app;
        let (responder, _rx) = responder(1);
        app.pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "run_command".into(), preview: "ls".into() },
            responder,
        );
        // Announced at a seq the snapshot below is late enough to know about.
        app.pending.on_discovered(&permission_dto("req-e1-1"), Some("e1".into()), Some(3));
        app.pending.on_discovered(&question_dto("req-e1-2"), Some("e1".into()), Some(5));
        app.open_current_prompt();

        tokio::time::timeout(NO_HANG, app.resync_at(std::time::Instant::now()))
            .await
            .expect("the resync never returned");

        assert_eq!(app.pending.keys(), ["req-e1-2"]);
        assert!(
            matches!(app.state.prompt, Some(PendingPrompt::Question { .. })),
            "the decision the engine is still waiting for never reached the screen: {:?}",
            app.state.prompt
        );
        harness.settle_wire().await;
        assert!(
            client_answers(&harness.frames()).is_empty(),
            "the retired decision was answered or cancelled: {:?}",
            harness.frames()
        );
    }

    #[tokio::test]
    async fn a_snapshot_that_drops_a_queued_decision_leaves_the_open_one_alone() {
        // A background subagent's request disappearing from the engine's list
        // says nothing about the modal the operator is reading, and must not
        // close it or narrate an outcome over it.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, _| {
            match method {
                "session/getState" => snapshot_listing(&["req-e1-1"]),
                _ => json!({}),
            }
        });
        let app = &mut harness.app;
        app.pending.on_discovered(&permission_dto("req-e1-1"), Some("e1".into()), Some(4));
        app.pending.on_discovered(&question_dto("req-e1-2"), Some("e1".into()), Some(5));
        app.open_current_prompt();
        let before = app.state.transcript.len();

        tokio::time::timeout(NO_HANG, app.resync_at(std::time::Instant::now()))
            .await
            .expect("the resync never returned");

        assert_eq!(app.pending.keys(), ["req-e1-1"], "the queued entry was not retired");
        assert!(
            matches!(app.state.prompt, Some(PendingPrompt::Permission { .. })),
            "the open decision was closed by another's disappearance: {:?}",
            app.state.prompt
        );
        assert_eq!(
            app.state.transcript.len(),
            before,
            "a queued request's retirement narrated something over the open decision: {:?}",
            app.state.transcript.blocks()
        );
        harness.settle_wire().await;
        assert!(
            client_answers(&harness.frames()).is_empty(),
            "the retired queued decision was answered or cancelled: {:?}",
            harness.frames()
        );
    }

    // -- Timed teardown and startup, and commands after the engine is gone -----
    //
    // Timed on purpose. What these cover is a terminal that awaits a response
    // no engine will ever send, so "it hangs" has to be a failed assertion
    // rather than a test run that never ends.

    /// The bound every command below is awaited under.
    const NO_HANG: std::time::Duration = std::time::Duration::from_secs(5);

    async fn within<F: std::future::Future>(what: &str, future: F) -> F::Output {
        match tokio::time::timeout(NO_HANG, future).await {
            Ok(value) => value,
            Err(_) => panic!("{what} never returned: the terminal is hung"),
        }
    }

    /// An app whose connection is live and whose engine answers nothing —
    /// which is what a stopped engine's transport looks like from here until
    /// something fails the request.
    fn silent_engine() -> Harness {
        app_pump(crate::local::AccessMode::TrustedLocal, |_, _| None)
    }

    /// Parsed by the real command parser, so a test drives exactly what the
    /// composer would.
    fn invocation(line: &str) -> crate::commands::Invocation {
        crate::commands::parse(line).expect("a command line")
    }

    #[tokio::test]
    async fn a_credential_store_that_never_answers_does_not_hold_the_terminal() {
        // The preflight runs before the loop starts, so an unbounded wait on
        // a credential store — a locked keychain, a keyring daemon that is
        // not there — held the whole terminal, and the first frame with it.
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        harness.app.metadata_timeout = std::time::Duration::from_millis(150);
        harness.app.set_auth_port(crate::local::auth::AuthPort::from_factory(|_| {
            Box::pin(async {
                // Opened, and never answered.
                std::future::pending::<()>().await;
                unreachable!()
            })
        }));

        within("the startup preflight", harness.app.preflight()).await;

        assert!(
            notices(&harness.app).is_empty(),
            "a store that never answered is not evidence about this profile: {:?}",
            notices(&harness.app)
        );
    }

    #[tokio::test]
    async fn a_staged_engine_the_loop_never_swapped_in_is_stopped_gracefully() {
        // A restart stages a replacement and the loop swaps it in between
        // iterations. An exit in between — an error, a quit typed at exactly
        // that moment — left that child to `Engine`'s `Drop`, which *kills*
        // it: its stdin never closes, so it never flushes its session or
        // closes its MCP servers.
        let dir = tempfile::tempdir().expect("temp dir");
        let marker = dir.path().join("stopped-gracefully.marker");
        let command = if cfg!(windows) {
            EngineCommand::new("powershell.exe")
                .arg("-NoProfile")
                .arg("-NonInteractive")
                .arg("-Command")
                .arg(format!(
                    "$null = [Console]::In.ReadToEnd(); \
                     New-Item -ItemType File -Force -Path '{}' | Out-Null",
                    marker.display()
                ))
        } else {
            EngineCommand::new("sh")
                .arg("-c")
                .arg(format!("cat >/dev/null; touch '{}'", marker.display()))
        };
        let (engine, inbound) = Engine::spawn(command).expect("the staged child starts");
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        harness.app.restarted = Some((engine, inbound));

        within("the teardown", harness.app.finish(Ok(()), std::time::Instant::now()))
            .await
            .expect("the run's own outcome");

        assert!(harness.app.restarted.is_none(), "the staged engine was left behind");
        assert!(
            marker.exists(),
            "the staged child was killed rather than asked to stop: its stdin never closed"
        );
    }

    #[tokio::test]
    async fn commands_that_need_an_engine_return_at_once_after_a_logout() {
        // The hang this closes. `/fork`, `/rewind` and the model picker went
        // straight to the connection, so after a sign-out — engine stopped,
        // session disconnected — each awaited a response that could not
        // arrive, and the awaiting happened inline in the event loop's own
        // select arm: no keystroke, no redraw, no way out.
        let mut harness = silent_engine();
        harness.app.set_engine_connected(false);

        within("/fork after a logout", harness.app.run_command(invocation("/fork"))).await;
        within("/rewind after a logout", harness.app.run_command(invocation("/rewind 2"))).await;
        within("a model switch after a logout", harness.app.switch_model("gpt-5")).await;

        assert!(
            harness.calls.lock().expect("calls").is_empty(),
            "a disconnected session still called the engine: {:?}",
            harness.calls.lock().expect("calls")
        );
        let notices = notices(&harness.app);
        assert_eq!(
            notices.iter().filter(|n| n.contains("disconnected")).count(),
            3,
            "each refusal must say why: {notices:?}"
        );
    }

    #[tokio::test]
    async fn local_commands_still_work_while_disconnected() {
        // The other half of the same gate: only the commands that need the
        // engine are refused. A disconnected session is still a usable one.
        let mut harness = silent_engine();
        harness.app.set_engine_connected(false);

        within("/help while disconnected", harness.app.run_command(invocation("/help"))).await;
        within("/status while disconnected", harness.app.run_command(invocation("/status"))).await;

        assert_eq!(outputs(&harness.app).len(), 2, "a local command was refused too");
        assert!(harness.calls.lock().expect("calls").is_empty());
    }

    #[tokio::test]
    async fn a_disconnected_steer_keeps_the_draft_and_a_recall_keeps_the_queue() {
        // Nothing the user typed may be lost to a disconnection: the steer is
        // put back in the composer, and the queue the engine still holds is
        // left exactly as it was rather than being emptied on a guess.
        let mut harness = silent_engine();
        harness
            .app
            .apply(UiEvent::Queued { text: "queued".into(), id: Some("m1".into()) });
        harness.app.set_engine_connected(false);

        within("steering after a logout", harness.app.steer("my draft".into())).await;
        assert_eq!(harness.app.composer.text(), "my draft", "the draft was lost");

        harness.app.composer.set_text("");
        let handled =
            within("a recall after a logout", harness.app.recall_pending_into_composer()).await;

        assert!(handled, "the keypress must be consumed rather than falling through");
        assert_eq!(harness.app.state.queued.len(), 1, "the queue was emptied on a guess");
        assert!(harness.app.composer.is_empty(), "an unconfirmed recall invented a draft");
        assert!(harness.calls.lock().expect("calls").is_empty());
    }

    #[tokio::test]
    async fn a_command_the_engine_never_answers_gives_the_terminal_back() {
        // Connected, and silent. The gate above cannot help here — there *is*
        // an engine — so the bound is what returns the terminal to the user.
        let mut harness = silent_engine();
        harness.app.metadata_timeout = std::time::Duration::from_millis(150);

        within("/fork against a silent engine", harness.app.run_command(invocation("/fork"))).await;
        within(
            "a model switch against a silent engine",
            harness.app.switch_model("gpt-5"),
        )
        .await;

        let notices = notices(&harness.app);
        assert_eq!(
            notices.iter().filter(|n| n.contains("did not answer")).count(),
            2,
            "a bounded read must say the engine went quiet: {notices:?}"
        );
    }
}

// ---------------------------------------------------------------------------
// Engine-owned settings, and who may write them
// ---------------------------------------------------------------------------

#[cfg(test)]
mod settings_gate_tests {
    use super::tests::{app_with, notices, outputs, Harness};
    use crate::commands::Invocation;
    use crate::config::Paths;
    use crate::local::AccessMode;
    use serde_json::{json, Value};

    /// Points an app at a throwaway settings tree, so "was anything written?"
    /// is a real question about a real file.
    fn sandboxed(mode: AccessMode, answer: impl Fn(&str, &Value) -> Value + Send + 'static)
        -> (Harness, tempfile::TempDir) {
        let dir = tempfile::tempdir().expect("temp dir");
        let mut harness = app_with(mode, answer);
        harness.app.paths = Paths {
            user_root: dir.path().to_path_buf(),
            project_root: dir.path().to_path_buf(),
        };
        // The credential store too, not just the settings: a test must never
        // be able to read — let alone write — the profile of the machine
        // running it.
        harness
            .app
            .set_auth_port(crate::local::auth::AuthPort::isolated(dir.path(), Vec::new()));
        (harness, dir)
    }

    fn ok(_method: &str, _params: &Value) -> Value {
        json!({ "ok": true })
    }

    /// Parsed by the real command parser, so a test drives exactly what the
    /// composer would.
    fn invocation(line: &str) -> Invocation {
        crate::commands::parse(line).expect("a command line")
    }

    /// The banner exactly as it was seeded into the transcript.
    fn banner_text(app: &crate::app::App) -> String {
        app.state()
            .transcript
            .blocks()
            .iter()
            .find_map(|block| match block {
                crate::transcript::Block::Banner { details, .. } => Some(details.join("\n")),
                _ => None,
            })
            .expect("the banner was seeded")
    }

    #[tokio::test]
    async fn an_api_only_banner_names_the_engines_provider_and_not_this_machines() {
        // The banner exists to stop you spending money with the wrong
        // account. Reading this machine's `settings.json` for a session whose
        // engine runs elsewhere names an account the session is not using.
        let (mut harness, _dir) = sandboxed(AccessMode::ApiOnly, ok);
        std::fs::write(
            harness.app.paths.settings(),
            json!({
                "defaultProvider": "a-local-only-account",
                "modelByProvider": { "a-local-only-account": "a-local-only-model" },
            })
            .to_string(),
        )
        .expect("seed local settings");
        harness.app.connected_provider = Some("github-copilot".into());
        harness.app.apply(crate::state::UiEvent::ModelChanged {
            id: "claude-opus-5".into(),
            context_limit: None,
        });

        harness.app.push_banner("/w");

        let banner = banner_text(&harness.app);
        assert!(banner.contains("github-copilot"), "{banner}");
        assert!(banner.contains("claude-opus-5"), "{banner}");
        assert!(
            !banner.contains("a-local-only-account") && !banner.contains("a-local-only-model"),
            "this machine's defaults were reported as the session's: {banner}"
        );
        assert!(banner.contains("engine host"), "{banner}");
    }

    #[tokio::test]
    async fn an_api_only_banner_never_says_nobody_is_signed_in() {
        // No credential on *this* machine says nothing at all about the
        // engine's, and `/login` here would write a file the engine never
        // reads.
        let (mut harness, _dir) = sandboxed(AccessMode::ApiOnly, ok);
        harness.app.push_banner("/w");

        let banner = banner_text(&harness.app);
        assert!(!banner.contains("/login"), "{banner}");
        assert!(banner.contains("engine host"), "{banner}");
    }

    #[tokio::test]
    async fn a_local_banner_still_reports_this_machines_defaults() {
        // The shipping behaviour: `coda` starts its own core, so this
        // machine's settings really are the ones the engine reads.
        let (mut harness, _dir) = sandboxed(AccessMode::TrustedLocal, ok);
        std::fs::write(
            harness.app.paths.settings(),
            json!({
                "defaultProvider": "anthropic-api-key",
                "modelByProvider": { "anthropic-api-key": "claude-sonnet-5" },
            })
            .to_string(),
        )
        .expect("seed local settings");

        harness.app.push_banner("/w");

        let banner = banner_text(&harness.app);
        assert!(banner.contains("anthropic-api-key"), "{banner}");
        assert!(banner.contains("claude-sonnet-5"), "{banner}");
        assert!(!banner.contains("engine host"), "a local session owns its own settings: {banner}");
    }

    #[tokio::test]
    async fn an_api_only_exit_summary_reports_the_api_values_not_the_local_file() {
        let (mut harness, _dir) = sandboxed(AccessMode::ApiOnly, ok);
        std::fs::write(
            harness.app.paths.settings(),
            json!({ "defaultProvider": "a-local-only-account" }).to_string(),
        )
        .expect("seed local settings");
        harness.app.connected_provider = Some("github-copilot".into());

        let summary = harness.app.exit_summary(std::time::Duration::from_secs(1));
        let printed = crate::branding::exit_lines(&summary).join("\n");

        assert!(printed.contains("github-copilot"), "{printed}");
        assert!(!printed.contains("a-local-only-account"), "{printed}");
        assert!(printed.contains("engine host"), "{printed}");
    }

    #[tokio::test]
    async fn an_api_only_exit_summary_says_it_was_never_told_rather_than_none() {
        let (harness, _dir) = sandboxed(AccessMode::ApiOnly, ok);
        let summary = harness.app.exit_summary(std::time::Duration::from_secs(1));
        let printed = crate::branding::exit_lines(&summary).join("\n");
        assert!(printed.contains("engine host"), "{printed}");
    }

    #[tokio::test]
    async fn the_permission_mode_is_re_read_after_it_changes_in_the_same_session() {
        // The catalogue was fetched once and cached for the life of the app,
        // so `/permissions` after `/yolo` reported the mode from before the
        // change — with the engine's own authority behind it.
        let mode = std::sync::Arc::new(std::sync::Mutex::new("default".to_string()));
        let engine_mode = std::sync::Arc::clone(&mode);
        let (mut harness, _dir) = sandboxed(AccessMode::ApiOnly, move |method, params| {
            match method {
                "config/describe" => json!({
                    "entries": [{
                        "key": "permissionMode",
                        "owner": "session",
                        "appliesAt": "nextPermissionCheck",
                        "mutable": true,
                        "value": *engine_mode.lock().expect("mode poisoned"),
                        "description": "How tool permissions are decided.",
                    }]
                }),
                "session/setPermissionMode" => {
                    // The engine really does change: whatever the client asks
                    // for is what it reports from now on.
                    if let Some(asked) = params.get("mode").and_then(Value::as_str) {
                        *engine_mode.lock().expect("mode poisoned") = asked.to_string();
                    }
                    json!({ "ok": true })
                }
                _ => json!({ "ok": true }),
            }
        });

        harness.app.run_command(invocation("/permissions")).await;
        assert!(
            outputs(&harness.app).join("\n").contains("Permission mode: default"),
            "{:?}",
            outputs(&harness.app)
        );

        harness.app.run_command(invocation("/permissions bypass")).await;
        harness.app.run_command(invocation("/permissions")).await;

        let reported = outputs(&harness.app);
        let last = reported.last().expect("a second report");
        assert!(
            last.contains("Permission mode: bypass"),
            "a cached catalogue reported the mode from before the change: {reported:?}"
        );
    }

    #[tokio::test]
    async fn an_engine_reported_config_change_invalidates_the_cached_catalogue() {
        // The change need not come from this client at all: another client,
        // or the engine itself, can move it and say so with
        // `event/configChanged`.
        let (mut harness, _dir) = sandboxed(AccessMode::ApiOnly, |method, _| match method {
            "config/describe" => json!({
                "entries": [{
                    "key": "permissionMode",
                    "owner": "session",
                    "appliesAt": "nextPermissionCheck",
                    "mutable": true,
                    "value": "plan",
                    "description": "How tool permissions are decided.",
                }]
            }),
            _ => json!({ "ok": true }),
        });
        harness.app.run_command(invocation("/permissions")).await;
        assert!(harness.app.config_catalog.is_some(), "the catalogue is cached to begin with");

        harness
            .app
            .apply_state_frame(crate::api::StateFrame::ConfigChanged { active: None, next: None });

        assert!(
            harness.app.config_catalog.is_none(),
            "a config change left a stale catalogue in place"
        );
    }

    #[tokio::test]
    async fn a_replaced_conversation_invalidates_the_cached_catalogue() {
        let (mut harness, _dir) = sandboxed(AccessMode::ApiOnly, |method, _| match method {
            "config/describe" => json!({ "entries": [] }),
            _ => json!({ "ok": true }),
        });
        harness.app.config_catalog =
            Some(coda_proto::config::ConfigDescribeResult { entries: Vec::new() });

        harness.app.apply_state_frame(crate::api::StateFrame::SessionChanged {
            reason: "resume".into(),
            session_id: "s2".into(),
            history_epoch: 1,
            history_length: 0,
        });

        assert!(
            harness.app.config_catalog.is_none(),
            "a different session's configuration is not this one's"
        );
    }

    #[tokio::test]
    async fn a_model_switch_that_could_not_be_saved_still_shows_the_model_that_changed() {
        // The session change already succeeded over the API. Returning early
        // on a *persistence* failure skipped closing the browser and
        // re-reading the active model, so the header and the picker kept
        // showing the previous one — as though nothing had happened.
        let dir = tempfile::tempdir().expect("temp dir");
        let mut harness = app_with(AccessMode::TrustedLocal, |method, _| match method {
            "session/setModel" => json!({ "ok": true }),
            "session/models" => json!({
                "models": [{ "id": "claude-opus-5", "displayName": "Opus 5" }],
                "providerId": "github-copilot",
                "activeModel": "claude-opus-5",
            }),
            _ => json!({ "ok": true }),
        });
        // A settings root whose parent is a *file*: the write cannot succeed,
        // and it fails the way a read-only or full disk fails.
        let blocker = dir.path().join("not-a-directory");
        std::fs::write(&blocker, b"x").expect("seed the blocker");
        harness.app.paths = Paths {
            user_root: blocker.join("nested"),
            project_root: dir.path().to_path_buf(),
        };
        harness.app.connected_provider = Some("github-copilot".into());
        harness
            .app
            .surfaces
            .push(Box::new(crate::surface::browser::BrowserSurface::new(
                crate::surface::browser::BrowserKind::Models,
                crate::browsers::models(&[], None, "live"),
            )));

        harness.app.switch_model("claude-opus-5").await;

        assert!(
            harness.calls.lock().expect("calls").iter().any(|m| m == "session/models"),
            "the active model was never re-read, so the header still shows the old one"
        );
        assert!(
            harness.app.surfaces.is_empty(),
            "the model browser was left open on a list that no longer describes the session"
        );
        let notices = notices(&harness.app);
        assert!(
            notices.iter().any(|n| n.contains("claude-opus-5")),
            "the change that did happen must be reported: {notices:?}"
        );
        assert!(
            notices.iter().any(|n| n.contains("save")),
            "the failure to persist it must be reported separately: {notices:?}"
        );
    }

    #[tokio::test]
    async fn a_model_switch_without_readable_provider_defaults_still_refreshes_the_ui() {
        for malformed in [false, true] {
            let dir = tempfile::tempdir().unwrap();
            if malformed {
                std::fs::write(dir.path().join("settings.json"), "{invalid").unwrap();
            }
            let mut harness = app_with(AccessMode::TrustedLocal, |method, _| match method {
                "session/setModel" => json!({ "ok": true }),
                "session/models" => json!({
                    "source": "live",
                    "models": [{ "id": "new-model", "displayName": "New Model" }],
                    "model": "new-model", "providerId": "github-copilot",
                }),
                _ => json!({}),
            });
            harness.app.paths = Paths {
                user_root: dir.path().to_path_buf(),
                project_root: dir.path().to_path_buf(),
            };
            harness.app.connected_provider = None;
            harness.app.surfaces.push(Box::new(
                crate::surface::browser::BrowserSurface::new(
                    crate::surface::browser::BrowserKind::Models,
                    crate::browsers::models(&[], None, "live"),
                ),
            ));
            harness.app.switch_model("new-model").await;
            assert!(harness.app.surfaces.is_empty(), "malformed={malformed}");
            assert_eq!(harness.app.state.model.as_deref(), Some("New Model"), "malformed={malformed}");
            assert!(notices(&harness.app).iter().any(|text| text.contains("not saved")));
        }
    }

    #[tokio::test]
    async fn a_refused_or_unconfirmed_model_change_is_never_persisted() {
        for (reply, unknown) in [
            (json!({ "ok": false, "note": "model is unavailable" }), false),
            (json!({}), true),
        ] {
            let dir = tempfile::tempdir().unwrap();
            let mut harness = app_with(AccessMode::TrustedLocal, move |method, _| match method {
                "session/setModel" => reply.clone(),
                _ => json!({}),
            });
            harness.app.paths = Paths {
                user_root: dir.path().to_path_buf(), project_root: dir.path().to_path_buf(),
            };
            harness.app.connected_provider = Some("github-copilot".into());
            harness.app.state.model = Some("old-model".into());
            harness.app.needs_resync = false;
            harness.app.switch_model("new-model").await;
            assert!(!dir.path().join("settings.json").exists(), "a JSON-RPC reply is not an operation acknowledgement");
            assert_eq!(harness.app.state.model.as_deref(), Some("old-model"));
            assert_eq!(harness.app.needs_resync, unknown);
            let visible = notices(&harness.app);
            assert!(!visible.iter().any(|notice| notice.contains("Model set to")));
            assert!(visible.iter().any(|notice| notice.contains(if unknown { "unknown" } else { "refused" })));
        }
    }

    #[tokio::test]
    async fn an_api_only_session_never_reads_this_machines_credentials_to_refuse() {
        // The refusal has to come *before* the store is opened. A message-only
        // assertion would pass while an API-only session probed the operator's
        // own credentials for an engine that has no business knowing about
        // them.
        let (mut harness, dir) = sandboxed(AccessMode::ApiOnly, ok);
        for line in ["/provider claude-ai", "/login copilot", "/logout", "/setup"] {
            harness.app.run_command(invocation(line)).await;
        }

        assert_eq!(
            harness.app.auth_port().opens(),
            0,
            "an API-only session opened this machine's credential store"
        );
        assert!(
            !dir.path().join("settings.json").exists(),
            "an API-only client wrote engine-owned settings"
        );
        assert!(harness.app.surfaces.is_empty(), "a sign-in surface opened anyway");
        let notices = notices(&harness.app);
        assert!(
            notices.iter().any(|n| n.contains("engine host") && n.contains("coda auth login")),
            "the refusal must name the command to run there instead: {notices:?}"
        );
    }

    #[tokio::test]
    async fn a_local_provider_switch_opens_the_sign_in_rather_than_writing_a_default_behind_it() {
        // Writing `defaultProvider` and restarting — which is what this used
        // to do — changes nothing about which *credential* the engine can
        // find, so a switch to an account with nothing stored silently came
        // back on the old one.
        let (mut harness, dir) = sandboxed(AccessMode::TrustedLocal, ok);
        let mut events = harness.app.auth.take_events();
        harness.app.run_command(invocation("/provider claude-ai")).await;

        // The screen is claimed before the store is touched, and the form
        // arrives when the read answers — exactly as the loop pumps it.
        assert!(
            harness.app.surfaces.top().is_some(),
            "the command did not claim the screen before reading anything"
        );
        let event = tokio::time::timeout(std::time::Duration::from_secs(10), events.recv())
            .await
            .expect("the profile was opened")
            .expect("the flow is still running");
        harness.app.on_auth_event(event).await;

        assert_eq!(harness.app.auth_port().opens(), 1, "the profile is opened exactly once");
        assert!(
            !dir.path().join("settings.json").exists(),
            "a provider was saved before any credential existed for it"
        );
        let surface = harness.app.surfaces.top().expect("the sign-in surface is open");
        assert!(
            surface.as_any().is::<crate::surface::auth::AuthChoiceSurface>(),
            "the switch did not open the sign-in"
        );
        assert_eq!(
            surface.modality(),
            crate::surface::Modality::Exclusive,
            "a live sign-in must not be pushed under another surface"
        );
    }

    #[tokio::test]
    async fn an_unknown_provider_is_refused_where_it_was_typed() {
        let (mut harness, _dir) = sandboxed(AccessMode::TrustedLocal, ok);
        harness.app.run_command(invocation("/login openai")).await;
        assert!(harness.app.surfaces.is_empty(), "an unknown name opened a sign-in anyway");
        let notices = notices(&harness.app);
        assert!(
            notices.iter().any(|n| n.contains("not one of this product's providers")),
            "{notices:?}"
        );
    }

    #[tokio::test]
    async fn a_running_turn_is_never_interrupted_to_change_the_connection() {
        let (mut harness, _dir) = sandboxed(AccessMode::TrustedLocal, ok);
        harness.app.state.apply(crate::state::UiEvent::Submitted { text: "work".into() });
        assert!(harness.app.state.is_busy(), "the fixture must actually be busy");

        harness.app.run_command(invocation("/login claude-ai")).await;
        assert!(harness.app.surfaces.is_empty(), "a sign-in started during a turn");
        assert!(
            harness.app.state.is_busy(),
            "the turn was implicitly cancelled to make room for a sign-in"
        );
        let notices = notices(&harness.app);
        assert!(notices.iter().any(|n| n.contains("A turn is running")), "{notices:?}");
    }

    #[tokio::test]
    async fn an_api_only_session_applies_a_permission_mode_without_claiming_to_save_it() {
        // The session setter is a plain RPC and works perfectly well against
        // a remote engine. Only the persistence is impossible.
        let (mut harness, dir) = sandboxed(AccessMode::ApiOnly, ok);
        harness.app.run_command(invocation("/permissions plan")).await;

        assert!(
            harness.calls.lock().expect("calls").iter().any(|m| m == "session/setPermissionMode"),
            "the session change itself must still be made"
        );
        assert!(
            !dir.path().join("settings.json").exists(),
            "an API-only client wrote an engine-owned default"
        );
    }

    #[tokio::test]
    async fn a_local_session_still_saves_the_permission_mode_for_the_next_start() {
        let (mut harness, dir) = sandboxed(AccessMode::TrustedLocal, ok);
        harness.app.run_command(invocation("/permissions plan")).await;
        let written = std::fs::read_to_string(dir.path().join("settings.json"))
            .expect("the local session saves its default");
        assert!(written.contains("plan"), "{written}");
    }

    #[tokio::test]
    async fn an_api_only_session_switches_the_model_without_writing_a_default() {
        let (mut harness, dir) = sandboxed(AccessMode::ApiOnly, ok);
        // The provider is known, so the only thing standing between this and
        // a settings write is the gate itself.
        harness.app.connected_provider = Some("github-copilot".into());
        harness.app.switch_model("claude-opus-5").await;

        assert!(
            harness.calls.lock().expect("calls").iter().any(|m| m == "session/setModel"),
            "the session change itself must still be made"
        );
        assert!(
            !dir.path().join("settings.json").exists(),
            "an API-only client wrote an engine-owned model default"
        );
    }

    #[tokio::test]
    async fn a_local_session_still_saves_the_model_for_its_provider() {
        let (mut harness, dir) = sandboxed(AccessMode::TrustedLocal, ok);
        harness.app.connected_provider = Some("github-copilot".into());
        harness.app.switch_model("claude-opus-5").await;
        let written = std::fs::read_to_string(dir.path().join("settings.json"))
            .expect("the local session saves its default");
        assert!(written.contains("claude-opus-5"), "{written}");
    }

    #[tokio::test]
    async fn an_api_only_session_refuses_header_edits_before_touching_the_filesystem() {
        let (mut harness, dir) = sandboxed(AccessMode::ApiOnly, ok);
        harness.app.run_command(invocation("/headers --set X-Trace on")).await;
        assert!(!dir.path().join("settings.json").exists(), "custom headers are the engine's");
    }

    #[tokio::test]
    async fn an_api_only_session_still_reports_its_own_diagnostics_but_writes_no_telemetry() {
        // The front-end's own log is the client's; the legacy telemetry
        // settings are read by the engine's host, which is not this machine.
        let (mut harness, dir) = sandboxed(AccessMode::ApiOnly, ok);
        harness.app.run_command(invocation("/log")).await;
        let shown = outputs(&harness.app).join("\n");
        assert!(!shown.is_empty(), "the client's own diagnostics must still be reported");

        harness.app.run_command(invocation("/log debug")).await;
        assert!(
            !dir.path().join("settings.json").exists(),
            "an API-only client wrote engine-owned telemetry settings"
        );
    }

    #[tokio::test]
    async fn setting_an_output_style_does_not_claim_a_restart_will_apply_it() {
        // `config/describe` reports `outputStyle` as `clientLocal` and *not*
        // mutable: this engine does not apply one. Telling the user to
        // restart the engine promises a behaviour change that never happens.
        let (mut harness, dir) = sandboxed(AccessMode::TrustedLocal, |method, _| match method {
            "config/describe" => json!({
                "entries": [{
                    "key": "outputStyle",
                    "owner": "clientLocal",
                    "appliesAt": "clientLocal",
                    "mutable": false,
                    "reason": "this engine does not apply an output style",
                    "allowedValues": [{ "value": "concise" }, { "value": "default" }],
                    "description": "Response style persona.",
                }]
            }),
            _ => json!({}),
        });
        harness.app.run_command(invocation("/output-style concise")).await;

        let shown = outputs(&harness.app).join("\n") + &notices(&harness.app).join("\n");
        assert!(
            !shown.contains("Restart the engine to apply"),
            "a promise the engine contradicts: {shown}"
        );
        assert!(
            shown.contains("does not apply"),
            "the client must say what actually happens: {shown}"
        );
        // Still a real client-local setting, and still written.
        let written = std::fs::read_to_string(dir.path().join("settings.json"))
            .expect("the client's own setting is saved");
        assert!(written.contains("concise"), "{written}");
    }

    #[tokio::test]
    async fn reporting_the_permission_mode_asks_the_engine_rather_than_this_machines_file() {
        // The engine owns the live mode; `settings.json` is only a startup
        // default, and on an API-only session it is not even the engine's
        // startup default. Reporting the local file as "the" mode described a
        // different machine's configuration as this session's.
        let (mut harness, _dir) = sandboxed(AccessMode::ApiOnly, |method, _| match method {
            "config/describe" => json!({
                "entries": [{
                    "key": "permissionMode",
                    "owner": "session",
                    "appliesAt": "nextPermissionCheck",
                    "mutable": true,
                    "value": "plan",
                    "description": "How tool permissions are decided.",
                }]
            }),
            _ => json!({ "ok": true }),
        });
        // A local file that says something else entirely: if it is consulted,
        // this is what would be shown.
        std::fs::write(
            harness.app.paths.settings(),
            json!({ "permissionMode": "bypass" }).to_string(),
        )
        .expect("seed local settings");

        harness.app.run_command(invocation("/permissions")).await;

        let shown = outputs(&harness.app).join("\n");
        let reported = shown.lines().next().unwrap_or_default();
        assert_eq!(
            reported, "Permission mode: plan",
            "the engine's live mode must be what is reported, not this machine's file"
        );
    }

    #[tokio::test]
    async fn the_settings_form_saves_this_clients_own_values_and_not_the_engines() {
        // One form, two owners: the theme and tool display are this client's,
        // while the permission mode and telemetry are read by the engine at
        // its own startup, on its own host. Saving all four here reported a
        // durable change for two of them that the engine never sees.
        let (mut harness, dir) = sandboxed(AccessMode::ApiOnly, ok);
        let settings = crate::config::Settings::empty_at(harness.app.paths.settings());
        harness
            .app
            .surfaces
            .push(Box::new(crate::surface::settings::SettingsSurface::new(&settings)));
        harness
            .app
            .apply_surface_action(crate::surface::SurfaceAction::SaveSettings)
            .await;

        let written = std::fs::read_to_string(dir.path().join("settings.json"))
            .expect("the client's own settings are still saved");
        assert!(written.contains("theme"), "client-local values must still save: {written}");
        assert!(
            !written.contains("permissionMode"),
            "an API-only client wrote an engine-owned value: {written}"
        );
        assert!(
            !written.contains("telemetry"),
            "an API-only client wrote engine-owned telemetry settings: {written}"
        );
        let notices = notices(&harness.app);
        assert!(
            notices.iter().any(|n| n.contains("engine")),
            "the user must be told which half was not saved: {notices:?}"
        );
    }

    #[tokio::test]
    async fn a_local_settings_form_still_saves_every_value_it_always_did() {
        let (mut harness, dir) = sandboxed(AccessMode::TrustedLocal, ok);
        let settings = crate::config::Settings::empty_at(harness.app.paths.settings());
        harness
            .app
            .surfaces
            .push(Box::new(crate::surface::settings::SettingsSurface::new(&settings)));
        harness
            .app
            .apply_surface_action(crate::surface::SurfaceAction::SaveSettings)
            .await;

        let written = std::fs::read_to_string(dir.path().join("settings.json"))
            .expect("settings are saved");
        assert!(written.contains("permissionMode"), "{written}");
        assert!(written.contains("theme"), "{written}");
    }

    #[tokio::test]
    async fn an_api_only_session_sets_effort_for_the_session_without_saving_it() {
        let (mut harness, dir) = sandboxed(AccessMode::ApiOnly, |method, _| match method {
            "session/setEffort" => json!({ "ok": true, "current": "high" }),
            _ => json!({ "ok": true }),
        });
        harness
            .app
            .apply_set_effort("high".into(), true, ("github-copilot".into(), "gpt-5".into()))
            .await;

        assert!(
            harness.calls.lock().expect("calls").iter().any(|m| m == "session/setEffort"),
            "the session change itself must still be made"
        );
        assert!(
            !dir.path().join("settings.json").exists(),
            "an API-only client wrote an engine-owned effort preference"
        );
    }
}
