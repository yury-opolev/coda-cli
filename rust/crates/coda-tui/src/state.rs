//! Application state and the reducer that advances it.
//!
//! All mutation flows through [`UiState::apply`]. Keeping transitions in one
//! place means the UI can be tested without a terminal, a running engine, or
//! any async machinery: feed events in, assert on the state that comes out.

use coda_proto::events::ToolCallStatus;
use coda_proto::{Correlation, Event};
use coda_render::tool::{CallStatus, ToolActivity, ToolCall, ToolDisplayMode};

use crate::coverage::HistoryCoverage;
use crate::hint::HintQueue;
use crate::progress::TurnProgress;
use crate::transcript::{same_call, ActivityKey, Block, NoticeLevel, PermissionDecision, Transcript};

/// What the agent is currently doing, shown in the status line.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Activity {
    /// Connected and awaiting input.
    #[default]
    Ready,
    /// Handshake in progress.
    Initializing,
    /// A turn is running.
    Working,
    /// The model is reasoning.
    Thinking,
    /// Blocked on the user answering a prompt.
    Waiting,
    /// No engine is answering: a sign-out, or a replacement that did not
    /// start.
    ///
    /// A state of its own rather than a quiet fall back to [`Activity::Ready`]
    /// because "ready" is a claim about an engine. After a deliberate
    /// disconnection the transcript, the draft and every local command still
    /// work — but nothing may be sent, so saying "ready" in green next to the
    /// model of a session that no longer exists is the one thing the status
    /// line must not do.
    Disconnected,
}

impl Activity {
    pub fn label(self) -> &'static str {
        match self {
            Activity::Ready => "ready",
            Activity::Initializing => "starting",
            Activity::Working => "working",
            Activity::Thinking => "thinking",
            Activity::Waiting => "waiting",
            Activity::Disconnected => "disconnected",
        }
    }

    pub fn role(self) -> coda_render::theme::Role {
        use coda_render::theme::Role;
        match self {
            Activity::Ready => Role::OperationalReady,
            Activity::Initializing => Role::OperationalInitializing,
            Activity::Working => Role::OperationalWorking,
            Activity::Thinking => Role::OperationalThinking,
            Activity::Waiting => Role::OperationalWaiting,
            // Not an operational state at all: nothing is running, and the
            // warning role is what distinguishes it from a healthy idle.
            Activity::Disconnected => Role::Warning,
        }
    }

    /// Whether an engine is believed to be answering.
    ///
    /// The rendering half of `App::engine_connected`, so a label, a model name
    /// and a spinner cannot disagree with each other about the same session.
    pub fn is_connected(self) -> bool {
        !matches!(self, Activity::Disconnected)
    }

    /// Whether this state should show a moving indicator.
    ///
    /// A spinner is a claim that something is happening. `Waiting` is blocked
    /// on the *user* answering a prompt, so spinning there claims progress
    /// that is not being made and invites them to wait for themselves.
    pub fn is_animated(self) -> bool {
        matches!(
            self,
            Activity::Working | Activity::Thinking | Activity::Initializing
        )
    }
}

/// Cumulative token usage for the session.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct Usage {
    pub input_tokens: i64,
    pub output_tokens: i64,
    /// Nominal context window, used to show a percentage.
    pub context_limit: i64,
    /// Dollars per million tokens in and out, when the model's price is known.
    ///
    /// Carried rather than looked up so the cost can be shown without the
    /// renderer reaching for a catalogue, and so an unpriced model shows
    /// nothing rather than a confident zero.
    pub price_per_million: Option<(f64, f64)>,
}

impl Usage {
    /// What this session has cost so far, in US dollars.
    pub fn estimated_cost(&self) -> Option<f64> {
        let (input, output) = self.price_per_million?;
        let per_million = |tokens: i64, price: f64| tokens as f64 / 1_000_000.0 * price;
        Some(per_million(self.input_tokens, input) + per_million(self.output_tokens, output))
    }

    /// Percentage of the context window consumed, if a limit is known.
    pub fn percent_used(&self) -> Option<u8> {
        if self.context_limit <= 0 {
            return None;
        }
        let ratio = self.input_tokens as f64 / self.context_limit as f64;
        Some((ratio * 100.0).clamp(0.0, 100.0) as u8)
    }
}

/// A user message queued while the agent is busy.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QueuedMessage {
    pub id: Option<String>,
    pub text: String,
    /// When this was queued, for display and for ordering recovery.
    pub queued_at: String,
}

/// Where in the conversation a delivered message is being put, relative to
/// when it actually happened.
///
/// The distinction is the difference between an honest transcript and a
/// plausible one: a follow-up delivered into the turn that is still running
/// belongs exactly where it is appended, while one confirmed after its turn
/// ended is being appended somewhere it never was, and that has to be said
/// rather than implied by position.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Placement {
    /// Appended into the turn it was delivered into.
    InTurn,
    /// Appended after its turn was already over.
    Late,
}

/// A prompt from the engine awaiting a user decision.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PendingPrompt {
    Permission { tool: String, preview: String },
    Question {
        question: String,
        options: Vec<String>,
        multi_select: bool,
        allow_free_text: bool,
    },
    PlanApproval { plan: String },
}

/// How an outstanding prompt ended when this client did not answer it.
///
/// The engine publishes `kind` and `outcome` on `event/requestResolved`, and
/// they are the only authority on what actually happened. A client that
/// discarded them had to supply a decision of its own, and the one it supplied
/// was always a refusal — so a permission granted from another client, a
/// question answered in a web UI, or an approval given over the API all
/// appeared here as denials.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ExternalResolution {
    /// A terminal outcome the engine published:
    /// `allowed`/`denied`/`approved`/`rejected`/`answered`/`noAnswer.<reason>`.
    ///
    /// Both fields are optional because an engine that predates them says
    /// only *that* the request ended. "We do not know how" is then the truth,
    /// and it is reported as such rather than being resolved into a verdict.
    /// The answer *text* is never carried: `requestResolved` does not publish
    /// it, so showing one would be this client inventing it.
    Outcome { kind: Option<coda_proto::state::PendingRequestKind>, outcome: Option<String> },
    /// The request is no longer addressable because the engine process that
    /// raised it was replaced. Nothing was decided — here or anywhere — so
    /// nothing is recorded as a decision.
    Retired,
}

/// What to tell the user about a prompt that ended without them answering it.
///
/// `None` means there is nothing honest to say: either nothing was on screen,
/// or the request was retired rather than decided. The wording states who
/// decided (not this terminal) and exactly what the engine reported — an
/// unrecognised label is quoted rather than mapped onto the nearest verdict,
/// because guessing here is how a grant becomes a refusal.
fn external_resolution_text(
    resolution: &ExternalResolution,
    prompt: Option<&PendingPrompt>,
) -> Option<String> {
    let ExternalResolution::Outcome { outcome, .. } = resolution else { return None };
    let subject = match prompt? {
        PendingPrompt::Permission { tool, .. } => format!("The permission request for {tool}"),
        PendingPrompt::Question { .. } => "That question".to_string(),
        PendingPrompt::PlanApproval { .. } => "The plan".to_string(),
    };
    let verdict = match outcome.as_deref() {
        Some("allowed") => "was allowed on another client.".to_string(),
        Some("denied") => "was denied on another client.".to_string(),
        Some("approved") => "was approved on another client.".to_string(),
        Some("rejected") => "was rejected on another client.".to_string(),
        // The answer text is deliberately absent from the wire: it is not
        // published, so it is not shown.
        Some("answered") => {
            "was answered on another client; the answer is not shown here.".to_string()
        }
        Some(other) => match other.strip_prefix("noAnswer.") {
            Some(reason) => format!("ended with no answer ({reason})."),
            None => format!("was resolved by the engine ({other})."),
        },
        None => "was resolved elsewhere; the engine did not say how.".to_string(),
    };
    Some(format!("{subject} {verdict}"))
}

/// Events the reducer understands.///
/// Engine notifications and local user actions are unified so that ordering
/// between them is explicit rather than accidental.
#[derive(Debug, Clone)]
pub enum UiEvent {
    /// A notification from the engine.
    Engine(Event),
    /// The handshake completed.
    Connected { session_id: String },
    /// The user submitted text.
    Submitted { text: String },
    /// The submission was queued because a turn was already running.
    Queued { text: String, id: Option<String> },
    /// The engine atomically withdrew these entries before delivery.
    SteeringRecalled { message_ids: Vec<String> },
    /// Delivery is confirmed, but its user text is already in rehydrated history.
    SteeringDeliveryReflected { message_ids: Vec<String> },
    /// A turn finished, from the `session/prompt` response.
    TurnFinished { interrupted: bool, error: Option<String> },
    /// The user asked to interrupt.
    InterruptRequested,
    /// The engine asked the user something.
    PromptRequested(PendingPrompt),
    /// The user answered the outstanding prompt.
    PromptAnswered { allowed: bool, answer: Option<String> },
    /// The outstanding prompt ended without this client answering it.
    ///
    /// Kept separate from [`UiEvent::PromptAnswered`] on purpose: that event
    /// means "the operator at this terminal decided", and the transcript
    /// records it as their decision. This one means the opposite, and must
    /// never be rendered as though the person sitting here chose anything.
    PromptResolved(ExternalResolution),
    /// Output produced locally by a slash command.
    CommandOutput { text: String },
    /// A git diff to display with syntax colouring.
    DiffOutput { text: String },
    /// A local status or error line.
    Notice { text: String, level: NoticeLevel },
    /// Passive recovery warnings obey the same safe boundaries as notifications.
    NotificationRecoveryWarning { text: String },
    /// The transcript was cleared.
    Cleared,
    /// The active model changed.
    ModelChanged { id: String, context_limit: Option<i64> },
    /// Fold or unfold the reasoning block at this index.
    ///
    /// An event rather than a direct call so it passes through the same
    /// invalidation as every other transcript change. Toggling the block
    /// directly left the fold flipped internally while the cached rows —
    /// rebuilt only on a width change — kept drawing the old shape, so a
    /// click did nothing at all once a turn had finished.
    ThinkingFoldToggled { block: usize },
    ToolGroupFoldToggled { block: usize },
    /// The tool display mode changed.
    DisplayModeChanged(ToolDisplayMode),
    /// Activates assistant-text buffering for the current and future turns.
    ///
    /// **Seam**: this event is emitted by the hook system when an `AgentResponse`
    /// redaction hook is configured.  It is never emitted by the current Rust
    /// front-end because the hook engine lives in `coda-agent` (another agent
    /// is porting that).  The full reducer logic is implemented here so it is
    /// correct and tested, ready for when the hook seam closes.
    EnableAssistantBuffering,
    /// The engine's own lifecycle, from `event/lifecycle` or a snapshot.
    ///
    /// Authoritative over anything derived locally: the engine is the only
    /// thing that knows whether the single-flight slot is free, and a legacy
    /// `event/turnComplete` arrives *before* it is released.
    CoreLifecycle(coda_proto::state::EngineLifecycle),
    /// The running turn's phase, from `event/activity`.
    ///
    /// Metadata only: it carries no conversation content, so applying it
    /// alongside the legacy content events cannot double anything.
    CoreActivity(coda_proto::state::ActivityPhase),
    /// Engine-owned truth, reconciled in one transaction.
    ///
    /// Sent on connect, after a resync, and after an engine-owned reset —
    /// never per token. Everything the engine owns (queue contents, effective
    /// configuration, usage, the turn clock, lifecycle) is replaced; the
    /// purely local presentation state (composer draft, unsent recovery,
    /// selection) is not.
    Snapshot(Box<coda_proto::state::StateSnapshot>),
    /// The conversation was rebuilt from `session/getHistory`.
    ///
    /// `coverage` is what *that* read proved about the committed prefix, from
    /// the answer's own fences. `None` means the rebuild carries no provable
    /// claim (a legacy engine, or a caller that is not a history read), and
    /// any previous claim is dropped rather than left to describe a
    /// conversation it no longer matches.
    Rehydrated {
        blocks: Vec<Block>,
        notices: Vec<String>,
        coverage: Option<HistoryCoverage>,
    },
    /// No engine is answering any more — a sign-out, an engine that went
    /// away, or a replacement that did not start.
    ///
    /// The conversation, the draft and the session's metadata are kept: this
    /// says what is *true now*, it does not throw anything away.
    EngineDisconnected,
    /// A freshly started engine was adopted as this session's connection.
    EngineAdopted,
}

/// The clock behind a live reasoning row.
///
/// Base offset plus a local instant, never a backdated `Instant`: the offset
/// can come from the engine (`TurnState.phase_elapsed_ms`), and a remote
/// duration older than this process cannot be subtracted from `Instant::now()`
/// at all.
#[derive(Debug, Clone, Copy)]
struct ThinkingClock {
    origin: std::time::Instant,
    offset_ms: i64,
}

impl ThinkingClock {
    fn start(now: std::time::Instant, offset_ms: i64) -> Self {
        Self { origin: now, offset_ms: offset_ms.max(0) }
    }

    fn elapsed_ms(&self, now: std::time::Instant) -> i64 {
        let local = now
            .saturating_duration_since(self.origin)
            .as_millis()
            .min(i64::MAX as u128) as i64;
        self.offset_ms.saturating_add(local)
    }

    /// Adopts the engine's own measure of this burst, but only when it is
    /// ahead of ours: a refresh of the same turn must never rewind a clock
    /// the user is watching.
    fn adopt(&mut self, reported_ms: i64, now: std::time::Instant) {
        let reported = reported_ms.max(0);
        if reported > self.elapsed_ms(now) {
            *self = Self::start(now, reported);
        }
    }
}

/// Provider-scoped display names learned from a `session/models` read,
/// keyed by the canonical model id.
///
/// A resync only ever reports the canonical `(providerId, model)` pair —
/// [`coda_proto::state::StateSnapshot`] carries no name for it, that is what
/// [`coda_proto::messages::ModelsResult`] is for. Caching what that read
/// said lets [`UiState::reconcile`] keep showing a friendly name across a
/// resync that cannot itself carry one, without ever inventing one: an id
/// this cache does not know, or one learned under a different provider,
/// resolves to itself.
#[derive(Debug, Clone, Default)]
pub struct ModelLabelCache {
    provider_id: Option<String>,
    labels: std::collections::HashMap<String, String>,
}

impl ModelLabelCache {
    /// Names are display-only: keep them bounded and on one terminal line.
    pub fn display_label(model: &coda_proto::messages::WireModel) -> String {
        let clean = coda_render::text::sanitize(model.label())
            .split_whitespace().collect::<Vec<_>>().join(" ");
        let clean = if clean.is_empty() {
            coda_render::text::sanitize(&model.id)
                .split_whitespace().collect::<Vec<_>>().join(" ")
        } else {
            clean
        };
        coda_render::text::truncate_with_ellipsis(&clean, 128)
    }

    /// Replaces the cache with one provider's current model list.
    ///
    /// A full replacement, not a merge: a `session/models` read is the
    /// engine's authoritative answer for that provider right now, so a name
    /// that changed or a model that was removed from the list must not leave
    /// a stale label behind under the old id.
    pub fn replace(&mut self, provider_id: Option<String>, models: &[coda_proto::messages::WireModel]) {
        self.provider_id = provider_id;
        self.labels.clear();
        for model in models {
            let label = Self::display_label(model);
            if label != model.id {
                self.labels.insert(model.id.clone(), label);
            }
        }
    }

    /// Discards every learned label.
    ///
    /// Called when the engine identity changes: a new process's names are
    /// not evidence about the previous one's, even for an id the two happen
    /// to share.
    pub fn clear(&mut self) {
        self.provider_id = None;
        self.labels.clear();
    }

    /// The friendly name for `id` under `provider_id`, or `id` itself when
    /// the cache does not know it — including when it was filled for a
    /// different provider, so one provider's names never decorate another
    /// provider's id.
    pub fn resolve<'a>(&'a self, provider_id: Option<&str>, id: &'a str) -> &'a str {
        if self.provider_id.as_deref() != provider_id {
            return id;
        }
        self.labels.get(id).map(String::as_str).unwrap_or(id)
    }
}

/// Everything the UI draws from.
#[derive(Debug)]
pub struct UiState {
    pub transcript: Transcript,
    pub activity: Activity,
    pub usage: Usage,
    pub session_id: Option<String>,
    pub model: Option<String>,
    pub effort: Option<String>,
    pub display_mode: ToolDisplayMode,
    /// Messages typed while a turn was running.
    pub queued: Vec<QueuedMessage>,
    /// Queued messages that never reached the model before the turn ended
    /// (cancelled, errored, or simply finished first).
    ///
    /// Kept — not dropped — so a message the user cared enough to type is
    /// recoverable with a single keystroke rather than silently lost. This is
    /// the one canonical store for their text once the turn ends; the notice
    /// shown alongside it is just a pointer to this list, not a second copy.
    pub unsent: Vec<QueuedMessage>,
    /// Delivered-but-not-yet-materialised transcript blocks.
    ///
    /// A steering delivery can be reported while the transcript's tail block
    /// (e.g. an in-progress assistant reply) is still open. Inserting the
    /// delivered `User` block immediately would become the new tail and split
    /// the reply the moment more text arrived. Instead it waits here until
    /// the tail actually closes, so "a" then a mid-stream delivery then "b"
    /// still lands as one assistant block, with the delivered message
    /// appended right after in chronological order.
    pending_deliveries: Vec<Block>,
    /// `event/agentMessage` notifications parked behind an open block for
    /// the same reason as `pending_deliveries` above — but tracked
    /// separately (see `flush_pending_agent_messages`) because a rehydration
    /// rescue for a *steering* delivery relies on the engine's own history
    /// already containing it, which is never true of a bus notification: a
    /// shared list would let it be silently dropped instead of flushed.
    pending_agent_messages: Vec<Block>,
    /// Text this client knows reached the model but which the engine's own
    /// rebuilt conversation does not contain.
    ///
    /// One cause, and it is legitimate: the live projection of a running turn
    /// is byte-budgeted, and a message that arrives with no room left is
    /// omitted from it entirely (`coda-serve`'s `push_user_block` returns
    /// without an entry when the remaining room is zero). Rebuilding from
    /// such a read and dropping the local copy would destroy the only full
    /// copy of what the operator typed.
    ///
    /// Deliberately *not* `unsent`: these did reach the model, so they are
    /// never described as unsent and never resent. They are kept as the
    /// operator's own text, recoverable with Up once the recovery list is
    /// empty.
    pub delivered_local: Vec<QueuedMessage>,
    /// What the last applied `session/getHistory` proved about the committed
    /// prefix, used to tell "already in the conversation I rebuilt" from
    /// "genuinely not on screen yet". See [`crate::coverage`].
    history_coverage: Option<HistoryCoverage>,
    /// The turn's own clock and phase, for the pinned activity row.
    ///
    /// `Some` from a successful local `Submitted` until the next one starts;
    /// `None` before the first turn and briefly at startup. Independent of
    /// `activity` so the row can show a truthful phase and elapsed time
    /// without changing what the status bar has always meant.
    pub turn_progress: Option<TurnProgress>,
    /// The engine's id for the turn [`Self::turn_progress`] is timing, once a
    /// snapshot has named it.
    ///
    /// `None` between a local submission and the first snapshot that
    /// describes it: the client started the clock before the engine had
    /// spoken, so it does not yet know the id. Single-flight means there is
    /// at most one running turn, so an unnamed running clock and the turn a
    /// snapshot reports are the same turn — but a *differently* named turn is
    /// not, and must not inherit the previous turn's reasoning.
    pub turn_id: Option<String>,
    /// The prompt currently blocking the turn, if any.
    pub prompt: Option<PendingPrompt>,
    /// Set once the user has asked to quit.
    pub should_quit: bool,
    /// Set while an interrupt has been requested but not yet acknowledged.
    pub interrupting: bool,
    /// When `Some`, assistant text is buffered here instead of being streamed
    /// to the transcript.  Activated by `UiEvent::EnableAssistantBuffering`
    /// (the seam for hook-phase-3 buffering).  Flushed as a completed block on
    /// turn success; withheld on interruption if the hook never rewrote it.
    pub assistant_buffer: Option<String>,
    /// Set when a `ResponseRewritten` engine event replaces the buffer's
    /// contents.  Determines whether to flush or withhold on interruption.
    pub buffer_rewritten_by_hook: bool,
    /// Frame of the working indicator.
    ///
    /// Advanced by the event loop rather than the renderer, so drawing the
    /// same state twice gives the same picture and the render tests stay
    /// deterministic.
    pub spinner: usize,
    /// Priority queue of transient hint-line messages.
    ///
    /// For things worth saying once and not worth keeping — "copied 412
    /// characters", "press Ctrl+C again to exit".  The highest-priority
    /// non-expired entry wins; when all entries age out the draw layer falls
    /// back to scroll guidance.
    pub hints: HintQueue,
    /// Timestamp source, injected so tests are deterministic.
    clock: fn() -> String,
    /// The live reasoning clock, while a reasoning block is open.
    thinking_clock: Option<ThinkingClock>,
    /// The engine's own lifecycle, once it has reported one.
    ///
    /// `None` on a legacy connection, where nothing publishes it and the
    /// pre-contract behaviour (a turn ends when `event/turnComplete` says so)
    /// is all there is.
    pub core_lifecycle: Option<coda_proto::state::EngineLifecycle>,
    /// Set between a local submission and the engine's first acknowledgement.
    ///
    /// The window is real and visible: a prompt is sent, and until the engine
    /// publishes `busy` a snapshot taken in between honestly reports an idle
    /// engine. Reconciling that snapshot without this flag would flip the UI
    /// back to "ready" under a submission the user has already made.
    optimistic_submit: bool,
    /// The model the **running** turn captured, when it differs from the one
    /// the next turn will use. `None` when they agree or nothing is running.
    ///
    /// There is no pending-change scheduler in the engine: a mid-turn model
    /// switch takes effect next turn, and this is what makes "changed" and
    /// "in effect" distinguishable rather than the UI claiming a switch that
    /// the running turn is not using.
    pub active_model: Option<String>,
    /// Friendly names learned from `session/models`, scoped to the provider
    /// the engine connected with.
    ///
    /// [`Self::reconcile`] is the only reader that needs this: a resync
    /// carries the canonical model id and nothing else, and this is how it
    /// still shows a name instead of regressing to that id.
    pub model_labels: ModelLabelCache,
    /// Stable ids of `event/agentMessage` notifications already materialised
    /// as a `Block::AgentMessage`, scoped to the current engine instance.
    ///
    /// Live delivery and recovery can both describe the same notification
    /// (e.g. a reconnect replays what was already seen live), so dedup is by
    /// this stable id, never by content or position. Cleared whenever the
    /// engine instance is replaced (see `serve.rs::adopt_engine`), so a new
    /// process's ids are never compared against the previous one's.
    agent_message_ids: std::collections::HashSet<String>,
}

impl Default for UiState {
    fn default() -> Self {
        Self::new()
    }
}

