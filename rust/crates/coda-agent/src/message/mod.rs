//! Engine-owned in-memory user-notification bus (Stage 2) plus the trusted
//! main-conversation inbox (Stage 3 chunk A).
//!
//! # What this is, and is not
//! [`MessageBus`] lets background work (a scheduled run, a subagent, or a
//! trusted "main" context) publish a short passive notification that the
//! user will see in the chat surface. It is **not** a channel back into the
//! model: nothing here wakes the main agent loop, feeds text into a prompt,
//! or otherwise causes an LLM call. That is unchanged for the *user* ring.
//!
//! # The main inbox (Stage 3 chunk A)
//! The SAME bus also carries a second, independent FIFO: the main
//! conversation's own inbox, fed by the `ask_main` tool via
//! [`MessageBus::publish_main`]. Unlike the user ring this queue IS delivered
//! into the model's own history — but only by the single trusted consumer
//! (the main `AgentLoop`, at an iteration boundary — see
//! `crate::agent::AgentLoop` step 4c and `crate::agent::stop::decide_stop`),
//! never by a child, never mid-batch, and never by preempting a tool that is
//! already running. Accepting a message onto this queue is an **asynchronous,
//! accepted-only handoff**: the caller (a task, subagent, or scheduled run)
//! never waits for an answer and never holds a permit while one is pending —
//! `publish_main` returns as soon as the bus takes custody, exactly like
//! `publish_user`.
//!
//! Both queues share one [`MessageBus`] and one internal gate lock
//! deliberately (see `Inner`'s doc comment): they are two independent FIFOs
//! for two independent consumers, not two independently-lockable resources,
//! so there is no dual-lock ordering hazard to get wrong.
//!
//! It is the mirror image of [`crate::steering::SteeringInbox`]: steering is
//! operator -> agent (mid-turn, delivered into history); this bus is
//! background-work -> user (passive, delivered into the presentation layer
//! only). A [`MessageBusObserver`] seam lets `coda-serve` mirror publications
//! into its own state/event-bus projection, following the same shape
//! `SteeringObserver` uses — see that module's docs for the lock-order
//! discipline this mirrors (`BUS -> STATE -> (outer) BUS`).
//!
//! # Lifetime
//! Entirely in-memory, entirely RAM: there is no persistence and no
//! cross-restart continuation. A fresh `MessageBus` starts at cursor `0` with
//! an empty ring; nothing here is loaded from or written to disk.
//!
//! # Cursor vs. `EventBus` seq
//! [`MessageBus::user_since`] pages by **this bus's own monotonic cursor**,
//! which is unrelated to `coda_serve`'s `EventBus` seq space. A caller must
//! not mix the two: the bus cursor identifies a position in *this* ring, the
//! `EventBus` seq identifies a position in the wire notification stream. Both
//! start at similar-looking small integers, which is exactly why call sites
//! must always be explicit about which cursor a value belongs to.
//!
//! # Idempotency
//! Idempotency is scoped to `(source identity, key)`: the same key from two
//! different trusted origins never collides. Reusing a key with the *same*
//! body is a no-op replay (the original receipt is returned, no new cursor is
//! consumed). Reusing a key with a *different* body is rejected as an
//! explicit conflict — never silently accepted as "new content, same key".
//! The idempotency horizon is bounded by ring retention: once a message
//! scrolls out of the ring, its key becomes reusable again. This is
//! documented behaviour, not a bug: an unbounded idempotency table would be
//! an unbounded memory leak in a long-running session.
//!
//! # Receipts
//! [`PublishReceipt`] means "the bus accepted this notification for
//! delivery." It never means "the user saw it" or "the user was presented
//! with it" — this bus has no acknowledgement channel from the presentation
//! layer, so making a stronger claim would be dishonest.

use std::collections::{HashMap, VecDeque};
use std::sync::Mutex;

/// Bound on [`UserMessage::label`] (UTF-8 chars).
pub const MAX_LABEL_CHARS: usize = 128;
/// Bound on [`UserMessage::context`] (UTF-8 chars).
pub const MAX_CONTEXT_CHARS: usize = 512;
/// Bound on [`UserMessage::body`] (UTF-8 chars). Chosen generously for a
/// short passive notification while still ruling out someone using this
/// channel to smuggle arbitrary large text to the user.
pub const MAX_BODY_CHARS: usize = 4000;
/// Bound on a caller-supplied idempotency key (UTF-8 chars). Keys are
/// retained in the idempotency table for as long as their originating
/// message stays in the ring (see module docs), so an unbounded key would be
/// an unbounded per-entry memory cost on top of the already-bounded ring.
/// Oversized keys are rejected outright (`PublishError::IdempotencyKeyTooLong`)
/// rather than silently truncated, since truncation would let two distinct
/// caller-chosen keys collide.
pub const MAX_IDEMPOTENCY_KEY_CHARS: usize = 200;
/// Default ring capacity (number of retained messages) before the oldest
/// entries are evicted to make room for new ones.
pub const DEFAULT_RING_CAPACITY: usize = 200;

