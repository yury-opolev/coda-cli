//! `EngineState` — the single authority for everything in `StateSnapshot`
//! (§3 of the serve API implementation plan).
//!
//! `EngineState` is **derived** state: written only from RPC handlers and the
//! agent-event sink path (see `crate::state_sink`). `coda-agent` never reads
//! it and never branches on it.
//!
//! # One transaction per observed event
//!
//! Every mutation goes through [`EngineState::transact`]: the closure mutates
//! a copy-on-write `StateInner` and returns the `(method, params, gated)`
//! triples to publish; `transact` swaps the `Arc` in and publishes **while
//! still holding STATE**, so a snapshot's `cursor` is always exactly the last
//! event this state reflects.
//!
//! Critically, that set of triples includes the **legacy wire event** for the
//! same observation, not just the new gated ones (C1). Publishing the state
//! transition under the lock and the legacy `event/*` frame after it meant a
//! snapshot at cursor `N` already contained text whose own event was still
//! going to be published at `N+1` — a client that applied everything after
//! `N` duplicated it. There is now exactly one transaction per `AgentEvent`,
//! carrying the view transition and every publication it implies.
//!
//! # CoW and lock order
//!
//! `inner: Mutex<Arc<StateInner>>` mutated through `Arc::make_mut`, so an
//! update only clones when a reader is still holding a snapshot (I2). All
//! bounded, `Arc`-shared data: the live-turn accumulator keeps text as
//! `Arc<str>` chunks, so even a real clone copies pointers, never the
//! accumulated bytes, and the wire `Vec<HistoryEntry>` is materialised once
//! per snapshot **outside** the lock — never cached back into state.
//!
//! Global lock order: `TURN → INBOX → STATE → BUS`. This module is the STATE
//! link and takes BUS itself, in that order, from inside `transact`. No
//! `.await` anywhere on this path.

pub mod live;
pub mod requests;
pub mod steering_observer;

pub use steering_observer::SteeringStateObserver;

use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use chrono::Utc;
use serde_json::Value;

use coda_proto::messages::CapabilityEntry;
use coda_proto::state_events as wire;
use coda_proto::state::{
    ActiveConfig, ActivityPhase, ConcurrentCounters, EffectiveConfig, EngineLifecycle,
    LastTurnOutcome, Limits, ModelRequestState, PendingRequestDto, PendingRequestKind,
    StateSnapshot, SteeringOutcomeDto, SteeringOutcomeKind, SteeringPendingDto, SteeringQueueState,
    ToolBatchRef, ToolCallState, ToolCallStateStatus, ToolsState, TurnErrorSummary, TurnState,
    UsageState, config_differences,
};

use crate::bus::EventBus;
use live::LiveTurnAccumulator;

pub const DEFAULT_OUTCOMES_RETAINED: usize = 200;
pub const DEFAULT_RECENT_TOOLS_RETAINED: usize = 100;
pub const DEFAULT_HISTORY_BLOCK_BYTES_CAP: i64 = 64 * 1024;
/// Cap on the steering `pending[].text` (§2.3a): full original text is kept
/// up to this size; beyond it, `textTruncated` is set rather than silently
/// mangling a user's draft.
pub const DEFAULT_STEERING_TEXT_CAP: usize = 64 * 1024;

fn now_rfc3339() -> String {
    Utc::now().to_rfc3339()
}

fn is_terminal(status: ToolCallStateStatus) -> bool {
    matches!(
        status,
        ToolCallStateStatus::Completed
            | ToolCallStateStatus::Failed
            | ToolCallStateStatus::Cancelled
            | ToolCallStateStatus::Skipped
    )
}

/// One `(method, params, gated)` event queued for publication by `transact`.
pub(crate) type PendingEvent = (String, Value, bool);

/// A consistent read of the live session's history fence, epoch, cursor and
/// in-flight turn — see [`EngineState::history_view`].
pub struct HistoryView {
    pub engine_instance_id: String,
    pub session_id: String,
    pub history_epoch: i64,
    /// The committed/live fence at the instant of this read.
    pub history_length: i64,
    pub cursor: i64,
    pub live: Option<LiveView>,
}

pub struct LiveView {
    pub entries: Vec<coda_proto::history::HistoryEntry>,
    pub truncated: bool,
    pub omitted_bytes: i64,
}

/// Convenience for a gated state event.
fn gated(method: &str, params: Value) -> PendingEvent {
    (method.to_string(), params, true)
}

/// How a turn finished, plus the two things that must become visible in the
/// *same* transaction as the live-turn reset (I1 / C1).
#[derive(Debug, Clone, Default)]
pub struct TurnEnd {
    pub turn_id: String,
    pub stop_reason: Option<String>,
    pub interrupted: bool,
    /// Safe classification only — never a raw provider or `Debug` string
    /// (S3): `lastTurnOutcome` is polled for the rest of the session.
    pub error: Option<TurnErrorSummary>,
    /// Committed conversation length at the exact instant the live turn is
    /// cleared. `None` leaves the fence unchanged (nothing was committed).
    pub history_length: Option<i64>,
    /// A legacy wire notification (typically `event/turnComplete`) published
    /// inside the same transaction, so no client can observe a state that
    /// disagrees with the events it has received.
    pub wire: Option<(String, Value)>,
}

/// Per-turn tracking. Deliberately **not** a cached wire `TurnState`: the
/// live entries are projected from the accumulator at snapshot time (I2).
#[derive(Clone)]
struct TurnTracking {
    turn_id: String,
    started_at: String,
    /// The monotonic counterpart of `started_at`. A remote client cannot
    /// measure duration from a UTC string on a clock it does not share, so
    /// the snapshot publishes the server's own elapsed time, computed from
    /// this at projection instead of being cached (it would be stale the
    /// moment it was stored).
    started_instant: Instant,
    phase: ActivityPhase,
    phase_since: String,
    /// The monotonic counterpart of `phase_since`. See `started_instant`.
    phase_since_instant: Instant,
    model_request: Option<ModelRequestState>,
    batches: Vec<ToolBatchRef>,
    active_config: ActiveConfig,
    concurrent: ConcurrentCounters,
    live: LiveTurnAccumulator,
    /// The phase the turn was in when it first started waiting on an
    /// operator. Restored when nothing is pending any more, so a question in
    /// the middle of a tool batch returns to `runningTools` rather than
    /// inventing a phase.
    phase_before_awaiting: Option<ActivityPhase>,
    /// Keyed by `(batch_id, call_id)` — a provider may reuse a `call_id`
    /// across batches (I4), so the call id alone is not an identity.
    tool_call_started_at: HashMap<(String, String), Instant>,
}

#[derive(Clone)]
pub(crate) struct StateInner {
    engine_instance_id: String,
    /// Test-only witness that counts how many times this state was actually
    /// copied, so the copy-on-write property can be asserted directly rather
    /// than inferred from an allocation address the allocator is free to
    /// reuse.
    #[cfg(test)]
    clone_count: CloneCounter,
    session_id: String,
    workspace_path: String,
    history_epoch: i64,
    history_length: i64,
    lifecycle: EngineLifecycle,
    initialized: bool,
    last_turn_outcome: Option<LastTurnOutcome>,
    turn: Option<TurnTracking>,
    steering_pending: Vec<SteeringPendingDto>,
    steering_outcomes: VecDeque<SteeringOutcomeDto>,
    steering_outcomes_evicted: bool,
    outcomes_retained_limit: usize,
    tools_recent: VecDeque<ToolCallState>,
    tools_evicted: bool,
    recent_tools_limit: usize,
    /// Mirror of the pending reverse-request registry.
    ///
    /// The registry (`state/requests.rs`) owns the one-shot senders and the
    /// exactly-once resolution; STATE only mirrors the *projection* so
    /// `session/getState` never has to take the REQUESTS lock and the
    /// documented order (`REQUESTS -> STATE -> BUS`) cannot invert.
    requests: Vec<PendingRequestDto>,
    usage: UsageStateInner,
    capabilities: HashMap<String, CapabilityEntry>,
    /// The configuration the **next** turn would run under.
    ///
    /// STATE owns it rather than the host, so a snapshot cannot pair it with
    /// a cursor that already covers the event correcting it: the commit that
    /// changes it and the `event/configChanged` announcing it are one
    /// transaction, and the projection reads it from the very `StateInner` it
    /// took the cursor with. A caller-supplied value would reintroduce
    /// exactly the read-then-project window this exists to close.
    next_config: ActiveConfig,
}

#[derive(Clone, Default)]
struct UsageStateInner {
    last_response: Option<(i64, i64)>,
    session_total: Option<(i64, i64)>,
}

/// Counts real `StateInner` copies, per `EngineState` instance so parallel
/// tests cannot interfere with each other.
#[cfg(test)]
#[derive(Default)]
struct CloneCounter(Arc<std::sync::atomic::AtomicUsize>);