impl UiState {
    pub fn new() -> Self {
        Self {
            transcript: Transcript::new(),
            activity: Activity::Initializing,
            usage: Usage::default(),
            session_id: None,
            model: None,
            effort: None,
            display_mode: ToolDisplayMode::default(),
            queued: Vec::new(),
            unsent: Vec::new(),
            pending_deliveries: Vec::new(),
            pending_agent_messages: Vec::new(),
            delivered_local: Vec::new(),
            history_coverage: None,
            turn_progress: None,
            turn_id: None,
            prompt: None,
            should_quit: false,
            interrupting: false,
            assistant_buffer: None,
            buffer_rewritten_by_hook: false,
            spinner: 0,
            hints: HintQueue::new(),

            clock: default_timestamp,
            thinking_clock: None,
            core_lifecycle: None,
            optimistic_submit: false,
            active_model: None,
            model_labels: ModelLabelCache::default(),
            agent_message_ids: std::collections::HashSet::new(),
        }
    }

    /// Builds a state with a fixed clock, for deterministic tests.
    pub fn with_clock(clock: fn() -> String) -> Self {
        Self {
            clock,
            ..Self::new()
        }
    }

    /// Whether a turn is in flight.
    pub fn is_busy(&self) -> bool {
        matches!(
            self.activity,
            Activity::Working | Activity::Thinking | Activity::Waiting
        )
    }

    /// Whether the transcript currently shows any of the *conversation* —
    /// as opposed to this client's own notices, banner and command output.
    ///
    /// The distinction matters when the engine reports an empty history: an
    /// empty conversation must replace a conversation, but it is not a reason
    /// to wipe the launch banner of a session that never had one.
    pub fn has_conversation(&self) -> bool {
        self.transcript.blocks().iter().any(|block| {
            matches!(
                block,
                Block::User { .. }
                    | Block::Assistant { .. }
                    | Block::Thinking { .. }
                    | Block::Tools { .. }
                    | Block::Permission { .. }
                    | Block::Question { .. }
            )
        })
    }

    /// Restores the most recently unsent message's text, removing it from
    /// the recovery list.
    ///
    /// LIFO: the last thing that failed to send is the most likely thing the
    /// user wants back. Returns `None` when nothing is recoverable, so the
    /// caller can fall back to ordinary history recall.
    ///
    /// Once the recovery list is empty this also hands back a *delivered*
    /// message whose text the engine's rebuilt conversation could not show
    /// (see [`Self::delivered_local`]). Recall is "give me my text back", not
    /// a claim that it was never sent — and the alternative is the only full
    /// copy of it existing nowhere at all.
    pub fn recall_unsent(&mut self) -> Option<String> {
        self.unsent
            .pop()
            .or_else(|| self.delivered_local.pop())
            .map(|m| m.text)
    }

    /// Closes whatever block is open, then appends any transcript inserts
    /// that were deferred while it was open.
    ///
    /// Every place that ends a block — a new one starting, a turn ending —
    /// calls this instead of `Transcript::close_open` directly, so a
    /// delivered steering message can never be lost or reordered: it always
    /// lands immediately after the reply it interrupted, whenever that reply
    /// actually finishes.
    fn close_open_and_flush(&mut self) {
        self.transcript.close_open();
        self.flush_pending_content();
    }

    /// Appends the deliveries that were waiting for the open block to close.
    ///
    /// Separate from [`Self::close_open_and_flush`] because a block can also
    /// be closed *in place* — `event/assistantTextComplete` and
    /// `event/thinkingComplete` mark the tail complete where it stands rather
    /// than closing it through the transcript. Those are precisely the
    /// boundaries a parked delivery is waiting for, and without this the
    /// messages sat in `pending_deliveries` after the reply they belonged
    /// behind had visibly finished — an operator who queued a dozen
    /// follow-ups watched the queue empty with nothing appearing in the
    /// conversation, until some later boundary happened to flush them.
    fn flush_pending_deliveries(&mut self) {
        for block in std::mem::take(&mut self.pending_deliveries) {
            self.transcript.push(block);
        }
    }

    /// Appends `event/agentMessage` notifications that were parked behind an
    /// open block — the same "never split active text" rule
    /// [`Self::flush_pending_deliveries`] exists for, kept as its own list
    /// (see [`Self::push_agent_message`]) rather than sharing
    /// `pending_deliveries`: a rebuild's rescue path
    /// ([`Self::retain_parked_deliveries`]) is specific to steering
    /// deliveries the engine's own history is guaranteed to already contain,
    /// which is not true of a notification from this bus — sharing the list
    /// would let a rebuild silently discard a still-parked notification
    /// instead of flushing it.
    fn flush_pending_agent_messages(&mut self) {
        for block in std::mem::take(&mut self.pending_agent_messages) {
            self.transcript.push(block);
        }
    }

    /// Every safe-boundary flush in one call: both kinds of content parked
    /// behind an open block land together, in the order they were parked
    /// within each list.
    fn flush_pending_content(&mut self) {
        self.flush_pending_deliveries();
        self.flush_pending_agent_messages();
    }

    /// Appends a delivered user message, deferring it if the transcript's
    /// tail is still open so it cannot split an in-progress reply.
    fn insert_delivered_user(&mut self, block: Block) {
        if self.transcript.open_tail().is_some() {
            self.pending_deliveries.push(block);
        } else {
            self.transcript.push(block);
        }
    }

    /// Whether the steering message `id` is already on screen — or already
    /// waiting to be, behind an open block.
    ///
    /// Identity is the queue id and nothing else. Two follow-ups with the
    /// same words are two follow-ups, so matching on text would silently
    /// swallow the second; and the id is on both the block this client
    /// materialised itself and the one a conversation rebuilt from the engine
    /// carries, so this answers the same question in both directions.
    fn delivery_is_shown(&self, id: &str) -> bool {
        let is_match = |block: &Block| {
            matches!(block, Block::User { queue_id: Some(queue_id), .. } if queue_id == id)
        };
        self.transcript.blocks().iter().any(is_match)
            || self.pending_deliveries.iter().any(is_match)
    }

    /// The text the conversation currently shows for steering message `id`.
    ///
    /// Looked up by id, never by words: this answers "how much of that
    /// message is on screen", which is a different question from "is this the
    /// same message", and only the id can answer the second one.
    fn shown_delivery_text(&self, id: &str) -> Option<&str> {
        self.transcript.blocks().iter().find_map(|block| match block {
            Block::User { text, queue_id: Some(queue_id), .. } if queue_id == id => {
                Some(text.as_str())
            }
            _ => None,
        })
    }

    /// Takes the local copies of every message in `message_ids` out of the
    /// queue, wherever this client is holding them.
    ///
    /// `unsent` is searched too, deliberately: a turn that ends before the
    /// delivery notification arrives strands the message there, and a
    /// notification that only looked at `queued` would find nothing, leaving
    /// a message the model *did* receive sitting in the recovery list
    /// labelled as never sent.
    fn take_delivered(&mut self, message_ids: &[String]) -> Vec<(QueuedMessage, Placement)> {
        let mut taken: Vec<(QueuedMessage, Placement)> = Vec::new();
        for (list, placement) in
            [(&mut self.queued, Placement::InTurn), (&mut self.unsent, Placement::Late)]
        {
            list.retain(|message| {
                let matched = message.id.as_ref().is_some_and(|id| message_ids.contains(id));
                if matched {
                    taken.push((message.clone(), placement));
                }
                !matched
            });
        }
        taken
    }

    /// Puts a delivered message on screen as the user's own, once.
    ///
    /// The text is this client's copy — the whole thing, including anything
    /// the engine's own queue preview had to cut — so what is shown is what
    /// was typed. A message with no id cannot be recognised later and is
    /// shown as it arrives; one whose id is already on screen is not shown
    /// again.
    ///
    /// The timestamp is when the operator actually queued it, never "now":
    /// a delivery confirmed after the fact still happened when it happened,
    /// and stamping the current time would state a send time that is false.
    fn materialize_delivered(&mut self, message: QueuedMessage) {
        if message.id.as_deref().is_some_and(|id| self.delivery_is_shown(id)) {
            return;
        }
        let timestamp = if message.queued_at.is_empty() {
            (self.clock)()
        } else {
            message.queued_at
        };
        self.insert_delivered_user(Block::User {
            text: message.text,
            timestamp,
            pending: false,
            queue_id: message.id,
        });
    }

    /// Shows every delivered message in `messages`, reporting how many of
    /// them were actually placed later than they happened.
    ///
    /// The count is what the caller turns into a notice, and that notice is
    /// the honest part. A message confirmed after its turn ended is appended
    /// at the bottom of the transcript, which is *not* where it was said — so
    /// rather than letting the position imply a chronology that is wrong, the
    /// placement is named out loud, together with the fact that nothing was
    /// sent a second time.
    fn materialize_all(&mut self, messages: Vec<(QueuedMessage, Placement)>) -> usize {
        let mut late = 0usize;
        for (message, placement) in messages {
            let already_shown =
                message.id.as_deref().is_some_and(|id| self.delivery_is_shown(id));
            if placement == Placement::Late && !already_shown {
                late += 1;
            }
            self.materialize_delivered(message);
        }
        late
    }

    /// Says that `count` messages have been put on screen somewhere other
    /// than where they were said.
    ///
    /// `corrected` when this client had already told the operator they were
    /// not sent: that claim was wrong and is withdrawn here rather than being
    /// left standing above a message that is now visibly in the conversation.
    fn note_late_placement(&mut self, count: usize, corrected: bool) {
        if count == 0 {
            return;
        }
        let one = count == 1;
        let subject = if one { "1 message".to_string() } else { format!("{count} messages") };
        let lead = if corrected {
            format!(
                "{subject} reported as not sent did reach the model after all. \
                 {} shown below",
                if one { "It is" } else { "They are" },
            )
        } else {
            format!(
                "{subject}, delivered earlier and confirmed only now, {} shown below",
                if one { "is" } else { "are" },
            )
        };
        self.notice(
            format!(
                "{lead} — below the conversation rather than in the place it was said, \
                 keeping the time {} sent — and {} not sent again.",
                if one { "it was" } else { "they were" },
                if one { "it was" } else { "they were" },
            ),
            NoticeLevel::Info,
        );
    }

    /// Settles the deliveries parked behind an open block when the
    /// conversation is rebuilt underneath them.
    ///
    /// The parked block is normally dropped, and that is safe for one
    /// specific reason: this client only ever learns of a delivery *after*
    /// the engine has recorded it, and the engine writes the text into the
    /// running turn's projection in the same transaction. So any rebuild that
    /// could contain a parked message does contain it — with its queue id,
    /// which is how the two are recognised as the same message.
    ///
    /// There is exactly one legitimate exception, and dropping the copy there
    /// destroys the operator's text: the live projection is byte-budgeted,
    /// and a steering message that arrives with little or no room left is cut
    /// short — or omitted from it entirely rather than shortened
    /// (`coda-serve`'s `push_user_block` returns without an entry when the
    /// room left is zero). The read says so — `liveTruncated` — so when a
    /// rebuild that announced truncation shows less of a parked message than
    /// this client is holding, the full text is kept rather than lost.
    ///
    /// It is kept *out* of the transcript on purpose. The rebuild is the
    /// engine's account of the conversation; appending a block the engine did
    /// not return would put this client's copy at the bottom of a
    /// conversation that has moved on, and committed history carries no queue
    /// id to recognise it by later. So it becomes recoverable text, never a
    /// second message and never a resend.
    fn retain_parked_deliveries(&mut self) {
        let parked = std::mem::take(&mut self.pending_deliveries);
        let truncated = self
            .history_coverage
            .as_ref()
            .is_some_and(HistoryCoverage::live_was_truncated);
        if !truncated {
            return;
        }
        let mut kept = 0usize;
        for block in parked {
            let Block::User { text, timestamp, queue_id: Some(id), .. } = block else {
                continue;
            };
            // Shown in full already: the rebuild is the better copy.
            if self.shown_delivery_text(&id).is_some_and(|shown| shown.len() >= text.len()) {
                continue;
            }
            kept += 1;
            self.delivered_local.push(QueuedMessage {
                id: Some(id),
                text,
                queued_at: timestamp,
            });
        }
        if kept > 0 {
            self.notice(
                format!(
                    "The engine's view of the running turn ran out of room, so {} not shown \
                     above in full. {} delivered — nothing was sent again — and your own \
                     full text is recoverable with Up.",
                    if kept == 1 {
                        "a delivered message is"
                    } else {
                        "some delivered messages are"
                    },
                    if kept == 1 { "It was" } else { "They were" },
                ),
                NoticeLevel::Warning,
            );
        }
    }

    /// Updates live elapsed time; returns whether its displayed second changed.
    pub(crate) fn tick_thinking(&mut self, now: std::time::Instant) -> bool {
        let Some(clock) = self.thinking_clock else {
            return false;
        };
        let Some(Block::Thinking { elapsed_ms, .. }) = self.transcript.open_tail() else {
            self.thinking_clock = None;
            return false;
        };
        let elapsed = clock.elapsed_ms(now);
        let changed = *elapsed_ms / 1000 != elapsed / 1000;
        *elapsed_ms = elapsed;
        changed
    }

    /// Puts a live reasoning row on screen for reasoning the engine says is
    /// happening now, without inventing any of its text.
    ///
    /// The event that opens one can be lost legitimately: a frame at or below
    /// a snapshot's cursor is dropped as already reflected, and a snapshot
    /// carries no conversation content to reflect it with. The authoritative
    /// `reasoning` phase is then the only thing left that says the model is
    /// reasoning *right now*, so it opens the row the lost frame would have.
    ///
    /// `reported_ms` is `TurnState.phase_elapsed_ms` when the phase is
    /// `Reasoning` — the engine's own measure of *this burst* — and `None`
    /// from a bare `event/activity`, which carries no duration.
    fn ensure_live_thinking(&mut self, now: std::time::Instant, reported_ms: Option<i64>) {
        // Buffered (hook) turns suppress reasoning entirely; a row opened
        // here would be the seam for the very text that must not be shown.
        if self.assistant_buffer.is_some() {
            return;
        }
        if matches!(self.transcript.open_tail(), Some(Block::Thinking { .. })) {
            match (self.thinking_clock.as_mut(), reported_ms) {
                (Some(clock), Some(reported)) => clock.adopt(reported, now),
                // Live on screen with no clock behind it: a rebuilt
                // conversation whose tail the engine is still streaming.
                (None, reported) => {
                    self.thinking_clock = Some(ThinkingClock::start(now, reported.unwrap_or(0)));
                }
                (Some(_), None) => {}
            }
            return;
        }
        // A finished burst is already on screen and the phase has not moved
        // on yet: it describes *that* burst, so reopening it would show the
        // same reasoning twice and restart a clock that is already frozen.
        if matches!(self.transcript.blocks().last(), Some(Block::Thinking { .. })) {
            return;
        }
        self.close_open_and_flush();
        let clock = ThinkingClock::start(now, reported_ms.unwrap_or(0));
        self.transcript.push(Block::Thinking {
            text: String::new(),
            elapsed_ms: clock.offset_ms,
            tokens: None,
            complete: false,
            expanded: false,
            done_at: None,
        });
        self.thinking_clock = Some(clock);
    }

    /// The live reasoning row's own state, taken before the conversation is
    /// rebuilt so a rebuild that describes the same burst can restore it.
    fn take_live_thinking(&mut self) -> Option<(ThinkingClock, bool)> {
        let expanded = match self.transcript.open_tail() {
            Some(Block::Thinking { expanded, .. }) => *expanded,
            _ => return None,
        };
        self.thinking_clock.map(|clock| (clock, expanded))
    }

    /// Restores a live reasoning row across a rebuild of the conversation.
    ///
    /// Only a tail the engine itself reported as still open is live: a
    /// committed reasoning summary is history and must never start ticking.
    fn restore_live_thinking(
        &mut self,
        carried: Option<(ThinkingClock, bool)>,
        now: std::time::Instant,
    ) {
        let Some(Block::Thinking { expanded, .. }) = self.transcript.open_tail() else {
            self.thinking_clock = None;
            return;
        };
        match carried {
            Some((clock, was_expanded)) => {
                *expanded = was_expanded;
                self.thinking_clock = Some(clock);
            }
            // The burst began before this client was watching it. How long it
            // has run is not knowable from a history read, so the clock starts
            // at zero rather than claiming a duration.
            None => self.thinking_clock = Some(ThinkingClock::start(now, 0)),
        }
    }

    /// Advances the state by one event.
    pub fn apply(&mut self, event: UiEvent) {
        self.apply_at(event, std::time::Instant::now());
    }

    fn apply_at(&mut self, event: UiEvent, now: std::time::Instant) {
        self.tick_thinking(now);
        match event {
            UiEvent::Engine(event) => self.apply_engine(event, now),
            UiEvent::Connected { session_id } => {
                self.session_id = Some(session_id);
                self.activity = Activity::Ready;
            }
            // The two ends of one lifetime, in the reducer rather than in the
            // renderer: the label, the spinner and `is_busy` all read the same
            // field, so they cannot describe different sessions.
            UiEvent::EngineDisconnected => {
                self.activity = Activity::Disconnected;
                // Nothing is running any more, so nothing may claim to be.
                self.interrupting = false;
                self.optimistic_submit = false;
                self.turn_progress = None;
                self.turn_id = None;
                self.thinking_clock = None;
                self.core_lifecycle = None;
            }
            UiEvent::EngineAdopted => {
                // A different process, with no turn of its own yet.
                self.activity = Activity::Ready;
                self.interrupting = false;
                self.optimistic_submit = false;
                self.turn_progress = None;
                self.turn_id = None;
                self.core_lifecycle = None;
                // Whatever this cache learned belonged to the process that
                // is gone: a replacement can reuse the same provider id for
                // a differently named model, and must re-earn its labels
                // rather than inherit its predecessor's.
                self.model_labels.clear();
            }
            UiEvent::Submitted { text } => {
                self.close_open_and_flush();
                self.transcript.push(Block::User {
                    text,
                    timestamp: (self.clock)(),
                    pending: false,
                    queue_id: None,
                });
                self.activity = Activity::Working;
                self.interrupting = false;
                // Optimism, on purpose: the engine has not been told yet, so
                // a snapshot taken right now truthfully says "idle". This
                // flag is what stops that honest answer from undoing the
                // feedback the user has already been given.
                self.optimistic_submit = true;
                // Started before any engine or network event, so the pinned
                // row has a truthful "0s, Working" to show on the very first
                // frame rather than waiting for the first response.
                self.turn_progress = Some(TurnProgress::start(now));
                // The engine has not named this turn yet, and inventing an id
                // would make the next snapshot look like a different turn.
                self.turn_id = None;
            }
            UiEvent::Queued { text, id } => {
                // Kept only here — never mirrored into the transcript as a
                // pending bubble. The old behaviour pushed a pending `User`
                // block immediately, which became the new tail and split
                // whatever assistant block was streaming into two. The
                // actual delivered message is appended later, at the safe
                // boundary the engine reports via `SteeringDelivered`.
                self.queued.push(QueuedMessage {
                    id,
                    text,
                    queued_at: (self.clock)(),
                });
            }
            UiEvent::SteeringRecalled { message_ids }
            | UiEvent::SteeringDeliveryReflected { message_ids } => {
                let retained = |message: &QueuedMessage| {
                    !message.id.as_ref().is_some_and(|id| message_ids.contains(id))
                };
                self.queued.retain(retained);
                self.unsent.retain(retained);
            }
            UiEvent::TurnFinished { interrupted, error } => {
                self.transcript.end_tool_group();
                self.close_open_and_flush();
                self.transcript.finalize_activities(None);
                self.strand_unsent_queue();
                self.settle_after_turn();
                self.interrupting = false;
                self.prompt = None;
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.finish(now);
                }

                if interrupted {
                    self.notice("Interrupted.", NoticeLevel::Warning);
                } else if let Some(error) = error {
                    self.notice(error, NoticeLevel::Error);
                }
            }
            UiEvent::InterruptRequested => {
                if self.is_busy() {
                    self.interrupting = true;
                }
            }
            UiEvent::PromptRequested(prompt) => {
                self.prompt = Some(prompt);
                self.activity = Activity::Waiting;
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.on_awaiting_approval(now);
                }
            }
            UiEvent::PromptAnswered { allowed, answer } => {
                let prompt = self.prompt.take();
                self.activity = Activity::Working;
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.on_resumed();
                }