/// Bound on the main inbox FIFO (Stage 3 chunk A, `ask_main`) — distinct from
/// [`DEFAULT_RING_CAPACITY`]. Unlike the user ring, the main queue never
/// silently evicts a pending item to make room for a new one: a full queue
/// refuses new publications outright (`AskMainError::QueueFull`) so a caller
/// can retry rather than losing a request it never knew was dropped. A
/// bounded backstop is still needed so a runaway background task cannot
/// queue unbounded pending work for the trusted main conversation.
pub const MAIN_QUEUE_CAPACITY: usize = 32;

/// Bound on the main inbox's "recently delivered" idempotency window. The
/// user ring's idempotency horizon rides on ring retention (an evicted
/// message's key becomes reusable); the main queue has no ring to piggy-back
/// on — a delivered item is removed outright — so this is its own explicit
/// bounded window. A retry immediately after a bulk drain (`take_main_for_delivery`)
/// still deduplicates against this window; once more than
/// `MAIN_DELIVERED_HORIZON` items have been delivered since, the same key
/// becomes reusable for new content. Documented behaviour, not a bug — an
/// unbounded table would be an unbounded memory leak in a long-running session.
pub const MAIN_DELIVERED_HORIZON: usize = 128;

/// Trusted provenance of a published message. Constructed only by trusted
/// Rust callers (the `notify_user` tool, resolving `ToolContext`); never
/// parsed from tool arguments or model output.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MessageSource {
    /// A run launched directly by the schedule runtime.
    ScheduledTask { definition_id: String, definition_name: Option<String>, task_id: String },
    /// A subagent (`task` tool), foreground or background, at any nesting
    /// depth. `task_id` is the task id the caller resolved via
    /// `TaskManager`; `label` is the human-readable task description.
    Subagent { task_id: String, label: String },
    /// The trusted main conversation context (not a task at all).
    Main,
}

impl MessageSource {
    /// Stable classification token — safe for logs, wire fields and tests.
    pub fn kind(&self) -> &'static str {
        match self {
            MessageSource::ScheduledTask { .. } => "scheduledTask",
            MessageSource::Subagent { .. } => "subagent",
            MessageSource::Main => "main",
        }
    }

    /// Stable identity string used to scope idempotency keys: two different
    /// origins must never collide on the same user-supplied key.
    fn scope_id(&self) -> String {
        match self {
            MessageSource::ScheduledTask { definition_id, .. } => {
                format!("scheduled:{definition_id}")
            }
            MessageSource::Subagent { task_id, .. } => format!("task:{task_id}"),
            MessageSource::Main => "main".to_owned(),
        }
    }

    /// Bounded, sanitized display label derived from the actual task/schedule
    /// identity — never from caller-supplied text, so a child cannot spoof a
    /// different source's label.
    fn display_label(&self) -> String {
        let raw = match self {
            MessageSource::ScheduledTask { definition_name, definition_id, .. } => {
                definition_name.clone().unwrap_or_else(|| definition_id.clone())
            }
            MessageSource::Subagent { label, .. } => label.clone(),
            MessageSource::Main => "main".to_owned(),
        };
        bound_chars(&raw, MAX_LABEL_CHARS)
    }

    /// The trusted task id this source is scoped to, when it is a task at
    /// all (`Main` is not a task and has none). This is the same id an
    /// external client can independently correlate against `TaskManager`
    /// snapshots — never a label, which is not guaranteed unique.
    pub fn trusted_task_id(&self) -> Option<&str> {
        match self {
            MessageSource::ScheduledTask { task_id, .. } => Some(task_id),
            MessageSource::Subagent { task_id, .. } => Some(task_id),
            MessageSource::Main => None,
        }
    }

    /// The scheduled definition id this source originated from, when it is a
    /// scheduled run (directly or, via propagation, one of its nested
    /// children). `None` for an ordinary subagent or `Main`.
    pub fn schedule_definition_id(&self) -> Option<&str> {
        match self {
            MessageSource::ScheduledTask { definition_id, .. } => Some(definition_id),
            MessageSource::Subagent { .. } | MessageSource::Main => None,
        }
    }
}

/// Bound a *derived* display label to at most `max_chars` UTF-8 characters,
/// collapsing it to a single control-character-free line first.
///
/// Used only for labels derived from a trusted source's own identity — never
/// for user-supplied text, which is rejected outright when oversized rather
/// than silently reshaped (see [`PublishError`]). Without the sanitisation
/// step a task/definition name containing a newline or an ANSI escape could
/// still corrupt a single-line rendering surface even though its *length*
/// was within bounds.
fn bound_chars(s: &str, max_chars: usize) -> String {
    let single_line: String =
        s.chars().map(|c| if c.is_control() { ' ' } else { c }).collect();
    let collapsed = single_line.split_whitespace().collect::<Vec<_>>().join(" ");
    if collapsed.chars().count() <= max_chars {
        collapsed
    } else {
        collapsed.chars().take(max_chars).collect()
    }
}