#[cfg(test)]
impl Clone for CloneCounter {
    fn clone(&self) -> Self {
        self.0.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        CloneCounter(Arc::clone(&self.0))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// StateInner transitions
//
// These are the transitions the agent-event sink composes into a single
// transaction alongside the legacy wire publication for the same event
// (`crate::state_sink`). They never touch the bus themselves; they only
// return what must be published.
// ─────────────────────────────────────────────────────────────────────────────

impl StateInner {
    pub(crate) fn current_turn_id(&self) -> Option<&str> {
        self.turn.as_ref().map(|t| t.turn_id.as_str())
    }

    /// Whether `request` is one the **running turn** is waiting on.
    ///
    /// A request with no `turnId` came from execution that belongs to no turn
    /// (a background subagent, a scheduled run). One with a *different*
    /// `turnId` came from a turn that has since ended. Neither is this turn
    /// waiting on the operator, and neither may move its public phase.
    fn request_belongs_to_running_turn(&self, request: &PendingRequestDto) -> bool {
        match (request.turn_id.as_deref(), self.current_turn_id()) {
            (Some(request_turn), Some(running)) => request_turn == running,
            _ => false,
        }
    }

    fn set_phase(&mut self, phase: ActivityPhase) -> Vec<PendingEvent> {
        let Some(t) = self.turn.as_mut() else { return Vec::new() };
        if t.phase == phase {
            return Vec::new();
        }
        t.phase = phase;
        t.phase_since = now_rfc3339();
        t.phase_since_instant = Instant::now();
        self.activity_event()
    }

    fn activity_event(&self) -> Vec<PendingEvent> {
        let Some(t) = self.turn.as_ref() else { return Vec::new() };
        vec![gated(
            coda_proto::events::event_method::ACTIVITY,
            serde_json::json!(wire::ActivityEvent {
                turn_id: t.turn_id.clone(),
                phase: t.phase,
                phase_since: t.phase_since.clone(),
            }),
        )]
    }

    pub(crate) fn model_request_started(&mut self, request_id: String) -> Vec<PendingEvent> {
        let now = now_rfc3339();
        if let Some(t) = self.turn.as_mut() {
            t.model_request =
                Some(ModelRequestState { request_id, started_at: now, observed_reasoning: false });
        }
        self.set_phase(ActivityPhase::WaitingForModel)
    }

    /// A `Thinking` delta.
    ///
    /// C5: `ThinkingStarted` reaches the sink as a `Thinking` event with an
    /// **empty** delta. That is the only observable moment reasoning begins
    /// for a provider that encrypts its reasoning, so it must move the phase
    /// to `reasoning` immediately even though it carries no text. Only a real
    /// `Thinking` event may do this — silence never infers reasoning.
    pub(crate) fn observed_thinking_delta(&mut self, delta: &str) -> Vec<PendingEvent> {
        if let Some(t) = self.turn.as_mut() {
            t.live.push_thinking_delta(delta);
            if let Some(mr) = t.model_request.as_mut() {
                mr.observed_reasoning = true;
            }
        }
        self.set_phase(ActivityPhase::Reasoning)
    }

    pub(crate) fn thinking_complete(&mut self) -> Vec<PendingEvent> {
        if let Some(t) = self.turn.as_mut() {
            t.live.flush_thinking();
        }
        Vec::new()
    }

    pub(crate) fn observed_text_delta(&mut self, delta: &str) -> Vec<PendingEvent> {
        if let Some(t) = self.turn.as_mut() {
            t.live.push_text_delta(delta);
        }
        self.set_phase(ActivityPhase::Responding)
    }

    pub(crate) fn tool_batch_started(
        &mut self,
        batch_id: String,
        call_ids: Vec<String>,
    ) -> Vec<PendingEvent> {
        if let Some(t) = self.turn.as_mut() {
            t.batches.push(ToolBatchRef { batch_id, started_at: now_rfc3339(), call_ids });
        }
        self.set_phase(ActivityPhase::RunningTools)
    }

    pub(crate) fn tool_batch_ended(&mut self, _batch_id: &str) -> Vec<PendingEvent> {
        self.set_phase(ActivityPhase::Preparing)
    }

    pub(crate) fn tool_call_started(
        &mut self,
        call_id: &str,
        batch_id: &str,
        tool_name: &str,
        input_json: &str,
    ) -> Vec<PendingEvent> {
        let Some(t) = self.turn.as_mut() else { return Vec::new() };
        let turn_id = t.turn_id.clone();
        t.live.push_tool_call(call_id, batch_id, &turn_id, tool_name, input_json);
        t.tool_call_started_at.insert((batch_id.to_string(), call_id.to_string()), Instant::now());
        self.tools_recent.push_back(ToolCallState {
            call_id: call_id.to_string(),
            batch_id: batch_id.to_string(),
            turn_id,
            tool_name: tool_name.to_string(),
            status: ToolCallStateStatus::Running,
            started_at: now_rfc3339(),
            elapsed_ms: None,
            ended_at: None,
            is_error: None,
            result_summary: None,
        });
        // C3: a call that starts and never finishes must not grow the table
        // without bound, and must never disappear silently.
        self.bound_tools();
        Vec::new()
    }

    /// Completes the call identified by `(batch_id, call_id)`.
    ///
    /// I4: matching on `call_id` alone completed the *first* entry with that
    /// id, so a provider that reuses tool-use ids across batches closed the
    /// wrong call; and a late result arriving after its turn ended rewrote
    /// the **current** turn's live entries. Both are keyed out here: the
    /// entry is located by `(batch_id, call_id)` newest-first among
    /// non-terminal calls, and the live projection is touched only when the
    /// located entry belongs to the turn that is actually running.
    pub(crate) fn tool_call_finished(
        &mut self,
        call_id: &str,
        batch_id: &str,
        status: ToolCallStateStatus,
        is_error: bool,
        content: &str,
    ) -> Vec<PendingEvent> {
        let key = (batch_id.to_string(), call_id.to_string());
        let elapsed_ms = self
            .turn
            .as_mut()
            .and_then(|t| t.tool_call_started_at.remove(&key))
            .map(|start| start.elapsed().as_millis() as i64);
        let (summary, _) = coda_proto::history::truncate_text(content, 512);

        // Who owns this `(batch_id, call_id)`? Newest first, terminal or not:
        // a turn end finalises its in-flight calls, so an abandoned call is
        // already terminal by the time its late result arrives and must still
        // be attributed to the turn that owned it.
        let owner_turn = self
            .tools_recent
            .iter()
            .rev()
            .find(|e| e.call_id == call_id && e.batch_id == batch_id)
            .map(|e| e.turn_id.clone());

        if let Some(entry) = self
            .tools_recent
            .iter_mut()
            .rev()
            .find(|e| e.call_id == call_id && e.batch_id == batch_id && !is_terminal(e.status))
        {
            entry.status = status;
            entry.is_error = Some(is_error);
            entry.ended_at = Some(now_rfc3339());
            entry.elapsed_ms = elapsed_ms;
            entry.result_summary = Some(summary);
        }

        let current_turn = self.current_turn_id().map(str::to_string);
        // Project into live entries only when this result belongs to the turn
        // that is actually running. An unregistered call (no `ToolCall` event
        // ever reached us) is attributed to the running turn's own stream,
        // which is where it arrived from.
        let belongs_to_running_turn = match &owner_turn {
            Some(owner) => Some(owner) == current_turn.as_ref(),
            None => true,
        };
        if belongs_to_running_turn {
            if let Some(t) = self.turn.as_mut() {
                let turn_id = t.turn_id.clone();
                t.live.push_tool_result(
                    call_id,
                    Some(batch_id),
                    &turn_id,
                    is_error,
                    content,
                    &format!("{status:?}"),
                );
            }
        }
        self.bound_tools();
        Vec::new()
    }

    pub(crate) fn usage_updated(&mut self, input_tokens: i64, output_tokens: i64) -> Vec<PendingEvent> {
        self.usage.last_response = Some((input_tokens, output_tokens));
        let (si, so) = self.usage.session_total.unwrap_or((0, 0));
        self.usage.session_total = Some((si + input_tokens, so + output_tokens));
        Vec::new()
    }

    /// Bounds the tool table. Terminal entries are evicted first; an entry
    /// still running is only dropped when nothing terminal is left, and
    /// either way `tools.truncated` is raised so the projection never claims
    /// to be the complete set of calls (C3).
    fn bound_tools(&mut self) {
        while self.tools_recent.len() > self.recent_tools_limit {
            let index = self
                .tools_recent
                .iter()
                .position(|e| is_terminal(e.status))
                .unwrap_or(0);
            self.tools_recent.remove(index);
            self.tools_evicted = true;
        }
    }

    /// Finalises every non-terminal call owned by `turn_id` (C3): an
    /// interrupted or failed turn used to leave its in-flight calls "running"
    /// for the rest of the process. Returns the ids it finalised, so the turn
    /// end can name them on the wire rather than leaving a state client to
    /// discover the change by polling (I4).
    fn finalise_tools_for_turn(
        &mut self,
        turn_id: &str,
        interrupted: bool,
        errored: bool,
    ) -> Vec<String> {
        let status = if interrupted {
            ToolCallStateStatus::Cancelled
        } else if errored {
            ToolCallStateStatus::Failed
        } else {
            // The turn ended cleanly while this call had produced no result:
            // it was pre-empted (steering) or abandoned with the batch.
            ToolCallStateStatus::Skipped
        };
        let ended_at = now_rfc3339();
        let started: HashMap<(String, String), Instant> = self
            .turn
            .as_mut()
            .map(|t| std::mem::take(&mut t.tool_call_started_at))
            .unwrap_or_default();
        let mut finalized = Vec::new();
        for entry in self.tools_recent.iter_mut() {
            if entry.turn_id != turn_id || is_terminal(entry.status) {
                continue;
            }
            entry.status = status;
            entry.is_error = Some(status == ToolCallStateStatus::Failed);
            entry.ended_at = Some(ended_at.clone());
            entry.elapsed_ms = started
                .get(&(entry.batch_id.clone(), entry.call_id.clone()))
                .map(|start| start.elapsed().as_millis() as i64);
            entry.result_summary = None;
            finalized.push(entry.call_id.clone());
        }
        self.bound_tools();
        finalized
    }

    fn lifecycle_event(&self) -> Vec<PendingEvent> {
        vec![gated(
            coda_proto::events::event_method::LIFECYCLE,
            serde_json::json!(wire::LifecycleEvent {
                lifecycle: self.lifecycle, initialized: self.initialized,
            }),
        )]
    }

    fn steering_queue_event(&self) -> Vec<PendingEvent> {
        let state = SteeringQueueState {
            pending_count: self.steering_pending.len() as i64,
            pending: self.steering_pending.clone(),
            outcomes: self.steering_outcomes.iter().cloned().collect(),
            outcomes_truncated: self.steering_outcomes_evicted,
            retained_outcomes: self.outcomes_retained_limit as i64,
        };
        let params = serde_json::json!(state);
        vec![gated(coda_proto::events::event_method::STEERING_QUEUE, params)]
    }
}

pub struct EngineState {
    inner: Mutex<Arc<StateInner>>,
    bus: Arc<EventBus>,
}

impl EngineState {
    pub fn new(
        bus: Arc<EventBus>,
        session_id: impl Into<String>,
        workspace_path: impl Into<String>,
        capabilities: HashMap<String, CapabilityEntry>,
        next_config: ActiveConfig,
    ) -> Self {
        let engine_instance_id = bus.engine_instance_id().to_string();
        let inner = StateInner {
            engine_instance_id,
            #[cfg(test)]
            clone_count: CloneCounter::default(),
            session_id: session_id.into(),
            workspace_path: workspace_path.into(),
            history_epoch: 0,
            history_length: 0,
            lifecycle: EngineLifecycle::Initializing,
            initialized: false,
            last_turn_outcome: None,
            turn: None,
            steering_pending: Vec::new(),
            steering_outcomes: VecDeque::new(),
            steering_outcomes_evicted: false,
            outcomes_retained_limit: DEFAULT_OUTCOMES_RETAINED,
            tools_recent: VecDeque::new(),
            tools_evicted: false,
            recent_tools_limit: DEFAULT_RECENT_TOOLS_RETAINED,
            requests: Vec::new(),
            usage: UsageStateInner::default(),
            capabilities,
            next_config,
        };
        Self { inner: Mutex::new(Arc::new(inner)), bus }
    }

    /// One atomic transaction: mutate the CoW `StateInner` and publish every
    /// event the mutation implies — gated *and* legacy — while STATE is still
    /// held (lock order `STATE -> BUS`, no `.await` anywhere in this path).
    pub(crate) fn transact<F>(&self, f: F)
    where
        F: FnOnce(&mut StateInner) -> Vec<PendingEvent>,
    {
        let mut guard = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        // CoW: only clones while a reader still holds the previous snapshot.
        let events = f(Arc::make_mut(&mut guard));
        for (method, params, gated) in events {
            self.bus.publish(&method, params, gated);
        }
    }

    /// `(Arc::clone, bus.cursor())`, read while STATE is held so the pair is
    /// exactly consistent — never takes INBOX.
    fn snapshot_cursor(&self) -> (Arc<StateInner>, i64) {
        let guard = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        let cursor = self.bus.cursor();
        (Arc::clone(&guard), cursor)
    }

    /// Direct access to the shared bus — used by `initialize` to negotiate
    /// `stateEvents` and to report `eventCursor`/`engineInstanceId`.
    pub fn bus_ref(&self) -> &Arc<EventBus> {
        &self.bus
    }

    /// Test-only: how many times `StateInner` has actually been copied.
    #[cfg(test)]
    pub(crate) fn state_copies(&self) -> usize {
        self.inner
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .clone_count
            .0
            .load(std::sync::atomic::Ordering::Relaxed)
    }

    /// Test-only: identity of the current `StateInner` allocation. Used to
    /// prove the copy-on-write property — an update must mutate in place
    /// unless a reader is genuinely still holding the previous snapshot.
    #[cfg(test)]
    pub(crate) fn inner_ptr(&self) -> *const StateInner {
        Arc::as_ptr(&self.inner.lock().unwrap_or_else(|p| p.into_inner()))
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    #[cfg(test)]
    pub(crate) fn mark_initialized(&self) {
        self.finish_initialization(true);
    }

    pub(crate) fn begin_initialization(&self) -> bool {
        let mut accepted = false;
        self.transact(|s| {
            if matches!(s.lifecycle, EngineLifecycle::Stopping | EngineLifecycle::Stopped) {
                return Vec::new();
            }
            accepted = true;
            if s.lifecycle == EngineLifecycle::Initializing {
                return Vec::new();
            }
            s.lifecycle = EngineLifecycle::Initializing;
            s.lifecycle_event()
        });
        accepted
    }

    pub(crate) fn finish_initialization(&self, completed: bool) -> bool {
        let mut accepted = false;
        self.transact(|s| {
            if matches!(s.lifecycle, EngineLifecycle::Stopping | EngineLifecycle::Stopped) {
                return Vec::new();
            }
            accepted = true;
            let before = (s.lifecycle, s.initialized);
            s.initialized |= completed;
            if s.initialized && s.lifecycle == EngineLifecycle::Initializing {
                s.lifecycle = EngineLifecycle::Ready;
            }
            if before == (s.lifecycle, s.initialized) {
                Vec::new()
            } else {
                s.lifecycle_event()
            }
        });
        accepted
    }

    /// Whether `initialize` has completed — the boundary the new Stage D
    /// methods are gated on (§2.7).
    pub fn is_initialized(&self) -> bool {
        self.inner.lock().unwrap_or_else(|p| p.into_inner()).initialized
    }

    /// The config captured into the running turn, if one is running.
    pub fn active_turn_config(&self) -> Option<ActiveConfig> {
        self.inner
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .turn
            .as_ref()
            .map(|t| t.active_config.clone())
    }

    /// Fork/rewind/compact/resume: bumps `historyEpoch`, re-fences
    /// `historyLength` and announces `event/sessionChanged` so the reset is
    /// never invisible (F2).
    pub fn session_changed(
        &self,
        reason: &'static str,
        session_id: impl Into<String>,
        history_length: i64,
    ) {
        let session_id = session_id.into();
        self.transact(move |s| {
            s.session_id = session_id.clone();
            s.history_epoch += 1;
            s.history_length = history_length;
            let params = serde_json::json!(wire::SessionChangedEvent {
                reason: reason.to_owned(),
                session_id,
                history_epoch: s.history_epoch,
                history_length,
            });
            vec![gated(coda_proto::events::event_method::SESSION_CHANGED, params)]
        });
    }

    // ── Turn / activity phase machine ────────────────────────────────────

    /// Opens the public turn, if the engine is still alive.
    ///
    /// C6: this is called the instant the single-flight slot is claimed —
    /// *before* credentials, session services or the agent are built — so
    /// `lifecycle: busy` and `turn.phase` never disagree with the flag that
    /// actually refuses a second prompt. `phase` is `preparing` for a prompt
    /// and `compacting` for an explicit compaction, which is equally busy and
    /// equally observable.
    ///
    /// **Admission is part of this transaction.** The transport stays open
    /// after `shutdown`, so nothing else stops a late prompt: admitted, it
    /// would overwrite `stopped` with `busy`, re-enable every configuration
    /// commit for the length of the turn, run a real model request and
    /// publish `ready` when the slot was released. A stopping/stopped engine
    /// therefore refuses here, while STATE is held, and publishes nothing.
    /// Returns whether the turn was opened; the caller must not claim the
    /// runtime slot, open the steering inbox or install a turn observer when
    /// it was not.
    pub fn begin_turn(
        &self,
        turn_id: impl Into<String>,
        user_text: &str,
        active_config: ActiveConfig,
        phase: ActivityPhase,
    ) -> bool {
        let turn_id = turn_id.into();
        let user_text = user_text.to_string();
        let mut admitted = false;
        let admitted_out = &mut admitted;
        self.transact(move |s| {
            if matches!(s.lifecycle, EngineLifecycle::Stopping | EngineLifecycle::Stopped) {
                return Vec::new();
            }
            *admitted_out = true;
            s.lifecycle = EngineLifecycle::Busy;
            let now = now_rfc3339();
            let started_instant = Instant::now();
            let mut live = LiveTurnAccumulator::new();
            live.push_user_text(&user_text);
            s.turn = Some(TurnTracking {
                turn_id,
                started_at: now.clone(),
                started_instant,
                phase,
                phase_since: now,
                phase_since_instant: started_instant,
                model_request: None,
                batches: Vec::new(),
                active_config,
                concurrent: ConcurrentCounters::default(),
                live,
                phase_before_awaiting: None,
                tool_call_started_at: HashMap::new(),
            });
            s.activity_event()
        });
        admitted
    }

    /// Replaces the running turn's `activeConfig` once the provider/client is
    /// actually resolved (C6: the turn is already public by then). Published
    /// (I4) so a state-events client converges on `config.active` without
    /// having to re-snapshot to notice the placeholder was replaced.
    pub fn turn_config_resolved(&self, turn_id: &str, active_config: ActiveConfig) {
        let turn_id = turn_id.to_string();
        self.transact(move |s| {
            let Some(t) = s.turn.as_mut() else { return Vec::new() };
            if t.turn_id != turn_id || t.active_config == active_config {
                return Vec::new();
            }
            t.active_config = active_config;
            let params = serde_json::json!(wire::ConfigChangedEvent::Active(wire::ActiveConfigChanged {
                turn_id,
                active: t.active_config.clone(),
            }));
            vec![gated(coda_proto::events::event_method::CONFIG_CHANGED, params)]
        });
    }

    /// Commits the engine's **next-turn** configuration (`session/setModel`,
    /// `session/setEffort`, `model/adjustEffort`,
    /// `session/setPermissionMode`, `session/setSystemPrompt`, `config/set`,
    /// and provider wiring) and announces it in the same transaction.
    ///
    /// There is no pending-change scheduler: the value is simply what the
    /// next turn / next permission check will read. Storing and publishing it
    /// together is what makes a snapshot self-consistent — the stored value
    /// and the event share one cursor position, so a `stateEvents` client
    /// that replays exclusively after `snapshot.cursor` can never be missing
    /// the change the snapshot already contains, nor be shown a snapshot that
    /// is behind its own cursor.
    ///
    /// **Admission is part of this transaction.** A stopping/stopped engine
    /// refuses the commit *while STATE is held*, and `commit` — which
    /// performs the caller's runtime mutation and returns the resulting
    /// record — is then never invoked at all. A caller-side pre-check could
    /// not do this: a writer that parks on a provider lookup can be overtaken
    /// by `shutdown`, and its late commit would otherwise mutate a dead
    /// engine's runtime and publish an event past the terminal lifecycle.
    /// Returns whether the commit was admitted.
    ///
    /// `commit` runs under STATE. It must therefore stay synchronous and must
    /// not reach for any lock the caller does not already hold — in practice
    /// it only writes the CONFIG record the caller has locked.
    ///
    /// `next` is a bounded scalar record (ids and mode labels); it never
    /// carries a credential, and it never carries the system-prompt text.
    pub(crate) fn commit_next_config<F>(&self, key: &'static str, commit: F) -> bool
    where
        F: FnOnce() -> ActiveConfig,
    {
        let mut admitted = false;
        // `move` is required to take ownership of `commit`; the flag is
        // captured as a mutable borrow so the answer survives the closure.
        let admitted_out = &mut admitted;
        self.transact(move |s| {
            if matches!(s.lifecycle, EngineLifecycle::Stopping | EngineLifecycle::Stopped) {
                // Nothing ran, nothing changed, nothing is published.
                return Vec::new();
            }
            *admitted_out = true;
            let next = commit();
            if s.next_config == next {
                // Nothing changed: announcing it would tell a converging
                // client to re-render a config it already has.
                return Vec::new();
            }
            s.next_config = next;
            let differing = s
                .turn
                .as_ref()
                .map(|t| config_differences(&t.active_config, &s.next_config))
                .unwrap_or_default();
            let params = serde_json::json!(wire::ConfigChangedEvent::Next(wire::NextConfigChanged {
                key: key.to_owned(),
                next: s.next_config.clone(),
                active: s.turn.as_ref().map(|t| t.active_config.clone()),
                differing,
            }));
            vec![gated(coda_proto::events::event_method::CONFIG_CHANGED, params)]
        });
        admitted
    }

    pub fn model_request_started(&self, request_id: impl Into<String>) {
        let request_id = request_id.into();
        self.transact(move |s| s.model_request_started(request_id));
    }

    pub fn observed_thinking_delta(&self, delta: &str) {
        let delta = delta.to_string();
        self.transact(move |s| s.observed_thinking_delta(&delta));
    }

    pub fn thinking_complete(&self) {
        self.transact(|s| s.thinking_complete());
    }

    pub fn observed_text_delta(&self, delta: &str) {
        let delta = delta.to_string();
        self.transact(move |s| s.observed_text_delta(&delta));
    }

    pub fn tool_batch_started(&self, batch_id: impl Into<String>, call_ids: Vec<String>) {
        let batch_id = batch_id.into();
        self.transact(move |s| s.tool_batch_started(batch_id, call_ids));
    }

    pub fn tool_batch_ended(&self, batch_id: impl Into<String>) {
        let batch_id = batch_id.into();
        self.transact(move |s| s.tool_batch_ended(&batch_id));
    }

    pub fn tool_call_started(&self, call_id: &str, batch_id: &str, tool_name: &str, input_json: &str) {
        let (call_id, batch_id, tool_name, input_json) =
            (call_id.to_string(), batch_id.to_string(), tool_name.to_string(), input_json.to_string());
        self.transact(move |s| s.tool_call_started(&call_id, &batch_id, &tool_name, &input_json));
    }

    pub fn tool_call_finished(
        &self,
        call_id: &str,
        batch_id: &str,
        status: ToolCallStateStatus,
        is_error: bool,
        content: &str,
    ) {
        let (call_id, batch_id, content) =
            (call_id.to_string(), batch_id.to_string(), content.to_string());
        self.transact(move |s| s.tool_call_finished(&call_id, &batch_id, status, is_error, &content));
    }

    /// Reaches the turn's **terminal outcome**: the live view is cleared, the
    /// committed-history fence moves, every in-flight tool call is finalised
    /// and `event/turnComplete` goes out — all in one transaction.
    ///
    /// I1: this deliberately does **not** publish `ready`. The engine is still
    /// holding the single-flight slot at this point (transcript persistence,
    /// unwinding), so reporting `ready` here would tell a client the engine
    /// is available while `session/prompt` still answers "busy". Availability
    /// is published by [`EngineState::release_turn`], which the turn guard
    /// calls while holding the very flag that decides it. A snapshot between
    /// the two reads `lifecycle: busy, turn: null, lastTurnOutcome: {...}` —
    /// "this turn is over, the engine is not yet free".
    ///
    /// Idempotent by turn id: returns `true` only when this call is the one
    /// that actually ended the turn, so the explicit end and `TurnGuard::drop`
    /// can both call it without double-publishing (C6).
    pub fn end_turn(&self, end: TurnEnd) -> bool {
        let ended = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let flag = Arc::clone(&ended);
        self.transact(move |s| {
            if s.current_turn_id() != Some(end.turn_id.as_str()) {
                return Vec::new();
            }
            flag.store(true, std::sync::atomic::Ordering::SeqCst);
            let finalized = s.finalise_tools_for_turn(&end.turn_id, end.interrupted, end.error.is_some());
            let ended_at = now_rfc3339();
            s.last_turn_outcome = Some(LastTurnOutcome {
                turn_id: end.turn_id.clone(),
                ended_at: ended_at.clone(),
                stop_reason: end.stop_reason.clone(),
                interrupted: end.interrupted,
                error: end.error.clone(),
            });
            if let Some(len) = end.history_length {
                s.history_length = len;
            }
            s.turn = None;

            let mut events: Vec<PendingEvent> = Vec::new();
            // The legacy frame first, so a snapshot at the resulting cursor
            // already reflects it.
            if let Some((method, params)) = end.wire.clone() {
                events.push((method, params, false));
            }
            // I4: every one of these mutations used to be invisible to a
            // state-events client, which then had to poll to notice that a
            // compaction, a failed preflight or a cancelled turn had
            // finalised tool calls and moved the history fence.
            events.push(gated(
                coda_proto::events::event_method::TURN_ENDED,
                serde_json::json!(wire::TurnEndedEvent {
                    turn_id: end.turn_id,
                    ended_at,
                    stop_reason: end.stop_reason,
                    interrupted: end.interrupted,
                    error: end.error,
                    history_epoch: s.history_epoch,
                    history_length: s.history_length,
                    finalized_call_ids: finalized,
                    tools_truncated: s.tools_evicted,
                }),
            ));
            events
        });
        ended.load(std::sync::atomic::Ordering::SeqCst)
    }

    /// Publishes availability: the single-flight slot has been released and a
    /// new prompt will be accepted.
    ///
    /// I1: the turn guard calls this **while holding the runtime flag it just
    /// cleared**, so "the snapshot says ready" and "a prompt is accepted"
    /// cannot disagree. It never re-opens a turn that has already started —
    /// a guard dropping late must not clobber its successor.
    pub fn release_turn(&self) {
        self.transact(|s| {
            if s.turn.is_some() || s.lifecycle != EngineLifecycle::Busy {
                // A new turn already claimed the slot, or the engine is
                // shutting down: nothing to publish.
                return Vec::new();
            }
            s.lifecycle = EngineLifecycle::Ready;
            s.lifecycle_event()
        });
    }

    /// Shutdown lifecycle (S5). `stopping`/`stopped` were reachable values in
    /// the wire enum that nothing ever set; they are now published for real.
    pub fn shutdown_started(&self) {
        self.transact(|s| {
            if matches!(s.lifecycle, EngineLifecycle::Stopping | EngineLifecycle::Stopped) {
                return Vec::new();
            }
            s.lifecycle = EngineLifecycle::Stopping;
            s.lifecycle_event()
        });
    }

    pub fn shutdown_completed(&self) {
        self.transact(|s| {
            if s.lifecycle == EngineLifecycle::Stopped {
                return Vec::new();
            }
            s.lifecycle = EngineLifecycle::Stopped;
            s.lifecycle_event()
        });
    }

    // ── Usage ─────────────────────────────────────────────────────────────

    pub fn usage_updated(&self, input_tokens: i64, output_tokens: i64) {
        self.transact(move |s| s.usage_updated(input_tokens, output_tokens));
    }

    // ── Steering projection (sole owner of `SteeringQueueState`) ─────────

    pub fn steering_enqueued(&self, message_id: &str, text: &str) {
        let (message_id, text) = (message_id.to_string(), text.to_string());
        self.transact(move |s| {
            let (capped, full_len) =
                coda_proto::history::truncate_text(&text, DEFAULT_STEERING_TEXT_CAP);
            s.steering_pending.push(SteeringPendingDto {
                message_id,
                enqueued_at: now_rfc3339(),
                text_length: full_len.unwrap_or(capped.len() as i64),
                text_truncated: full_len.is_some(),
                text: capped,
            });
            s.steering_queue_event()
        });
    }

    pub fn steering_delivered(&self, message_ids: &[String], turn_id: Option<&str>) {
        let (ids, turn_id) = (message_ids.to_vec(), turn_id.map(|s| s.to_string()));
        self.transact(move |s| {
            record_outcomes(s, &ids, SteeringOutcomeKind::Delivered, turn_id.as_deref())
        });
    }

    pub fn steering_recalled(&self, message_ids: &[String]) {
        let ids = message_ids.to_vec();
        self.transact(move |s| record_outcomes(s, &ids, SteeringOutcomeKind::Recalled, None));
    }

    /// Idempotent by message id (defensive; the inbox itself already only
    /// calls this once per batch of dropped entries — see
    /// `coda_agent::steering`).
    pub fn steering_turn_ended_dropped(&self, message_ids: &[String], turn_id: Option<&str>) {
        let (ids, turn_id) = (message_ids.to_vec(), turn_id.map(|s| s.to_string()));
        self.transact(move |s| {
            record_outcomes(s, &ids, SteeringOutcomeKind::CancelledTurnEnded, turn_id.as_deref())
        });
    }

    // ── Read accessors ────────────────────────────────────────────────────

    pub fn steering_pending_snapshot(&self) -> Vec<SteeringPendingDto> {
        self.inner.lock().unwrap_or_else(|p| p.into_inner()).steering_pending.clone()
    }
    /// The canonical id of the turn currently running, if any.
    pub fn current_turn_id(&self) -> Option<String> {
        self.inner
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .current_turn_id()
            .map(str::to_string)
    }

    /// A consistent read of everything `session/getHistory` needs to describe
    /// the live session, taken while STATE is held so `cursor`, the
    /// committed/live fence and the live projection agree exactly.
    ///
    /// The DTO materialisation happens *outside* the lock (I2).
    pub fn history_view(&self) -> HistoryView {
        let (inner, cursor) = self.snapshot_cursor();
        HistoryView {
            engine_instance_id: inner.engine_instance_id.clone(),
            session_id: inner.session_id.clone(),
            history_epoch: inner.history_epoch,
            history_length: inner.history_length,
            cursor,
            live: inner.turn.as_ref().map(|t| LiveView {
                entries: t.live.project(),
                truncated: t.live.truncated,
                omitted_bytes: t.live.omitted_bytes,
            }),
        }
    }

    // ── Pending reverse requests (§2.7) ──────────────────────────────────

    /// A request became outstanding. Publishes `event/requestPending` and,
    /// when it belongs to the **running turn**, moves the public phase to
    /// `awaitingUserInput` — remembering the phase to restore so a question
    /// raised inside a tool batch returns to `runningTools`.
    ///
    /// A request that belongs to no turn (a background subagent, a scheduled
    /// run) or to a *different* turn is still published and still listed, but
    /// it must not rewrite this turn's phase: the turn is not the thing
    /// waiting on the operator.
    fn on_request_pending(&self, dto: PendingRequestDto, all: Vec<PendingRequestDto>) {
        self.transact(move |s| {
            s.requests = all;
            let mut events = vec![gated(
                coda_proto::events::event_method::REQUEST_PENDING,
                serde_json::json!(wire::RequestPendingEvent {
                    request: dto.clone(),
                    requests: s.requests.clone(),
                }),
            )];
            if !s.request_belongs_to_running_turn(&dto) {
                return events;
            }
            if let Some(t) = s.turn.as_mut() {
                if t.phase != ActivityPhase::AwaitingUserInput {
                    t.phase_before_awaiting = Some(t.phase);
                }
            }
            events.extend(s.set_phase(ActivityPhase::AwaitingUserInput));
            events
        });
    }

    /// A request reached a terminal outcome.
    fn on_request_resolved(
        &self,
        request_id: String,
        kind: PendingRequestKind,
        outcome: String,
        all: Vec<PendingRequestDto>,
    ) {
        self.transact(move |s| {
            s.requests = all;
            let mut events = vec![gated(
                coda_proto::events::event_method::REQUEST_RESOLVED,
                serde_json::json!(wire::RequestResolvedEvent {
                    request_id,
                    kind,
                    outcome,
                    requests: s.requests.clone(),
                }),
            )];
            // Unpark when nothing *this turn* is waiting on remains. The
            // condition used to be "no requests at all", so a single
            // long-lived background approval pinned the running turn in
            // `awaitingUserInput` for the rest of its life.
            let owned_outstanding = s
                .requests
                .iter()
                .any(|r| s.request_belongs_to_running_turn(r));
            if !owned_outstanding {
                // Only rewind if the turn is *still* parked on the operator.
                // A real transition observed while the request was
                // outstanding (a tool result, a new model request) is newer
                // information; restoring the remembered phase over it would
                // move the public phase backwards.
                let still_waiting =
                    s.turn.as_ref().is_some_and(|t| t.phase == ActivityPhase::AwaitingUserInput);
                let restore = s.turn.as_mut().and_then(|t| t.phase_before_awaiting.take());
                if let (true, Some(phase)) = (still_waiting, restore) {
                    events.extend(s.set_phase(phase));
                }
            }
            events
        });
    }

    // ── Projection ────────────────────────────────────────────────────────

    /// Builds the wire `StateSnapshot`. Takes STATE only long enough to clone
    /// the `Arc` and read `bus.cursor()`; the DTO assembly below — including
    /// the live-turn materialisation — runs outside any lock (I2).
    ///
    /// Everything it reports — including `config.next` — comes from that one
    /// captured `StateInner`, so the snapshot and its cursor always describe
    /// the same instant. It deliberately takes no configuration argument:
    /// a caller that read the config separately would reintroduce the window
    /// where a commit lands between the read and the cursor, and its
    /// correction would then sit *before* the cursor where an exclusive
    /// replay can never deliver it.
    pub fn project(&self, contract_version: &str, limits: Limits) -> StateSnapshot {
        let (inner, cursor) = self.snapshot_cursor();

        let steering = SteeringQueueState {
            pending_count: inner.steering_pending.len() as i64,
            pending: inner.steering_pending.clone(),
            outcomes: inner.steering_outcomes.iter().cloned().collect(),
            outcomes_truncated: inner.steering_outcomes_evicted,
            retained_outcomes: inner.outcomes_retained_limit as i64,
        };

        let tools = ToolsState {
            active: inner.tools_recent.iter().filter(|t| !is_terminal(t.status)).cloned().collect(),
            recently_completed: inner
                .tools_recent
                .iter()
                .filter(|t| is_terminal(t.status))
                .cloned()
                .collect(),
            truncated: inner.tools_evicted,
            retained: inner.recent_tools_limit as i64,
        };

        let usage = UsageState {
            last_response: inner.usage.last_response.map(|(i, o)| coda_proto::state::UsagePair {
                input_tokens: i,
                output_tokens: o,
            }),
            session: inner.usage.session_total.map(|(i, o)| coda_proto::state::UsagePair {
                input_tokens: i,
                output_tokens: o,
            }),
            context_limit: None,
            unknown_fields: Vec::new(),
        };

        let turn = inner.turn.as_ref().map(|t| TurnState {
            turn_id: t.turn_id.clone(),
            started_at: t.started_at.clone(),
            // Measured here, at snapshot time, from the engine's own
            // monotonic clock — never cached (it would be stale the instant
            // it was stored) and never derived from the UTC string, which a
            // remote client cannot trust for durations.
            elapsed_ms: Some(t.started_instant.elapsed().as_millis() as i64),
            phase: t.phase,
            phase_since: t.phase_since.clone(),
            phase_elapsed_ms: Some(t.phase_since_instant.elapsed().as_millis() as i64),
            model_request: t.model_request.clone(),
            batches: t.batches.clone(),
            live_entries: t.live.project(),
            live_truncated: t.live.truncated,
            live_omitted_bytes: t.live.omitted_bytes,
            active_config: t.active_config.clone(),
            concurrent: t.concurrent.clone(),
        });

        StateSnapshot {
            contract_version: contract_version.to_string(),
            engine_instance_id: inner.engine_instance_id.clone(),
            session_id: inner.session_id.clone(),
            workspace_path: inner.workspace_path.clone(),
            cursor,
            history_epoch: inner.history_epoch,
            history_length: inner.history_length,
            lifecycle: inner.lifecycle,
            initialized: inner.initialized,
            last_turn_outcome: inner.last_turn_outcome.clone(),
            turn,
            steering,
            tools,
            requests: inner.requests.clone(),
            config: EffectiveConfig {
                active: inner.turn.as_ref().map(|t| t.active_config.clone()),
                differing: inner
                    .turn
                    .as_ref()
                    .map(|t| config_differences(&t.active_config, &inner.next_config))
                    .unwrap_or_default(),
                next: inner.next_config.clone(),
            },
            usage,
            limits,
            capabilities: inner.capabilities.clone(),
        }
    }
}

/// `EngineState` is the [`RequestObserver`]: the registry hands it a
/// fully-projected pending list and it owns the transaction (and therefore
/// the cursor/bus ordering) that publishes it.
impl requests::RequestObserver for EngineState {
    /// The turn the *calling execution* belongs to — never the turn that
    /// merely happens to be running.
    ///
    /// A background subagent or a scheduled run shares this engine's
    /// `PromptChannel` but runs on a detached task, so it carries no turn
    /// scope and its request is attributed to none. Reading
    /// `EngineState.turn` here would hand it the identity of whatever
    /// unrelated turn was in flight at that instant (see
    /// [`crate::turn_scope`]).
    fn current_turn_id(&self) -> Option<String> {
        crate::turn_scope::current()
    }

    fn request_pending(&self, dto: &PendingRequestDto, all: Vec<PendingRequestDto>) {
        self.on_request_pending(dto.clone(), all);
    }

    fn request_resolved(
        &self,
        request_id: &str,
        kind: PendingRequestKind,
        outcome: &str,
        all: Vec<PendingRequestDto>,
    ) {
        self.on_request_resolved(request_id.to_string(), kind, outcome.to_string(), all);
    }
}

fn record_outcomes(
    s: &mut StateInner,    message_ids: &[String],
    outcome: SteeringOutcomeKind,
    turn_id: Option<&str>,
) -> Vec<PendingEvent> {
    if message_ids.is_empty() {
        return Vec::new();
    }
    let at = now_rfc3339();
    let mut newly_recorded = false;
    for id in message_ids {
        s.steering_pending.retain(|p| &p.message_id != id);
        // Defensive idempotency: skip if this exact (id, outcome) is
        // already the most recent record for that id.
        if s.steering_outcomes.iter().any(|o| &o.message_id == id && o.outcome == outcome) {
            continue;
        }
        newly_recorded = true;
        s.steering_outcomes.push_back(SteeringOutcomeDto {
            message_id: id.clone(),
            outcome,
            at: at.clone(),
            turn_id: turn_id.map(str::to_string),
        });
        while s.steering_outcomes.len() > s.outcomes_retained_limit {
            s.steering_outcomes.pop_front();
            s.steering_outcomes_evicted = true;
        }
    }
    if newly_recorded {
        s.steering_queue_event()
    } else {
        Vec::new()
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::bus::EventBus;
    use std::collections::HashMap;

    fn test_state() -> Arc<EngineState> {
        let (tx, _rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        Arc::new(EngineState::new(bus, "s1", "/work", HashMap::new(), active_config()))
    }

    pub(crate) fn active_config() -> ActiveConfig {
        ActiveConfig {
            provider_id: Some("anthropic".into()),
            model: "claude-opus-4-5".into(),
            effort: None,
            effort_is_auto: true,
            permission_mode: "default".into(),
            system_prompt_source: "default".into(),
        }
    }

    pub(crate) fn limits() -> Limits {
        Limits {
            ring_envelopes: 2048,
            ring_bytes: 4 * 1024 * 1024,
            live_bytes_cap: 256 * 1024,
            outcomes_retained: 200,
            history_block_bytes_cap: 64 * 1024,
            max_history_page: 500,
            max_session_page: 200,
        }
    }

    fn ended(turn_id: &str) -> TurnEnd {
        TurnEnd { turn_id: turn_id.into(), stop_reason: Some("end_turn".into()), ..Default::default() }
    }

    // ── Pending reverse requests: phase + snapshot section (Stage D) ─────

    fn pending_dto(id: &str, kind: PendingRequestKind) -> PendingRequestDto {
        PendingRequestDto {
            request_id: id.into(),
            kind,
            issued_at: "2026-09-08T00:00:00Z".into(),
            turn_id: Some("t1".into()),
            call_id: None,
            display: serde_json::json!({ "question": "?" }),
            fail_closed_default: kind.fail_closed_default().into(),
        }
    }

    // ── Config commits and the cursor that is supposed to cover them ──────

    /// A config commit that lands before a snapshot is taken must be *inside*
    /// that snapshot, not behind its cursor.
    ///
    /// The reader used to read the next-turn config from the host and only
    /// then take STATE + `bus.cursor()`. A commit landing between those two
    /// steps published `event/configChanged` (burning a seq) and then handed
    /// the reader a snapshot that still carried the pre-commit config *under a
    /// cursor that already covered the correcting event*. An exclusive-replay
    /// client (`session/getEvents(afterCursor: snapshot.cursor)`) therefore
    /// never sees the correction and keeps the stale `config.next` for the
    /// rest of the session.
    #[test]
    fn a_config_commit_that_precedes_a_snapshot_is_inside_that_snapshot() {
        let (tx, _rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        let state = Arc::new(EngineState::new(
            Arc::clone(&bus),
            "s1",
            "/work",
            HashMap::new(),
            active_config(),
        ));

        // The commit lands where a reader's own pre-read would have gone
        // stale — the reader has no say in the matter any more.
        let mut committed = active_config();
        committed.model = "claude-sonnet-4-5".into();
        assert!(state.commit_next_config("model", || committed), "a live engine admits the commit");

        let snap = state.project("v1", limits());
        assert_eq!(
            snap.config.next.model, "claude-sonnet-4-5",
            "a committed config change must be visible in the snapshot that follows it"
        );

        let after = bus
            .get_events("engine-1", snap.cursor, 100)
            .expect("same instance");
        assert!(
            after.events.is_empty(),
            "nothing may correct this snapshot after its own cursor — a replay \
             client would never receive it: {:?}",
            after.events.iter().map(|e| e.method.clone()).collect::<Vec<_>>()
        );
    }

    /// The projection has no second opinion about the next config: there is
    /// exactly one place it can come from, so a caller cannot hand it a value
    /// that disagrees with what was committed and published.
    #[test]
    fn the_projection_reads_the_next_config_it_published() {
        let (tx, _rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        let state = Arc::new(EngineState::new(
            Arc::clone(&bus),
            "s1",
            "/work",
            HashMap::new(),
            active_config(),
        ));

        assert_eq!(
            state.project("v1", limits()).config.next,
            active_config(),
            "the seeded startup config is the next config until something commits"
        );

        let mut committed = active_config();
        committed.effort = Some("high".into());
        committed.effort_is_auto = false;
        assert!(state.commit_next_config("effort", || committed.clone()), "a live engine admits the commit");

        let published = bus
            .get_events("engine-1", 0, 100)
            .expect("same instance")
            .events
            .into_iter()
            .filter(|e| e.method == coda_proto::events::event_method::CONFIG_CHANGED)
            .next_back()
            .expect("the commit is published");
        let snap = state.project("v1", limits());
        assert_eq!(published.params["next"]["effort"], "high");
        assert_eq!(snap.config.next, committed, "snapshot and event must agree");
    }

    // ── Server-monotonic turn duration (remote/rehydrating clients) ───────

    /// A remote or rehydrating client cannot compute "how long has this turn
    /// been running" from `startedAt` alone: its own wall clock may be
    /// skewed, in a different VM, or simply wrong, and re-deriving from a
    /// UTC string on every resync makes the timer jump. The snapshot must
    /// therefore carry the *server's own* monotonic duration, so a client can
    /// seed its timer instead of guessing or resetting to zero.
    #[test]
    fn a_running_turn_reports_the_servers_own_monotonic_elapsed_time() {
        let state = test_state();
        state.begin_turn("t1", "do it", active_config(), ActivityPhase::Preparing);

        let first = state.project("v1", limits()).turn.expect("a turn is running");
        let elapsed = first.elapsed_ms.expect("a running turn must publish its own duration");
        assert!(elapsed >= 0, "duration is never negative: {elapsed}");

        std::thread::sleep(std::time::Duration::from_millis(30));
        let second = state.project("v1", limits()).turn.expect("still running");
        let later = second.elapsed_ms.expect("still published");
        assert!(
            later >= elapsed + 20,
            "the duration must advance with real time, not be a constant: {elapsed} -> {later}"
        );
        assert_eq!(
            second.started_at, first.started_at,
            "and the UTC start stays exactly as it was — elapsedMs is additional, not a replacement"
        );
    }

    /// Resynchronising must not restart the clock. Two snapshots taken over
    /// the life of one turn must report a non-decreasing duration, which is
    /// what lets a reconnecting client continue the timer rather than reset
    /// it to 0.
    #[test]
    fn resnapshotting_never_rewinds_the_reported_turn_duration() {
        let state = test_state();
        state.begin_turn("t1", "do it", active_config(), ActivityPhase::Preparing);
        let mut previous = 0;
        for _ in 0..5 {
            std::thread::sleep(std::time::Duration::from_millis(5));
            let now = state
                .project("v1", limits())
                .turn
                .expect("running")
                .elapsed_ms
                .expect("published");
            assert!(now >= previous, "a resync must never rewind the timer: {previous} -> {now}");
            previous = now;
        }
    }

    /// The phase clock is separate: "waiting for the model for 40 s" is a
    /// different fact from "this turn started 4 min ago", and a phase change
    /// must restart the phase clock without disturbing the turn clock.
    #[test]
    fn a_phase_change_restarts_the_phase_clock_but_not_the_turn_clock() {
        let state = test_state();
        state.begin_turn("t1", "do it", active_config(), ActivityPhase::Preparing);
        std::thread::sleep(std::time::Duration::from_millis(30));

        let before = state.project("v1", limits()).turn.expect("running");
        let turn_before = before.elapsed_ms.expect("turn duration");
        let phase_before = before.phase_elapsed_ms.expect("phase duration");
        assert!(phase_before >= 20, "the phase has been running as long as the turn: {phase_before}");

        state.tool_batch_started("b1", vec!["c1".into()]);
        let after = state.project("v1", limits()).turn.expect("running");
        assert_eq!(after.phase, ActivityPhase::RunningTools);
        assert!(
            after.phase_elapsed_ms.expect("phase duration") < phase_before,
            "entering a new phase restarts the phase clock"
        );
        assert!(
            after.elapsed_ms.expect("turn duration") >= turn_before,
            "but the turn clock keeps running: {turn_before} -> {:?}",
            after.elapsed_ms
        );
    }

    /// An idle engine publishes no turn at all, so there is nothing to
    /// report a duration for — absent, never `0`.
    #[test]
    fn an_idle_engine_reports_no_turn_and_therefore_no_duration() {
        let state = test_state();
        let snap = state.project("v1", limits());
        assert!(snap.turn.is_none());
        let v = serde_json::to_value(&snap).unwrap();
        assert!(v.get("turn").is_none(), "absent, never a zero-duration phantom turn: {v}");
    }

    // ── Provenance: only the running turn's own requests park its phase ───

    fn foreign_pending_dto(id: &str, turn_id: Option<&str>) -> PendingRequestDto {
        PendingRequestDto {
            request_id: id.into(),
            kind: PendingRequestKind::Permission,
            issued_at: "2026-09-09T00:00:00Z".into(),
            turn_id: turn_id.map(str::to_string),
            call_id: None,
            display: serde_json::json!({ "toolName": "run_command" }),
            fail_closed_default: PendingRequestKind::Permission.fail_closed_default().into(),
        }
    }

    /// Background subagents and scheduled runs share the engine's permission
    /// prompt but run on their own tasks, outliving the turn that started
    /// them. Their approvals are real and must stay discoverable — but they
    /// are **not** the foreground turn waiting on the operator. Parking that
    /// turn's public phase on them tells every client the wrong thing about a
    /// turn that is streaming perfectly happily.
    #[test]
    fn a_request_that_does_not_belong_to_the_running_turn_never_parks_its_phase() {
        let state = test_state();
        state.begin_turn("t-foreground", "do it", active_config(), ActivityPhase::Preparing);
        state.tool_batch_started("b1", vec!["c1".into()]);

        // Unknown origin: a background subagent that no scope attributed.
        let orphan = foreign_pending_dto("req-engine-1-9", None);
        state.on_request_pending(orphan.clone(), vec![orphan.clone()]);

        let snap = state.project("v1", limits());
        assert_eq!(
            snap.turn.unwrap().phase,
            ActivityPhase::RunningTools,
            "the foreground turn is still running tools; it is not waiting on anyone"
        );
        assert_eq!(snap.requests, vec![orphan], "but the request is still discoverable");
    }

    /// The same for a request stamped with a *different* turn — a background
    /// subagent started by an earlier turn that is still running.
    #[test]
    fn a_request_from_a_previous_turn_never_parks_the_current_one() {
        let state = test_state();
        state.begin_turn("t-second", "do it", active_config(), ActivityPhase::Preparing);
        state.tool_batch_started("b1", vec!["c1".into()]);

        let stale = foreign_pending_dto("req-engine-1-9", Some("t-first"));
        state.on_request_pending(stale.clone(), vec![stale.clone()]);

        assert_eq!(
            state.project("v1", limits()).turn.unwrap().phase,
            ActivityPhase::RunningTools,
            "a request belonging to another turn must not rewrite this turn's phase"
        );
    }

    /// The mirror image: an outstanding *foreign* request must not hold the
    /// running turn parked after its own question has been answered. The
    /// restore condition used to be "no requests left at all", so one
    /// long-lived background approval pinned the foreground turn in
    /// `awaitingUserInput` for the rest of its life.
    #[test]
    fn a_background_request_never_pins_the_running_turn_in_awaiting_user_input() {
        let state = test_state();
        state.begin_turn("t1", "do it", active_config(), ActivityPhase::Preparing);
        state.tool_batch_started("b1", vec!["c1".into()]);

        let background = foreign_pending_dto("req-engine-1-1", None);
        let mine = pending_dto("req-engine-1-2", PendingRequestKind::Question);
        state.on_request_pending(background.clone(), vec![background.clone()]);
        state.on_request_pending(mine.clone(), vec![background.clone(), mine.clone()]);
        assert_eq!(
            state.project("v1", limits()).turn.unwrap().phase,
            ActivityPhase::AwaitingUserInput,
            "my own question does park me"
        );

        // My question is answered; the background approval is still pending.
        state.on_request_resolved(
            mine.request_id.clone(),
            PendingRequestKind::Question,
            "answered".into(),
            vec![background.clone()],
        );

        let snap = state.project("v1", limits());
        assert_eq!(
            snap.turn.unwrap().phase,
            ActivityPhase::RunningTools,
            "this turn is no longer waiting on the operator, whatever a background task is doing"
        );
        assert_eq!(snap.requests, vec![background], "and the background request is still listed");
    }

    #[test]
    fn a_pending_request_parks_the_public_phase_on_the_operator() {
        let state = test_state();
        state.begin_turn("t1", "do it", active_config(), ActivityPhase::Preparing);
        state.tool_batch_started("b1", vec!["c1".into()]);
        assert_eq!(
            state.project("v1", limits()).turn.unwrap().phase,
            ActivityPhase::RunningTools
        );

        let dto = pending_dto("req-engine-1-1", PendingRequestKind::Question);
        state.on_request_pending(dto.clone(), vec![dto.clone()]);

        let snap = state.project("v1", limits());
        assert_eq!(snap.turn.unwrap().phase, ActivityPhase::AwaitingUserInput);
        assert_eq!(snap.requests, vec![dto.clone()], "and it is discoverable in the snapshot");

        // Resolving restores the phase the turn was really in.
        state.on_request_resolved(
            dto.request_id.clone(),
            PendingRequestKind::Question,
            "noAnswer.disconnected".into(),
            Vec::new(),
        );
        let snap = state.project("v1", limits());
        assert_eq!(
            snap.turn.unwrap().phase,
            ActivityPhase::RunningTools,
            "a question inside a tool batch returns to runningTools, not to an invented phase"
        );
        assert!(snap.requests.is_empty());
    }

    #[test]
    fn a_second_pending_request_keeps_the_phase_and_does_not_lose_the_one_to_restore() {
        let state = test_state();
        state.begin_turn("t1", "do it", active_config(), ActivityPhase::Preparing);
        state.tool_batch_started("b1", vec!["c1".into()]);

        let first = pending_dto("req-engine-1-1", PendingRequestKind::Permission);
        let second = pending_dto("req-engine-1-2", PendingRequestKind::Question);
        state.on_request_pending(first.clone(), vec![first.clone()]);
        state.on_request_pending(second.clone(), vec![first.clone(), second.clone()]);

        // One resolves; the other is still outstanding, so we stay parked.
        state.on_request_resolved(
            first.request_id.clone(),
            PendingRequestKind::Permission,
            "denied".into(),
            vec![second.clone()],
        );
        assert_eq!(
            state.project("v1", limits()).turn.unwrap().phase,
            ActivityPhase::AwaitingUserInput
        );

        state.on_request_resolved(
            second.request_id.clone(),
            PendingRequestKind::Question,
            "answered".into(),
            Vec::new(),
        );
        assert_eq!(
            state.project("v1", limits()).turn.unwrap().phase,
            ActivityPhase::RunningTools,
            "the phase to restore survived two nested requests"
        );
    }

    /// A real transition observed while a request was outstanding is newer
    /// information than the remembered phase; restoring over it would move
    /// the public phase backwards.
    #[test]
    fn resolving_never_rewinds_a_phase_that_has_already_moved_on() {
        let state = test_state();
        state.begin_turn("t1", "do it", active_config(), ActivityPhase::Preparing);
        state.tool_batch_started("b1", vec!["c1".into()]);

        let dto = pending_dto("req-engine-1-1", PendingRequestKind::Question);
        state.on_request_pending(dto.clone(), vec![dto.clone()]);
        // Something genuinely newer happens while the operator is deciding.
        state.observed_text_delta("meanwhile");
        assert_eq!(
            state.project("v1", limits()).turn.unwrap().phase,
            ActivityPhase::Responding
        );

        state.on_request_resolved(
            dto.request_id.clone(),
            PendingRequestKind::Question,
            "answered".into(),
            Vec::new(),
        );
        assert_eq!(
            state.project("v1", limits()).turn.unwrap().phase,
            ActivityPhase::Responding,
            "the newer transition wins; the phase must not move backwards"
        );
    }

    #[test]
    fn effective_config_derives_differences_rather_than_inventing_a_queue() {
        let state = test_state();
        state.begin_turn("t1", "do it", active_config(), ActivityPhase::Preparing);

        // The next turn would use a different model and permission mode.
        let mut next = active_config();
        next.model = "claude-sonnet-4-5".into();
        next.permission_mode = "plan".into();
        assert!(state.commit_next_config("model", || next), "a live engine admits the commit");

        let snap = state.project("v1", limits());
        let differing = snap.config.differing;
        let by_key = |k: &str| differing.iter().find(|d| d.key == k).cloned();

        let model = by_key("model").expect("model differs");
        assert_eq!(model.active.as_deref(), Some("claude-opus-4-5"));
        assert_eq!(model.next.as_deref(), Some("claude-sonnet-4-5"));
        assert_eq!(model.applies_when, coda_proto::config::AppliesWhen::NextTurn);

        let mode = by_key("permissionMode").expect("mode differs");
        assert_eq!(
            mode.applies_when,
            coda_proto::config::AppliesWhen::NextPermissionCheck,
            "permission mode is read at the next check, not the next turn"
        );

        assert!(by_key("effort").is_none(), "unchanged keys must not be listed");
        assert!(by_key("provider").is_none());
    }

    #[test]
    fn an_idle_engine_reports_no_differing_config_because_there_is_nothing_to_differ_from() {
        let state = test_state();
        let mut next = active_config();
        next.model = "something-else".into();
        assert!(state.commit_next_config("model", || next), "a live engine admits the commit");
        let snap = state.project("v1", limits());
        assert!(snap.config.active.is_none());
        assert!(snap.config.differing.is_empty());
    }

    #[test]
    fn begin_turn_enters_preparing_and_projects_the_user_text_as_live_entry_zero() {        let state = test_state();
        state.begin_turn("t1", "do the thing", active_config(), ActivityPhase::Preparing);
        let snap = state.project("v1", limits());
        assert_eq!(snap.lifecycle, EngineLifecycle::Busy);
        let turn = snap.turn.expect("turn must be present");
        assert_eq!(turn.turn_id, "t1");
        assert_eq!(turn.phase, ActivityPhase::Preparing);
        assert_eq!(turn.live_entries[0].role, "user");
    }

    #[test]
    fn model_request_started_before_any_model_output_moves_to_waiting_for_model() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.model_request_started("req-1");
        let snap = state.project("v1", limits());
        let turn = snap.turn.unwrap();
        assert_eq!(turn.phase, ActivityPhase::WaitingForModel);
        assert_eq!(turn.model_request.unwrap().request_id, "req-1");
    }

    #[test]
    fn silence_after_model_request_is_never_read_as_reasoning() {
        // Regression guard for the explicit requirement: snapshot must never
        // infer reasoning from effort/silence — only a real ThinkingStarted/
        // Delta event may set `reasoning`.
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.model_request_started("req-1");
        let snap = state.project("v1", limits());
        assert_eq!(snap.turn.unwrap().phase, ActivityPhase::WaitingForModel);
    }

    // ── C5: ThinkingStarted carries no text but is real evidence ──────────
    #[test]
    fn an_empty_thinking_delta_enters_reasoning_immediately() {
        // `StreamEvent::ThinkingStarted` reaches the sink as
        // `AgentEvent::Thinking { delta: "" }`. For a provider that encrypts
        // its reasoning that is the *only* signal reasoning has begun, so it
        // must move the phase even though it carries nothing to project.
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.model_request_started("req-1");
        state.observed_thinking_delta("");
        let snap = state.project("v1", limits());
        let turn = snap.turn.unwrap();
        assert_eq!(turn.phase, ActivityPhase::Reasoning, "an empty Thinking delta is still real evidence");
        assert!(turn.model_request.unwrap().observed_reasoning);
        assert!(turn.live_entries.len() <= 1, "no phantom reasoning text may be invented");
    }

    #[test]
    fn observed_thinking_then_text_moves_through_reasoning_to_responding() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.model_request_started("req-1");
        state.observed_thinking_delta("considering");
        let mid = state.project("v1", limits());
        assert_eq!(mid.turn.as_ref().unwrap().phase, ActivityPhase::Reasoning);
        assert!(mid.turn.unwrap().model_request.unwrap().observed_reasoning);

        state.thinking_complete();
        state.observed_text_delta("Here is the answer.");
        let after = state.project("v1", limits());
        assert_eq!(after.turn.unwrap().phase, ActivityPhase::Responding);
    }

    // ── C5: a snapshot must show the thoughts its own cursor covers ───────
    #[test]
    fn a_snapshot_taken_mid_reasoning_carries_the_thoughts_its_cursor_covers() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.observed_thinking_delta("step one, ");
        state.observed_thinking_delta("step two");
        let snap = state.project("v1", limits());
        let reasoning: String = snap
            .turn
            .unwrap()
            .live_entries
            .iter()
            .flat_map(|e| e.blocks.iter())
            .filter_map(|b| match b {
                coda_proto::history::HistoryBlock::ReasoningSummary { text, .. } => Some(text.clone()),
                _ => None,
            })
            .collect();
        assert_eq!(
            reasoning, "step one, step two",
            "the cursor already covers these Thinking events, so the snapshot must contain them"
        );
    }

    #[test]
    fn tool_batch_and_call_lifecycle_moves_through_running_tools_and_back() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.model_request_started("req-1");
        state.tool_batch_started("b1", vec!["c1".into()]);
        let running = state.project("v1", limits());
        assert_eq!(running.turn.as_ref().unwrap().phase, ActivityPhase::RunningTools);

        state.tool_call_started("c1", "b1", "read_file", "{}");
        let with_active = state.project("v1", limits());
        assert_eq!(with_active.tools.active.len(), 1);
        assert_eq!(with_active.tools.active[0].turn_id, "t1");
        assert_eq!(with_active.tools.active[0].batch_id, "b1");

        state.tool_call_finished("c1", "b1", ToolCallStateStatus::Completed, false, "ok");
        let after_finish = state.project("v1", limits());
        assert!(after_finish.tools.active.is_empty());
        assert_eq!(after_finish.tools.recently_completed.len(), 1);
        assert!(after_finish.tools.recently_completed[0].elapsed_ms.is_some());

        state.tool_batch_ended("b1");
        let back = state.project("v1", limits());
        assert_eq!(back.turn.unwrap().phase, ActivityPhase::Preparing);
    }

    // ── I4: a reused call id must not complete the wrong call ─────────────
    #[test]
    fn a_call_id_reused_in_a_later_batch_does_not_complete_the_earlier_call() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.tool_batch_started("b1", vec!["toolu_1".into()]);
        state.tool_call_started("toolu_1", "b1", "read_file", "{}");
        state.tool_batch_started("b2", vec!["toolu_1".into()]);
        state.tool_call_started("toolu_1", "b2", "write_file", "{}");

        state.tool_call_finished("toolu_1", "b2", ToolCallStateStatus::Completed, false, "written");

        let snap = state.project("v1", limits());
        assert_eq!(snap.tools.active.len(), 1, "the first batch's call is still running");
        assert_eq!(snap.tools.active[0].batch_id, "b1");
        assert_eq!(snap.tools.recently_completed.len(), 1);
        assert_eq!(snap.tools.recently_completed[0].batch_id, "b2");
        assert_eq!(snap.tools.recently_completed[0].tool_name, "write_file");
    }

    #[test]
    fn a_late_result_from_a_previous_turn_never_rewrites_the_running_turns_live_entries() {
        let state = test_state();
        state.begin_turn("t1", "first", active_config(), ActivityPhase::Preparing);
        state.tool_batch_started("b1", vec!["c1".into()]);
        state.tool_call_started("c1", "b1", "read_file", "{}");
        // Turn 1 ends with the call still in flight.
        state.end_turn(TurnEnd { turn_id: "t1".into(), interrupted: true, ..Default::default() });

        state.begin_turn("t2", "second", active_config(), ActivityPhase::Preparing);
        state.observed_text_delta("fresh turn");
        // The abandoned call finally reports back.
        state.tool_call_finished("c1", "b1", ToolCallStateStatus::Completed, false, "stale payload");

        let snap = state.project("v1", limits());
        let live_text = serde_json::to_string(&snap.turn.unwrap().live_entries).unwrap();
        assert!(
            !live_text.contains("stale payload"),
            "a result owned by an ended turn must never appear in the running turn's live entries"
        );
    }

    // ── C3: a turn end must finalise its in-flight calls ──────────────────
    #[test]
    fn an_interrupted_turn_cancels_its_in_flight_tool_calls_instead_of_leaving_them_running() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.tool_batch_started("b1", vec!["c1".into(), "c2".into()]);
        state.tool_call_started("c1", "b1", "bash", "{}");
        state.tool_call_started("c2", "b1", "bash", "{}");
        state.tool_call_finished("c1", "b1", ToolCallStateStatus::Completed, false, "done");

        state.end_turn(TurnEnd { turn_id: "t1".into(), interrupted: true, ..Default::default() });

        let snap = state.project("v1", limits());
        assert!(snap.tools.active.is_empty(), "no call may stay active once its turn is over");
        let c2 = snap
            .tools
            .recently_completed
            .iter()
            .find(|t| t.call_id == "c2")
            .expect("the interrupted call must still be reported");
        assert_eq!(c2.status, ToolCallStateStatus::Cancelled);
        assert!(c2.ended_at.is_some(), "a finalised call must carry the time it ended");
        assert!(c2.elapsed_ms.is_some());
    }

    #[test]
    fn a_failed_turn_marks_its_in_flight_tool_calls_failed_and_a_clean_end_marks_them_skipped() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.tool_call_started("c1", "b1", "bash", "{}");
        state.end_turn(TurnEnd {
            turn_id: "t1".into(),
            error: Some(TurnErrorSummary {
                category: "llm.server_error".into(),
                status: Some(500),
                parameter: None,
            }),
            ..Default::default()
        });
        let snap = state.project("v1", limits());
        assert_eq!(snap.tools.recently_completed[0].status, ToolCallStateStatus::Failed);

        state.begin_turn("t2", "go", active_config(), ActivityPhase::Preparing);
        state.tool_call_started("c2", "b2", "bash", "{}");
        state.end_turn(ended("t2"));
        let snap = state.project("v1", limits());
        let c2 = snap.tools.recently_completed.iter().find(|t| t.call_id == "c2").unwrap();
        assert_eq!(c2.status, ToolCallStateStatus::Skipped);
    }

    #[test]
    fn tool_storage_stays_bounded_when_calls_start_without_ever_finishing() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        for i in 0..(DEFAULT_RECENT_TOOLS_RETAINED * 3) {
            state.tool_call_started(&format!("c{i}"), "b1", "bash", "{}");
        }
        let snap = state.project("v1", limits());
        let total = snap.tools.active.len() + snap.tools.recently_completed.len();
        assert!(
            total <= DEFAULT_RECENT_TOOLS_RETAINED,
            "the tool table must stay inside its advertised bound, saw {total}"
        );
        assert!(snap.tools.truncated, "dropping calls must be declared, never silent");
        assert_eq!(snap.tools.retained, DEFAULT_RECENT_TOOLS_RETAINED as i64);
    }