                match prompt {
                    Some(PendingPrompt::Permission { tool, preview }) => {
                        self.transcript.push(Block::Permission {
                            tool,
                            preview,
                            decision: if allowed {
                                PermissionDecision::Allowed
                            } else {
                                PermissionDecision::Denied
                            },
                        });
                    }
                    Some(PendingPrompt::Question { question, .. }) => {
                        self.transcript.push(Block::Question { question, answer });
                    }
                    Some(PendingPrompt::PlanApproval { .. }) => {
                        self.notice(
                            if allowed {
                                "Plan approved."
                            } else {
                                "Plan rejected."
                            },
                            NoticeLevel::Info,
                        );
                    }
                    None => {}
                }
            }
            UiEvent::PromptResolved(resolution) => {
                // The decision was made somewhere else, or never made at all.
                // The modal comes down and the turn stops waiting either way;
                // what must *not* happen is a decision block, because this
                // client did not decide anything and its transcript would be
                // claiming that the operator here did.
                let prompt = self.prompt.take();
                self.activity = Activity::Working;
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.on_resumed();
                }
                if let Some(text) = external_resolution_text(&resolution, prompt.as_ref()) {
                    let level = match &resolution {
                        ExternalResolution::Outcome { outcome, .. } => match outcome.as_deref() {
                            Some("allowed") | Some("approved") | Some("answered") => {
                                NoticeLevel::Info
                            }
                            _ => NoticeLevel::Warning,
                        },
                        ExternalResolution::Retired => NoticeLevel::Warning,
                    };
                    self.notice(text, level);
                }
            }
            UiEvent::CommandOutput { text } => {
                self.close_open_and_flush();
                self.transcript.push(Block::CommandOutput { text });
            }
            UiEvent::DiffOutput { text } => {
                self.close_open_and_flush();
                self.transcript.push(Block::Diff { raw: text });
            }
            UiEvent::Notice { text, level } => self.notice(text, level),
            UiEvent::NotificationRecoveryWarning { text } => {
                self.push_notification_block(Block::Notice { text, level: NoticeLevel::Warning });
            }
            UiEvent::Cleared => {
                self.transcript.clear();
                self.queued.clear();
                self.unsent.clear();
                self.pending_deliveries.clear();
                self.pending_agent_messages.clear();
                self.delivered_local.clear();
                // The conversation that read described is not on screen any
                // more, so it can no longer vouch for anything.
                self.history_coverage = None;
                self.turn_progress = None;
            }
            UiEvent::ModelChanged { id, context_limit } => {
                self.model = Some(id);
                if let Some(limit) = context_limit {
                    self.usage.context_limit = limit;
                }
            }
            UiEvent::ThinkingFoldToggled { block } => {
                self.transcript.toggle_fold(block);
            }
            UiEvent::ToolGroupFoldToggled { block } => {
                self.transcript.toggle_tool_group(block);
            }
            UiEvent::DisplayModeChanged(mode) => self.display_mode = mode,
            UiEvent::EnableAssistantBuffering => {
                // Activate buffering; an empty buffer means "buffering on, no text yet".
                if self.assistant_buffer.is_none() {
                    self.assistant_buffer = Some(String::new());
                    self.buffer_rewritten_by_hook = false;
                }
            }
            UiEvent::CoreLifecycle(lifecycle) => self.apply_lifecycle(lifecycle, now),
            UiEvent::CoreActivity(phase) => self.apply_activity_phase(phase, now, None),
            UiEvent::Snapshot(snapshot) => self.reconcile(&snapshot, now),
            UiEvent::Rehydrated { blocks, notices, coverage } => {
                // The engine owns what the *conversation* is. Anything the UI
                // had of it is a projection of an older answer to the same
                // question, so it is replaced outright — but the banner, the
                // launch notices, slash-command output and a rendered diff
                // are this client's own and no history read can return them,
                // so they are kept rather than swept away with it.
                //
                // A rebuild taken mid-burst still describes the burst that is
                // running, so the live row's clock and fold survive it rather
                // than the reasoning appearing to start over. `take_live_thinking`
                // reads `Transcript::open_tail`, which only ever looks at the
                // literal last block — so a parked notification must NOT be
                // flushed onto the transcript before this runs: doing so
                // stops the still-open `Thinking` block from being the tail,
                // losing the clock, and then leaves the rebuilt (still open)
                // burst sitting *behind* the notification, no longer the
                // tail either — so the next delta would silently open a
                // second `Thinking`/`Assistant` block instead of continuing
                // the first.
                let carried = self.take_live_thinking();
                self.transcript.replace_conversation(blocks);
                self.restore_live_thinking(carried, now);
                // A notification parked behind the block this rebuild just
                // replaced has no engine-side history to fall back on
                // (unlike a delivered steering message — see
                // `retain_parked_deliveries`), so it must still land rather
                // than being dropped with the old transcript. It is flushed
                // only now, and only at the same safe boundary
                // `push_agent_message` itself requires: if the rebuild
                // carried the burst over still open, the park must survive
                // it too — the burst has not ended, so nothing here is a
                // safe boundary yet. A later terminal event
                // (`ThinkingComplete`/`AssistantTextComplete`/`TurnComplete`/
                // `Error`/`LimitReached`) already flushes pending content at
                // its own boundary and releases it then.
                if self.transcript.open_tail().is_none() {
                    self.flush_pending_agent_messages();
                }
                // The claim travels with the rebuild it came from: a read
                // whose provenance is unknown replaces the previous claim
                // with nothing rather than leaving it to describe a
                // conversation it no longer matches.
                self.history_coverage = coverage;
                self.retain_parked_deliveries();
                for notice in notices {
                    self.notice(notice, NoticeLevel::Warning);
                }
            }
        }
        if !matches!(self.transcript.open_tail(), Some(Block::Thinking { .. })) {
            self.thinking_clock = None;
        }
    }

    fn apply_engine(&mut self, event: Event, now: std::time::Instant) {
        match event {
            Event::AssistantText { delta } => {
                if delta.is_empty() {
                    return;
                }
                self.activity = Activity::Working;
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.on_responding(now);
                }
                // Buffering mode: accumulate instead of streaming to the transcript.
                if let Some(buf) = self.assistant_buffer.as_mut() {
                    buf.push_str(&delta);
                    return;
                }
                match self.transcript.open_tail() {
                    Some(Block::Assistant { text, .. }) => text.push_str(&delta),
                    _ => {
                        self.close_open_and_flush();
                        self.transcript.push(Block::Assistant {
                            text: delta,
                            complete: false,
                        });
                    }
                }
            }
            Event::AssistantTextComplete => {
                // While buffering, the buffer is flushed on TurnComplete, not here.
                if self.assistant_buffer.is_none() {
                    if let Some(Block::Assistant { complete, .. }) = self.transcript.open_tail() {
                        *complete = true;
                        // The reply is over, and that is exactly the boundary
                        // a delivery parked behind it was waiting for.
                        self.flush_pending_content();
                    }
                }
            }
            Event::Thinking { delta } => {
                // Progress tracks reasoning independently of the transcript
                // buffering rule below: whether or not the *text* is shown,
                // the model genuinely started reasoning, and the pinned row
                // must say so. An empty first delta still counts.
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.on_thinking_start(now);
                }
                // Suppress thinking display during a buffered turn (C# rule).
                if self.assistant_buffer.is_some() {
                    return;
                }
                self.activity = Activity::Thinking;
                match self.transcript.open_tail() {
                    Some(Block::Thinking { text, .. }) => text.push_str(&delta),
                    _ => {
                        self.close_open_and_flush();
                        self.transcript.push(Block::Thinking {
                            text: delta,
                            elapsed_ms: 0,
                            tokens: None,
                            complete: false,
                            expanded: false,
                            done_at: None,
                        });
                        self.thinking_clock = Some(ThinkingClock::start(now, 0));
                    }
                }
            }
            Event::ThinkingComplete {
                elapsed_ms,
                thinking_tokens,
            } => {
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.on_thinking_end(now);
                }
                // Suppress during buffered turn.
                if self.assistant_buffer.is_some() {
                    return;
                }
                let done_at = (self.clock)();
                if let Some(Block::Thinking {
                    elapsed_ms: elapsed,
                    tokens,
                    complete,
                    done_at: done_at_field,
                    ..
                }) = self.transcript.open_tail()
                {
                    *elapsed = elapsed_ms;
                    *tokens = thinking_tokens;
                    *complete = true;
                    *done_at_field = Some(done_at);
                    // The burst is over: anything parked behind it lands now
                    // rather than waiting for some later boundary.
                    self.flush_pending_content();
                } else {
                    // No block to finish. Either none was ever started — a
                    // provider that encrypts its reasoning sends no deltas at
                    // all, only a signed block at the end whose text is empty
                    // — or this completion is a duplicate of one already
                    // shown. A real second burst announces itself with a
                    // `Thinking` event first, so a completion landing on an
                    // already-finished row is the latter and changes nothing:
                    // adding a row for it invented a burst that never ran.
                    if matches!(
                        self.transcript.blocks().last(),
                        Some(Block::Thinking { complete: true, .. })
                    ) {
                        self.activity = Activity::Working;
                        return;
                    }
                    self.close_open_and_flush();
                    self.transcript.push(Block::Thinking {
                        text: String::new(),
                        elapsed_ms,
                        tokens: thinking_tokens,
                        complete: true,
                        expanded: false,
                        done_at: Some(done_at),
                    });
                }
                self.activity = Activity::Working;
            }
            Event::ToolCall {
                tool_name,
                input_json,
                correlation,
            } => {
                self.activity = Activity::Working;
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.on_tool_call_started(now);
                }
                let key = ActivityKey::from_correlation(&correlation);
                let index = self.batch_for_new_call(&key);
                if let Some(Block::Tools { activity, calls, .. }) =
                    self.transcript.blocks_mut().get_mut(index)
                {
                    activity.calls.push(ToolCall::new(tool_name, input_json));
                    calls.push(correlation);
                }
            }
            Event::ToolProgress {
                tool_name,
                elapsed_ms,
                correlation,
            } => {
                if let Some((block, call)) = self.locate_call(&correlation, &tool_name) {
                    if let Some(Block::Tools { activity, .. }) =
                        self.transcript.blocks_mut().get_mut(block)
                    {
                        if let Some(call) = activity.calls.get_mut(call) {
                            call.elapsed_ms = Some(elapsed_ms);
                        }
                    }
                }
            }
            Event::ToolResult {
                tool_name,
                content,
                is_error,
                status,
                correlation,
            } => {
                let status = map_status(status, is_error);
                match self.locate_call(&correlation, &tool_name) {
                    Some((block, call)) => {
                        if let Some(Block::Tools { activity, .. }) =
                            self.transcript.blocks_mut().get_mut(block)
                        {
                            if let Some(call) = activity.calls.get_mut(call) {
                                call.result = Some(content);
                                call.is_error = is_error;
                                call.status = status;
                            }
                        }
                    }
                    None => {
                        // A result with no matching call still deserves to be
                        // shown rather than silently dropped.
                        let key = ActivityKey::from_correlation(&correlation);
                        let index = self.batch_for_new_call(&key);
                        if let Some(Block::Tools { activity, calls, .. }) =
                            self.transcript.blocks_mut().get_mut(index)
                        {
                            let mut call = ToolCall::new(&tool_name, "{}");
                            call.result = Some(content);
                            call.is_error = is_error;
                            call.status = status;
                            activity.calls.push(call);
                            calls.push(correlation);
                        }
                    }
                }
            }
            Event::TurnComplete {
                interrupted,
                root_turn_id,
                ..
            } => {
                self.transcript.end_tool_group();
                self.close_open_and_flush();
                self.transcript.finalize_activities(root_turn_id.as_deref());
                self.strand_unsent_queue();

                // Flush or withhold the assistant buffer.
                // Rule (from C# UiReducer.HandleTurnCompleted / HandleTurnInterrupted):
                // - success → always flush (show the buffered text as a completed block)
                // - interrupted → flush only if the hook already ran (buffer was rewritten);
                //   otherwise withhold to avoid surfacing raw unreviewed model output.
                if interrupted {
                    self.flush_or_withhold_buffer();
                } else {
                    self.flush_buffer();
                }
                self.assistant_buffer = None;
                self.buffer_rewritten_by_hook = false;

                self.settle_after_turn();
                self.interrupting = false;
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.finish(now);
                }
                if interrupted {
                    self.notice("Interrupted.", NoticeLevel::Warning);
                }
            }
            Event::Usage {
                input_tokens,
                output_tokens,
            } => {
                self.usage.input_tokens = input_tokens;
                self.usage.output_tokens = output_tokens;
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.on_usage(input_tokens, output_tokens);
                }
            }
            Event::Error { message } => {
                self.close_open_and_flush();
                self.notice(message, NoticeLevel::Error);
            }
            Event::LimitReached { message, .. } => {
                self.close_open_and_flush();
                self.notice(message, NoticeLevel::Warning);
            }
            Event::SteeringDelivered { message_ids } => {
                // Delivered messages stop being "queued": each becomes a real
                // `User` block. Matching by id (never position) means a
                // duplicate notification finds nothing left to deliver and is
                // a no-op, and delivering the middle of three queued messages
                // promotes only that one.
                //
                // The same id may already have been settled by a snapshot
                // that overtook this event — the engine's state moves before
                // the notification does — so the text is placed only if it is
                // not on screen already.
                //
                // A message taken back out of the recovery list was stranded
                // there by the turn ending first, so appending it now puts it
                // after content that came later. That is said out loud rather
                // than left for the position to imply.
                let delivered = self.take_delivered(&message_ids);
                let corrected = delivered.iter().any(|(_, p)| *p == Placement::Late);
                let late = self.materialize_all(delivered);
                self.note_late_placement(late, corrected);
            }
            Event::TaskCompleted {
                description,
                status,
                ..
            } => {
                let level = if status == "failed" {
                    NoticeLevel::Error
                } else {
                    NoticeLevel::Info
                };
                self.notice(format!("Task {status}: {description}"), level);
            }
            Event::AgentMessage { id, label, text, context, source, task_id, schedule_definition_id, .. } => {
                self.push_agent_message(id, label, text, context, source, task_id, schedule_definition_id);
            }
            Event::PromptRewritten { hook_command, .. } => {
                self.notice(
                    format!("Prompt rewritten by hook: {hook_command}"),
                    NoticeLevel::Info,
                );
            }
            Event::PermissionDecided {
                tool_name, decision, ..
            } => {
                self.transcript.push(Block::Permission {
                    tool: tool_name,
                    preview: "(decided by hook)".to_string(),
                    decision: if decision == "allow" {
                        PermissionDecision::Allowed
                    } else {
                        PermissionDecision::Denied
                    },
                });
            }
            Event::SubagentBlocked { reason, .. } => {
                self.notice(format!("Subagent blocked: {reason}"), NoticeLevel::Warning);
            }
            Event::SubagentResultModified { .. } => {
                // MINOR 8: SubagentResultModified is an informational hook event;
                // the TUI has no UI to display hook payloads, so it is silently
                // accepted (like ToolInputModified and ToolResultModified).
            }
            Event::CompactionCancelled { hook_command, .. } => {
                // Worth surfacing: the user asked for compaction (or it was
                // triggered automatically) and a hook prevented it, so the
                // context is still full and the next turn may hit the limit.
                self.notice(
                    format!("Compaction cancelled by hook: {hook_command}"),
                    NoticeLevel::Warning,
                );
            }
            Event::PostCompactContextInjected { .. } => {
                // Informational: a hook added context after compaction. The
                // content lands in the history rather than the transcript.
            }
            // Informational events with no transcript representation.
            Event::Stop { .. }
            | Event::StreamProgress { .. }
            | Event::ScheduleLifecycle { .. }
            | Event::ToolInputModified { .. }
            | Event::ToolResultModified { .. }
            | Event::PermissionsUpdated { .. }
            | Event::Unknown { .. } => {}
            // When a display-mutating AgentResponse hook rewrites the assistant
            // response, replace the buffer with the hook's display content so
            // the final render uses the cleaned output.  When buffering is off,
            // the text was already streamed to the transcript — this is a no-op.
            // Rule from C# UiReducer: ResponseRewrittenEvent.
            Event::ResponseRewritten { display_content, .. } => {
                if self.assistant_buffer.is_some() {
                    self.assistant_buffer = Some(display_content);
                    self.buffer_rewritten_by_hook = true;
                }
            }
        }
    }

    /// Chooses the batch a newly seen call belongs to.
    ///
    /// A batch may only be extended while it is still the last block: once
    /// assistant text or anything else follows, later calls open a new batch,
    /// which is what keeps interleaved text and tools in the right order.
    fn batch_for_new_call(&mut self, key: &ActivityKey) -> usize {
        if let Some(Block::Tools {
            activity,
            key: existing,
            ..
        }) = self.transcript.blocks().last()
        {
            if !activity.complete && existing == key {
                return self.transcript.len() - 1;
            }
        }

        self.close_open_and_flush();
        self.transcript.push(Block::Tools {
            activity: ToolActivity::default(),
            key: key.clone(),
            calls: Vec::new(),
        });
        self.transcript.len() - 1
    }

    /// Finds the block and call index a correlation refers to.
    ///
    /// Scans backwards for the batch that already owns this exact call, so a
    /// result still lands correctly when assistant text has since opened a new
    /// batch. Falls back to the most recent unfinished call of the same name
    /// when the engine omitted correlation ids.
    fn locate_call(&self, correlation: &Correlation, tool_name: &str) -> Option<(usize, usize)> {
        let blocks = self.transcript.blocks();

        if correlation.call_id.is_some() {
            for (block_index, block) in blocks.iter().enumerate().rev() {
                let Block::Tools { calls, .. } = block else {
                    continue;
                };
                if let Some(call_index) =
                    calls.iter().position(|known| same_call(known, correlation))
                {
                    return Some((block_index, call_index));
                }
            }
            return None;
        }

        // No id to match on: the newest unfinished call of this name is the
        // only defensible guess, and matches a single-threaded turn.
        for (block_index, block) in blocks.iter().enumerate().rev() {
            let Block::Tools { activity, .. } = block else {
                continue;
            };
            if let Some(call_index) = activity
                .calls
                .iter()
                .rposition(|call| call.name == tool_name && !call.status.is_terminal())
            {
                return Some((block_index, call_index));
            }
        }
        None
    }

    fn notice(&mut self, text: impl Into<String>, level: NoticeLevel) {
        self.transcript.push(Block::Notice {
            text: text.into(),
            level,
        });
    }

    /// Materialises one `event/agentMessage` notification, deduplicated by
    /// its stable id.
    ///
    /// Parked behind an open block rather than appended unconditionally —
    /// the same "safe append, never split" rule `notice()` relies on does
    /// **not** hold for a whole new block: `Transcript::open_tail` only ever
    /// looks at the literal last block, so pushing straight through would
    /// stop being that tail and the next streamed delta would open a *second*
    /// `Assistant`/`Thinking` block instead of continuing the first. See
    /// `flush_pending_agent_messages` for where a parked one actually lands.
    fn push_agent_message(
        &mut self,
        id: String,
        label: String,
        text: String,
        context: Option<String>,
        source: String,
        task_id: Option<String>,
        schedule_definition_id: Option<String>,
    ) {
        if !self.agent_message_ids.insert(id.clone()) {
            return;
        }
        let block =
            Block::AgentMessage { id, label, text, context, source, task_id, schedule_definition_id };
        self.push_notification_block(block);
    }

    fn push_notification_block(&mut self, block: Block) {
        if self.transcript.open_tail().is_some() {
            self.pending_agent_messages.push(block);
        } else {
            self.transcript.push(block);
        }
    }

    /// Resets agent-message dedup bookkeeping. Called on engine-instance
    /// replacement (`serve.rs::adopt_engine`) so a new process's ids are
    /// never compared against a previous instance's — the bus cursor is
    /// engine-scoped and does not survive a replacement either. Any
    /// notification still parked from the superseded process is dropped
    /// along with it — it is engine-scoped and would otherwise resurface
    /// under a process it no longer belongs to.
    pub fn reset_agent_message_dedup(&mut self) {
        self.agent_message_ids.clear();
        self.pending_agent_messages.clear();
    }


    // ── Engine-owned truth ───────────────────────────────────────────────

    /// What the UI reports once a turn's content is complete.
    ///
    /// A legacy `event/turnComplete` means "this turn produced its last
    /// output". It does **not** mean the engine will accept another prompt:
    /// the single-flight slot is released separately, and the engine's own
    /// `lifecycle` is what says so. Claiming ready here — as this did before
    /// the state contract existed — let the UI invite a prompt that the
    /// engine then refused as busy, which read as the app losing a message.
    ///
    /// On a legacy connection there is no lifecycle to consult, so the old
    /// behaviour is exactly preserved.
    fn settle_after_turn(&mut self) {
        self.optimistic_submit = false;
        self.prompt = None;
        match self.core_lifecycle {
            Some(coda_proto::state::EngineLifecycle::Busy) => {
                // The engine still owns the slot. Keep showing work in
                // progress until it publishes `ready`.
                self.activity = Activity::Working;
            }
            _ => self.activity = Activity::Ready,
        }
    }

    fn apply_lifecycle(
        &mut self,
        lifecycle: coda_proto::state::EngineLifecycle,
        now: std::time::Instant,
    ) {
        use coda_proto::state::EngineLifecycle as L;
        self.core_lifecycle = Some(lifecycle);
        match lifecycle {
            L::Busy => {
                self.optimistic_submit = false;
                if self.activity == Activity::Ready || self.activity == Activity::Initializing {
                    self.activity = Activity::Working;
                }
            }
            L::Ready => {
                // The slot is free. Anything still shown as running is over,
                // including a turn whose own completion event never arrived.
                if !matches!(self.activity, Activity::Waiting) || self.prompt.is_none() {
                    self.activity = Activity::Ready;
                }
                self.optimistic_submit = false;
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.finish(now);
                }
            }
            L::Initializing => self.activity = Activity::Initializing,
            L::Stopping | L::Stopped => {
                if let Some(progress) = self.turn_progress.as_mut() {
                    progress.finish(now);
                }
            }
        }
    }

    fn apply_activity_phase(
        &mut self,
        phase: coda_proto::state::ActivityPhase,
        now: std::time::Instant,
        phase_elapsed_ms: Option<i64>,
    ) {
        use coda_proto::state::ActivityPhase as P;
        use coda_proto::state::EngineLifecycle as L;
        self.optimistic_submit = false;
        // A phase frame is published only while a turn is open, so observing
        // one *is* observing a busy engine. The engine does not publish a
        // separate `lifecycle: busy` when a turn starts — it publishes the
        // phase — so without this the client would only learn the slot was
        // taken by taking a snapshot, and `event/turnComplete` would then be
        // free to claim ready while the engine still held it.
        if !matches!(self.core_lifecycle, Some(L::Stopping) | Some(L::Stopped)) {
            self.core_lifecycle = Some(L::Busy);
        }
        // Authoritative reasoning shows as reasoning at once, whether or not
        // the frame that opened the burst survived the fence. Only a real
        // `reasoning` phase does this — silence never infers it.
        if phase == P::Reasoning {
            self.ensure_live_thinking(now, phase_elapsed_ms);
        }
        let Some(progress) = self.turn_progress.as_mut() else {
            // A phase for a turn this client never saw start: adopt the
            // status without inventing a clock for it. The next snapshot
            // seeds the clock from the engine's own elapsed time.
            self.activity = match phase {
                P::AwaitingUserInput => Activity::Waiting,
                P::Reasoning => Activity::Thinking,
                _ => Activity::Working,
            };
            return;
        };
        match phase {
            // Reasoning is published only on a real provider thinking delta,
            // never inferred from silence or from an effort setting.
            P::Reasoning => {
                progress.on_thinking_start(now);
                self.activity = Activity::Thinking;
            }
            P::RunningTools => {
                progress.on_tool_call_started(now);
                self.activity = Activity::Working;
            }
            P::Responding => {
                progress.on_responding(now);
                self.activity = Activity::Working;
            }
            P::AwaitingUserInput => {
                progress.on_awaiting_approval(now);
                self.activity = Activity::Waiting;
            }
            P::Preparing | P::WaitingForModel | P::Compacting | P::Maintenance => {
                if self.activity != Activity::Working {
                    progress.on_resumed();
                }
                self.activity = Activity::Working;
            }
        }
    }

    /// Replaces everything the engine owns with what the engine says.
    ///
    /// Called on connect, after a resync and after an engine-owned reset —
    /// never per token, and never as a substitute for the event stream.
    /// Purely local presentation state (the composer draft, the unsent
    /// recovery list, selection) is untouched: the engine has no opinion
    /// about it and overwriting it would lose the user's own work.
    pub fn reconcile(
        &mut self,
        snapshot: &coda_proto::state::StateSnapshot,
        now: std::time::Instant,
    ) {
        use coda_proto::state::EngineLifecycle as L;

        if !snapshot.session_id.is_empty() {
            self.session_id = Some(snapshot.session_id.clone());
        }
        self.core_lifecycle = Some(snapshot.lifecycle);

        // Configuration: `next` is what a change the user just made will
        // apply to, which is what the header has always meant; `active` is
        // what the running turn actually captured. Reporting `next` as if the
        // running turn were using it is the specific dishonesty to avoid.
        let next = &snapshot.config.next;
        self.model =
            Some(self.model_labels.resolve(next.provider_id.as_deref(), &next.model).to_string());
        self.effort = if next.effort_is_auto { None } else { next.effort.clone() };
        self.active_model = snapshot
            .config
            .active
            .as_ref()
            // Compared canonically, before either side is turned into a
            // label: two different ids that happen to resolve to the same
            // display name must still count as "the running turn differs".
            .filter(|active| active.model != next.model)
            .map(|active| self.model_labels.resolve(active.provider_id.as_deref(), &active.model).to_string());

        // Usage. `session` is the running total; `lastResponse` is one
        // response's cost and must never be presented as a total.
        if let Some(limit) = snapshot.usage.context_limit {
            self.usage.context_limit = limit;
        }
        if let Some(session) = snapshot.usage.session {
            self.usage.input_tokens = session.input_tokens;
            self.usage.output_tokens = session.output_tokens;
        }

        self.reconcile_queue(snapshot);

        // The turn clock, seeded from the engine's own monotonic elapsed
        // time rather than re-derived from a remote wall clock.
        match &snapshot.turn {
            Some(turn) => {
                // A snapshot reports how long the turn has run; it never
                // reports how that time was spent. When it describes the turn
                // this client is already timing, the observed accumulators —
                // reasoning segments, whether reasoning happened at all, the
                // last response's tokens — are facts the snapshot does not
                // contradict, so they are kept and only the baseline moves.
                let same_turn = self.turn_progress.as_ref().is_some_and(|p| !p.is_finished())
                    && self.turn_id.as_deref().is_none_or(|id| id == turn.turn_id);
                let mut progress = match (same_turn, turn.elapsed_ms) {
                    (true, elapsed) => {
                        let mut progress =
                            self.turn_progress.clone().expect("same turn implies a clock");
                        // No elapsed time reported (an older engine): the
                        // local clock for this same turn is still valid and
                        // is left exactly as it is, rather than reset.
                        if let Some(elapsed) = elapsed {
                            progress.adopt_elapsed(elapsed, now);
                        }
                        progress
                    }
                    (false, Some(elapsed)) => TurnProgress::rehydrate(elapsed, now),
                    // A turn this client was not watching, with no elapsed
                    // time reported: there is nothing to carry over, and the
                    // previous turn's clock would be a different turn's.
                    (false, None) => TurnProgress::start(now),
                };
                if let Some(usage) = snapshot.usage.last_response {
                    progress.on_usage(usage.input_tokens, usage.output_tokens);
                }
                self.turn_progress = Some(progress);
                self.turn_id = Some(turn.turn_id.clone());
                // `phase_elapsed_ms` measures the phase, so it is this
                // burst's own duration only while the phase *is* reasoning.
                let reasoning_ms = (turn.phase == coda_proto::state::ActivityPhase::Reasoning)
                    .then_some(turn.phase_elapsed_ms)
                    .flatten();
                self.apply_activity_phase(turn.phase, now, reasoning_ms);
            }
            None => {
                if self.turn_progress.as_ref().is_some_and(|p| !p.is_finished()) {
                    if let Some(progress) = self.turn_progress.as_mut() {
                        progress.finish(now);
                    }
                }
            }
        }

        // Lifecycle last, so it has the final word on what the status bar
        // says — except during the optimistic window, where the engine
        // genuinely has not been told about the submission yet.
        match snapshot.lifecycle {
            L::Ready if self.optimistic_submit => {}
            L::Ready if !snapshot.requests.is_empty() => self.activity = Activity::Waiting,
            lifecycle => self.apply_lifecycle(lifecycle, now),
        }
    }

    /// Replaces the pending-steering queue with the engine's own list.
    ///
    /// The engine's `text` is the **full original message**, not a preview,
    /// so adopting it cannot downgrade what the user typed. The one case
    /// where it could — an over-cap message the engine marked
    /// `textTruncated` — keeps the local copy, because this client still has
    /// the whole thing and handing back a shortened version on recall would
    /// silently mangle a draft.
    ///
    /// A local entry that has *disappeared* from the pending list is not
    /// simply forgotten. The engine publishes a terminal outcome for every
    /// message it stops holding, and that outcome is the only thing that says
    /// whether the text reached the model:
    ///
    /// - `delivered` — it is part of the conversation now, so the message
    ///   this client is holding becomes a real `User` block rather than being
    ///   quietly discarded. Dropping the local copy on the strength of the
    ///   outcome alone is what made a delivered follow-up vanish: the
    ///   snapshot arrives before the `steeringDelivered` event, that event
    ///   then searches a queue the snapshot has already emptied, and nothing
    ///   ever puts the text on screen.
    ///
    ///   Unless the conversation on screen already contains it. The outcome
    ///   ring is *retained*: the same `delivered` outcome is republished in
    ///   every snapshot for as long as the ring holds it, so a client that
    ///   materialised nothing (both delivery reports were lost across a
    ///   reconnect) and then rebuilt the conversation from
    ///   `session/getHistory` would append a second copy underneath the one
    ///   the rebuild already showed — committed history carries no queue id,
    ///   so the two cannot be told apart by identity. [`crate::coverage`]
    ///   decides that by the read's own fences rather than by matching text,
    ///   and when the read covers the committed prefix the snapshot describes
    ///   the local receipt is simply cleared.
    /// - any other terminal outcome — it never reached the model, so the full
    ///   original draft moves to the recovery list, exactly as a turn ending
    ///   with a queue would have done.
    /// - **no outcome at all** — the outcome ring is bounded, so this is
    ///   genuinely unknown. The text is kept recoverable and the doubt is
    ///   stated; it is never resent, and it is never described as "not sent".
    fn reconcile_queue(&mut self, snapshot: &coda_proto::state::StateSnapshot) {
        use coda_proto::state::SteeringOutcomeKind as Outcome;

        let steering = &snapshot.steering;
        let mut reconciled = Vec::with_capacity(steering.pending.len());
        for entry in &steering.pending {
            let local = self
                .queued
                .iter()
                .find(|q| q.id.as_deref() == Some(entry.message_id.as_str()));
            let text = match (entry.text_truncated, local) {
                (true, Some(local)) => local.text.clone(),
                _ => entry.text.clone(),
            };
            reconciled.push(QueuedMessage {
                id: Some(entry.message_id.clone()),
                text,
                queued_at: local
                    .map(|l| l.queued_at.clone())
                    .unwrap_or_else(|| (self.clock)()),
            });
        }

        // The last `delivered` outcome for `id`, if the engine reports one.
        let delivery = |id: &str| {
            steering
                .outcomes
                .iter()
                .rev()
                .find(|o| o.message_id == id)
                .filter(|o| o.outcome == Outcome::Delivered)
        };
        // Whether the conversation currently on screen already accounts for
        // that delivery, from the fences of the history read that built it.
        let covered = |state: &Self, id: &str| {
            delivery(id).is_some_and(|outcome| {
                state
                    .history_coverage
                    .as_ref()
                    .is_some_and(|coverage| coverage.covers_delivery(snapshot, outcome))
            })
        };
        // Where a delivery would land if it is placed now: into the turn it
        // was delivered into, or after that turn was already over.
        let placement = |id: &str| match delivery(id) {
            Some(outcome) if crate::coverage::delivery_turn_is_finished(snapshot, outcome) => {
                Placement::Late
            }
            _ => Placement::InTurn,
        };
        // Positively: the engine names the turn running *now* as the one this
        // message was delivered into. Anything less — a turn that has ended,
        // an engine that names no turn at all — is not that claim.
        let delivered_into_running_turn = |id: &str| {
            matches!(
                (delivery(id).and_then(|o| o.turn_id.as_deref()), snapshot.turn.as_ref()),
                (Some(delivered_into), Some(running)) if running.turn_id == delivered_into
            )
        };

        // What happened to the entries the engine no longer lists.
        let mut delivered: Vec<(QueuedMessage, Placement)> = Vec::new();
        let mut not_delivered: Vec<QueuedMessage> = Vec::new();
        let mut unexplained: Vec<QueuedMessage> = Vec::new();
        for local in &self.queued {
            let Some(id) = local.id.as_deref() else { continue };
            if steering.pending.iter().any(|p| p.message_id == id) {
                continue;
            }
            match steering.outcomes.iter().rev().find(|o| o.message_id == id).map(|o| o.outcome) {
                // Already in the conversation the engine handed back: the
                // receipt is settled by dropping it, not by showing the text
                // a second time under a rebuild that already contains it.
                Some(Outcome::Delivered) if covered(self, id) => {}
                Some(Outcome::Delivered) => delivered.push((local.clone(), placement(id))),
                Some(_) => not_delivered.push(local.clone()),
                None => unexplained.push(local.clone()),
            }
        }

        // A message the turn stranded in the recovery list, which the engine
        // then reported as delivered after all. The notification and the turn
        // ending race on the wire, and the loser must not leave a message the
        // model received sitting under "not sent" — nor be resent.
        //
        // Unless the rebuilt conversation already accounts for it, in which
        // case the receipt is cleared and nothing is appended: the operator
        // can see it in the conversation, so there is no correction to make.
        let mut late: Vec<QueuedMessage> = Vec::new();
        for message in &self.unsent {
            let Some(id) = message.id.as_deref() else { continue };
            if delivery(id).is_none() || covered(self, id) {
                continue;
            }
            late.push(message.clone());
        }
        self.unsent.retain(|message| {
            let Some(id) = message.id.as_deref() else { return true };
            delivery(id).is_none()
        });

        // A local entry the engine has never acknowledged (no id yet) is
        // still the user's message and is kept: it is in flight, not gone.
        let unacknowledged: Vec<QueuedMessage> =
            self.queued.iter().filter(|q| q.id.is_none()).cloned().collect();
        reconciled.extend(unacknowledged);
        self.queued = reconciled;

        let corrected = !late.is_empty();
        let placed: Vec<(QueuedMessage, Placement)> = delivered
            .into_iter()
            .chain(late.into_iter().map(|message| {
                // A message the turn end stranded is, by construction,
                // confirmed after that turn ended. Only the engine naming the
                // turn running *now* makes appending it chronologically true.
                let placement = match message.id.as_deref() {
                    Some(id) if delivered_into_running_turn(id) => Placement::InTurn,
                    _ => Placement::Late,
                };
                (message, placement)
            }))
            .collect();
        let shown_late = self.materialize_all(placed);
        self.note_late_placement(shown_late, corrected);
        // Nothing is said for a receipt the rebuilt conversation already
        // accounts for: the message is on screen — or honestly announced as
        // part of an earlier page that was not loaded — and a notice here
        // would be describing a problem that does not exist.

        if !not_delivered.is_empty() {
            let n = not_delivered.len();
            self.unsent.extend(not_delivered);
            self.notice(
                format!(
                    "{n} queued {} did not reach the model — press Up on an empty message \
                     box to recover {}.",
                    if n == 1 { "message" } else { "messages" },
                    if n == 1 { "it" } else { "them" },
                ),
                NoticeLevel::Warning,
            );
        }
        if !unexplained.is_empty() {
            let n = unexplained.len();
            self.unsent.extend(unexplained);
            self.notice(
                format!(
                    "The engine no longer lists {n} queued {} and did not report what \
                     happened to {}; {} may already have been delivered, so nothing was \
                     resent. The text is recoverable with Up.",
                    if n == 1 { "message" } else { "messages" },
                    if n == 1 { "it" } else { "them" },
                    if n == 1 { "it" } else { "they" },
                ),
                NoticeLevel::Warning,
            );
        }
    }

    /// Moves anything still queued when a turn ends into the recoverable
    /// `unsent` list, and says so once.
    ///
    /// Never silently drops a message the user typed: it did not reach the
    /// model, but it is not lost either. `unsent` is the one place its text
    /// lives from here on — the notice only names how many, so recovering it
    /// later never risks reading a stale copy.
    fn strand_unsent_queue(&mut self) {
        if self.queued.is_empty() {
            return;
        }
        let stranded = std::mem::take(&mut self.queued);
        self.unsent.extend(stranded);
        let n = self.unsent.len();
        self.notice(
            if n == 1 {
                "1 message was not sent — press Up on an empty message box to recover it."
                    .to_string()
            } else {
                format!(
                    "{n} messages were not sent — press Up on an empty message box to recover them."
                )
            },
            NoticeLevel::Warning,
        );
    }

    /// Flushes the assistant buffer as a completed block when non-empty.
    ///
    /// An empty buffer produces no block (a turn with only tool calls should
    /// not leave an empty assistant block behind).  No-op when not buffering.
    fn flush_buffer(&mut self) {
        if let Some(buf) = self.assistant_buffer.take() {
            if !buf.is_empty() {
                self.transcript.push(Block::Assistant {
                    text: buf,
                    complete: true,
                });
            }
        }
        self.assistant_buffer = None;
        self.buffer_rewritten_by_hook = false;
    }

    /// On interruption or error: flushes the buffer only if the hook already
    /// ran and rewrote the content; otherwise withholds the raw model text and
    /// adds a notice.
    ///
    /// Prevents surfacing unreviewed model output when an AgentResponse
    /// redaction hook was supposed to inspect it but the turn ended first.
    /// Rule from C# `UiReducer.FlushOrWithholdAssistantBuffer`.
    fn flush_or_withhold_buffer(&mut self) {
        let Some(buf) = self.assistant_buffer.take() else {
            return;
        };
        if !self.buffer_rewritten_by_hook && !buf.is_empty() {
            // Withhold: show a notice instead of the raw text.
            self.notice(
                "[response withheld — interrupted before the redaction hook ran]",
                NoticeLevel::Warning,
            );
        } else {
            // Hook ran (or buffer is empty): show what the hook put in.
            if !buf.is_empty() {
                self.transcript.push(Block::Assistant {
                    text: buf,
                    complete: true,
                });
            }
        }
        self.assistant_buffer = None;
        self.buffer_rewritten_by_hook = false;
    }
}