/// One notification accepted onto the bus.
#[derive(Debug, Clone)]
pub struct UserMessage {
    /// Stable id (uuid, no hyphens) — unique for the process lifetime.
    pub id: String,
    /// This bus's own monotonic cursor position. Distinct from any
    /// `coda_serve::EventBus` seq (see module docs).
    pub cursor: u64,
    /// Derived, bounded, sanitized label identifying the origin (task/schedule
    /// name) — never spoofable via tool arguments.
    pub label: String,
    /// The notification body (bounded, see [`MAX_BODY_CHARS`]).
    pub body: String,
    /// Optional short additional context (bounded, see [`MAX_CONTEXT_CHARS`]).
    pub context: Option<String>,
    /// Stable classification of the origin ("scheduledTask" | "subagent" | "main").
    pub source_kind: &'static str,
    /// The trusted task id this notification is scoped to, when the source
    /// is a task at all (`Main` has none). Derived only from
    /// `source.trusted_task_id()` at publish time — never from caller
    /// arguments — so an external client has a stable, unique correlation
    /// handle instead of only a non-unique `label`.
    pub task_id: Option<String>,
    /// The scheduled definition id this notification originated from, when
    /// the source is a scheduled run (directly or as a nested child of one).
    /// `None` for an ordinary subagent or `Main`.
    pub schedule_definition_id: Option<String>,
    /// The caller-supplied idempotency key, if any (opaque, not displayed).
    pub idempotency_key: Option<String>,
    /// Internal idempotency scope id (not part of the wire payload) — kept
    /// so eviction can clean up the matching idempotency-table entry.
    scope_id: String,
}

/// Why a publish attempt was refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PublishError {
    /// The bus has been closed; no further publication is possible.
    Closed,
    /// `body` was empty (or all whitespace).
    EmptyBody,
    /// `body` exceeded [`MAX_BODY_CHARS`].
    BodyTooLong,
    /// `context` exceeded [`MAX_CONTEXT_CHARS`].
    ContextTooLong,
    /// The caller-supplied idempotency key exceeded [`MAX_IDEMPOTENCY_KEY_CHARS`].
    /// Rejected rather than truncated: truncating could make two distinct
    /// caller-chosen keys collide.
    IdempotencyKeyTooLong,
    /// The same `(source, key)` was already used with **different** content
    /// (body or context). Never silently accepted as new content — the
    /// caller must pick a new key or resend the exact same content to get
    /// the original receipt.
    IdempotencyConflict,
}

impl PublishError {
    pub fn as_str(&self) -> &'static str {
        match self {
            PublishError::Closed => "closed",
            PublishError::EmptyBody => "emptyBody",
            PublishError::BodyTooLong => "bodyTooLong",
            PublishError::ContextTooLong => "contextTooLong",
            PublishError::IdempotencyKeyTooLong => "idempotencyKeyTooLong",
            PublishError::IdempotencyConflict => "idempotencyConflict",
        }
    }
}

/// The result of a successful `publish_user` call.
///
/// `accepted` only ever means "the bus took custody of this notification for
/// later delivery" — never "the user saw it". See module docs.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PublishReceipt {
    pub id: String,
    pub cursor: u64,
    /// `true` when this call matched an existing `(source, key)` with
    /// identical content and returned the *original* receipt rather than
    /// publishing again.
    pub deduplicated: bool,
}

/// Exact bounds of notifications lost to ring eviction, reported alongside
/// `gap: true` so a client is told precisely what it lost rather than only
/// that it lost something.
///
/// Exact because the bus assigns a cursor to every accepted (non-deduplicated)
/// publication with no gaps in the sequence, and the ring evicts strictly in
/// cursor order: every cursor in `from..=to` was therefore actually published
/// and actually evicted before this read could reach it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DroppedRange {
    /// The first (lowest) evicted cursor this read could not recover.
    pub from: u64,
    /// The last (highest) evicted cursor — the bus's own high-water mark.
    pub to: u64,
    /// `to - from + 1`, provided pre-computed so a caller never has to
    /// reproduce the off-by-one arithmetic themselves.
    pub count: u64,
}

/// The result of a non-destructive `user_since` read.
#[derive(Debug, Clone)]
pub struct SinceResult {
    pub messages: Vec<UserMessage>,
    /// The cursor a subsequent call should pass to continue from here.
    ///
    /// Never advances past what this call actually returned: a page that
    /// returned nothing (whether because the caller is caught up, or because
    /// `limit: Some(0)` truncated a non-empty page to nothing) reports
    /// `after_cursor` unchanged rather than jumping ahead, so a caller can
    /// never mistake "asked for zero" or "nothing new yet" for "read
    /// everything".
    pub next_cursor: u64,
    /// `true` when the requested cursor was already behind the oldest
    /// message still retained in the ring: some notifications between the
    /// requested cursor and the oldest retained one were evicted and can
    /// never be recovered. Reported honestly rather than silently resuming
    /// from whatever happens to remain.
    pub gap: bool,
    /// Exact range/count of the evicted notifications, set exactly when
    /// `gap` is `true`.
    pub dropped: Option<DroppedRange>,
    /// `true` when more messages exist beyond the page returned (bounded
    /// paging); call again with `next_cursor` to continue.
    pub truncated: bool,
}

// ── Main inbox (Stage 3 chunk A, `ask_main`) ──────────────────────────────────