    #[test]
    fn end_turn_reaches_the_terminal_outcome_but_availability_is_published_separately() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        assert!(state.end_turn(ended("t1")));

        // I1: the turn itself is over — live view cleared, outcome recorded —
        // but the engine still holds the single-flight slot, so it must not
        // yet claim to be `ready`.
        let finalizing = state.project("v1", limits());
        assert_eq!(
            finalizing.lifecycle,
            EngineLifecycle::Busy,
            "a terminal turn does not by itself make the engine available"
        );
        assert!(finalizing.turn.is_none());
        let outcome = finalizing.last_turn_outcome.clone().unwrap();
        assert_eq!(outcome.turn_id, "t1");
        assert_eq!(outcome.stop_reason.as_deref(), Some("end_turn"));
        assert!(!outcome.interrupted);

        state.release_turn();
        let ready = state.project("v1", limits());
        assert_eq!(ready.lifecycle, EngineLifecycle::Ready);
        assert_eq!(ready.last_turn_outcome.unwrap().turn_id, "t1");
    }

    #[test]
    fn releasing_never_clobbers_a_turn_that_has_already_started() {
        // A guard dropping late must not rewrite the state of its successor.
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.end_turn(ended("t1"));
        state.begin_turn("t2", "go again", active_config(), ActivityPhase::Preparing);

        state.release_turn(); // the previous turn's guard, arriving late
        let snap = state.project("v1", limits());
        assert_eq!(snap.lifecycle, EngineLifecycle::Busy, "the new turn must stay busy");
        assert_eq!(snap.turn.unwrap().turn_id, "t2");
    }

    // ── I4: every silent mutation now converges without polling ───────────
    #[test]
    fn a_turn_end_publishes_its_outcome_fence_and_finalised_calls() {
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        bus.enable_state_events();
        let state = Arc::new(EngineState::new(Arc::clone(&bus), "s1", "/work", HashMap::new(), active_config()));

        state.begin_turn("t1", "go", active_config(), ActivityPhase::Compacting);
        state.tool_call_started("c1", "b1", "bash", "{}");
        // No wire frame at all — the compaction/preflight/drop shape that used
        // to mutate state completely silently.
        state.end_turn(TurnEnd {
            turn_id: "t1".into(),
            interrupted: true,
            history_length: Some(9),
            ..Default::default()
        });
        state.release_turn();

        let published = drain_methods(&mut rx);
        let ended = published
            .iter()
            .find(|(m, _)| m == coda_proto::events::event_method::TURN_ENDED)
            .expect("a state client must learn the turn ended without polling");
        assert_eq!(ended.1["turnId"], "t1");
        assert_eq!(ended.1["interrupted"], true);
        assert_eq!(ended.1["historyLength"], 9, "the fence must travel with the event");
        assert_eq!(
            ended.1["finalizedCallIds"].as_array().unwrap(),
            &vec![serde_json::json!("c1")],
            "silently finalised calls must be named"
        );
        let lifecycle = published
            .iter()
            .find(|(m, _)| m == coda_proto::events::event_method::LIFECYCLE)
            .expect("availability must be announced too");
        assert_eq!(lifecycle.1["lifecycle"], "ready");
    }

    #[test]
    fn resolving_the_turn_config_is_announced_rather_than_applied_silently() {
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        bus.enable_state_events();
        let state = Arc::new(EngineState::new(Arc::clone(&bus), "s1", "/work", HashMap::new(), active_config()));
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        let _ = drain_methods(&mut rx);

        let mut resolved = active_config();
        resolved.provider_id = Some("copilot".into());
        state.turn_config_resolved("t1", resolved.clone());

        let published = drain_methods(&mut rx);
        let changed = published
            .iter()
            .find(|(m, _)| m == coda_proto::events::event_method::CONFIG_CHANGED)
            .expect("replacing the placeholder config must be observable");
        assert_eq!(changed.1["active"]["providerId"], "copilot");

        // An identical re-resolution publishes nothing.
        state.turn_config_resolved("t1", resolved);
        assert!(drain_methods(&mut rx)
            .iter()
            .all(|(m, _)| m != coda_proto::events::event_method::CONFIG_CHANGED));
    }

    // ── S5: shutdown states are real, not decorative enum values ──────────
    #[test]
    fn shutdown_publishes_stopping_then_stopped() {
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        bus.enable_state_events();
        let state = Arc::new(EngineState::new(Arc::clone(&bus), "s1", "/work", HashMap::new(), active_config()));
        state.mark_initialized();
        let _ = drain_methods(&mut rx);

        state.shutdown_started();
        assert_eq!(state.project("v1", limits()).lifecycle, EngineLifecycle::Stopping);
        state.shutdown_completed();
        assert_eq!(state.project("v1", limits()).lifecycle, EngineLifecycle::Stopped);

        let lifecycles: Vec<String> = drain_methods(&mut rx)
            .into_iter()
            .filter(|(m, _)| m == coda_proto::events::event_method::LIFECYCLE)
            .map(|(_, p)| p["lifecycle"].as_str().unwrap().to_string())
            .collect();
        assert_eq!(lifecycles, vec!["stopping", "stopped"]);

        // A shutdown must not be re-opened by a stale turn release.
        state.release_turn();
        assert_eq!(state.project("v1", limits()).lifecycle, EngineLifecycle::Stopped);
    }

    fn drain_methods(
        rx: &mut tokio::sync::mpsc::UnboundedReceiver<Vec<u8>>,
    ) -> Vec<(String, Value)> {
        let mut out = Vec::new();
        while let Ok(frame) = rx.try_recv() {
            let text = String::from_utf8(frame).unwrap();
            let body = text.find("\r\n\r\n").map(|i| i + 4).unwrap_or(0);
            let msg: Value = serde_json::from_str(&text[body..]).unwrap();
            out.push((msg["method"].as_str().unwrap().to_string(), msg["params"].clone()));
        }
        out
    }

    // ── C6: end_turn is idempotent so seal + Drop cannot double-publish ───
    #[test]
    fn end_turn_is_idempotent_by_turn_id() {
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        let state = Arc::new(EngineState::new(Arc::clone(&bus), "s1", "/work", HashMap::new(), active_config()));
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);

        let wire = Some(("event/turnComplete".to_string(), serde_json::json!({ "interrupted": false })));
        assert!(state.end_turn(TurnEnd { turn_id: "t1".into(), wire: wire.clone(), ..Default::default() }));
        assert!(
            !state.end_turn(TurnEnd { turn_id: "t1".into(), wire, ..Default::default() }),
            "a second end for the same turn must be a no-op"
        );

        let mut turn_completes = 0;
        while let Ok(frame) = rx.try_recv() {
            let text = String::from_utf8(frame).unwrap();
            if text.contains("event/turnComplete") {
                turn_completes += 1;
            }
        }
        assert_eq!(turn_completes, 1, "the wire frame must be published exactly once");
    }

    #[test]
    fn ending_a_turn_that_is_not_running_changes_nothing() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        assert!(!state.end_turn(ended("some-other-turn")));
        let snap = state.project("v1", limits());
        assert_eq!(snap.turn.unwrap().turn_id, "t1", "an unrelated end must not clear the live turn");
    }

    // ── I1: committed history and live entries never overlap ──────────────
    #[test]
    fn the_history_fence_moves_in_the_same_transaction_as_the_live_turn_reset() {
        let state = test_state();
        state.session_changed("resume", "s1", 4); // 4 committed messages
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.observed_text_delta("answering");

        let mid = state.project("v1", limits());
        assert_eq!(mid.history_length, 4, "the in-flight turn is never counted as committed");
        assert!(mid.turn.is_some());

        state.end_turn(TurnEnd {
            turn_id: "t1".into(),
            stop_reason: Some("end_turn".into()),
            history_length: Some(6),
            ..Default::default()
        });
        let after = state.project("v1", limits());
        assert_eq!(after.history_length, 6);
        assert!(after.turn.is_none(), "the fence moved and the live view was cleared as one step");
    }

    #[test]
    fn snapshot_never_duplicates_a_completed_turn_in_both_history_and_live_entries() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        state.observed_text_delta("hello");
        state.end_turn(TurnEnd {
            turn_id: "t1".into(),
            stop_reason: Some("end_turn".into()),
            history_length: Some(2),
            ..Default::default()
        });
        let snap = state.project("v1", limits());
        assert!(snap.turn.is_none(), "ended turn must not leave a stale liveEntries view");
    }

    // ── Usage is real, not permanently unknown ────────────────────────────
    #[test]
    fn observed_usage_reaches_the_snapshot_and_accumulates_across_responses() {
        let state = test_state();
        let idle = state.project("v1", limits());
        assert!(idle.usage.last_response.is_none(), "unknown usage is None, never a false zero");

        state.usage_updated(1500, 320);
        state.usage_updated(200, 40);
        let snap = state.project("v1", limits());
        let last = snap.usage.last_response.unwrap();
        assert_eq!((last.input_tokens, last.output_tokens), (200, 40));
        let session = snap.usage.session.unwrap();
        assert_eq!((session.input_tokens, session.output_tokens), (1700, 360));
    }

    #[test]
    fn concurrent_counters_are_unknown_not_zero_by_default() {
        // C4: background/scheduled work runs against a `NullSink`, so this
        // engine genuinely does not know these counts. `None` says so; a
        // confident `0` would be a lie, and is what this guards against.
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        let snap = state.project("v1", limits());
        let turn = snap.turn.unwrap();
        assert!(turn.concurrent.background_tasks.is_none());
        assert!(turn.concurrent.scheduled_runs.is_none());
    }

    #[test]
    fn session_changed_bumps_history_epoch_and_emits_a_gated_event() {
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        let state = Arc::new(EngineState::new(Arc::clone(&bus), "s1", "/work", HashMap::new(), active_config()));
        bus.enable_state_events();

        state.session_changed("fork", "s2", 3);
        let snap = state.project("v1", limits());
        assert_eq!(snap.history_epoch, 1);
        assert_eq!(snap.session_id, "s2");
        assert_eq!(snap.history_length, 3);

        assert!(rx.try_recv().is_ok(), "event/sessionChanged must be published");
    }

    // ── I2: copy-on-write means *on write while shared*, not on every write ─
    #[test]
    fn streaming_deltas_do_not_clone_the_whole_state_when_no_reader_holds_it() {
        // The previous implementation cloned `StateInner` unconditionally on
        // every update and re-materialised the entire accumulated live text
        // into a cached wire field on every delta — quadratic in the length
        // of a streamed reply. With a genuine `Arc::make_mut`, an update with
        // no outstanding reader mutates the same allocation.
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);
        let before = state.state_copies();
        for i in 0..500 {
            state.observed_text_delta(&format!("delta-{i} "));
        }
        assert_eq!(
            state.state_copies(),
            before,
            "500 streamed deltas must not copy the whole state once per delta"
        );

        // And when a reader *is* holding the previous snapshot, the next
        // write does copy — that is the correctness half of the same rule.
        let held = {
            let guard = state.inner.lock().unwrap();
            Arc::clone(&guard)
        };
        state.observed_text_delta("after a reader took a snapshot");
        assert_eq!(state.state_copies(), before + 1, "a shared state must be copied before write");
        assert_ne!(state.inner_ptr(), Arc::as_ptr(&held));
        // The reader's snapshot is unaffected by the write that followed it.
        assert!(!held
            .turn
            .as_ref()
            .unwrap()
            .live
            .assistant_text()
            .contains("after a reader"));
    }

    #[test]
    fn the_live_projection_is_built_from_one_accumulator_not_a_cached_copy() {
        // Nothing is cached back into state: the projected entries are
        // materialised per snapshot, so two consecutive projections of an
        // unchanged state are equal, and the accumulator itself remains the
        // single source of the text.
        let state = test_state();
        state.begin_turn("t1", "", active_config(), ActivityPhase::Preparing);
        for i in 0..50 {
            state.observed_text_delta(&format!("{i},"));
        }
        let first = state.project("v1", limits());
        let second = state.project("v1", limits());
        assert_eq!(first.turn.as_ref().unwrap().live_entries, second.turn.as_ref().unwrap().live_entries);
        let expected: String = (0..50).map(|i| format!("{i},")).collect();
        assert_eq!(live_text(&first), expected);
    }

    #[test]
    fn many_concurrent_readers_never_observe_a_snapshot_ahead_of_its_own_cursor() {
        let state = test_state();
        state.begin_turn("t1", "go", active_config(), ActivityPhase::Preparing);

        let stop = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let (observed_tx, observed_rx) = std::sync::mpsc::sync_channel(1);
        let writer_state = Arc::clone(&state);
        let writer_stop = Arc::clone(&stop);
        let writer = std::thread::spawn(move || {
            for i in 0..2000 {
                writer_state.observed_text_delta(&format!(" {i}"));
                if i == 1000 {
                    // A fast writer used to finish before the reader was
                    // scheduled, making the concurrency test vacuous/flaky.
                    observed_rx.recv().expect("reader observes a mid-stream snapshot");
                }
            }
            writer_stop.store(true, std::sync::atomic::Ordering::SeqCst);
        });

        let reader_state = Arc::clone(&state);
        let reader_stop = Arc::clone(&stop);
        let reader = std::thread::spawn(move || {
            let mut checked = 0;
            let mut previous_len = 0usize;
            let mut previous_cursor = 0i64;
            let mut observed_tx = Some(observed_tx);
            while !reader_stop.load(std::sync::atomic::Ordering::SeqCst) {
                let snap = reader_state.project("v1", limits());
                let len = live_text(&snap).len();
                // Both the projection and its cursor move forward together;
                // neither may ever go backwards relative to the other.
                assert!(len >= previous_len);
                assert!(snap.cursor >= previous_cursor);
                previous_len = len;
                previous_cursor = snap.cursor;
                checked += 1;
                if len > 0 {
                    if let Some(tx) = observed_tx.take() {
                        tx.send(()).expect("writer is waiting for an observation");
                    }
                }
            }
            checked
        });

        writer.join().unwrap();
        let checked = reader.join().unwrap();
        assert!(checked > 0, "reader must have observed at least one snapshot");

        let final_snap = state.project("v1", limits());
        let text = live_text(&final_snap);
        let expected: String = (0..2000).map(|i| format!(" {i}")).collect();
        assert_eq!(text, expected, "the final projection must be the exact concatenation of every delta");
    }

    pub(crate) fn live_text(snap: &StateSnapshot) -> String {
        snap.turn
            .as_ref()
            .map(|t| {
                t.live_entries
                    .iter()
                    .filter(|e| e.role == "assistant")
                    .flat_map(|e| e.blocks.iter())
                    .filter_map(|b| match b {
                        coda_proto::history::HistoryBlock::Text { text, .. } => Some(text.as_str()),
                        _ => None,
                    })
                    .collect()
            })
            .unwrap_or_default()
    }
}