fn map_status(status: Option<ToolCallStatus>, is_error: bool) -> CallStatus {
    match status {
        Some(ToolCallStatus::Pending) => CallStatus::Pending,
        Some(ToolCallStatus::AwaitingApproval) => CallStatus::AwaitingApproval,
        Some(ToolCallStatus::Running) => CallStatus::Running,
        Some(ToolCallStatus::Succeeded) => CallStatus::Succeeded,
        Some(ToolCallStatus::Failed) => CallStatus::Failed,
        Some(ToolCallStatus::Cancelled) => CallStatus::Cancelled,
        Some(ToolCallStatus::Skipped) => CallStatus::Skipped,
        // The engine may omit the status; the error flag still tells us enough.
        None if is_error => CallStatus::Failed,
        None => CallStatus::Succeeded,
    }
}

fn default_timestamp() -> String {
    use time::OffsetDateTime;
    let now = OffsetDateTime::now_local().unwrap_or_else(|_| OffsetDateTime::now_utc());
    format!("{:02}:{:02}", now.hour(), now.minute())
}

/// Returns `true` for events that bypass the 30 FPS streaming throttle.
///
/// Lives beside the events it classifies rather than in the loop that acts on
/// it: adding a variant and forgetting to say whether it is critical is a
/// silent latency bug, and the two are easiest to keep in step when they are
/// in the same file.
///
/// Mirrors `UiActor.IsCritical` in C#: turn boundaries, errors, prompts,
/// session lifecycle, and mode changes all get immediate frames.
pub(crate) fn is_critical_event(event: &UiEvent) -> bool {
        match event {
        UiEvent::TurnFinished { .. }
        | UiEvent::Connected { .. }
        | UiEvent::PromptRequested(_)
        | UiEvent::PromptAnswered { .. }
        | UiEvent::PromptResolved(_)
        | UiEvent::Notice { .. }
        | UiEvent::NotificationRecoveryWarning { .. }
        | UiEvent::Cleared
        | UiEvent::ModelChanged { .. }
        | UiEvent::DisplayModeChanged(_)
        // A fold is a direct response to a click, so it must repaint at once
        // rather than waiting for the streaming throttle: an idle session
        // produces no further frames to carry it.
        | UiEvent::ThinkingFoldToggled { .. }
        | UiEvent::ToolGroupFoldToggled { .. }
        | UiEvent::Submitted { .. }
        | UiEvent::Queued { .. }
        | UiEvent::SteeringRecalled { .. }
        | UiEvent::SteeringDeliveryReflected { .. }
        | UiEvent::InterruptRequested => true,
        UiEvent::Engine(inner) => match inner {
            coda_proto::Event::TurnComplete { .. }
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
        // Engine-owned truth: rare, and every one of them changes what the
        // status line or the whole transcript says. None of them is a
        // streaming delta, so throttling them would only add latency to a
        // correction.
        UiEvent::CoreLifecycle(_)
        | UiEvent::CoreActivity(_)
        | UiEvent::Snapshot(_)
        | UiEvent::Rehydrated { .. } => true,
        UiEvent::CommandOutput { .. } | UiEvent::DiffOutput { .. } => true,
        // The status line's claim about the whole session changed. An idle
        // disconnected terminal produces no further frames to carry it.
        UiEvent::EngineDisconnected | UiEvent::EngineAdopted => true,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_proto::events::ToolCallStatus;

    fn fixed_clock() -> String {
        "09:41".to_string()
    }

    fn state() -> UiState {
        UiState::with_clock(fixed_clock)
    }

    fn correlation(call_id: &str) -> Correlation {
        Correlation {
            root_turn_id: Some("t1".into()),
            activity_id: Some("a1".into()),
            call_id: Some(call_id.into()),
            source_id: Some("root:t1".into()),
            ..Default::default()
        }
    }

    fn assistant_text(state: &UiState) -> Option<&str> {
        state.transcript.blocks().iter().find_map(|b| match b {
            Block::Assistant { text, .. } => Some(text.as_str()),
            _ => None,
        })
    }

    fn notice_texts(state: &UiState) -> Vec<String> {
        state
            .transcript
            .blocks()
            .iter()
            .filter_map(|b| match b {
                Block::Notice { text, .. } => Some(text.clone()),
                _ => None,
            })
            .collect()
    }

    fn tools(state: &UiState) -> Option<&ToolActivity> {
        state.transcript.blocks().iter().find_map(|b| match b {
            Block::Tools { activity, .. } => Some(activity),
            _ => None,
        })
    }

    #[test]
    fn starts_in_the_initializing_activity() {
        assert_eq!(state().activity, Activity::Initializing);
    }

    #[test]
    fn becomes_ready_once_connected() {
        let mut state = state();
        state.apply(UiEvent::Connected {
            session_id: "s1".into(),
        });
        assert_eq!(state.activity, Activity::Ready);
        assert_eq!(state.session_id.as_deref(), Some("s1"));
        assert!(!state.is_busy());
    }

    #[test]
    fn a_submission_appends_a_user_block_and_starts_working() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "hi".into() });

        assert_eq!(state.activity, Activity::Working);
        assert!(state.is_busy());
        match &state.transcript.blocks()[0] {
            Block::User {
                text,
                timestamp,
                pending,
                ..
            } => {
                assert_eq!(text, "hi");
                assert_eq!(timestamp, "09:41");
                assert!(!pending);
            }
            other => panic!("expected a user block, got {other:?}"),
        }
    }

    #[test]
    fn assistant_deltas_accumulate_into_one_block() {
        let mut state = state();
        for delta in ["Hel", "lo ", "world"] {
            state.apply(UiEvent::Engine(Event::AssistantText {
                delta: delta.into(),
            }));
        }
        assert_eq!(assistant_text(&state), Some("Hello world"));
        assert_eq!(state.transcript.len(), 1);
    }

    #[test]
    fn an_empty_delta_does_not_open_a_block() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: String::new() }));
        assert!(state.transcript.is_empty());
    }

    #[test]
    fn completing_assistant_text_closes_the_block() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "hi".into() }));
        state.apply(UiEvent::Engine(Event::AssistantTextComplete));

        assert!(state.transcript.open_tail().is_none());
    }

    #[test]
    fn text_after_a_completed_block_starts_a_new_one() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "one".into() }));
        state.apply(UiEvent::Engine(Event::AssistantTextComplete));
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "two".into() }));

        assert_eq!(state.transcript.len(), 2);
    }

    #[test]
    fn thinking_deltas_accumulate_and_set_the_activity() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "hmm".into() }));
        assert_eq!(state.activity, Activity::Thinking);

        state.apply(UiEvent::Engine(Event::Thinking { delta: "...".into() }));
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 2500,
            thinking_tokens: Some(90),
        }));

        match &state.transcript.blocks()[0] {
            Block::Thinking {
                text,
                elapsed_ms,
                tokens,
                complete,
                done_at,
                ..
            } => {
                assert_eq!(text, "hmm...");
                assert_eq!(*elapsed_ms, 2500);
                assert_eq!(*tokens, Some(90));
                assert!(complete);
                assert_eq!(done_at.as_deref(), Some("09:41"));
            }
            other => panic!("expected a thinking block, got {other:?}"),
        }
        assert_eq!(state.activity, Activity::Working);
    }

    #[test]
    fn a_signed_only_thinking_completion_still_records_a_done_time() {
        // A provider that encrypts its reasoning sends no deltas at all —
        // only the completion. It must still show a truthful, frozen local
        // done time even though no visible text or duration was ever streamed.
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 0,
            thinking_tokens: None,
        }));

        match &state.transcript.blocks()[0] {
            Block::Thinking { text, complete, done_at, .. } => {
                assert!(text.is_empty());
                assert!(complete);
                assert_eq!(done_at.as_deref(), Some("09:41"));
            }
            other => panic!("expected a thinking block, got {other:?}"),
        }
    }

    #[test]
    fn tool_calls_group_into_one_batch() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "grep".into(),
            input_json: "{}".into(),
            correlation: correlation("c2"),
        }));

        assert_eq!(state.transcript.len(), 1);
        assert_eq!(tools(&state).expect("a batch").calls.len(), 2);
    }

    #[test]
    fn a_tool_result_is_matched_to_its_call_by_correlation_id() {
        let mut state = state();
        for (name, id) in [("read_file", "c1"), ("grep", "c2")] {
            state.apply(UiEvent::Engine(Event::ToolCall {
                tool_name: name.into(),
                input_json: "{}".into(),
                correlation: correlation(id),
            }));
        }

        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "grep".into(),
            content: "found".into(),
            is_error: false,
            status: Some(ToolCallStatus::Succeeded),
            correlation: correlation("c2"),
        }));

        let calls = &tools(&state).expect("a batch").calls;
        assert_eq!(calls[0].status, CallStatus::Running, "first call untouched");
        assert_eq!(calls[1].status, CallStatus::Succeeded);
        assert_eq!(calls[1].result.as_deref(), Some("found"));
    }

    #[test]
    fn two_calls_to_the_same_tool_stay_distinct() {
        let mut state = state();
        for id in ["c1", "c2"] {
            state.apply(UiEvent::Engine(Event::ToolCall {
                tool_name: "read_file".into(),
                input_json: "{}".into(),
                correlation: correlation(id),
            }));
        }
        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "read_file".into(),
            content: "second".into(),
            is_error: false,
            status: Some(ToolCallStatus::Succeeded),
            correlation: correlation("c2"),
        }));

        let calls = &tools(&state).expect("a batch").calls;
        assert_eq!(calls[0].result, None);
        assert_eq!(calls[1].result.as_deref(), Some("second"));
    }

    #[test]
    fn a_result_without_correlation_ids_falls_back_to_the_tool_name() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: Correlation::default(),
        }));
        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "read_file".into(),
            content: "body".into(),
            is_error: false,
            status: None,
            correlation: Correlation::default(),
        }));

        let calls = &tools(&state).expect("a batch").calls;
        assert_eq!(calls.len(), 1, "should not have created a second call");
        assert_eq!(calls[0].result.as_deref(), Some("body"));
    }

    #[test]
    fn an_orphan_result_is_still_shown() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "never_called".into(),
            content: "surprise".into(),
            is_error: true,
            status: None,
            correlation: correlation("zz"),
        }));

        let calls = &tools(&state).expect("a batch").calls;
        assert_eq!(calls.len(), 2);
        assert_eq!(calls[1].name, "never_called");
        assert_eq!(calls[1].status, CallStatus::Failed);
    }

    #[test]
    fn a_missing_status_is_inferred_from_the_error_flag() {
        assert_eq!(map_status(None, false), CallStatus::Succeeded);
        assert_eq!(map_status(None, true), CallStatus::Failed);
    }

    #[test]
    fn tool_progress_updates_the_elapsed_time() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "run_command".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::ToolProgress {
            tool_name: "run_command".into(),
            elapsed_ms: 1500,
            correlation: correlation("c1"),
        }));

        assert_eq!(tools(&state).expect("a batch").calls[0].elapsed_ms, Some(1500));
    }

    #[test]
    fn turn_complete_closes_the_batch_and_returns_to_ready() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        }));

        assert!(tools(&state).expect("a batch").complete);
        assert_eq!(state.activity, Activity::Ready);
    }

    #[test]
    fn an_interrupted_turn_adds_a_warning() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: None,
            interrupted: true,
            root_turn_id: None,
            activity_id: None,
        }));

        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::Notice { level: NoticeLevel::Warning, .. })
        ));
    }

    #[test]
    fn an_engine_error_becomes_an_error_notice() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Error {
            message: "provider returned 400".into(),
        }));

        match state.transcript.blocks().last() {
            Some(Block::Notice { text, level }) => {
                assert_eq!(text, "provider returned 400");
                assert_eq!(*level, NoticeLevel::Error);
            }
            other => panic!("expected an error notice, got {other:?}"),
        }
    }

    #[test]
    fn a_limit_is_reported_as_a_warning_not_an_error() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::LimitReached {
            kind: "max_tokens".into(),
            message: "hit the cap".into(),
        }));
        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::Notice { level: NoticeLevel::Warning, .. })
        ));
    }

    #[test]
    fn usage_events_update_the_counters() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Usage {
            input_tokens: 1200,
            output_tokens: 300,
        }));
        assert_eq!(state.usage.input_tokens, 1200);
        assert_eq!(state.usage.output_tokens, 300);
    }

    #[test]
    fn usage_percentage_needs_a_known_context_limit() {
        let mut usage = Usage {
            input_tokens: 50_000,
            output_tokens: 0,
            context_limit: 0,
            price_per_million: None,
        };
        assert_eq!(usage.percent_used(), None);

        usage.context_limit = 200_000;
        assert_eq!(usage.percent_used(), Some(25));
    }

    #[test]
    fn usage_percentage_is_clamped_at_one_hundred() {
        let usage = Usage {
            input_tokens: 400_000,
            output_tokens: 0,
            context_limit: 200_000,
            price_per_million: None,
        };
        assert_eq!(usage.percent_used(), Some(100));
    }

    #[test]
    fn queued_messages_do_not_appear_in_the_transcript() {
        let mut state = state();
        let before = state.transcript.blocks().len();
        state.apply(UiEvent::Queued {
            text: "later".into(),
            id: Some("s1".into()),
        });

        assert_eq!(state.queued.len(), 1);
        assert_eq!(
            state.transcript.blocks().len(),
            before,
            "queuing must not touch the transcript at all"
        );
    }

    #[test]
    fn delivered_steering_appends_a_real_user_block() {
        let mut state = state();
        state.apply(UiEvent::Queued {
            text: "later".into(),
            id: Some("s1".into()),
        });
        state.apply(UiEvent::Engine(Event::SteeringDelivered {
            message_ids: vec!["s1".into()],
        }));

        assert!(state.queued.is_empty());
        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::User { pending: false, text, .. }) if text == "later"
        ));
    }

    #[test]
    fn a_duplicate_delivery_notification_does_not_duplicate_the_block() {
        let mut state = state();
        state.apply(UiEvent::Queued {
            text: "later".into(),
            id: Some("s1".into()),
        });
        state.apply(UiEvent::Engine(Event::SteeringDelivered {
            message_ids: vec!["s1".into()],
        }));
        let count_after_first = state.transcript.blocks().len();
        state.apply(UiEvent::Engine(Event::SteeringDelivered {
            message_ids: vec!["s1".into()],
        }));

        assert_eq!(
            state.transcript.blocks().len(),
            count_after_first,
            "a duplicate ack must not insert the message twice"
        );
    }

    #[test]
    fn a_message_delivered_mid_stream_does_not_split_the_assistant_reply() {
        // a -> Queued(m) -> b must render as one assistant block "ab", with
        // the delivered message appearing only once that block closes.
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "go".into() });
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "a".into() }));
        state.apply(UiEvent::Queued {
            text: "steered".into(),
            id: Some("s1".into()),
        });
        // Delivered while the assistant block is still open.
        state.apply(UiEvent::Engine(Event::SteeringDelivered {
            message_ids: vec!["s1".into()],
        }));
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "b".into() }));

        let assistant_texts: Vec<&str> = state
            .transcript
            .blocks()
            .iter()
            .filter_map(|b| match b {
                Block::Assistant { text, .. } => Some(text.as_str()),
                _ => None,
            })
            .collect();
        assert_eq!(assistant_texts, vec!["ab"], "the reply must stay one block");

        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        }));

        // The delivered message now appears, in order, after the reply it interrupted.
        let kinds: Vec<&str> = state
            .transcript
            .blocks()
            .iter()
            .map(|b| match b {
                Block::User { text, .. } if text == "go" => "user:go",
                Block::Assistant { text, .. } if text == "ab" => "assistant:ab",
                Block::User { text, .. } if text == "steered" => "user:steered",
                _ => "other",
            })
            .collect();
        assert_eq!(kinds, vec!["user:go", "assistant:ab", "user:steered"]);
    }

    #[test]
    fn a_permission_prompt_moves_to_waiting_then_records_the_decision() {
        let mut state = state();
        state.apply(UiEvent::PromptRequested(PendingPrompt::Permission {
            tool: "run_command".into(),
            preview: "rm -rf /".into(),
        }));
        assert_eq!(state.activity, Activity::Waiting);
        assert!(state.prompt.is_some());

        state.apply(UiEvent::PromptAnswered {
            allowed: false,
            answer: None,
        });

        assert!(state.prompt.is_none());
        assert_eq!(state.activity, Activity::Working);
        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::Permission { decision: PermissionDecision::Denied, .. })
        ));
    }

    #[test]
    fn a_permission_allowed_elsewhere_is_never_recorded_as_a_denial() {
        // Another client answered. Fabricating a decision this client did not
        // make — and always the *refusing* one — described an approval as a
        // refusal, with a Denied block to prove it.
        let mut state = state();
        state.apply(UiEvent::PromptRequested(PendingPrompt::Permission {
            tool: "run_command".into(),
            preview: "ls".into(),
        }));

        state.apply(UiEvent::PromptResolved(ExternalResolution::Outcome {
            kind: Some(coda_proto::state::PendingRequestKind::Permission),
            outcome: Some("allowed".into()),
        }));

        assert!(state.prompt.is_none(), "the modal must come down");
        assert_eq!(state.activity, Activity::Working);
        assert!(
            !state
                .transcript
                .blocks()
                .iter()
                .any(|b| matches!(b, Block::Permission { decision: PermissionDecision::Denied, .. })),
            "a denial was invented: {:?}",
            state.transcript.blocks()
        );
        let notices = notice_texts(&state);
        assert!(
            notices.iter().any(|n| n.contains("allowed") && n.contains("run_command")),
            "the engine's own verdict must be what is shown: {notices:?}"
        );
    }

    #[test]
    fn a_permission_denied_elsewhere_says_so_as_the_engines_verdict() {
        let mut state = state();
        state.apply(UiEvent::PromptRequested(PendingPrompt::Permission {
            tool: "run_command".into(),
            preview: "ls".into(),
        }));
        state.apply(UiEvent::PromptResolved(ExternalResolution::Outcome {
            kind: Some(coda_proto::state::PendingRequestKind::Permission),
            outcome: Some("denied".into()),
        }));

        let notices = notice_texts(&state);
        assert!(notices.iter().any(|n| n.contains("denied")), "{notices:?}");
        assert!(state.prompt.is_none());
    }

    #[test]
    fn a_question_answered_elsewhere_does_not_invent_the_answer() {
        // `event/requestResolved` says *that* it was answered, never with
        // what. Showing an answer here would be this client making one up.
        let mut state = state();
        state.apply(UiEvent::PromptRequested(PendingPrompt::Question {
            question: "Which?".into(),
            options: vec!["a".into()],
            multi_select: false,
            allow_free_text: true,
        }));

        state.apply(UiEvent::PromptResolved(ExternalResolution::Outcome {
            kind: Some(coda_proto::state::PendingRequestKind::Question),
            outcome: Some("answered".into()),
        }));

        assert!(
            !state
                .transcript
                .blocks()
                .iter()
                .any(|b| matches!(b, Block::Question { answer: Some(_), .. })),
            "an answer was fabricated: {:?}",
            state.transcript.blocks()
        );
        let notices = notice_texts(&state);
        assert!(
            notices.iter().any(|n| n.contains("answered")),
            "the resolution must still be reported: {notices:?}"
        );
    }

    #[test]
    fn a_question_that_ended_with_no_answer_reports_the_reason() {
        let mut state = state();
        state.apply(UiEvent::PromptRequested(PendingPrompt::Question {
            question: "Which?".into(),
            options: vec!["a".into()],
            multi_select: false,
            allow_free_text: true,
        }));
        state.apply(UiEvent::PromptResolved(ExternalResolution::Outcome {
            kind: Some(coda_proto::state::PendingRequestKind::Question),
            outcome: Some("noAnswer.cancelled".into()),
        }));

        let notices = notice_texts(&state);
        assert!(
            notices.iter().any(|n| n.contains("no answer") && n.contains("cancelled")),
            "{notices:?}"
        );
    }

    #[test]
    fn a_resolution_without_a_verdict_says_only_what_is_known() {
        let mut state = state();
        state.apply(UiEvent::PromptRequested(PendingPrompt::PlanApproval {
            plan: "do the thing".into(),
        }));
        state.apply(UiEvent::PromptResolved(ExternalResolution::Outcome {
            kind: None,
            outcome: None,
        }));

        let notices = notice_texts(&state);
        assert!(notices.iter().any(|n| n.contains("resolved")), "{notices:?}");
        assert!(
            !notices.iter().any(|n| n.contains("rejected") || n.contains("approved")),
            "an unknown outcome must not be guessed: {notices:?}"
        );
    }

    #[test]
    fn a_retired_prompt_records_no_decision_and_no_verdict() {
        // The engine that raised it was replaced: nothing was decided, here
        // or anywhere, so there is nothing to report as an outcome.
        let mut state = state();
        state.apply(UiEvent::PromptRequested(PendingPrompt::Permission {
            tool: "run_command".into(),
            preview: "ls".into(),
        }));
        let before = state.transcript.len();

        state.apply(UiEvent::PromptResolved(ExternalResolution::Retired));

        assert!(state.prompt.is_none(), "the modal must come down");
        assert_eq!(state.activity, Activity::Working);
        assert_eq!(
            state.transcript.len(),
            before,
            "a retirement is not a decision: {:?}",
            state.transcript.blocks()
        );
    }

    #[test]
    fn a_question_records_its_answer() {
        let mut state = state();
        state.apply(UiEvent::PromptRequested(PendingPrompt::Question {
            question: "Which?".into(),
            options: vec!["a".into()],
            multi_select: false,
            allow_free_text: true,
        }));
        state.apply(UiEvent::PromptAnswered {
            allowed: true,
            answer: Some("a".into()),
        });

        match state.transcript.blocks().last() {
            Some(Block::Question { question, answer }) => {
                assert_eq!(question, "Which?");
                assert_eq!(answer.as_deref(), Some("a"));
            }
            other => panic!("expected a question block, got {other:?}"),
        }
    }

    #[test]
    fn interrupt_is_ignored_when_idle() {
        let mut state = state();
        state.apply(UiEvent::Connected {
            session_id: "s".into(),
        });
        state.apply(UiEvent::InterruptRequested);
        assert!(!state.interrupting);
    }

    #[test]
    fn interrupt_is_recorded_while_busy() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "go".into() });
        state.apply(UiEvent::InterruptRequested);
        assert!(state.interrupting);

        state.apply(UiEvent::TurnFinished {
            interrupted: true,
            error: None,
        });
        assert!(!state.interrupting);
    }

    #[test]
    fn a_failed_turn_surfaces_its_error() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "go".into() });
        state.apply(UiEvent::TurnFinished {
            interrupted: false,
            error: Some("boom".into()),
        });

        assert_eq!(state.activity, Activity::Ready);
        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::Notice { text, level: NoticeLevel::Error }) if text == "boom"
        ));
    }

    #[test]
    fn clearing_empties_the_transcript_and_the_queue() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "hi".into() });
        state.apply(UiEvent::Queued {
            text: "later".into(),
            id: None,
        });
        state.apply(UiEvent::Cleared);

        assert!(state.transcript.is_empty());
        assert!(state.queued.is_empty());
    }

    #[test]
    fn changing_the_model_updates_the_context_limit() {
        let mut state = state();
        state.apply(UiEvent::ModelChanged {
            id: "gpt-5".into(),
            context_limit: Some(400_000),
        });
        assert_eq!(state.model.as_deref(), Some("gpt-5"));
        assert_eq!(state.usage.context_limit, 400_000);
    }

    #[test]
    fn unknown_events_are_ignored_without_disturbing_state() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "hi".into() }));
        let before = state.transcript.len();

        state.apply(UiEvent::Engine(Event::Unknown {
            method: "event/futureThing".into(),
            params: None,
        }));
        state.apply(UiEvent::Engine(Event::StreamProgress {
            phase: "progress".into(),
            chunks: 1,
            chars: 2,
            elapsed_ms: 3,
        }));

        assert_eq!(state.transcript.len(), before);
        assert_eq!(assistant_text(&state), Some("hi"));
    }

    #[test]
    fn a_result_still_lands_after_text_opened_a_new_batch() {
        // Interleaved text is common; the result for an earlier call must be
        // routed back to the batch that owns it, not appended to the new one.
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::AssistantText {
            delta: "thinking about it".into(),
        }));
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "grep".into(),
            input_json: "{}".into(),
            correlation: correlation("c2"),
        }));

        // The late result for the *first* call arrives now.
        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "read_file".into(),
            content: "file body".into(),
            is_error: false,
            status: Some(ToolCallStatus::Succeeded),
            correlation: correlation("c1"),
        }));

        let batches: Vec<&ToolActivity> = state
            .transcript
            .blocks()
            .iter()
            .filter_map(|b| match b {
                Block::Tools { activity, .. } => Some(activity),
                _ => None,
            })
            .collect();

        assert_eq!(batches.len(), 2, "text should have opened a second batch");
        assert_eq!(batches[0].calls.len(), 1, "no orphan was appended");
        assert_eq!(
            batches[0].calls[0].result.as_deref(),
            Some("file body"),
            "the result did not reach the batch that owns the call"
        );
        assert_eq!(batches[1].calls.len(), 1);
        assert!(batches[1].calls[0].result.is_none());
    }

    #[test]
    fn interleaved_text_and_tools_keep_their_transcript_order() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::AssistantText {
            delta: "between".into(),
        }));
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "grep".into(),
            input_json: "{}".into(),
            correlation: correlation("c2"),
        }));

        let kinds: Vec<&str> = state
            .transcript
            .blocks()
            .iter()
            .map(|b| match b {
                Block::Tools { .. } => "tools",
                Block::Assistant { .. } => "assistant",
                _ => "other",
            })
            .collect();

        assert_eq!(kinds, vec!["tools", "assistant", "tools"]);
    }

    #[test]
    fn an_interrupted_batch_resolves_its_unfinished_calls() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "run_command".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: None,
            interrupted: true,
            root_turn_id: Some("t1".into()),
            activity_id: None,
        }));

        let calls = &tools(&state).expect("a batch").calls;
        assert_eq!(
            calls[0].status,
            CallStatus::Cancelled,
            "a running call must not stay running after the turn ends"
        );
    }

    #[test]
    fn finalising_resolves_every_open_batch_not_just_the_last() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "a".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "x".into() }));
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "b".into(),
            input_json: "{}".into(),
            correlation: correlation("c2"),
        }));
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: None,
            interrupted: true,
            root_turn_id: Some("t1".into()),
            activity_id: None,
        }));

        for block in state.transcript.blocks() {
            if let Block::Tools { activity, .. } = block {
                assert!(activity.complete, "a batch was left open");
                for call in &activity.calls {
                    assert!(
                        call.status.is_terminal(),
                        "call {:?} was left unresolved",
                        call.name
                    );
                }
            }
        }
    }

    #[test]
    fn recalled_ids_remove_only_the_confirmed_pending_messages() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "go".into() });
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "a".into() }));
        for id in ["delivered", "recalled", "still-pending"] {
            state.apply(UiEvent::Queued { text: id.into(), id: Some(id.into()) });
        }
        state.apply(UiEvent::Engine(Event::SteeringDelivered { message_ids: vec!["delivered".into()] }));
        state.apply(UiEvent::SteeringRecalled { message_ids: vec!["recalled".into()] });
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "b".into() }));
        assert_eq!(state.queued.len(), 1);
        assert_eq!(state.queued[0].id.as_deref(), Some("still-pending"));
        assert!(state.transcript.blocks().iter().any(|block|
            matches!(block, Block::Assistant { text, .. } if text == "ab")));
        assert!(!state.transcript.blocks().iter().any(|block|
            matches!(block, Block::User { text, .. } if text == "recalled")));
        state.apply(UiEvent::TurnFinished { interrupted: false, error: None });
        assert_eq!(state.unsent.len(), 1);
        assert_eq!(state.unsent[0].text, "still-pending");
    }

    #[test]
    fn unsent_messages_preserve_prior_turns_until_recalled() {
        let mut state = state();
        for text in ["first unsent", "second unsent"] {
            state.apply(UiEvent::Submitted { text: "go".into() });
            state.apply(UiEvent::Queued { text: text.into(), id: Some(text.into()) });
            state.apply(UiEvent::TurnFinished { interrupted: false, error: None });
        }
        assert_eq!(state.unsent.len(), 2);
        assert_eq!(state.recall_unsent().as_deref(), Some("second unsent"));
        assert_eq!(state.recall_unsent().as_deref(), Some("first unsent"));
        assert_eq!(state.recall_unsent(), None);
    }

    #[test]
    fn undelivered_queued_messages_become_recoverable_when_the_turn_ends() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "go".into() });
        state.apply(UiEvent::Queued {
            text: "never delivered".into(),
            id: Some("s1".into()),
        });
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        }));

        assert!(state.queued.is_empty());
        assert_eq!(state.unsent.len(), 1);
        assert_eq!(state.unsent[0].text, "never delivered");
        assert!(
            !state
                .transcript
                .blocks()
                .iter()
                .any(|b| matches!(b, Block::User { pending: true, .. })),
            "an undelivered message must never appear as a pending bubble"
        );
        assert!(
            state
                .transcript
                .blocks()
                .iter()
                .any(|b| matches!(b, Block::Notice { level: NoticeLevel::Warning, .. })),
            "losing a message silently is exactly what must not happen"
        );

        // Recoverable, not auto-resent: it is still sitting there for Up to restore.
        assert_eq!(state.recall_unsent().as_deref(), Some("never delivered"));
        assert!(state.unsent.is_empty());
    }

    #[test]
    fn recall_unsent_pops_the_most_recent_message_first() {
        let mut state = state();
        state.unsent = vec![
            QueuedMessage { id: None, text: "first".into(), queued_at: String::new() },
            QueuedMessage { id: None, text: "second".into(), queued_at: String::new() },
        ];
        assert_eq!(state.recall_unsent().as_deref(), Some("second"));
        assert_eq!(state.recall_unsent().as_deref(), Some("first"));
        assert_eq!(state.recall_unsent(), None);
    }

    #[test]
    fn steering_delivery_matches_by_id_not_position() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "go".into() });
        for id in ["s1", "s2", "s3"] {
            state.apply(UiEvent::Queued {
                text: id.to_string(),
                id: Some(id.to_string()),
            });
        }

        // Only the middle one is delivered.
        state.apply(UiEvent::Engine(Event::SteeringDelivered {
            message_ids: vec!["s2".into()],
        }));

        assert!(
            state
                .transcript
                .blocks()
                .iter()
                .any(|b| matches!(b, Block::User { text, pending: false, .. } if text == "s2")),
            "the delivered message must appear as a real user block"
        );
        let still_queued: Vec<&str> = state.queued.iter().map(|m| m.text.as_str()).collect();
        assert_eq!(still_queued, vec!["s1", "s3"], "the wrong message was delivered");
    }

    #[test]
    fn a_delivery_for_an_unknown_id_promotes_nothing() {
        let mut state = state();
        state.apply(UiEvent::Queued {
            text: "queued".into(),
            id: Some("s1".into()),
        });
        state.apply(UiEvent::Engine(Event::SteeringDelivered {
            message_ids: vec!["other".into()],
        }));

        assert_eq!(state.queued.len(), 1, "the unmatched message stays queued");
        assert!(
            !state.transcript.blocks().iter().any(|b| matches!(b, Block::User { .. })),
            "nothing should have reached the transcript yet"
        );
    }

    #[test]
    fn a_full_turn_produces_the_expected_block_sequence() {        let mut state = state();
        state.apply(UiEvent::Connected {
            session_id: "s1".into(),
        });
        state.apply(UiEvent::Submitted {
            text: "fix the build".into(),
        });
        state.apply(UiEvent::Engine(Event::AssistantText {
            delta: "Looking at it.".into(),
        }));
        state.apply(UiEvent::Engine(Event::AssistantTextComplete));
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "run_command".into(),
            input_json: r#"{"command":"cargo build"}"#.into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "run_command".into(),
            content: "ok".into(),
            is_error: false,
            status: Some(ToolCallStatus::Succeeded),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        }));

        let kinds: Vec<&str> = state
            .transcript
            .blocks()
            .iter()
            .map(|b| match b {
                Block::User { .. } => "user",
                Block::Assistant { .. } => "assistant",
                Block::Tools { .. } => "tools",
                Block::Notice { .. } => "notice",
                _ => "other",
            })
            .collect();

        assert_eq!(kinds, vec!["user", "assistant", "tools"]);
        assert_eq!(state.activity, Activity::Ready);
        assert!(!state.is_busy());
    }

    #[test]
    fn diff_output_event_adds_a_diff_block_to_the_transcript() {
        let raw = "diff --git a/foo.rs b/foo.rs\n\
            --- a/foo.rs\n\
            +++ b/foo.rs\n\
            @@ -1 +1 @@\n\
            -old\n\
            +new\n";
        let mut state = state();
        state.apply(UiEvent::DiffOutput { text: raw.to_string() });
        assert!(
            matches!(state.transcript.blocks().last(), Some(Block::Diff { .. })),
            "expected a Diff block"
        );
    }

    #[test]
    fn thinking_complete_without_prior_delta_still_records_that_it_reasoned() {
        // This used to be asserted as a no-op, faithfully porting the C#
        // (`UiReducer.CompleteThinking` returns the state unchanged when no
        // incomplete block exists). Both were wrong in the same way: a
        // provider that encrypts its reasoning sends no deltas, so there is
        // never a block to complete and the reasoning vanished entirely.
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 500,
            thinking_tokens: None,
        }));
        assert_eq!(
            state.transcript.len(),
            1,
            "the turn must show that it reasoned, even with nothing to show for it"
        );
    }

    #[test]
    fn a_finished_thinking_block_is_not_reopened_by_a_later_complete() {
        // Completing twice must not resurrect the first burst or append an
        // empty second one to it.
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "first".into() }));
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 100,
            thinking_tokens: None,
        }));
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 999,
            thinking_tokens: None,
        }));

        match &state.transcript.blocks()[0] {
            Block::Thinking { text, elapsed_ms, .. } => {
                assert_eq!(text, "first");
                assert_eq!(*elapsed_ms, 100, "the finished burst was rewritten");
            }
            other => panic!("expected a thinking block: {other:?}"),
        }
        assert_eq!(
            state.transcript.len(),
            1,
            "the duplicate completion invented a second burst"
        );
    }

    #[test]
    fn a_second_thinking_burst_does_not_append_to_the_completed_first_burst() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "first".into() }));
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 100,
            thinking_tokens: None,
        }));
        // Second burst starts a new block, not an append.
        state.apply(UiEvent::Engine(Event::Thinking { delta: "second".into() }));
        assert_eq!(state.transcript.len(), 2, "expected two separate thinking blocks");
        match &state.transcript.blocks()[1] {
            Block::Thinking { text, complete, .. } => {
                assert_eq!(text, "second");
                assert!(!complete, "second burst should still be open");
            }
            other => panic!("expected Thinking block, got {other:?}"),
        }
    }

    #[test]
    fn turn_finished_finalizes_an_open_thinking_block() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "reasoning".into() }));
        // Turn ends without an explicit ThinkingComplete.
        state.apply(UiEvent::TurnFinished {
            interrupted: false,
            error: None,
        });
        match &state.transcript.blocks()[0] {
            Block::Thinking { complete, .. } => {
                assert!(complete, "thinking block must be marked complete when the turn ends");
            }
            other => panic!("expected Thinking block, got {other:?}"),
        }
    }

    #[test]
    fn already_complete_thinking_block_elapsed_ms_is_not_clobbered_by_turn_finish() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "reasoning".into() }));
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 1000,
            thinking_tokens: Some(42),
        }));
        state.apply(UiEvent::TurnFinished {
            interrupted: false,
            error: None,
        });
        match &state.transcript.blocks()[0] {
            Block::Thinking { elapsed_ms, tokens, .. } => {
                assert_eq!(*elapsed_ms, 1000, "frozen elapsed_ms must not be clobbered");
                assert_eq!(*tokens, Some(42), "frozen tokens must not be clobbered");
            }
            other => panic!("expected Thinking block, got {other:?}"),
        }
    }

    // ---- assistant buffering (withhold-on-interrupt) --------------------

    fn start_buffered_turn(state: &mut UiState) {
        state.apply(UiEvent::EnableAssistantBuffering);
        state.apply(UiEvent::Submitted { text: "go".into() });
    }

    #[test]
    fn enable_buffering_sets_the_buffer_to_empty_string() {
        let mut state = state();
        state.apply(UiEvent::EnableAssistantBuffering);
        assert!(state.assistant_buffer.is_some());
        assert_eq!(state.assistant_buffer.as_deref(), Some(""));
    }

    #[test]
    fn assistant_deltas_accumulate_in_buffer_when_buffering() {
        let mut state = state();
        start_buffered_turn(&mut state);
        for delta in ["Hel", "lo", " world"] {
            state.apply(UiEvent::Engine(Event::AssistantText { delta: delta.into() }));
        }
        // Nothing in the transcript yet.
        assert!(assistant_text(&state).is_none());
        assert_eq!(state.assistant_buffer.as_deref(), Some("Hello world"));
    }

    #[test]
    fn thinking_is_suppressed_during_buffered_turn() {
        let mut state = state();
        start_buffered_turn(&mut state);
        state.apply(UiEvent::Engine(Event::Thinking { delta: "reasoning".into() }));
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 100,
            thinking_tokens: None,
        }));
        assert!(
            !state.transcript.blocks().iter().any(|b| matches!(b, Block::Thinking { .. })),
            "thinking blocks must be suppressed during buffered turns"
        );
    }

    #[test]
    fn buffer_is_flushed_as_complete_block_on_successful_turn() {
        let mut state = state();
        start_buffered_turn(&mut state);
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "answer".into() }));
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        }));
        assert!(state.assistant_buffer.is_none(), "buffer must be cleared after flush");
        assert_eq!(
            assistant_text(&state),
            Some("answer"),
            "buffered text must appear in the transcript on success"
        );
        match state.transcript.blocks().iter().find(|b| matches!(b, Block::Assistant { .. })) {
            Some(Block::Assistant { complete, .. }) => assert!(*complete, "flushed block must be complete"),
            _ => panic!("no assistant block found"),
        }
    }

    #[test]
    fn empty_buffer_on_success_produces_no_assistant_block() {
        let mut state = state();
        start_buffered_turn(&mut state);
        // No deltas — turn ends immediately.
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        }));
        assert!(
            !state.transcript.blocks().iter().any(|b| matches!(b, Block::Assistant { .. })),
            "an empty buffer must not produce an assistant block"
        );
    }

    #[test]
    fn buffer_is_withheld_on_interrupt_when_hook_never_ran() {
        let mut state = state();
        start_buffered_turn(&mut state);
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "secret".into() }));
        // Turn interrupted, hook never ran.
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: None,
            interrupted: true,
            root_turn_id: None,
            activity_id: None,
        }));
        assert!(state.assistant_buffer.is_none());
        // Raw text must NOT appear.
        assert!(
            assistant_text(&state).is_none(),
            "withheld buffer must not show raw model text"
        );
        // Instead a warning notice.
        assert!(
            state.transcript.blocks().iter().any(|b| {
                matches!(b, Block::Notice { level: NoticeLevel::Warning, text }
                    if text.contains("withheld"))
            }),
            "expected a 'withheld' notice"
        );
    }

    #[test]
    fn buffer_is_flushed_on_interrupt_when_hook_already_rewrote_it() {
        let mut state = state();
        start_buffered_turn(&mut state);
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "raw".into() }));
        // Hook rewrites the buffer.
        state.apply(UiEvent::Engine(Event::ResponseRewritten {
            hook_command: "redact".into(),
            original_response: "raw".into(),
            display_content: "sanitized".into(),
            modified_response: None,
        }));
        assert!(state.buffer_rewritten_by_hook);
        // Now interrupt.
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: None,
            interrupted: true,
            root_turn_id: None,
            activity_id: None,
        }));
        // Hook's version must appear.
        assert_eq!(
            assistant_text(&state),
            Some("sanitized"),
            "hook-rewritten buffer must be flushed even on interrupt"
        );
        assert!(state.assistant_buffer.is_none());
        assert!(!state.buffer_rewritten_by_hook);
    }

    #[test]
    fn response_rewritten_while_not_buffering_is_a_noop() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "normal".into() }));
        // No buffering active; ResponseRewritten must not change anything.
        state.apply(UiEvent::Engine(Event::ResponseRewritten {
            hook_command: "redact".into(),
            original_response: "normal".into(),
            display_content: "changed".into(),
            modified_response: None,
        }));
        assert!(state.assistant_buffer.is_none());
        assert_eq!(assistant_text(&state), Some("normal"));
    }

    #[test]
    fn enable_buffering_is_idempotent() {
        let mut state = state();
        state.apply(UiEvent::EnableAssistantBuffering);
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "first".into() }));
        // Second enable must not reset the buffer.
        state.apply(UiEvent::EnableAssistantBuffering);
        assert_eq!(
            state.assistant_buffer.as_deref(),
            Some("first"),
            "second EnableAssistantBuffering must not clear accumulated text"
        );
    }

    #[test]
    fn folding_goes_through_the_reducer_so_the_layout_is_rebuilt() {
        // The fold must be an event, not a direct call on the transcript.
        // Everything that changes rendered rows invalidates the cached layout
        // by passing through here; a direct call skipped that, so the block
        // flipped internally and the screen stayed exactly as it was --
        // meaning a click did nothing at all on a finished turn.
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "reasoning".into() }));

        let expanded = |s: &UiState| match &s.transcript.blocks()[0] {
            Block::Thinking { expanded, .. } => *expanded,
            other => panic!("expected a thinking block, got {other:?}"),
        };
        assert!(!expanded(&state), "reasoning should start folded");

        state.apply(UiEvent::ThinkingFoldToggled { block: 0 });
        assert!(expanded(&state), "the event did not open the block");

        state.apply(UiEvent::ThinkingFoldToggled { block: 0 });
        assert!(!expanded(&state), "the event did not close the block again");
    }

    #[test]
    fn folding_a_block_that_is_not_there_is_ignored() {
        let mut state = state();
        state.apply(UiEvent::ThinkingFoldToggled { block: 99 });
        assert!(state.transcript.blocks().is_empty());
    }

    #[test]
    fn reasoning_with_no_deltas_still_appears() {
        // A provider that encrypts its reasoning sends no ThinkingDelta at
        // all -- only a signed block at the end, with the text empty and the
        // content in the signature. ThinkingComplete then had no open block
        // to update, so the whole turn showed no sign of having reasoned:
        // the response marker appeared, time passed, and that was all.
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 0,
            thinking_tokens: None,
        }));

        match state.transcript.blocks().first() {
            Some(Block::Thinking { complete, text, .. }) => {
                assert!(complete, "the block must arrive already finished");
                assert!(text.is_empty(), "there was no reasoning text to show");
            }
            other => panic!("no reasoning block was created: {other:?}"),
        }
    }

    #[test]
    fn reasoning_that_did_stream_is_still_updated_not_duplicated() {
        // The normal path must not regress into creating a second block.
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "reasoning".into() }));
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 2500,
            thinking_tokens: Some(90),
        }));

        let thinking: Vec<_> = state
            .transcript
            .blocks()
            .iter()
            .filter(|b| matches!(b, Block::Thinking { .. }))
            .collect();
        assert_eq!(thinking.len(), 1, "a second block was created");
        match thinking[0] {
            Block::Thinking { text, elapsed_ms, tokens, complete, .. } => {
                assert_eq!(text, "reasoning");
                assert_eq!(*elapsed_ms, 2500);
                assert_eq!(*tokens, Some(90));
                assert!(complete);
            }
            other => panic!("expected a thinking block: {other:?}"),
        }
    }
    #[test]
    fn bodyless_thinking_start_displays_and_ticks_until_the_item_finishes() {
        use std::time::{Duration, Instant};
        let mut state = state();
        let start = Instant::now();
        state.apply_at(UiEvent::Engine(Event::Thinking { delta: String::new() }), start);
        assert_eq!(state.activity, Activity::Thinking);
        assert!(!state.transcript.is_foldable(0));
        for mode in [ToolDisplayMode::Summary, ToolDisplayMode::Compact, ToolDisplayMode::Full] {
            let rows = state.transcript.render(80, mode);
            assert!(rows.iter().any(|row| row.text.contains("Thinking... 0s")));
        }
        assert!(state.tick_thinking(start + Duration::from_secs(65)));
        assert!(state.transcript.render(80, ToolDisplayMode::Summary).iter()
            .any(|row| row.text.contains("Thinking... 1:05")));
        state.apply_at(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 65_000, thinking_tokens: None,
        }), start + Duration::from_secs(65));
        state.apply_at(UiEvent::Engine(Event::AssistantText { delta: "answer".into() }),
            start + Duration::from_secs(66));
        assert!(!state.tick_thinking(start + Duration::from_secs(70)));
        assert!(matches!(&state.transcript.blocks()[0],
            Block::Thinking { complete: true, elapsed_ms: 65_000, .. }));
        assert_eq!(state.transcript.blocks().len(), 2);
    }

    #[test]
    fn live_thinking_clock_advances_without_deltas_and_keeps_its_start() {
        use std::time::{Duration, Instant};
        let mut state = state();
        let start = Instant::now();
        state.apply_at(UiEvent::Engine(Event::Thinking { delta: "first".into() }), start);
        assert!(!state.tick_thinking(start + Duration::from_millis(999)));
        assert!(state.tick_thinking(start + Duration::from_secs(1)));
        state.apply_at(
            UiEvent::Engine(Event::Thinking { delta: " second".into() }),
            start + Duration::from_secs(30),
        );
        assert!(state.tick_thinking(start + Duration::from_secs(65)));
        let rows = state.transcript.render(80, ToolDisplayMode::Summary);
        assert!(rows.iter().any(|row| row.text.contains("Thinking... 1:05")));
    }

    #[test]
    fn live_thinking_clock_stops_at_completion_and_restarts_for_each_burst() {
        use std::time::{Duration, Instant};
        let mut state = state();
        let start = Instant::now();
        state.apply_at(UiEvent::Engine(Event::Thinking { delta: "first".into() }), start);
        assert!(state.tick_thinking(start + Duration::from_secs(5)));
        state.apply_at(
            UiEvent::Engine(Event::ThinkingComplete { elapsed_ms: 4500, thinking_tokens: None }),
            start + Duration::from_secs(6),
        );
        assert!(!state.tick_thinking(start + Duration::from_secs(10)));
        assert!(matches!(state.transcript.blocks()[0], Block::Thinking { elapsed_ms: 4500, .. }));
        state.apply_at(
            UiEvent::Engine(Event::Thinking { delta: "second".into() }),
            start + Duration::from_secs(20),
        );
        assert!(!state.tick_thinking(start + Duration::from_millis(20_999)));
        assert!(state.tick_thinking(start + Duration::from_secs(21)));
        assert!(matches!(state.transcript.blocks()[1], Block::Thinking { elapsed_ms: 1000, .. }));
    }

    #[test]
    fn live_thinking_clock_freezes_on_implicit_completion_and_clears_on_reset() {
        use std::time::{Duration, Instant};
        for event in [
            UiEvent::TurnFinished { interrupted: true, error: None },
            UiEvent::Engine(Event::AssistantText { delta: "answer".into() }),
            UiEvent::Cleared,
        ] {
            let mut state = state();
            let start = Instant::now();
            state.apply_at(UiEvent::Engine(Event::Thinking { delta: "reasoning".into() }), start);
            state.apply_at(event, start + Duration::from_secs(3));
            assert!(!state.tick_thinking(start + Duration::from_secs(10)));
            if let Some(Block::Thinking { complete, elapsed_ms, .. }) = state.transcript.blocks().first() {
                assert!(*complete);
                assert_eq!(*elapsed_ms, 3000);
            }
        }
    }

    #[test]
    fn submitted_starts_turn_progress_before_any_engine_event() {
        use std::time::Instant;
        let mut state = state();
        let now = Instant::now();
        state.apply_at(UiEvent::Submitted { text: "go".into() }, now);

        let progress = state.turn_progress.as_ref().expect("progress must start on Submitted");
        assert_eq!(progress.phase(), crate::progress::Phase::Working);
        assert_eq!(progress.elapsed_ms(now), 0);
    }

    #[test]
    fn turn_progress_tracks_the_true_phase_as_engine_events_arrive() {
        use std::time::{Duration, Instant};
        let mut state = state();
        let start = Instant::now();
        state.apply_at(UiEvent::Submitted { text: "go".into() }, start);

        state.apply_at(
            UiEvent::Engine(Event::Thinking { delta: "hmm".into() }),
            start + Duration::from_secs(1),
        );
        assert_eq!(state.turn_progress.as_ref().unwrap().phase(), crate::progress::Phase::Thinking);

        state.apply_at(
            UiEvent::Engine(Event::ThinkingComplete { elapsed_ms: 1000, thinking_tokens: None }),
            start + Duration::from_secs(2),
        );
        state.apply_at(
            UiEvent::Engine(Event::ToolCall {
                tool_name: "run_command".into(),
                input_json: "{}".into(),
                correlation: correlation("c1"),
            }),
            start + Duration::from_secs(3),
        );
        assert_eq!(
            state.turn_progress.as_ref().unwrap().phase(),
            crate::progress::Phase::RunningTools
        );

        state.apply_at(
            UiEvent::Engine(Event::AssistantText { delta: "done".into() }),
            start + Duration::from_secs(4),
        );
        assert_eq!(
            state.turn_progress.as_ref().unwrap().phase(),
            crate::progress::Phase::Responding
        );
    }

    #[test]
    fn turn_progress_freezes_when_the_turn_ends_and_survives_a_duplicate_end() {
        use std::time::{Duration, Instant};
        let mut state = state();
        let start = Instant::now();
        state.apply_at(UiEvent::Submitted { text: "go".into() }, start);
        state.apply_at(
            UiEvent::Engine(Event::TurnComplete {
                stop_reason: Some("end_turn".into()),
                interrupted: false,
                root_turn_id: None,
                activity_id: None,
            }),
            start + Duration::from_secs(5),
        );
        assert_eq!(state.turn_progress.as_ref().unwrap().elapsed_ms(start + Duration::from_secs(5)), 5_000);

        // TurnFinished (the RPC-response path) follows almost every
        // TurnComplete; it must not move the already-frozen clock.
        state.apply_at(
            UiEvent::TurnFinished { interrupted: false, error: None },
            start + Duration::from_secs(50),
        );
        assert_eq!(
            state.turn_progress.as_ref().unwrap().elapsed_ms(start + Duration::from_secs(999)),
            5_000,
            "a duplicate end must not move an already-frozen clock"
        );
    }

    #[test]
    fn turn_progress_reports_the_last_responses_tokens_only_once_usage_arrives() {
        use std::time::Instant;
        let mut state = state();
        let start = Instant::now();
        state.apply_at(UiEvent::Submitted { text: "go".into() }, start);
        assert_eq!(state.turn_progress.as_ref().unwrap().last_response_tokens(), None);

        state.apply_at(
            UiEvent::Engine(Event::Usage { input_tokens: 100, output_tokens: 42 }),
            start,
        );
        assert_eq!(
            state.turn_progress.as_ref().unwrap().last_response_tokens(),
            Some((100, 42))
        );
    }

    #[test]
    fn a_new_turn_starts_a_fresh_progress_clock() {
        use std::time::{Duration, Instant};
        let mut state = state();
        let start = Instant::now();
        state.apply_at(UiEvent::Submitted { text: "first".into() }, start);
        state.apply_at(
            UiEvent::TurnFinished { interrupted: false, error: None },
            start + Duration::from_secs(5),
        );

        let second_start = start + Duration::from_secs(100);
        state.apply_at(UiEvent::Submitted { text: "second".into() }, second_start);
        assert_eq!(state.turn_progress.as_ref().unwrap().elapsed_ms(second_start), 0);
        assert!(!state.turn_progress.as_ref().unwrap().is_finished());
    }

    // ── Engine-owned truth (Stage E) ─────────────────────────────────────

    mod engine_truth {
        use super::*;
        use coda_proto::messages::CONTRACT_VERSION;
        use coda_proto::state::{
            ActiveConfig, ActivityPhase, EffectiveConfig, EngineLifecycle, Limits,
            SteeringOutcomeDto, SteeringOutcomeKind, SteeringPendingDto, SteeringQueueState,
            ToolsState, TurnState, UsagePair, UsageState,
        };
        use std::time::{Duration, Instant};

        fn outcome(message_id: &str, kind: SteeringOutcomeKind) -> SteeringOutcomeDto {
            SteeringOutcomeDto {
                message_id: message_id.into(),
                outcome: kind,
                at: "now".into(),
                turn_id: None,
            }
        }

        /// Every notice currently in the transcript, for asserting on what the
        /// user was actually told.
        fn notice_texts(state: &UiState) -> Vec<String> {
            state
                .transcript
                .blocks()
                .iter()
                .filter_map(|block| match block {
                    Block::Notice { text, .. } => Some(text.clone()),
                    _ => None,
                })
                .collect()
        }

        fn active_config(model: &str) -> ActiveConfig {
            ActiveConfig {
                provider_id: Some("anthropic".into()),
                model: model.into(),
                effort: None,
                effort_is_auto: true,
                permission_mode: "default".into(),
                system_prompt_source: "default".into(),
            }
        }

        fn snapshot(lifecycle: EngineLifecycle) -> coda_proto::state::StateSnapshot {
            coda_proto::state::StateSnapshot {
                contract_version: CONTRACT_VERSION.into(),
                engine_instance_id: "e1".into(),
                session_id: "s1".into(),
                workspace_path: "/w".into(),
                cursor: 10,
                history_epoch: 0,
                history_length: 4,
                lifecycle,
                initialized: true,
                last_turn_outcome: None,
                turn: None,
                steering: SteeringQueueState::default(),
                tools: ToolsState::default(),
                requests: Vec::new(),
                config: EffectiveConfig {
                    active: None,
                    next: active_config("claude-sonnet"),
                    differing: Vec::new(),
                },
                usage: UsageState::default(),
                limits: Limits {
                    ring_envelopes: 2048,
                    ring_bytes: 4 << 20,
                    live_bytes_cap: 262_144,
                    outcomes_retained: 64,
                    history_block_bytes_cap: 65_536,
                    max_history_page: 500,
                    max_session_page: 200,
                },
                capabilities: Default::default(),
            }
        }

        fn turn(phase: ActivityPhase, elapsed_ms: Option<i64>) -> TurnState {
            TurnState {
                turn_id: "t1".into(),
                started_at: "2026-09-08T09:00:00+00:00".into(),
                elapsed_ms,
                phase,
                phase_since: "2026-09-08T09:00:10+00:00".into(),
                phase_elapsed_ms: Some(1_000),
                model_request: None,
                batches: Vec::new(),
                live_entries: Vec::new(),
                live_truncated: false,
                live_omitted_bytes: 0,
                active_config: active_config("claude-sonnet"),
                concurrent: Default::default(),
            }
        }

        #[test]
        fn a_finished_turn_does_not_claim_ready_while_the_engine_still_holds_the_slot() {
            // `event/turnComplete` says the turn produced its last output.
            // The engine releases the single-flight slot separately, and
            // until it does a new prompt is refused as busy. Showing "ready"
            // in that window invited a prompt that would bounce.
            let mut state = state();
            state.apply(UiEvent::CoreLifecycle(EngineLifecycle::Busy));
            state.apply(UiEvent::Submitted { text: "hi".into() });
            state.apply(UiEvent::Engine(Event::TurnComplete {
                stop_reason: None,
                interrupted: false,
                root_turn_id: None,
                activity_id: None,
            }));
            assert_eq!(state.activity, Activity::Working, "the engine is still busy");
            assert!(state.is_busy());

            state.apply(UiEvent::CoreLifecycle(EngineLifecycle::Ready));
            assert_eq!(state.activity, Activity::Ready);
            assert!(!state.is_busy());
        }

        #[test]
        fn a_legacy_connection_keeps_the_previous_turn_complete_behaviour() {
            // No lifecycle is ever published by a legacy engine, so the old
            // rule — the turn ends when its completion event says so — must
            // still hold exactly.
            let mut state = state();
            state.apply(UiEvent::Submitted { text: "hi".into() });
            state.apply(UiEvent::Engine(Event::TurnComplete {
                stop_reason: None,
                interrupted: false,
                root_turn_id: None,
                activity_id: None,
            }));
            assert_eq!(state.activity, Activity::Ready);
        }

        #[test]
        fn a_snapshot_taken_in_the_optimistic_window_does_not_undo_the_submission() {
            // Between the local submit and the engine being told, a snapshot
            // honestly says "ready". Applying that literally would flip the
            // UI back under a prompt the user has already sent.
            let mut state = state();
            state.apply(UiEvent::Submitted { text: "hi".into() });
            state.apply(UiEvent::Snapshot(Box::new(snapshot(EngineLifecycle::Ready))));
            assert_eq!(state.activity, Activity::Working);
        }

        #[test]
        fn a_snapshot_seeds_the_turn_clock_from_the_engines_own_elapsed_time() {
            // Server-monotonic duration, not a remote wall clock and not a
            // reset to zero.
            let mut state = state();
            let now = Instant::now();
            let mut snapshot = snapshot(EngineLifecycle::Busy);
            snapshot.turn = Some(turn(ActivityPhase::RunningTools, Some(185_000)));
            state.apply_at(UiEvent::Snapshot(Box::new(snapshot)), now);

            let progress = state.turn_progress.as_ref().expect("a running turn has a clock");
            assert_eq!(progress.elapsed_ms(now), 185_000);
            assert_eq!(
                progress.elapsed_ms(now + Duration::from_secs(5)),
                190_000,
                "the rehydrated clock keeps running"
            );
            assert_eq!(state.activity, Activity::Working);
        }

        #[test]
        fn a_snapshot_never_claims_reasoning_the_client_did_not_observe() {
            let mut state = state();
            let now = Instant::now();
            let mut snapshot = snapshot(EngineLifecycle::Busy);
            snapshot.turn = Some(turn(ActivityPhase::WaitingForModel, Some(30_000)));
            state.apply_at(UiEvent::Snapshot(Box::new(snapshot)), now);
            assert_eq!(state.activity, Activity::Working, "silence is not reasoning");
            assert_eq!(state.turn_progress.as_ref().unwrap().reasoning_ms(now), None);
        }

        #[test]
        fn only_an_observed_reasoning_phase_shows_thinking() {
            let mut state = state();
            let now = Instant::now();
            state.apply_at(UiEvent::Submitted { text: "hi".into() }, now);
            state.apply_at(UiEvent::CoreActivity(ActivityPhase::Reasoning), now);
            assert_eq!(state.activity, Activity::Thinking);
            assert_eq!(state.turn_progress.as_ref().unwrap().reasoning_ms(now), Some(0));
        }

        #[test]
        fn an_authoritative_reasoning_phase_opens_the_row_its_lost_event_would_have() {
            // The frame that opens a burst can be dropped legitimately — a
            // snapshot's cursor covers it, and the snapshot carries no
            // content to replace it with. The phase is then the only thing
            // that says reasoning is happening now.
            let mut state = state();
            let now = Instant::now();
            state.apply_at(UiEvent::Submitted { text: "hi".into() }, now);
            state.apply_at(UiEvent::CoreActivity(ActivityPhase::Reasoning), now);
            match state.transcript.blocks().last() {
                Some(Block::Thinking { text, complete, elapsed_ms, .. }) => {
                    assert!(text.is_empty(), "no reasoning text may be invented");
                    assert!(!complete);
                    assert_eq!(*elapsed_ms, 0);
                }
                other => panic!("expected a live thinking block, got {other:?}"),
            }
            assert!(state.tick_thinking(now + Duration::from_secs(1)), "the clock must run");
        }

        #[test]
        fn a_reasoning_snapshot_seeds_the_burst_clock_and_never_rewinds_it() {
            let mut state = state();
            let now = Instant::now();
            state.apply_at(UiEvent::Submitted { text: "hi".into() }, now);
            let mut first = snapshot(EngineLifecycle::Busy);
            let mut running = turn(ActivityPhase::Reasoning, Some(30_000));
            running.phase_elapsed_ms = Some(12_000);
            first.turn = Some(running.clone());
            state.apply_at(UiEvent::Snapshot(Box::new(first)), now);
            assert!(matches!(
                state.transcript.blocks().last(),
                Some(Block::Thinking { elapsed_ms: 12_000, complete: false, .. })
            ));

            // A refresh of the same burst reporting *less* must not rewind a
            // clock the user is watching.
            let later = now + Duration::from_secs(5);
            let mut stale = snapshot(EngineLifecycle::Busy);
            running.phase_elapsed_ms = Some(1_000);
            stale.turn = Some(running);
            state.apply_at(UiEvent::Snapshot(Box::new(stale)), later);
            state.tick_thinking(later);
            match state.transcript.blocks().last() {
                Some(Block::Thinking { elapsed_ms, .. }) => {
                    assert_eq!(*elapsed_ms, 17_000, "the clock went backwards")
                }
                other => panic!("expected a live thinking block, got {other:?}"),
            }
        }

        #[test]
        fn a_reasoning_phase_that_has_not_caught_up_never_reopens_a_finished_burst() {
            // `thinkingComplete` closes the burst; a snapshot taken before
            // the engine's phase moves on still names `reasoning`. That is
            // the burst already on screen, not a new one.
            let mut state = state();
            let now = Instant::now();
            state.apply_at(UiEvent::Engine(Event::Thinking { delta: "hmm".into() }), now);
            state.apply_at(
                UiEvent::Engine(Event::ThinkingComplete { elapsed_ms: 2_000, thinking_tokens: None }),
                now + Duration::from_secs(2),
            );
            state.apply_at(
                UiEvent::CoreActivity(ActivityPhase::Reasoning),
                now + Duration::from_secs(3),
            );
            let bursts = state
                .transcript
                .blocks()
                .iter()
                .filter(|block| matches!(block, Block::Thinking { .. }))
                .count();
            assert_eq!(bursts, 1, "a frozen burst was reopened");
            assert!(!state.tick_thinking(now + Duration::from_secs(9)));
        }

        #[test]
        fn a_second_burst_after_other_content_gets_its_own_row_from_the_phase_alone() {
            let mut state = state();
            let now = Instant::now();
            state.apply_at(UiEvent::Engine(Event::Thinking { delta: "first".into() }), now);
            state.apply_at(
                UiEvent::Engine(Event::ThinkingComplete { elapsed_ms: 1_000, thinking_tokens: None }),
                now + Duration::from_secs(1),
            );
            state.apply_at(
                UiEvent::Engine(Event::AssistantText { delta: "partial".into() }),
                now + Duration::from_secs(2),
            );
            state.apply_at(
                UiEvent::CoreActivity(ActivityPhase::Reasoning),
                now + Duration::from_secs(3),
            );
            let bursts: Vec<&Block> = state
                .transcript
                .blocks()
                .iter()
                .filter(|block| matches!(block, Block::Thinking { .. }))
                .collect();
            assert_eq!(bursts.len(), 2, "a distinct burst must get a distinct row");
            assert!(matches!(bursts[0], Block::Thinking { complete: true, .. }));
            assert!(matches!(bursts[1], Block::Thinking { complete: false, .. }));
        }

        #[test]
        fn a_buffered_turn_shows_no_reasoning_row_even_from_an_authoritative_phase() {
            // Hook buffering withholds the whole assistant response until a
            // rewrite has had its say; a row opened here is the seam the
            // withheld reasoning text would land in.
            let mut state = state();
            let now = Instant::now();
            state.apply(UiEvent::EnableAssistantBuffering);
            state.apply_at(UiEvent::CoreActivity(ActivityPhase::Reasoning), now);
            assert!(
                !state.transcript.blocks().iter().any(|b| matches!(b, Block::Thinking { .. })),
                "a buffered turn must not show reasoning"
            );
        }

        #[test]
        fn a_rebuilt_conversation_keeps_a_live_burst_running_and_folded_as_it_was() {
            let mut state = state();
            let now = Instant::now();
            state.apply_at(UiEvent::Engine(Event::Thinking { delta: "half".into() }), now);
            state.apply(UiEvent::ThinkingFoldToggled { block: 0 });
            state.tick_thinking(now + Duration::from_secs(4));

            // The engine's own projection of the same, still-running burst.
            state.apply_at(
                UiEvent::Rehydrated {
                    blocks: vec![Block::Thinking {
                        text: "half".into(),
                        elapsed_ms: 0,
                        tokens: None,
                        complete: false,
                        expanded: false,
                        done_at: None,
                    }],
                    notices: Vec::new(),
                    coverage: None,
                },
                now + Duration::from_secs(4),
            );
            state.tick_thinking(now + Duration::from_secs(6));
            match state.transcript.blocks().last() {
                Some(Block::Thinking { complete, elapsed_ms, expanded, .. }) => {
                    assert!(!complete, "the running burst was rebuilt as history");
                    assert_eq!(*elapsed_ms, 6_000, "the clock restarted");
                    assert!(*expanded, "the fold the user opened was lost");
                }
                other => panic!("expected a live thinking block, got {other:?}"),
            }
        }

        #[test]
        fn a_rebuilt_conversation_never_makes_historical_reasoning_tick() {
            let mut state = state();
            let now = Instant::now();
            state.apply_at(
                UiEvent::Rehydrated {
                    blocks: vec![Block::Thinking {
                        text: "old reasoning".into(),
                        elapsed_ms: 0,
                        tokens: None,
                        complete: true,
                        expanded: false,
                        done_at: None,
                    }],
                    notices: Vec::new(),
                    coverage: None,
                },
                now,
            );
            assert!(!state.tick_thinking(now + Duration::from_secs(30)));
            assert!(matches!(
                state.transcript.blocks().last(),
                Some(Block::Thinking { complete: true, elapsed_ms: 0, .. })
            ));
        }

        #[test]
        fn the_queue_comes_from_the_engine_and_keeps_the_users_own_full_text() {
            let mut state = state();
            state.apply(UiEvent::Queued { text: "one".into(), id: Some("m1".into()) });
            state.apply(UiEvent::Queued { text: "two".into(), id: Some("m2".into()) });

            // The engine says only m2 is still pending: m1 was delivered.
            let mut snapshot = snapshot(EngineLifecycle::Busy);
            snapshot.steering = SteeringQueueState {
                pending_count: 1,
                pending: vec![SteeringPendingDto {
                    message_id: "m2".into(),
                    enqueued_at: "now".into(),
                    text: "two".into(),
                    text_length: 3,
                    text_truncated: false,
                }],
                outcomes: Vec::new(),
                outcomes_truncated: false,
                retained_outcomes: 64,
            };
            state.apply(UiEvent::Snapshot(Box::new(snapshot)));

            assert_eq!(state.queued.len(), 1);
            assert_eq!(state.queued[0].id.as_deref(), Some("m2"));
            assert_eq!(state.queued[0].text, "two");
        }

        #[test]
        fn an_over_cap_queued_message_keeps_the_local_full_draft() {
            // The engine caps `text` at 64 KiB and says so. Adopting the
            // shortened copy would silently mangle the draft the user gets
            // back when they recall it.
            let mut state = state();
            let long = "x".repeat(200);
            state.apply(UiEvent::Queued { text: long.clone(), id: Some("m1".into()) });

            let mut snapshot = snapshot(EngineLifecycle::Busy);
            snapshot.steering = SteeringQueueState {
                pending_count: 1,
                pending: vec![SteeringPendingDto {
                    message_id: "m1".into(),
                    enqueued_at: "now".into(),
                    text: "x".repeat(50),
                    text_length: 200,
                    text_truncated: true,
                }],
                outcomes: Vec::new(),
                outcomes_truncated: false,
                retained_outcomes: 64,
            };
            state.apply(UiEvent::Snapshot(Box::new(snapshot)));
            assert_eq!(state.queued[0].text, long, "the user's own full text survived");
        }

        #[test]
        fn a_message_the_engine_has_not_acknowledged_yet_is_not_dropped() {
            let mut state = state();
            state.apply(UiEvent::Queued { text: "in flight".into(), id: None });
            state.apply(UiEvent::Snapshot(Box::new(snapshot(EngineLifecycle::Busy))));
            assert_eq!(state.queued.len(), 1);
            assert_eq!(state.queued[0].text, "in flight");
        }

        #[test]
        fn the_unsent_recovery_list_is_never_touched_by_reconciliation() {
            // `unsent` is the user's own recoverable text. The engine has no
            // opinion about it and must not be able to erase it.
            let mut state = state();
            state.apply(UiEvent::Queued { text: "lost".into(), id: Some("m1".into()) });
            state.apply(UiEvent::TurnFinished { interrupted: true, error: None });
            assert_eq!(state.unsent.len(), 1);

            state.apply(UiEvent::Snapshot(Box::new(snapshot(EngineLifecycle::Ready))));
            assert_eq!(state.unsent.len(), 1);
            assert_eq!(state.recall_unsent().as_deref(), Some("lost"));
        }

        #[test]
        fn configuration_distinguishes_what_the_running_turn_uses_from_what_is_next() {
            let mut state = state();
            let mut snapshot = snapshot(EngineLifecycle::Busy);
            snapshot.config.active = Some(active_config("claude-haiku"));
            snapshot.turn = Some(turn(ActivityPhase::Responding, Some(1_000)));
            state.apply(UiEvent::Snapshot(Box::new(snapshot)));

            assert_eq!(state.model.as_deref(), Some("claude-sonnet"), "next turn's model");
            assert_eq!(
                state.active_model.as_deref(),
                Some("claude-haiku"),
                "the running turn kept the model it captured"
            );
        }

        /// A `session/models` result carries names a snapshot cannot: this
        /// is the fixture for the model this file's fixtures return
        /// (`active_config` always names provider `anthropic`).
        fn wire_model(id: &str, display_name: &str) -> coda_proto::messages::WireModel {
            serde_json::from_value(serde_json::json!({ "id": id, "displayName": display_name }))
                .expect("wire model")
        }

        #[test]
        fn a_resync_shows_the_friendly_name_a_model_list_already_taught_it() {
            // This is the reported bug: `/model` (or startup) reads
            // `session/models` and the header shows "Claude Sonnet 4.5" —
            // then the very next resync overwrote it with the canonical
            // "claude-sonnet", because `reconcile` assigned `next.model`
            // verbatim instead of resolving it through what had just been
            // learned.
            let mut state = state();
            state
                .model_labels
                .replace(Some("anthropic".into()), &[wire_model("claude-sonnet", "Claude Sonnet 4.5")]);

            state.apply(UiEvent::Snapshot(Box::new(snapshot(EngineLifecycle::Ready))));

            assert_eq!(
                state.model.as_deref(),
                Some("Claude Sonnet 4.5"),
                "a resync must not regress a known model back to its raw id"
            );
        }

        #[test]
        fn both_the_running_and_the_next_model_resolve_through_the_cache() {
            let mut state = state();
            state.model_labels.replace(
                Some("anthropic".into()),
                &[
                    wire_model("claude-sonnet", "Claude Sonnet 4.5"),
                    wire_model("claude-haiku", "Claude Haiku 4.5"),
                ],
            );
            let mut snapshot = snapshot(EngineLifecycle::Busy);
            snapshot.config.active = Some(active_config("claude-haiku"));
            snapshot.turn = Some(turn(ActivityPhase::Responding, Some(1_000)));

            state.apply(UiEvent::Snapshot(Box::new(snapshot)));

            assert_eq!(state.model.as_deref(), Some("Claude Sonnet 4.5"), "next turn's name");
            assert_eq!(
                state.active_model.as_deref(),
                Some("Claude Haiku 4.5"),
                "the running turn's own name"
            );
        }

        #[test]
        fn an_id_the_cache_does_not_know_falls_back_to_itself_not_the_first_cached_name() {
            let mut state = state();
            // Cached under a *different* id than the snapshot reports.
            state
                .model_labels
                .replace(Some("anthropic".into()), &[wire_model("claude-opus-5", "Claude Opus 5")]);

            state.apply(UiEvent::Snapshot(Box::new(snapshot(EngineLifecycle::Ready))));

            assert_eq!(
                state.model.as_deref(),
                Some("claude-sonnet"),
                "an unlisted id must show itself, never an unrelated cached name"
            );
        }

        #[test]
        fn a_name_learned_for_one_provider_never_decorates_another_providers_same_id() {
            // Two providers can both use an id like "gpt-5" for different
            // models; a label cached for one must not bleed onto the other's
            // row just because the id string matches.
            let mut state = state();
            state
                .model_labels
                .replace(Some("openai".into()), &[wire_model("claude-sonnet", "OpenAI's Claude-Sonnet-Named-Thing")]);

            // `active_config`/`snapshot` report provider "anthropic".
            state.apply(UiEvent::Snapshot(Box::new(snapshot(EngineLifecycle::Ready))));

            assert_eq!(
                state.model.as_deref(),
                Some("claude-sonnet"),
                "a different provider's cached name must not apply"
            );
        }

        #[test]
        fn adopting_a_replacement_engine_drops_the_previous_ones_cached_names() {
            let mut state = state();
            state
                .model_labels
                .replace(Some("anthropic".into()), &[wire_model("claude-sonnet", "Claude Sonnet 4.5")]);
            assert_eq!(state.model_labels.resolve(Some("anthropic"), "claude-sonnet"), "Claude Sonnet 4.5");

            state.apply(UiEvent::EngineAdopted);

            assert_eq!(
                state.model_labels.resolve(Some("anthropic"), "claude-sonnet"),
                "claude-sonnet",
                "a replacement engine's names are not evidence about the previous one's"
            );
        }

        #[test]
        fn usage_reports_the_session_total_and_the_last_response_separately() {
            let mut state = state();
            let mut snapshot = snapshot(EngineLifecycle::Busy);
            snapshot.usage = UsageState {
                last_response: Some(UsagePair { input_tokens: 10, output_tokens: 3 }),
                session: Some(UsagePair { input_tokens: 900, output_tokens: 120 }),
                context_limit: Some(200_000),
                unknown_fields: Vec::new(),
            };
            snapshot.turn = Some(turn(ActivityPhase::Responding, Some(1_000)));
            state.apply(UiEvent::Snapshot(Box::new(snapshot)));

            assert_eq!(state.usage.input_tokens, 900, "the session total, not one response");
            assert_eq!(state.usage.context_limit, 200_000);
            assert_eq!(
                state.turn_progress.as_ref().unwrap().last_response_tokens(),
                Some((10, 3)),
                "the pinned row reports the last response, labelled as such"
            );
        }

        #[test]
        fn a_mid_turn_resync_keeps_the_reasoning_this_client_already_watched() {
            // The snapshot brings back one fact — how long the engine says the
            // turn has run — and says nothing about how that time was spent.
            // Rebuilding the clock from it wiped the reasoning segment this
            // client observed, so a turn that reasoned for three seconds and
            // then ran a tool reported no reasoning at all after a resync.
            let mut state = state();
            let start = Instant::now();
            state.apply_at(UiEvent::Submitted { text: "hi".into() }, start);
            state.apply_at(UiEvent::CoreActivity(ActivityPhase::Reasoning), start);
            state.apply_at(
                UiEvent::CoreActivity(ActivityPhase::RunningTools),
                start + Duration::from_secs(3),
            );
            state.apply_at(
                UiEvent::Engine(Event::Usage { input_tokens: 11, output_tokens: 7 }),
                start + Duration::from_secs(3),
            );

            let now = start + Duration::from_secs(4);
            let mut snapshot = snapshot(EngineLifecycle::Busy);
            snapshot.turn = Some(turn(ActivityPhase::RunningTools, Some(120_000)));
            state.apply_at(UiEvent::Snapshot(Box::new(snapshot)), now);

            let progress = state.turn_progress.as_ref().expect("the turn still has a clock");
            assert_eq!(progress.elapsed_ms(now), 120_000, "the engine owns the duration");
            assert_eq!(
                progress.reasoning_ms(now),
                Some(3_000),
                "the reasoning this client observed was discarded by the resync"
            );
            assert_eq!(
                progress.last_response_tokens(),
                Some((11, 7)),
                "the last response's tokens were discarded by the resync"
            );
        }

        #[test]
        fn a_snapshot_describing_a_different_turn_starts_a_fresh_clock() {
            // Carrying reasoning across a turn boundary would credit the new
            // turn with the previous one's thinking.
            let mut state = state();
            let start = Instant::now();
            state.apply_at(UiEvent::Submitted { text: "hi".into() }, start);
            state.apply_at(UiEvent::CoreActivity(ActivityPhase::Reasoning), start);

            let now = start + Duration::from_secs(2);
            let mut first = snapshot(EngineLifecycle::Busy);
            first.turn = Some(turn(ActivityPhase::Reasoning, Some(2_000)));
            state.apply_at(UiEvent::Snapshot(Box::new(first)), now);

            let mut second = snapshot(EngineLifecycle::Busy);
            let mut other = turn(ActivityPhase::WaitingForModel, Some(500));
            other.turn_id = "t2".into();
            second.turn = Some(other);
            state.apply_at(UiEvent::Snapshot(Box::new(second)), now);

            let progress = state.turn_progress.as_ref().expect("a clock for the new turn");
            assert_eq!(progress.elapsed_ms(now), 500);
            assert_eq!(
                progress.reasoning_ms(now),
                None,
                "the new turn inherited the previous turn's reasoning"
            );
        }

        #[test]
        fn a_queued_message_the_engine_reports_as_delivered_is_not_kept_as_unsent() {
            let mut state = state();
            state.apply(UiEvent::Queued { text: "one".into(), id: Some("m1".into()) });

            let mut snapshot = snapshot(EngineLifecycle::Busy);
            snapshot.steering = SteeringQueueState {
                pending_count: 0,
                pending: Vec::new(),
                outcomes: vec![outcome("m1", SteeringOutcomeKind::Delivered)],
                outcomes_truncated: false,
                retained_outcomes: 64,
            };
            state.apply(UiEvent::Snapshot(Box::new(snapshot)));

            assert!(state.queued.is_empty());
            assert!(state.unsent.is_empty(), "a delivered message is in the conversation");
        }

        #[test]
        fn a_queued_message_the_engine_reports_as_not_delivered_stays_recoverable() {
            // `cancelledTurnEnded` is a terminal outcome that says the message
            // never reached the model. Dropping it silently loses text the
            // user typed; the recovery list is where it belongs.
            let mut state = state();
            state.apply(UiEvent::Queued {
                text: "  the whole draft  ".into(),
                id: Some("m1".into()),
            });

            let mut snapshot = snapshot(EngineLifecycle::Ready);
            snapshot.steering = SteeringQueueState {
                pending_count: 0,
                pending: Vec::new(),
                outcomes: vec![outcome("m1", SteeringOutcomeKind::CancelledTurnEnded)],
                outcomes_truncated: false,
                retained_outcomes: 64,
            };
            state.apply(UiEvent::Snapshot(Box::new(snapshot)));

            assert!(state.queued.is_empty());
            assert_eq!(
                state.unsent.iter().map(|m| m.text.as_str()).collect::<Vec<_>>(),
                ["  the whole draft  "],
                "the original draft must survive intact, whitespace included"
            );
        }

        #[test]
        fn a_queued_message_with_no_reported_outcome_is_kept_and_the_doubt_is_stated() {
            // The outcome ring is bounded, so "not listed and no outcome" is
            // genuinely unknown. Claiming "not sent" would be a guess, and
            // resending it would be a guess with consequences.
            let mut state = state();
            state.apply(UiEvent::Queued { text: "ambiguous".into(), id: Some("m1".into()) });

            let mut snapshot = snapshot(EngineLifecycle::Busy);
            snapshot.steering = SteeringQueueState {
                pending_count: 0,
                pending: Vec::new(),
                outcomes: vec![outcome("m9", SteeringOutcomeKind::Delivered)],
                outcomes_truncated: true,
                retained_outcomes: 64,
            };
            state.apply(UiEvent::Snapshot(Box::new(snapshot)));

            assert!(state.queued.is_empty());
            assert_eq!(state.unsent.len(), 1, "the text is kept for recovery");
            assert_eq!(state.unsent[0].text, "ambiguous");

            let notices = notice_texts(&state);
            assert!(
                notices.iter().any(|n| n.contains("did not report")),
                "the uncertainty must be stated, not hidden: {notices:?}"
            );
            assert!(
                !notices.iter().any(|n| n.contains("was not sent")),
                "an unknown outcome must not be reported as a fact: {notices:?}"
            );
        }

        #[test]
        fn rehydrating_replaces_the_conversation_rather_than_appending_to_it() {
            let mut state = state();
            state.apply(UiEvent::Submitted { text: "stale".into() });
            state.apply(UiEvent::Rehydrated {
                blocks: vec![
                    Block::User {
                        text: "from history".into(),
                        timestamp: String::new(),
                        pending: false,
                        queue_id: None,
                    },
                    Block::Assistant { text: "answered".into(), complete: true },
                ],
                notices: Vec::new(),
                coverage: None,
            });
            assert_eq!(state.transcript.len(), 2);
            match &state.transcript.blocks()[0] {
                Block::User { text, .. } => assert_eq!(text, "from history"),
                other => panic!("expected the rehydrated prompt, got {other:?}"),
            }
        }

        #[test]
        fn rehydrating_keeps_the_blocks_this_client_owns() {
            // The banner, the launch notice, `/help` output and a `/diff` are
            // not a projection of the engine's history and no history read
            // can return them. Clearing the transcript wholesale lost them on
            // every resume, gap and compaction.
            let mut state = state();
            state.transcript.push(Block::Banner {
                wordmark: vec!["coda".into()],
                details: vec!["cwd: /w".into()],
            });
            state.notice("Forked from session abc; the original is untouched.", NoticeLevel::Info);
            state.apply(UiEvent::Submitted { text: "stale".into() });
            state.apply(UiEvent::CommandOutput { text: "Permission mode: plan".into() });
            state.apply(UiEvent::DiffOutput { text: "diff --git a b".into() });

            state.apply(UiEvent::Rehydrated {
                blocks: vec![
                    Block::User {
                        text: "from history".into(),
                        timestamp: String::new(),
                        pending: false,
                        queue_id: None,
                    },
                    Block::Assistant { text: "answered".into(), complete: true },
                ],
                notices: Vec::new(),
                coverage: None,
            });

            let blocks = state.transcript.blocks();
            assert!(matches!(blocks.first(), Some(Block::Banner { .. })), "{blocks:?}");
            assert!(
                blocks.iter().any(|b| matches!(b, Block::Notice { text, .. } if text.contains("Forked"))),
                "the launch notice was thrown away: {blocks:?}"
            );
            assert!(
                blocks.iter().any(|b| matches!(b, Block::CommandOutput { .. })),
                "slash-command output was thrown away: {blocks:?}"
            );
            assert!(
                blocks.iter().any(|b| matches!(b, Block::Diff { .. })),
                "a rendered diff was thrown away: {blocks:?}"
            );
            assert!(
                !blocks.iter().any(|b| matches!(b, Block::User { text, .. } if text == "stale")),
                "the replaced conversation is still on screen: {blocks:?}"
            );
            assert!(
                blocks
                    .iter()
                    .any(|b| matches!(b, Block::User { text, .. } if text == "from history")),
                "{blocks:?}"
            );
        }

        #[test]
        fn rehydrating_an_empty_conversation_clears_the_old_one_but_not_the_banner() {
            // A rewind to the very start: the engine says there is no
            // conversation, and the screen must agree — without losing the
            // client's own blocks in the process.
            let mut state = state();
            state.transcript.push(Block::Banner {
                wordmark: vec!["coda".into()],
                details: vec![],
            });
            state.apply(UiEvent::Submitted { text: "stale".into() });

            state.apply(UiEvent::Rehydrated {
                blocks: Vec::new(),
                notices: Vec::new(),
                coverage: None,
            });

            assert!(!state.has_conversation(), "{:?}", state.transcript.blocks());
            assert!(matches!(state.transcript.blocks().first(), Some(Block::Banner { .. })));
        }

        #[test]
        fn rehydrating_keeps_a_queued_message_out_of_the_transcript_and_in_the_queue() {
            // A queued preview is deliberately not a transcript block: it is
            // state.queued, drawn by the composer's own pending list. A
            // rebuild of the conversation must therefore neither lose it nor
            // turn it into a bubble that the next rebuild would wipe.
            let mut state = state();
            state.apply(UiEvent::Queued { text: "send this next".into(), id: Some("m1".into()) });
            assert_eq!(state.queued.len(), 1);

            state.apply(UiEvent::Rehydrated {
                blocks: vec![Block::Assistant { text: "from history".into(), complete: true }],
                notices: Vec::new(),
                coverage: None,
            });

            assert_eq!(state.queued.len(), 1, "the queued message was lost with the transcript");
            assert_eq!(state.queued[0].text, "send this next");
            assert!(
                !state
                    .transcript
                    .blocks()
                    .iter()
                    .any(|b| matches!(b, Block::User { pending: true, .. })),
                "a queued preview must never be mirrored into the transcript: {:?}",
                state.transcript.blocks()
            );
        }

        #[test]
        fn rehydrating_keeps_the_decisions_taken_at_this_terminal() {
            let mut state = state();
            state.apply(UiEvent::PromptRequested(PendingPrompt::Permission {
                tool: "run_command".into(),
                preview: "ls".into(),
            }));
            state.apply(UiEvent::PromptAnswered { allowed: true, answer: None });
            state.apply(UiEvent::Submitted { text: "stale".into() });

            state.apply(UiEvent::Rehydrated {
                blocks: vec![Block::Assistant { text: "from history".into(), complete: true }],
                notices: Vec::new(),
                coverage: None,
            });

            assert!(
                state.transcript.blocks().iter().any(|b| matches!(
                    b,
                    Block::Permission { decision: PermissionDecision::Allowed, .. }
                )),
                "the operator's own decision was erased: {:?}",
                state.transcript.blocks()
            );
        }

        /// I3 (Stage 2 review): a rebuild taken mid-reasoning-burst must park
        /// a still-parked `event/agentMessage` notification *across* the
        /// rebuild, not flush it before the live `Thinking` row's clock/fold
        /// are carried over. Flushing first pushes the notification onto the
        /// OLD transcript's tail, so `take_live_thinking` (which only ever
        /// looks at the literal last block) finds no open `Thinking` there
        /// any more, loses the clock, and the rebuilt open `Thinking` block
        /// ends up *behind* the notification — no longer the transcript's
        /// tail — so the next delta opens a SECOND `Thinking` block instead
        /// of continuing the first.
        #[test]
        fn rehydrating_mid_burst_parks_a_notification_across_the_rebuild_and_flushes_at_the_next_safe_boundary(
        ) {
            let mut state = state();
            state.apply(UiEvent::Engine(Event::Thinking { delta: "first ".into() }));
            assert!(state.thinking_clock.is_some(), "the live burst must have started a clock");

            state.apply(UiEvent::Engine(Event::AgentMessage {
                id: "m1".into(),
                cursor: 1,
                label: "main".into(),
                text: "background note".into(),
                context: None,
                source: "main".into(),
                task_id: None,
                schedule_definition_id: None,
            }));
            assert!(
                state
                    .transcript
                    .blocks()
                    .iter()
                    .all(|b| !matches!(b, Block::AgentMessage { .. })),
                "parked behind the open block, not shown yet"
            );

            // The engine reports the SAME burst still running (a reconnect
            // mid-turn), exactly as `Transcript::replace_conversation`
            // requires for it to be carried over open.
            state.apply(UiEvent::Rehydrated {
                blocks: vec![Block::Thinking {
                    text: "first ".into(),
                    elapsed_ms: 0,
                    tokens: None,
                    complete: false,
                    expanded: false,
                    done_at: None,
                }],
                notices: Vec::new(),
                coverage: None,
            });

            let thinking_blocks: Vec<_> = state
                .transcript
                .blocks()
                .iter()
                .filter(|b| matches!(b, Block::Thinking { .. }))
                .collect();
            assert_eq!(
                thinking_blocks.len(),
                1,
                "the rebuild must not have already split the burst: {:?}",
                state.transcript.blocks()
            );
            assert!(
                state.thinking_clock.is_some(),
                "the live clock must survive a rebuild that reports the same open burst"
            );
            assert!(
                state
                    .transcript
                    .blocks()
                    .iter()
                    .all(|b| !matches!(b, Block::AgentMessage { .. })),
                "still parked across the rebuild — the burst has not ended"
            );

            // Further reasoning must continue the SAME block.
            state.apply(UiEvent::Engine(Event::Thinking { delta: "second".into() }));
            let thinking_blocks: Vec<_> = state
                .transcript
                .blocks()
                .iter()
                .filter(|b| matches!(b, Block::Thinking { .. }))
                .collect();
            assert_eq!(
                thinking_blocks.len(),
                1,
                "a second Thinking block means the rebuild silently ended the first: {:?}",
                state.transcript.blocks()
            );
            assert!(matches!(
                thinking_blocks[0],
                Block::Thinking { text, .. } if text == "first second"
            ));

            // The safe boundary: the parked notification lands only now.
            state.apply(UiEvent::Engine(Event::ThinkingComplete {
                elapsed_ms: 10,
                thinking_tokens: None,
            }));
            let agent_texts: Vec<String> = state
                .transcript
                .blocks()
                .iter()
                .filter_map(|b| match b {
                    Block::AgentMessage { text, .. } => Some(text.clone()),
                    _ => None,
                })
                .collect();
            assert_eq!(agent_texts, vec!["background note".to_string()]);
        }

        /// The idle/terminal release half of the same fix: if the rebuilt
        /// burst never receives another delta at all (the turn was actually
        /// interrupted, or errored, or simply completed exactly as the
        /// engine last reported it), the parked notification must still be
        /// released by whichever terminal event closes the block — not left
        /// parked forever because `Rehydrated` itself declined to flush it.
        #[test]
        fn a_parked_notification_across_a_rehydrate_is_released_by_turn_complete_with_no_further_delta()
        {
            let mut state = state();
            state.apply(UiEvent::Engine(Event::Thinking { delta: "first ".into() }));
            state.apply(UiEvent::Engine(Event::AgentMessage {
                id: "m1".into(),
                cursor: 1,
                label: "main".into(),
                text: "background note".into(),
                context: None,
                source: "main".into(),
                task_id: None,
                schedule_definition_id: None,
            }));

            state.apply(UiEvent::Rehydrated {
                blocks: vec![Block::Thinking {
                    text: "first ".into(),
                    elapsed_ms: 0,
                    tokens: None,
                    complete: false,
                    expanded: false,
                    done_at: None,
                }],
                notices: Vec::new(),
                coverage: None,
            });

            // No further `Thinking` delta ever arrives — the turn simply
            // ends (interrupted, errored, or completed exactly as reported).
            state.apply(UiEvent::Engine(Event::TurnComplete {
                stop_reason: None,
                interrupted: false,
                root_turn_id: None,
                activity_id: None,
            }));

            let agent_texts: Vec<String> = state
                .transcript
                .blocks()
                .iter()
                .filter_map(|b| match b {
                    Block::AgentMessage { text, .. } => Some(text.clone()),
                    _ => None,
                })
                .collect();
            assert_eq!(
                agent_texts,
                vec!["background note".to_string()],
                "a parked notification must not be stranded forever with no further delta"
            );
        }

        #[test]
        fn a_live_result_never_rewrites_a_replayed_call_that_merely_shares_an_id() {
            // The transcript can now hold a rehydrated call from an earlier
            // turn *and* a live call with the same provider id. Matching on
            // the id alone would put the live output into the old call.
            let mut state = state();
            state.apply(UiEvent::Rehydrated {
                blocks: vec![Block::Tools {
                    activity: ToolActivity {
                        calls: vec![{
                            let mut call = ToolCall::new("read_file", "{}");
                            call.result = Some("old output".into());
                            call.status = CallStatus::Succeeded;
                            call
                        }],
                        complete: true,
                        ..Default::default()
                    },
                    key: ActivityKey {
                        root_turn_id: Some("old-turn".into()),
                        activity_id: Some("old-batch".into()),
                    },
                    calls: vec![Correlation {
                        root_turn_id: Some("old-turn".into()),
                        activity_id: Some("old-batch".into()),
                        call_id: Some("toolu_1".into()),
                        source_id: Some("toolu_1".into()),
                        ..Default::default()
                    }],
                }],
                notices: Vec::new(),
                coverage: None,
            });

            let live = Correlation {
                root_turn_id: Some("new-turn".into()),
                activity_id: Some("new-batch".into()),
                call_id: Some("toolu_1".into()),
                source_id: Some("toolu_1".into()),
                ..Default::default()
            };
            state.apply(UiEvent::Engine(Event::ToolCall {
                tool_name: "read_file".into(),
                input_json: "{}".into(),
                correlation: live.clone(),
            }));
            state.apply(UiEvent::Engine(Event::ToolResult {
                tool_name: "read_file".into(),
                content: "new output".into(),
                is_error: false,
                status: Some(ToolCallStatus::Succeeded),
                correlation: live,
            }));

            let groups: Vec<&ToolActivity> = state
                .transcript
                .blocks()
                .iter()
                .filter_map(|b| match b {
                    Block::Tools { activity, .. } => Some(activity),
                    _ => None,
                })
                .collect();
            assert_eq!(groups.len(), 2, "the live call opened its own group");
            assert_eq!(groups[0].calls[0].result.as_deref(), Some("old output"));
            assert_eq!(groups[1].calls[0].result.as_deref(), Some("new output"));
        }
    }

    // ── Stage 2: `event/agentMessage` reducer ─────────────────────────────

    fn agent_message_texts(state: &UiState) -> Vec<String> {
        state
            .transcript
            .blocks()
            .iter()
            .filter_map(|b| match b {
                Block::AgentMessage { text, .. } => Some(text.clone()),
                _ => None,
            })
            .collect()
    }

    #[test]
    fn agent_message_event_materialises_a_distinct_block() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AgentMessage {
            id: "m1".into(),
            cursor: 1,
            label: "nightly audit".into(),
            text: "report ready".into(),
            context: None,
            source: "scheduledTask".into(),
            task_id: Some("task-1".into()),
            schedule_definition_id: Some("def-1".into()),
        }));
        assert_eq!(agent_message_texts(&state), vec!["report ready".to_string()]);
        // Never a Notice and never an Assistant block: it is its own kind.
        assert!(notice_texts(&state).is_empty());
        assert!(assistant_text(&state).is_none());
    }

    #[test]
    fn agent_message_is_deduplicated_by_stable_id_not_content() {
        let mut state = state();
        let make = || Event::AgentMessage {
            id: "dup".into(),
            cursor: 1,
            label: "main".into(),
            text: "same text".into(),
            context: None,
            source: "main".into(),
            task_id: None,
            schedule_definition_id: None,
        };
        state.apply(UiEvent::Engine(make()));
        state.apply(UiEvent::Engine(make()));
        assert_eq!(agent_message_texts(&state).len(), 1, "a repeated id must not duplicate the block");
    }

    #[test]
    fn agent_message_with_different_id_but_same_text_is_not_deduplicated() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AgentMessage {
            id: "a".into(),
            cursor: 1,
            label: "main".into(),
            text: "same text".into(),
            context: None,
            source: "main".into(),
            task_id: None,
            schedule_definition_id: None,
        }));
        state.apply(UiEvent::Engine(Event::AgentMessage {
            id: "b".into(),
            cursor: 2,
            label: "main".into(),
            text: "same text".into(),
            context: None,
            source: "main".into(),
            task_id: None,
            schedule_definition_id: None,
        }));
        assert_eq!(agent_message_texts(&state).len(), 2);
    }

    /// The critical regression this stage's own review flagged: pushing a
    /// whole new block straight onto the tail while an `Assistant` block is
    /// still open stops it being `Transcript::open_tail` (which only ever
    /// looks at the literal last block) — so the *next* delta would silently
    /// open a SECOND `Assistant` block instead of continuing the first one,
    /// splitting one reply into two. A notification must park behind the
    /// open block and only land once it is actually safe.
    #[test]
    fn agent_message_never_splits_an_open_assistant_block_across_further_deltas() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "partial ".into() }));

        // The notification arrives mid-burst — it must NOT be visible yet
        // (that would require inserting ahead of/through the open block).
        state.apply(UiEvent::Engine(Event::AgentMessage {
            id: "m1".into(),
            cursor: 1,
            label: "subagent".into(),
            text: "child finished".into(),
            context: None,
            source: "subagent".into(),
            task_id: Some("task-1".into()),
            schedule_definition_id: None,
        }));
        assert!(
            agent_message_texts(&state).is_empty(),
            "a notification behind a still-open block must be parked, not shown yet"
        );

        // Further assistant deltas AND reasoning deltas must still continue
        // the SAME open block — proving nothing was split by the park.
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "reply".into() }));
        let assistant_blocks: Vec<_> = state
            .transcript
            .blocks()
            .iter()
            .filter(|b| matches!(b, Block::Assistant { .. }))
            .collect();
        assert_eq!(
            assistant_blocks.len(),
            1,
            "the notification must never cause a second Assistant block to open"
        );
        assert!(matches!(
            assistant_blocks[0],
            Block::Assistant { text, complete: false } if text == "partial reply"
        ));

        // Completing the turn is the safe boundary: the parked notification
        // lands now, after the reply it was parked behind.
        state.apply(UiEvent::Engine(Event::AssistantTextComplete));
        assert_eq!(agent_message_texts(&state), vec!["child finished".to_string()]);
        let blocks = state.transcript.blocks();
        let assistant_index =
            blocks.iter().position(|b| matches!(b, Block::Assistant { .. })).unwrap();
        let agent_index =
            blocks.iter().position(|b| matches!(b, Block::AgentMessage { .. })).unwrap();
        assert!(agent_index > assistant_index, "the notification lands AFTER the reply it interrupted");
    }

    /// Same invariant, proven through a `Thinking` burst instead of an
    /// `Assistant` one, and through `ThinkingComplete` (the boundary that
    /// finishes a block "in place" rather than through `Transcript::close_open`).
    #[test]
    fn agent_message_never_splits_an_open_thinking_block_and_flushes_on_thinking_complete() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "first ".into() }));

        state.apply(UiEvent::Engine(Event::AgentMessage {
            id: "m1".into(),
            cursor: 1,
            label: "main".into(),
            text: "background note".into(),
            context: None,
            source: "main".into(),
            task_id: None,
            schedule_definition_id: None,
        }));
        assert!(agent_message_texts(&state).is_empty());

        state.apply(UiEvent::Engine(Event::Thinking { delta: "second".into() }));
        let thinking_blocks: Vec<_> = state
            .transcript
            .blocks()
            .iter()
            .filter(|b| matches!(b, Block::Thinking { .. }))
            .collect();
        assert_eq!(thinking_blocks.len(), 1, "reasoning must never be split by a parked notification");
        assert!(matches!(
            thinking_blocks[0],
            Block::Thinking { text, .. } if text == "first second"
        ));

        state.apply(UiEvent::Engine(Event::ThinkingComplete { elapsed_ms: 10, thinking_tokens: None }));
        assert_eq!(agent_message_texts(&state), vec!["background note".to_string()]);
    }

    #[test]
    fn reset_agent_message_dedup_allows_a_previously_seen_id_again() {
        let mut state = state();
        let make = || Event::AgentMessage {
            id: "m1".into(),
            cursor: 1,
            label: "main".into(),
            text: "hello".into(),
            context: None,
            source: "main".into(),
            task_id: None,
            schedule_definition_id: None,
        };
        state.apply(UiEvent::Engine(make()));
        state.reset_agent_message_dedup();
        state.apply(UiEvent::Engine(make()));
        // Two distinct materialisations after the reset — a new engine
        // instance's cursor space starts over and must not be suppressed by
        // the previous instance's ids.
        assert_eq!(agent_message_texts(&state).len(), 2);
    }

    /// A notification still parked behind an open block when the engine
    /// instance is replaced must not resurface once the new process's
    /// content starts arriving — it belonged to a process that is gone.
    #[test]
    fn reset_agent_message_dedup_drops_a_still_parked_notification() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "mid reply".into() }));
        state.apply(UiEvent::Engine(Event::AgentMessage {
            id: "m1".into(),
            cursor: 1,
            label: "main".into(),
            text: "stale notification".into(),
            context: None,
            source: "main".into(),
            task_id: None,
            schedule_definition_id: None,
        }));
        assert!(agent_message_texts(&state).is_empty(), "parked, not yet shown");

        state.reset_agent_message_dedup();
        state.apply(UiEvent::Engine(Event::AssistantTextComplete));
        assert!(
            agent_message_texts(&state).is_empty(),
            "a notification parked by a superseded engine instance must not surface later"
        );
    }

    #[test]
    fn agent_message_block_is_client_owned_and_survives_history_hydration() {
        assert!(Block::AgentMessage {
            id: "m1".into(),
            label: "main".into(),
            text: "hi".into(),
            context: None,
            source: "main".into(),
            task_id: None,
            schedule_definition_id: None,
        }
        .is_client_owned());
    }
}