/// One accepted-only request queued for the trusted main conversation's own
/// inbox. Distinct from [`UserMessage`]: this is background-work -> main
/// *agent* (and IS eventually injected into the model's own history), not
/// background-work -> user (passive display only). Constructed only inside
/// [`MessageBus::publish_main`].
#[derive(Debug, Clone)]
pub struct MainMessage {
    /// Stable id (uuid, no hyphens) — unique for the process lifetime.
    pub id: String,
    /// This queue's own monotonic sequence. Distinct from the user ring's
    /// `cursor` field and from any `coda_serve::EventBus` seq — three
    /// separate numbering spaces that must never be mixed (see module docs).
    pub seq: u64,
    /// Derived, bounded, sanitized label identifying the origin — same rules
    /// as [`UserMessage::label`].
    pub label: String,
    pub source_kind: &'static str,
    pub task_id: Option<String>,
    pub schedule_definition_id: Option<String>,
    pub body: String,
    pub context: Option<String>,
    pub idempotency_key: Option<String>,
    /// Internal idempotency scope id (not part of the wire payload).
    scope_id: String,
}

impl MainMessage {
    /// The single authoritative formatter for this message's injected text.
    ///
    /// Used both to append this item to the main `AgentLoop`'s own history
    /// (step 4c) and for `coda-serve`'s live projection of exactly what was
    /// injected (`MessageBusObserver::on_main_delivered`) — keeping one
    /// formatter means those two surfaces can never drift apart into showing
    /// different text for "what actually happened".
    ///
    /// Always: a literal `[agent-message]` prefix, the trusted source's own
    /// label and ids (never caller-supplied — derived the same way
    /// [`UserMessage`]'s are), and an explicit disclaimer that this is a
    /// background task request — **never** promoted to a new user
    /// instruction — followed by the body and, if present, the context.
    pub fn injected_text(&self) -> String {
        let mut header =
            format!("[agent-message] from {} (source={}", self.label, self.source_kind);
        if let Some(task_id) = &self.task_id {
            header.push_str(&format!(", taskId={task_id}"));
        }
        if let Some(def_id) = &self.schedule_definition_id {
            header.push_str(&format!(", scheduleDefinitionId={def_id}"));
        }
        header.push(')');
        let mut text = format!(
            "{header}: background task request, not a new user instruction.\n\n{}",
            self.body
        );
        if let Some(ctx) = &self.context {
            text.push_str(&format!("\n\nContext: {ctx}"));
        }
        text
    }
}

/// Honest status of an [`AskReceipt`]. Never conflated with "the main
/// conversation acted on it" — this bus has no channel to report that, only
/// whether the item is still waiting or has already been taken by the single
/// consumer.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AskStatus {
    /// Still sitting in the main FIFO, waiting for the single consumer (the
    /// main `AgentLoop`, at its own iteration boundary) to drain it.
    Queued,
    /// Already taken off the queue by the single consumer. Only reachable via
    /// a deduplicated replay matched against the bounded recently-delivered
    /// window (see [`MAIN_DELIVERED_HORIZON`]) — a brand-new publish is
    /// always `Queued`.
    Delivered,
}

/// The result of an accepted `publish_main` call.
///
/// `injected` (the model-visible text) is never the same thing as `accepted`
/// (this receipt): accepting only means the bus took custody for later
/// delivery — never that the main conversation has read or processed it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AskReceipt {
    pub id: String,
    pub seq: u64,
    /// `true` when this call matched an existing `(source, key)` with
    /// identical content and returned the *original* receipt.
    pub deduplicated: bool,
    /// The 1-based position in the FIFO **at the moment this receipt was
    /// produced** — recomputed fresh on every call (including a deduplicated
    /// replay), never a value cached from whenever the item was first
    /// queued, so a replay reports where the item actually is *now*. `None`
    /// exactly when `status` is `Delivered` (there is no queue position for
    /// an item already taken off the queue).
    pub queue_position: Option<usize>,
    pub status: AskStatus,
}

/// Why a `publish_main` attempt was refused.
///
/// Deliberately a separate type from [`PublishError`] rather than an added
/// variant on it: the two queues share their content *limits* but not their
/// failure surface — `QueueFull` has no meaning for the user ring (which
/// evicts instead of refusing), and folding it into `PublishError` would
/// leave every existing exhaustive match on that enum with a branch
/// `publish_user` can never actually produce.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AskMainError {
    /// The bus has been closed; no further publication is possible.
    Closed,
    EmptyBody,
    BodyTooLong,
    ContextTooLong,
    IdempotencyKeyTooLong,
    /// The same `(source, key)` was already used with **different** content.
    IdempotencyConflict,
    /// The main FIFO is at [`MAIN_QUEUE_CAPACITY`] and this is not a retry of
    /// an already-pending (or recently-delivered) `(source, key)`. Pending
    /// requests are never evicted to make room — the caller must wait for
    /// the single consumer to drain the queue and retry.
    QueueFull,
}

impl AskMainError {
    pub fn as_str(&self) -> &'static str {
        match self {
            AskMainError::Closed => "closed",
            AskMainError::EmptyBody => "emptyBody",
            AskMainError::BodyTooLong => "bodyTooLong",
            AskMainError::ContextTooLong => "contextTooLong",
            AskMainError::IdempotencyKeyTooLong => "idempotencyKeyTooLong",
            AskMainError::IdempotencyConflict => "idempotencyConflict",
            AskMainError::QueueFull => "queueFull",
        }
    }
}

