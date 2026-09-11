//! Engine-owned in-memory user-notification bus (Stage 2).
//!
//! # What this is, and is not
//! [`MessageBus`] lets background work (a scheduled run, a subagent, or a
//! trusted "main" context) publish a short passive notification that the
//! user will see in the chat surface. It is **not** a channel back into the
//! model: nothing here wakes the main agent loop, feeds text into a prompt,
//! or otherwise causes an LLM call. That is explicitly out of scope for this
//! stage (see the crate-level Stage 3 notes for `ask_main`/the main inbox).
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

struct Inner {
    ring: VecDeque<UserMessage>,
    next_cursor: u64,
    capacity: usize,
    closed: bool,
    /// Highest cursor ever evicted from the ring (`0` until the first
    /// eviction happens). Used to report `gap` honestly regardless of
    /// whether the caller's cursor is `0` or not.
    evicted_up_to: u64,
    /// Keyed by `(scope_id, key)`. Entries are removed as their message
    /// scrolls out of the ring (bounded by the same retention window).
    idempotency: HashMap<(String, String), IdempotencyRecord>,
}

/// Thread-safe, bounded-ring, engine-owned user-notification bus.
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

    /// Close the bus: refuses all subsequent `publish_user` calls. Reads via
    /// `user_since` remain available against whatever was already retained.
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
}

impl Default for MessageBus {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests;