/// Synchronous observer for bus transitions (mirrors `SteeringObserver`).
///
/// Implemented by `coda-serve`, never by `coda-agent` itself. Invoked while
/// still holding the bus's own gate lock, so "the mutation happened" and
/// "the observer saw it" are one atomic step. Must never call back into the
/// bus (no re-entrancy) and must never `.await`.
pub trait MessageBusObserver: Send + Sync {
    /// A message was newly published (not a deduplicated replay).
    fn on_published(&self, _msg: &UserMessage) {}
    /// One or more messages were evicted from the ring to make room. The
    /// range is `[from_cursor, to_cursor]` inclusive.
    fn on_overflow(&self, _from_cursor: u64, _to_cursor: u64) {}
    /// The bus was closed.
    fn on_closed(&self) {}
    /// A request was newly accepted onto the main inbox (not a deduplicated
    /// replay). This is `coda-serve`'s sole wake signal for the idle
    /// main-inbox execution path — it must never itself publish into the
    /// user ring (this queue and the user ring stay fully independent).
    fn on_main_accepted(&self, _msg: &MainMessage) {}
    /// One or more main-inbox items were drained for delivery by the single
    /// consumer (never called with an empty slice). Lets `coda-serve`
    /// project the exact `injected_text()` of each item into its own live
    /// history view — the same no-await, drain-then-append relationship
    /// `SteeringObserver::on_delivered` documents: by the time this fires the
    /// items are already (atomically, under this same gate lock) off the
    /// queue, so there is no window where a second reader could see them
    /// twice or miss them.
    fn on_main_delivered(&self, _msgs: &[MainMessage]) {}
}

struct IdempotencyRecord {
    cursor: u64,
    /// The exact retained body — compared for equality, not hashed, so two
    /// different bodies can never collide into a false "identical replay".
    body: String,
    /// The exact retained context — part of the identity of "the same
    /// notification", so the same key/body with a *different* context must
    /// still conflict rather than being treated as a replay.
    context: Option<String>,
}

/// Idempotency record for the main-inbox FIFO. Independent from
/// [`IdempotencyRecord`] above (the user ring's table): the same
/// `(source, key)` used once for `notify_user` and once for `ask_main` must
/// never collide (they live in entirely separate `HashMap`s).
struct MainIdempotencyRecord {
    seq: u64,
    id: String,
    body: String,
    context: Option<String>,
}

struct Inner {
    ring: VecDeque<UserMessage>,
    next_cursor: u64,
    capacity: usize,
    /// Shared close flag: `true` refuses **both** `publish_user` and
    /// `publish_main`. Reads (`user_since`, and the main inbox's read-only
    /// accessors) remain available against whatever was already retained.
    closed: bool,
    /// Highest cursor ever evicted from the ring (`0` until the first
    /// eviction happens). Used to report `gap` honestly regardless of
    /// whether the caller's cursor is `0` or not.
    evicted_up_to: u64,
    /// Keyed by `(scope_id, key)`. Entries are removed as their message
    /// scrolls out of the ring (bounded by the same retention window).
    idempotency: HashMap<(String, String), IdempotencyRecord>,

    // ── Main inbox (Stage 3 chunk A) ──────────────────────────────────────
    // Deliberately kept inside this SAME `Inner`/gate rather than a second
    // `Mutex`: two independent consumers (the presentation layer vs. the
    // single main `AgentLoop`) need distinct QUEUES, not distinct LOCKS — a
    // second mutex here would only introduce a lock-order hazard for no
    // benefit, since every mutation of either queue is already one short
    // critical section under the one gate.
    main_queue: VecDeque<MainMessage>,
    next_main_seq: u64,
    /// Highest `MainMessage::seq` ever handed to the single consumer by
    /// [`MessageBus::take_main_for_delivery`] (`0` until the first drain).
    ///
    /// Deliberately distinct from `next_main_seq - 1` (the *acceptance*
    /// high-water mark): acceptance says a request was queued, this says it
    /// actually left the queue. A caller that needs to know whether a run
    /// made real delivery progress cannot use the acceptance seq for it —
    /// that number does not move on a drain.
    main_delivered_seq: u64,
    /// Idempotency records for main-queue items still pending delivery.
    main_pending_idempotency: HashMap<(String, String), MainIdempotencyRecord>,
    /// Bounded "recently delivered" idempotency window (see
    /// [`MAIN_DELIVERED_HORIZON`]). `main_delivered_order` tracks insertion
    /// order so the oldest entry can be evicted once the window is full;
    /// `main_delivered_idempotency` is the O(1) lookup table kept in sync
    /// with it.
    main_delivered_idempotency: HashMap<(String, String), MainIdempotencyRecord>,
    main_delivered_order: VecDeque<(String, String)>,
}

/// Thread-safe, bounded-ring, engine-owned user-notification bus, plus the
/// trusted main conversation's own bounded FIFO inbox (Stage 3 chunk A).
pub struct MessageBus {
    gate: Mutex<Inner>,
    observer: Option<std::sync::Arc<dyn MessageBusObserver>>,
}

impl MessageBus {
    pub fn new() -> Self {
        Self::with_capacity_and_observer(DEFAULT_RING_CAPACITY, None)
    }

    pub fn with_observer(observer: Option<std::sync::Arc<dyn MessageBusObserver>>) -> Self {
        Self::with_capacity_and_observer(DEFAULT_RING_CAPACITY, observer)
    }

    pub fn with_capacity_and_observer(
        capacity: usize,
        observer: Option<std::sync::Arc<dyn MessageBusObserver>>,
    ) -> Self {
        assert!(capacity > 0, "MessageBus capacity must be positive");
        Self {
            gate: Mutex::new(Inner {
                ring: VecDeque::with_capacity(capacity),
                next_cursor: 1,
                capacity,
                closed: false,
                evicted_up_to: 0,
                idempotency: HashMap::new(),
                main_queue: VecDeque::with_capacity(MAIN_QUEUE_CAPACITY),
                next_main_seq: 1,
                main_delivered_seq: 0,
                main_pending_idempotency: HashMap::new(),
                main_delivered_idempotency: HashMap::new(),
                main_delivered_order: VecDeque::new(),
            }),
            observer,
        }
    }

    /// The current cursor — the position of the most recently published
    /// message, or `0` if nothing has been published yet.
    pub fn cursor(&self) -> u64 {
        self.gate.lock().unwrap().next_cursor - 1
    }

    pub fn is_closed(&self) -> bool {
        self.gate.lock().unwrap().closed
    }

    /// Publish a notification from `source`. `label` is derived from the
    /// source's own identity (never from `body`/`context`), bounded to
    /// [`MAX_LABEL_CHARS`].
    pub fn publish_user(
        &self,
        source: &MessageSource,
        body: impl Into<String>,
        context: Option<String>,
        idempotency_key: Option<String>,
    ) -> Result<PublishReceipt, PublishError> {
        let body = body.into();
        if body.trim().is_empty() {
            return Err(PublishError::EmptyBody);
        }
        if body.chars().count() > MAX_BODY_CHARS {
            return Err(PublishError::BodyTooLong);
        }
        if let Some(ctx) = &context {
            if ctx.chars().count() > MAX_CONTEXT_CHARS {
                return Err(PublishError::ContextTooLong);
            }
        }

        let mut inner = self.gate.lock().unwrap();
        if inner.closed {
            return Err(PublishError::Closed);
        }

        if let Some(key) = &idempotency_key {
            if key.chars().count() > MAX_IDEMPOTENCY_KEY_CHARS {
                return Err(PublishError::IdempotencyKeyTooLong);
            }
        }

        let scope_id = source.scope_id();
        if let Some(key) = &idempotency_key {
            let map_key = (scope_id.clone(), key.clone());
            if let Some(existing) = inner.idempotency.get(&map_key) {
                // Exact content compare — same key with a different context
                // (even if the body matches) is a conflict, never a replay.
                if existing.body == body && existing.context == context {
                    // Identical replay: return the original receipt untouched.
                    let cursor = existing.cursor;
                    let id = inner
                        .ring
                        .iter()
                        .find(|m| m.cursor == cursor)
                        .map(|m| m.id.clone())
                        .expect(
                            "an idempotency record must never outlive its message: both are \
                             removed together on eviction (see the eviction loop below), so a \
                             tracked record whose message is missing is a bus invariant \
                             violation, not a reachable runtime state",
                        );
                    return Ok(PublishReceipt { id, cursor, deduplicated: true });
                }
                return Err(PublishError::IdempotencyConflict);
            }
        }

        let id = uuid::Uuid::new_v4().to_string().replace('-', "");
        let cursor = inner.next_cursor;
        inner.next_cursor += 1;

        let msg = UserMessage {
            id: id.clone(),
            cursor,
            label: source.display_label(),
            body: body.clone(),
            context: context.clone(),
            source_kind: source.kind(),
            task_id: source.trusted_task_id().map(str::to_owned),
            schedule_definition_id: source.schedule_definition_id().map(str::to_owned),
            idempotency_key: idempotency_key.clone(),
            scope_id: scope_id.clone(),
        };

        if let Some(key) = idempotency_key {
            inner
                .idempotency
                .insert((scope_id, key), IdempotencyRecord { cursor, body, context });
        }

        inner.ring.push_back(msg.clone());
        let mut overflow_range: Option<(u64, u64)> = None;
        while inner.ring.len() > inner.capacity {
            if let Some(evicted) = inner.ring.pop_front() {
                overflow_range = Some(match overflow_range {
                    None => (evicted.cursor, evicted.cursor),
                    Some((from, _)) => (from, evicted.cursor),
                });
                inner.evicted_up_to = inner.evicted_up_to.max(evicted.cursor);
                // Drop the idempotency record alongside the evicted message
                // (bounded by the same retention window — see module docs).
                if let Some(key) = &evicted.idempotency_key {
                    inner.idempotency.remove(&(evicted.scope_id.clone(), key.clone()));
                }
            }
        }

        if let Some(obs) = &self.observer {
            obs.on_published(&msg);
            if let Some((from, to)) = overflow_range {
                obs.on_overflow(from, to);
            }
        }

        Ok(PublishReceipt { id, cursor, deduplicated: false })
    }

    /// Non-destructive read of every message after `after_cursor`, optionally
    /// bounded to `limit` entries (pagination).
    ///
    /// `limit: Some(0)` returns an empty page **without** advancing
    /// `next_cursor` past `after_cursor` — a caller must not mistake "asked
    /// for zero" for "read everything up to the latest". Rejecting `0`
    /// outright is the RPC layer's job (`coda_serve`'s `NormalizeQuery`); this
    /// method stays a total function so an internal caller can never trigger
    /// the historical bug where an empty truncation silently jumped the
    /// cursor to the latest message and skipped everything in between.
    pub fn user_since(&self, after_cursor: u64, limit: Option<usize>) -> SinceResult {
        let inner = self.gate.lock().unwrap();
        let gap = after_cursor < inner.evicted_up_to;
        let dropped = gap.then(|| DroppedRange {
            from: after_cursor + 1,
            to: inner.evicted_up_to,
            count: inner.evicted_up_to - after_cursor,
        });

        let mut all: Vec<&UserMessage> =
            inner.ring.iter().filter(|m| m.cursor > after_cursor).collect();
        all.sort_by_key(|m| m.cursor);

        let truncated = match limit {
            Some(n) => all.len() > n,
            None => false,
        };
        if let Some(n) = limit {
            all.truncate(n);
        }

        // Never past what was actually returned: an empty page (whether the
        // caller is caught up, or `limit: Some(0)` truncated a non-empty one)
        // must report `after_cursor` unchanged, not the bus's own latest.
        let next_cursor = all.last().map(|m| m.cursor).unwrap_or(after_cursor);
        let messages = all.into_iter().cloned().collect();

        SinceResult { messages, next_cursor, gap, dropped, truncated }
    }

    /// Close the bus: refuses all subsequent `publish_user` **and**
    /// `publish_main` calls (one shared `closed` flag — see `Inner`). Reads
    /// (`user_since`, `has_pending_main`, `pending_main_len`, `main_seq`)
    /// remain available against whatever was already retained.
    pub fn close(&self) {
        let mut inner = self.gate.lock().unwrap();
        if inner.closed {
            return;
        }
        inner.closed = true;
        drop(inner);
        if let Some(obs) = &self.observer {
            obs.on_closed();
        }
    }

    // ── Main inbox (Stage 3 chunk A, `ask_main`) ──────────────────────────

    /// The main FIFO's own high-water sequence — the seq of the most
    /// recently accepted `ask_main` publication, or `0` if none yet.
    /// Distinct from [`MessageBus::cursor`] (the user ring) and from any
    /// `coda_serve::EventBus` seq (see module docs).
    pub fn main_seq(&self) -> u64 {
        self.gate.lock().unwrap().next_main_seq - 1
    }

    /// `true` when the main FIFO has at least one undelivered item.
    pub fn has_pending_main(&self) -> bool {
        !self.gate.lock().unwrap().main_queue.is_empty()
    }

    /// The exact number of undelivered items in the main FIFO.
    pub fn pending_main_len(&self) -> usize {
        self.gate.lock().unwrap().main_queue.len()
    }

    /// The main FIFO's own **delivery** high-water mark — the seq of the most
    /// recently drained item, or `0` if nothing has ever been drained.
    ///
    /// Distinct from [`MessageBus::main_seq`], which is the *acceptance*
    /// high-water mark and does not move when items are consumed. A caller
    /// deciding whether a completed run actually made progress on this queue
    /// must compare this value across the run: `main_seq` would be unchanged
    /// either way, and `pending_main_len` alone cannot distinguish "the run
    /// delivered the two items that were waiting" from "the run delivered
    /// nothing and two more arrived".
    pub fn main_delivered_seq(&self) -> u64 {
        self.gate.lock().unwrap().main_delivered_seq
    }

    /// Publish an accepted-only request into the trusted main conversation's
    /// inbox (`ask_main`).
    ///
    /// This is an **asynchronous, accepted-only handoff**: the call returns
    /// as soon as the bus takes custody — it never blocks waiting for the
    /// main conversation to read or act on the request, and there is no
    /// reply channel back to the caller (see module docs).
    ///
    /// Idempotency is looked up BEFORE the capacity check, so a retry of an
    /// already-pending (or recently-delivered, see [`MAIN_DELIVERED_HORIZON`])
    /// `(source, key)` still succeeds even when the queue is otherwise full —
    /// only genuinely new content can be refused with
    /// [`AskMainError::QueueFull`].
    pub fn publish_main(
        &self,
        source: &MessageSource,
        body: impl Into<String>,
        context: Option<String>,
        idempotency_key: Option<String>,
    ) -> Result<AskReceipt, AskMainError> {
        let body = body.into();
        if body.trim().is_empty() {
            return Err(AskMainError::EmptyBody);
        }
        if body.chars().count() > MAX_BODY_CHARS {
            return Err(AskMainError::BodyTooLong);
        }
        if let Some(ctx) = &context {
            if ctx.chars().count() > MAX_CONTEXT_CHARS {
                return Err(AskMainError::ContextTooLong);
            }
        }
        if let Some(key) = &idempotency_key {
            if key.chars().count() > MAX_IDEMPOTENCY_KEY_CHARS {
                return Err(AskMainError::IdempotencyKeyTooLong);
            }
        }

        let mut inner = self.gate.lock().unwrap();
        if inner.closed {
            return Err(AskMainError::Closed);
        }

        let scope_id = source.scope_id();

        // ── Idempotency lookup FIRST (pending, then recently-delivered) —
        // a matched key must succeed even at capacity; see doc comment above.
        if let Some(key) = &idempotency_key {
            let map_key = (scope_id.clone(), key.clone());

            if let Some(existing) = inner.main_pending_idempotency.get(&map_key) {
                if existing.body == body && existing.context == context {
                    let seq = existing.seq;
                    let id = existing.id.clone();
                    // Recomputed fresh, never the stale position from when
                    // this item was originally queued.
                    let position =
                        inner.main_queue.iter().position(|m| m.seq == seq).map(|p| p + 1);
                    return Ok(AskReceipt {
                        id,
                        seq,
                        deduplicated: true,
                        queue_position: position,
                        status: AskStatus::Queued,
                    });
                }
                return Err(AskMainError::IdempotencyConflict);
            }

            if let Some(existing) = inner.main_delivered_idempotency.get(&map_key) {
                if existing.body == body && existing.context == context {
                    return Ok(AskReceipt {
                        id: existing.id.clone(),
                        seq: existing.seq,
                        deduplicated: true,
                        queue_position: None,
                        status: AskStatus::Delivered,
                    });
                }
                return Err(AskMainError::IdempotencyConflict);
            }
        }

        // ── Capacity check — only reached for genuinely NEW content; a
        // matched pending/delivered key above already returned above without
        // consuming a slot. Pending items are never evicted to make room.
        if inner.main_queue.len() >= MAIN_QUEUE_CAPACITY {
            return Err(AskMainError::QueueFull);
        }

        let id = uuid::Uuid::new_v4().to_string().replace('-', "");
        let seq = inner.next_main_seq;
        inner.next_main_seq += 1;

        let msg = MainMessage {
            id: id.clone(),
            seq,
            label: source.display_label(),
            source_kind: source.kind(),
            task_id: source.trusted_task_id().map(str::to_owned),
            schedule_definition_id: source.schedule_definition_id().map(str::to_owned),
            body: body.clone(),
            context: context.clone(),
            idempotency_key: idempotency_key.clone(),
            scope_id: scope_id.clone(),
        };

        if let Some(key) = idempotency_key {
            inner.main_pending_idempotency.insert(
                (scope_id, key),
                MainIdempotencyRecord { seq, id: id.clone(), body, context },
            );
        }

        inner.main_queue.push_back(msg.clone());
        let position = inner.main_queue.len();

        if let Some(obs) = &self.observer {
            obs.on_main_accepted(&msg);
        }

        Ok(AskReceipt {
            id,
            seq,
            deduplicated: false,
            queue_position: Some(position),
            status: AskStatus::Queued,
        })
    }

    /// Atomically drain every pending main-inbox item, in FIFO order.
    ///
    /// The single intended consumer is the trusted main `AgentLoop`'s own
    /// iteration boundary (see `crate::agent::AgentLoop` step 4c and
    /// `crate::agent::stop::decide_stop`) — every callsite MUST enforce that
    /// itself (checking `is_main_context`); this method does not refuse a
    /// second caller, exactly like `SteeringInbox::take_all_for_delivery`
    /// does not.
    ///
    /// Each drained item's idempotency record (if it had a key) moves from
    /// the pending table into the bounded recently-delivered window (see
    /// [`MAIN_DELIVERED_HORIZON`]), so an immediate retry with the same key
    /// still deduplicates.
    pub fn take_main_for_delivery(&self) -> Vec<MainMessage> {
        let mut inner = self.gate.lock().unwrap();
        if inner.main_queue.is_empty() {
            return Vec::new();
        }
        let drained: Vec<MainMessage> = inner.main_queue.drain(..).collect();
        if let Some(last) = drained.last() {
            // FIFO order, so the last item carries the highest seq.
            inner.main_delivered_seq = inner.main_delivered_seq.max(last.seq);
        }

        for item in &drained {
            if let Some(key) = &item.idempotency_key {
                let map_key = (item.scope_id.clone(), key.clone());
                if let Some(record) = inner.main_pending_idempotency.remove(&map_key) {
                    inner.main_delivered_order.push_back(map_key.clone());
                    inner.main_delivered_idempotency.insert(map_key, record);
                    while inner.main_delivered_order.len() > MAIN_DELIVERED_HORIZON {
                        if let Some(oldest) = inner.main_delivered_order.pop_front() {
                            inner.main_delivered_idempotency.remove(&oldest);
                        }
                    }
                }
            }
        }

        // Observer runs while `inner` is still held — atomic with the drain,
        // matching `SteeringInbox::take_all_for_delivery`'s discipline: by
        // the time `on_main_delivered` fires the items are already off the
        // queue under this same lock, so no second reader can see them twice
        // or miss them.
        if let Some(obs) = &self.observer {
            obs.on_main_delivered(&drained);
        }

        drained
    }
}

impl Default for MessageBus {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests;
