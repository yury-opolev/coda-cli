//! The pending reverse-request registry (§2.7, Stage D).
//!
//! One registry serves **both** ways a server-initiated `request/*` can be
//! answered:
//!
//! - the ordinary JSON-RPC response to the original request, routed by the
//!   transport read loop, and
//! - the out-of-band `session/resolveRequest` / `session/cancelRequest` RPCs,
//!   used by a client that reconnected, or by a second surface that discovered
//!   the request through `session/getPendingRequests`.
//!
//! # Exactly-once, validated before consumption
//!
//! Both paths funnel through [`PendingRegistry::resolve_with`]. The entry's
//! *kind* is inspected and the caller's outcome is validated **while the entry
//! is still in the map**; only a valid outcome removes it. A duplicate reply, a
//! reply of the wrong kind, or a handle from a previous engine process
//! therefore cannot consume an entry — and, critically, cannot grant a second
//! permission for a request that was already denied.
//!
//! # Handles are bound to the engine instance
//!
//! A handle is `req-<engineInstanceId>-<n>`. The numeric part alone would be
//! reusable across engine processes: a client holding `req-7` from a killed
//! engine could silently allow a completely different tool call in the next
//! one. Binding the instance into the handle makes that a typed rejection.
//!
//! # Locking
//!
//! `REQUESTS -> STATE -> BUS`. The map is a `std::sync::Mutex` and is **never**
//! held across an `.await`; the observer callbacks are synchronous and
//! `EngineState` never calls back into this registry (it mirrors the projected
//! list instead), so the order cannot invert.
//!
//! The observer is invoked **while the REQUESTS lock is held**. That is not an
//! accident: computing the projection under the lock and publishing it after
//! releasing it let two concurrent operations deliver their lists in the
//! opposite order to the one they were built in. The state layer assigns
//! whatever list it is handed, so a late `[]` from a resolution could erase a
//! request that was registered after it — and, because the phase machine
//! leaves `awaitingUserInput` when the list empties, restore the turn's phase
//! while the engine really was still waiting on the operator. Holding the lock
//! across the callback makes "the last list published is the newest list" a
//! structural property rather than a scheduling accident.

use std::collections::HashMap;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::Utc;
use coda_proto::state::{PendingRequestDto, PendingRequestKind};
use coda_tool::{AnswerOutcome, NoAnswerReason};
use serde_json::Value;
use tokio::sync::oneshot;

/// Default cap on any free-text field carried in a pending request's
/// `display` (a plan, a tool input preview, a question). These are shown in a
/// UI, not replayed to a model, so a bounded label is enough — and an
/// unbounded one would let a tool input balloon every snapshot.
pub const DEFAULT_DISPLAY_TEXT_CAP: usize = 8 * 1024;

/// The environment variable that opts a production deployment into a reverse
/// request timeout. **Default `0` = off** (§2.7): `fail_all_pending` already
/// covers connection loss and the cancellation token covers interrupt, so an
/// unbounded wait only persists while a healthy client is connected and a
/// human is deciding. A default timeout would cancel slow humans.
pub const REQUEST_TIMEOUT_ENV: &str = "CODA_SERVE_REQUEST_TIMEOUT";

/// Reads [`REQUEST_TIMEOUT_ENV`] as whole seconds. `0`, absent, or unparseable
/// all mean "no timeout" — an unreadable value must never silently become a
/// short one.
pub fn configured_timeout() -> Option<Duration> {
    let raw = std::env::var(REQUEST_TIMEOUT_ENV).ok()?;
    let secs: u64 = raw.trim().parse().ok()?;
    (secs > 0).then(|| Duration::from_secs(secs))
}

// ─────────────────────────────────────────────────────────────────────────────
// Outcomes
// ─────────────────────────────────────────────────────────────────────────────

/// The terminal value handed back to the waiting `issue` call.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RequestOutcome {
    Permission { allow: bool },
    Question(AnswerOutcome),
    PlanApproval { approve: bool },
}

impl RequestOutcome {
    pub fn kind(&self) -> PendingRequestKind {
        match self {
            RequestOutcome::Permission { .. } => PendingRequestKind::Permission,
            RequestOutcome::Question(_) => PendingRequestKind::Question,
            RequestOutcome::PlanApproval { .. } => PendingRequestKind::PlanApproval,
        }
    }

    /// A stable, secret-free label for `event/requestResolved`.
    pub fn label(&self) -> String {
        match self {
            RequestOutcome::Permission { allow: true } => "allowed".into(),
            RequestOutcome::Permission { allow: false } => "denied".into(),
            RequestOutcome::PlanApproval { approve: true } => "approved".into(),
            RequestOutcome::PlanApproval { approve: false } => "rejected".into(),
            RequestOutcome::Question(AnswerOutcome::Answered(_)) => "answered".into(),
            RequestOutcome::Question(AnswerOutcome::NoAnswer(r)) => {
                format!("noAnswer.{}", r.as_str())
            }
        }
    }

    /// The fail-closed outcome for a kind: deny, no answer, reject.
    pub fn fail_closed(kind: PendingRequestKind, reason: NoAnswerReason) -> Self {
        match kind {
            PendingRequestKind::Permission => RequestOutcome::Permission { allow: false },
            PendingRequestKind::PlanApproval => RequestOutcome::PlanApproval { approve: false },
            PendingRequestKind::Question => {
                RequestOutcome::Question(AnswerOutcome::NoAnswer(reason))
            }
        }
    }
}

/// Why `resolve`/`cancel` refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ResolveError {
    /// The handle was never minted by this engine instance.
    InstanceMismatch { expected: String },
    /// Not a `req-<instance>-<n>` handle at all.
    MalformedHandle,
    /// No such pending request — either it never existed, or it was already
    /// resolved (by the raw response, a timeout, or a previous call).
    Unknown,
    /// The offered outcome does not match the request's kind.
    KindMismatch { expected: PendingRequestKind },
    /// The payload matched the kind but carried no usable value.
    MalformedOutcome { detail: String },
}

// ─────────────────────────────────────────────────────────────────────────────
// Observer
// ─────────────────────────────────────────────────────────────────────────────

/// The seam through which the registry publishes into `EngineState`.
///
/// The registry deliberately does not know about `EngineState`: it hands over
/// a fully-projected list and lets the state layer own the transaction (and
/// therefore the cursor/bus ordering).
pub trait RequestObserver: Send + Sync {
    /// The turn currently running, so a pending request can be attributed.
    fn current_turn_id(&self) -> Option<String>;
    fn request_pending(&self, dto: &PendingRequestDto, all: Vec<PendingRequestDto>);
    fn request_resolved(
        &self,
        request_id: &str,
        kind: PendingRequestKind,
        outcome: &str,
        all: Vec<PendingRequestDto>,
    );
}

// ─────────────────────────────────────────────────────────────────────────────
// Registry
// ─────────────────────────────────────────────────────────────────────────────

struct PendingEntry {
    dto: PendingRequestDto,
    tx: oneshot::Sender<RequestOutcome>,
}

pub struct PendingRegistry {
    engine_instance_id: String,
    inner: Mutex<HashMap<i64, PendingEntry>>,
    observer: Mutex<Option<Arc<dyn RequestObserver>>>,
    next_id: AtomicI64,
}

impl PendingRegistry {
    pub fn new(engine_instance_id: impl Into<String>) -> Self {
        Self {
            engine_instance_id: engine_instance_id.into(),
            inner: Mutex::new(HashMap::new()),
            observer: Mutex::new(None),
            next_id: AtomicI64::new(1),
        }
    }

    pub fn engine_instance_id(&self) -> &str {
        &self.engine_instance_id
    }

    pub fn set_observer(&self, observer: Arc<dyn RequestObserver>) {
        *self.observer.lock().unwrap_or_else(|p| p.into_inner()) = Some(observer);
    }

    fn observer(&self) -> Option<Arc<dyn RequestObserver>> {
        self.observer.lock().unwrap_or_else(|p| p.into_inner()).clone()
    }

    /// The next `(numeric id, public handle)` pair.
    ///
    /// The numeric id keeps its existing meaning as the JSON-RPC request id,
    /// so a legacy client that only ever replies to `request/*` is unaffected.
    pub fn mint_id(&self) -> (i64, String) {
        let n = self.next_id.fetch_add(1, Ordering::Relaxed);
        (n, format!("req-{}-{n}", self.engine_instance_id))
    }

    fn parse_handle(&self, handle: &str) -> Result<i64, ResolveError> {
        let rest = handle.strip_prefix("req-").ok_or(ResolveError::MalformedHandle)?;
        let (instance, n) = rest.rsplit_once('-').ok_or(ResolveError::MalformedHandle)?;
        if instance != self.engine_instance_id {
            return Err(ResolveError::InstanceMismatch {
                expected: self.engine_instance_id.clone(),
            });
        }
        n.parse::<i64>().map_err(|_| ResolveError::MalformedHandle)
    }

    /// Register a request and return the receiver the caller awaits.
    ///
    /// Registration and publication happen before the wire frame is written
    /// (see `prompts.rs`), so a client can never observe a `request/*` for
    /// something `session/getPendingRequests` does not yet know about.
    ///
    /// The map mutation, the projection and the observer callback are one
    /// critical section (see the module docs): a concurrent operation cannot
    /// publish a newer list and then be overwritten by this older one.
    pub fn register(
        &self,
        numeric_id: i64,
        kind: PendingRequestKind,
        display: Value,
        call_id: Option<String>,
    ) -> oneshot::Receiver<RequestOutcome> {
        let observer = self.observer();
        let (tx, rx) = oneshot::channel();
        let mut map = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        // Taken under REQUESTS, so the lock order is REQUESTS -> STATE
        // throughout this call, exactly as the observer callback below.
        let dto = PendingRequestDto {
            request_id: format!("req-{}-{numeric_id}", self.engine_instance_id),
            kind,
            issued_at: Utc::now().to_rfc3339(),
            turn_id: observer.as_ref().and_then(|o| o.current_turn_id()),
            call_id,
            display,
            fail_closed_default: kind.fail_closed_default().to_string(),
        };
        map.insert(numeric_id, PendingEntry { dto: dto.clone(), tx });
        let all = project(&map);
        if let Some(observer) = observer {
            observer.request_pending(&dto, all);
        }
        drop(map);
        rx
    }

    /// Resolve by numeric id (the raw `request/*` response path).
    ///
    /// `build` receives the entry's kind so a malformed or wrong-kind payload
    /// is rejected **before** the entry is consumed.
    pub fn resolve_numeric<F>(&self, numeric_id: i64, build: F) -> Result<RequestOutcome, ResolveError>
    where
        F: FnOnce(PendingRequestKind) -> Result<RequestOutcome, ResolveError>,
    {
        self.resolve_inner(numeric_id, build)
    }

    /// Resolve by public handle (the `session/resolveRequest` path).
    pub fn resolve_with<F>(&self, handle: &str, build: F) -> Result<RequestOutcome, ResolveError>
    where
        F: FnOnce(PendingRequestKind) -> Result<RequestOutcome, ResolveError>,
    {
        let numeric_id = self.parse_handle(handle)?;
        self.resolve_inner(numeric_id, build)
    }

    fn resolve_inner<F>(&self, numeric_id: i64, build: F) -> Result<RequestOutcome, ResolveError>
    where
        F: FnOnce(PendingRequestKind) -> Result<RequestOutcome, ResolveError>,
    {
        let mut map = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        // Peek the kind and validate BEFORE removing: a rejected payload
        // must leave the request outstanding, not silently drop it, and a
        // duplicate reply must not be able to consume a second entry.
        let kind = map.get(&numeric_id).ok_or(ResolveError::Unknown)?.dto.kind;
        let outcome = build(kind)?;
        if outcome.kind() != kind {
            return Err(ResolveError::KindMismatch { expected: kind });
        }
        let entry = map.remove(&numeric_id).ok_or(ResolveError::Unknown)?;
        let all = project(&map);
        let label = outcome.label();
        // The receiver may already be gone if the waiting future was dropped
        // between our map removal and here; the request genuinely is no longer
        // pending either way, so the terminal outcome is still published.
        let _ = entry.tx.send(outcome.clone());
        // Published while REQUESTS is still held (module docs): an empty list
        // must never overtake a registration that happened after it.
        if let Some(observer) = self.observer() {
            observer.request_resolved(&entry.dto.request_id, entry.dto.kind, &label, all);
        }
        drop(map);
        Ok(outcome)
    }

    /// Withdraw an entry the waiting future is abandoning (cancellation,
    /// timeout, a failed write) **and** publish its terminal outcome, as one
    /// critical section.
    ///
    /// Returns the outcome when this call was the one that removed the entry;
    /// `None` when someone else already resolved it, which is exactly the
    /// "exactly once" guarantee. Removal and announcement are deliberately
    /// not separable: splitting them reopened the out-of-order window this
    /// registry exists to close.
    pub(crate) fn withdraw_and_publish(
        &self,
        numeric_id: i64,
        outcome_for: impl FnOnce(PendingRequestKind) -> RequestOutcome,
    ) -> Option<RequestOutcome> {
        let mut map = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        let entry = map.remove(&numeric_id)?;
        let all = project(&map);
        let outcome = outcome_for(entry.dto.kind);
        let label = outcome.label();
        if let Some(observer) = self.observer() {
            observer.request_resolved(&entry.dto.request_id, entry.dto.kind, &label, all);
        }
        drop(map);
        Some(outcome)
    }

    /// Fail every outstanding request closed. Used on connection loss (EOF)
    /// and at shutdown.
    ///
    /// The drain and every announcement happen under one REQUESTS lock, so a
    /// request registered concurrently either lands in the drained set or is
    /// registered strictly afterwards — never hidden by a late empty list.
    pub fn fail_all(&self, reason: NoAnswerReason) {
        let mut map = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        let drained: Vec<(PendingRequestDto, oneshot::Sender<RequestOutcome>)> =
            map.drain().map(|(_, e)| (e.dto, e.tx)).collect();
        let observer = self.observer();
        for (dto, tx) in drained {
            let outcome = RequestOutcome::fail_closed(dto.kind, reason);
            let label = outcome.label();
            let _ = tx.send(outcome);
            if let Some(observer) = &observer {
                observer.request_resolved(&dto.request_id, dto.kind, &label, Vec::new());
            }
        }
        drop(map);
    }

    /// The current pending list, newest last.
    pub fn list(&self) -> Vec<PendingRequestDto> {
        project(&self.inner.lock().unwrap_or_else(|p| p.into_inner()))
    }

    pub fn is_empty(&self) -> bool {
        self.inner.lock().unwrap_or_else(|p| p.into_inner()).is_empty()
    }
}

fn project(map: &HashMap<i64, PendingEntry>) -> Vec<PendingRequestDto> {
    let mut entries: Vec<(&i64, &PendingEntry)> = map.iter().collect();
    entries.sort_by_key(|(id, _)| **id);
    entries.into_iter().map(|(_, e)| e.dto.clone()).collect()
}

/// Caps a free-text display field on a UTF-8 boundary, marking the omission
/// explicitly. Never silently shortened.
pub fn display_text(text: &str, cap: usize) -> Value {
    let (capped, full_len) = coda_proto::history::truncate_text(text, cap);
    match full_len {
        None => Value::String(capped),
        Some(len) => serde_json::json!({
            "text": capped,
            "omittedReason": "tooLarge",
            "fullLength": len,
        }),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct RecordingObserver {
        turn_id: Option<String>,
        pending: Mutex<Vec<String>>,
        resolved: Mutex<Vec<(String, String)>>,
        latest_list: Mutex<Vec<PendingRequestDto>>,
    }

    impl RequestObserver for RecordingObserver {
        fn current_turn_id(&self) -> Option<String> {
            self.turn_id.clone()
        }
        fn request_pending(&self, dto: &PendingRequestDto, all: Vec<PendingRequestDto>) {
            self.pending.lock().unwrap().push(dto.request_id.clone());
            *self.latest_list.lock().unwrap() = all;
        }
        fn request_resolved(
            &self,
            request_id: &str,
            _kind: PendingRequestKind,
            outcome: &str,
            all: Vec<PendingRequestDto>,
        ) {
            self.resolved.lock().unwrap().push((request_id.to_string(), outcome.to_string()));
            *self.latest_list.lock().unwrap() = all;
        }
    }

    fn registry_with_observer() -> (Arc<PendingRegistry>, Arc<RecordingObserver>) {
        let registry = Arc::new(PendingRegistry::new("engine-a"));
        let observer = Arc::new(RecordingObserver { turn_id: Some("turn-1".into()), ..Default::default() });
        registry.set_observer(Arc::clone(&observer) as Arc<dyn RequestObserver>);
        (registry, observer)
    }

    fn register_permission(registry: &PendingRegistry) -> (String, oneshot::Receiver<RequestOutcome>) {
        let (numeric, handle) = registry.mint_id();
        let rx = registry.register(
            numeric,
            PendingRequestKind::Permission,
            serde_json::json!({ "toolName": "run_command" }),
            None,
        );
        (handle, rx)
    }

    #[test]
    fn a_registered_request_is_discoverable_and_attributed_to_the_running_turn() {
        let (registry, observer) = registry_with_observer();
        let (handle, _rx) = register_permission(&registry);

        let list = registry.list();
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].request_id, handle);
        assert_eq!(list[0].turn_id.as_deref(), Some("turn-1"));
        assert_eq!(list[0].fail_closed_default, "deny");
        assert_eq!(observer.pending.lock().unwrap().as_slice(), &[handle]);
    }

    #[tokio::test]
    async fn a_second_resolution_of_the_same_handle_is_refused_and_cannot_grant_twice() {
        let (registry, _observer) = registry_with_observer();
        let (handle, rx) = register_permission(&registry);

        registry
            .resolve_with(&handle, |_| Ok(RequestOutcome::Permission { allow: false }))
            .expect("first resolution wins");
        assert_eq!(rx.await.unwrap(), RequestOutcome::Permission { allow: false });

        // A duplicate reply — arriving late, or replayed by a confused client
        // — must not be able to turn a denial into an allow.
        let second = registry.resolve_with(&handle, |_| Ok(RequestOutcome::Permission { allow: true }));
        assert_eq!(second, Err(ResolveError::Unknown));
        assert!(registry.is_empty());
    }

    #[test]
    fn a_wrong_kind_outcome_is_refused_before_the_entry_is_consumed() {
        let (registry, _observer) = registry_with_observer();
        let (handle, _rx) = register_permission(&registry);

        // The caller offers a question answer for a permission request.
        let err = registry
            .resolve_with(&handle, |_| {
                Ok(RequestOutcome::Question(AnswerOutcome::Answered("yes".into())))
            })
            .expect_err("kind mismatch must be refused");
        assert_eq!(err, ResolveError::KindMismatch { expected: PendingRequestKind::Permission });
        assert_eq!(registry.list().len(), 1, "the request must still be outstanding");
    }

    #[test]
    fn a_validation_failure_leaves_the_request_outstanding() {
        let (registry, _observer) = registry_with_observer();
        let (handle, _rx) = register_permission(&registry);

        let err = registry
            .resolve_with(&handle, |_| {
                Err(ResolveError::MalformedOutcome { detail: "missing allow".into() })
            })
            .expect_err("malformed payload must be refused");
        assert!(matches!(err, ResolveError::MalformedOutcome { .. }));
        assert_eq!(registry.list().len(), 1, "a malformed reply must not consume the request");
    }

    #[test]
    fn a_handle_from_another_engine_instance_is_rejected_rather_than_reapplied() {
        let (registry, _observer) = registry_with_observer();
        let (_handle, _rx) = register_permission(&registry);

        // Same numeric id, different engine instance — exactly what a client
        // holding a handle across an engine restart would present.
        let stale = "req-engine-b-1";
        let err = registry
            .resolve_with(stale, |_| Ok(RequestOutcome::Permission { allow: true }))
            .expect_err("a stale-instance handle must never resolve anything");
        assert_eq!(err, ResolveError::InstanceMismatch { expected: "engine-a".into() });
        assert_eq!(registry.list().len(), 1);
    }

    #[test]
    fn a_malformed_handle_is_rejected() {
        let (registry, _observer) = registry_with_observer();
        for bad in ["", "1", "req-", "notreq-engine-a-1", "req-engine-a-notanumber"] {
            assert!(
                registry.resolve_with(bad, |_| Ok(RequestOutcome::Permission { allow: true })).is_err(),
                "{bad} must not resolve"
            );
        }
    }

    #[tokio::test]
    async fn failing_all_pending_denies_permissions_and_answers_no_question() {
        let (registry, observer) = registry_with_observer();
        let (_p_handle, permission_rx) = register_permission(&registry);
        let (q_numeric, _q_handle) = registry.mint_id();
        let question_rx = registry.register(
            q_numeric,
            PendingRequestKind::Question,
            serde_json::json!({ "question": "Delete?", "options": ["Delete", "Keep"] }),
            None,
        );

        registry.fail_all(NoAnswerReason::Disconnected);

        assert_eq!(permission_rx.await.unwrap(), RequestOutcome::Permission { allow: false });
        assert_eq!(
            question_rx.await.unwrap(),
            RequestOutcome::Question(AnswerOutcome::NoAnswer(NoAnswerReason::Disconnected)),
            "SECURITY: a lost connection is never the first option"
        );
        assert!(registry.is_empty());
        let resolved = observer.resolved.lock().unwrap().clone();
        assert!(resolved.iter().any(|(_, o)| o == "denied"));
        assert!(resolved.iter().any(|(_, o)| o == "noAnswer.disconnected"));
    }

    #[test]
    fn withdrawing_twice_only_succeeds_once() {
        let (registry, _observer) = registry_with_observer();
        let (numeric, _handle) = registry.mint_id();
        let _rx = registry.register(numeric, PendingRequestKind::PlanApproval, serde_json::json!({}), None);
        assert!(
            registry
                .withdraw_and_publish(numeric, |k| RequestOutcome::fail_closed(
                    k,
                    NoAnswerReason::Cancelled
                ))
                .is_some()
        );
        assert!(
            registry
                .withdraw_and_publish(numeric, |k| RequestOutcome::fail_closed(
                    k,
                    NoAnswerReason::Cancelled
                ))
                .is_none(),
            "exactly-once withdrawal"
        );
    }

    #[test]
    fn an_oversized_display_field_is_capped_with_an_explicit_marker() {
        let long = "x".repeat(DEFAULT_DISPLAY_TEXT_CAP + 500);
        let v = display_text(&long, DEFAULT_DISPLAY_TEXT_CAP);
        assert_eq!(v["omittedReason"], "tooLarge");
        assert_eq!(v["fullLength"], (DEFAULT_DISPLAY_TEXT_CAP + 500) as i64);
        assert_eq!(v["text"].as_str().unwrap().len(), DEFAULT_DISPLAY_TEXT_CAP);
    }

    #[test]
    fn a_short_display_field_stays_a_plain_string() {
        assert_eq!(display_text("hi", DEFAULT_DISPLAY_TEXT_CAP), Value::String("hi".into()));
    }

    // ── F4 (review): projections are published in the order they are built

    /// An observer that stalls on demand and records the order in which
    /// projections actually arrive.
    #[derive(Default)]
    struct StallingObserver {
        entered: Mutex<Option<std::sync::mpsc::Sender<()>>>,
        armed: std::sync::atomic::AtomicBool,
        /// Every projected list handed over, in arrival order.
        seen: Mutex<Vec<Vec<String>>>,
    }

    impl StallingObserver {
        /// The *next* callback (whichever it is) stalls for 250 ms.
        fn arm(&self, tx: std::sync::mpsc::Sender<()>) {
            *self.entered.lock().unwrap() = Some(tx);
            self.armed.store(true, Ordering::Relaxed);
        }
        fn stall_if_armed(&self) {
            if !self.armed.swap(false, Ordering::Relaxed) {
                return;
            }
            if let Some(tx) = self.entered.lock().unwrap().take() {
                let _ = tx.send(());
            }
            std::thread::sleep(Duration::from_millis(250));
        }
        fn record(&self, all: Vec<PendingRequestDto>) {
            self.seen.lock().unwrap().push(all.into_iter().map(|d| d.request_id).collect());
        }
        fn latest(&self) -> Vec<String> {
            self.seen.lock().unwrap().last().cloned().unwrap_or_default()
        }
    }

    impl RequestObserver for StallingObserver {
        fn current_turn_id(&self) -> Option<String> {
            Some("turn-1".into())
        }
        fn request_pending(&self, _dto: &PendingRequestDto, all: Vec<PendingRequestDto>) {
            self.stall_if_armed();
            self.record(all);
        }
        fn request_resolved(
            &self,
            _request_id: &str,
            _kind: PendingRequestKind,
            _outcome: &str,
            all: Vec<PendingRequestDto>,
        ) {
            self.stall_if_armed();
            self.record(all);
        }
    }

    fn stalling_registry() -> (Arc<PendingRegistry>, Arc<StallingObserver>) {
        let observer = Arc::new(StallingObserver::default());
        let registry = Arc::new(PendingRegistry::new("engine-a"));
        registry.set_observer(Arc::clone(&observer) as Arc<dyn RequestObserver>);
        (registry, observer)
    }

    /// The projected list was computed under the REQUESTS lock and published
    /// **after** releasing it. Two concurrent operations could therefore
    /// deliver their projections in the opposite order to the one they were
    /// built in — and the state layer, which simply assigns whatever list it
    /// is handed, would end up advertising a stale one. Here that erases a
    /// genuinely outstanding request.
    #[test]
    fn a_later_registration_is_never_erased_by_an_earlier_slow_projection() {
        let (registry, observer) = stalling_registry();
        let (tx, entered) = std::sync::mpsc::channel();
        observer.arm(tx);

        let first = {
            let registry = Arc::clone(&registry);
            std::thread::spawn(move || register_permission(&registry))
        };
        entered.recv_timeout(Duration::from_secs(5)).expect("the first observer call must start");

        let second = {
            let registry = Arc::clone(&registry);
            std::thread::spawn(move || register_permission(&registry))
        };
        let _keep_a = first.join().unwrap();
        let _keep_b = second.join().unwrap();

        assert_eq!(
            observer.latest().len(),
            2,
            "the last projection published must be the newest one; got {:?} while the registry \
             itself holds {:?}",
            observer.seen.lock().unwrap(),
            registry.list().iter().map(|d| &d.request_id).collect::<Vec<_>>()
        );
        assert_eq!(
            observer.latest(),
            registry.list().into_iter().map(|d| d.request_id).collect::<Vec<_>>(),
            "the published list must agree with the registry"
        );
    }

    /// The dangerous direction: a slow *resolution* projection (`[]`)
    /// overtaking a newer registration makes the state layer believe nothing
    /// is outstanding, which restores the turn phase out of
    /// `awaitingUserInput` while a request is still waiting on the operator.
    #[test]
    fn a_slow_resolution_projection_never_hides_a_request_registered_after_it() {
        let (registry, observer) = stalling_registry();
        let (handle, _rx) = register_permission(&registry);

        let (tx, entered) = std::sync::mpsc::channel();
        observer.arm(tx);
        let resolver = {
            let registry = Arc::clone(&registry);
            let handle = handle.clone();
            std::thread::spawn(move || {
                registry.resolve_with(&handle, |_| Ok(RequestOutcome::Permission { allow: false }))
            })
        };
        entered.recv_timeout(Duration::from_secs(5)).expect("the resolution callback must start");

        let registrar = {
            let registry = Arc::clone(&registry);
            std::thread::spawn(move || register_permission(&registry))
        };
        resolver.join().unwrap().expect("resolution succeeds");
        let _keep = registrar.join().unwrap();

        assert_eq!(
            observer.latest(),
            registry.list().into_iter().map(|d| d.request_id).collect::<Vec<_>>(),
            "SECURITY-adjacent: an empty list published late makes the engine believe it is no \
             longer waiting on the operator; seen order was {:?}",
            observer.seen.lock().unwrap()
        );
        assert_eq!(observer.latest().len(), 1, "one request really is still outstanding");
    }

    /// Under real concurrency the published list must converge on the
    /// registry's own contents — never on an older projection.
    #[test]
    fn concurrent_registrations_and_resolutions_converge_on_the_registry() {
        let (registry, observer) = registry_with_observer();
        let mut handles = Vec::new();
        for _ in 0..8 {
            let registry = Arc::clone(&registry);
            handles.push(std::thread::spawn(move || {
                let mut keep = Vec::new();
                for _ in 0..40 {
                    let (handle, rx) = register_permission(&registry);
                    keep.push(rx);
                    let _ = registry
                        .resolve_with(&handle, |_| Ok(RequestOutcome::Permission { allow: false }));
                    let (h2, rx2) = register_permission(&registry);
                    keep.push(rx2);
                    let _ = registry.withdraw_and_publish(parse_numeric(&h2), |k| {
                        RequestOutcome::fail_closed(k, NoAnswerReason::Cancelled)
                    });
                }
                keep
            }));
        }
        let _keep: Vec<_> = handles.into_iter().map(|h| h.join().unwrap()).collect();

        let registry_ids: Vec<String> =
            registry.list().into_iter().map(|d| d.request_id).collect();
        let published: Vec<String> =
            observer.latest_list.lock().unwrap().iter().map(|d| d.request_id.clone()).collect();
        assert_eq!(
            published, registry_ids,
            "the last published projection must describe the registry as it now is"
        );
    }

    fn parse_numeric(handle: &str) -> i64 {
        handle.rsplit_once('-').unwrap().1.parse().unwrap()
    }

    /// Exactly-once holds across every path, under concurrency: each
    /// registered request produces exactly one terminal outcome.
    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn every_request_reaches_exactly_one_terminal_outcome_under_contention() {
        let (registry, observer) = registry_with_observer();
        let mut receivers = Vec::new();
        let mut handles = Vec::new();
        for _ in 0..50 {
            let (handle, rx) = register_permission(&registry);
            handles.push(handle);
            receivers.push(rx);
        }

        // Three racing resolvers per request, plus a cancel.
        let mut workers = Vec::new();
        for handle in &handles {
            for allow in [true, false, true] {
                let registry = Arc::clone(&registry);
                let handle = handle.clone();
                workers.push(std::thread::spawn(move || {
                    registry.resolve_with(&handle, move |_| Ok(RequestOutcome::Permission { allow }))
                }));
            }
        }
        let wins = workers
            .into_iter()
            .filter(|_| true)
            .map(|w| w.join().unwrap())
            .filter(Result::is_ok)
            .count();
        assert_eq!(wins, handles.len(), "exactly one resolution per request may succeed");
        assert!(registry.is_empty());
        assert_eq!(observer.resolved.lock().unwrap().len(), handles.len());
        assert!(observer.latest_list.lock().unwrap().is_empty());
        for rx in receivers {
            rx.await.expect("every waiter is answered exactly once");
        }
    }

    #[test]
    fn the_timeout_is_off_unless_explicitly_configured() {
        // Deliberately not touching the process env (parallel tests): the
        // parser is the contract, and `0`/garbage must both mean "off".
        for raw in ["0", "", "off", "-1", "not-a-number"] {
            let parsed: Option<Duration> = raw
                .trim()
                .parse::<u64>()
                .ok()
                .and_then(|s| (s > 0).then(|| Duration::from_secs(s)));
            assert!(parsed.is_none(), "{raw:?} must mean no timeout");
        }
        assert_eq!(
            "30".parse::<u64>().ok().and_then(|s| (s > 0).then(|| Duration::from_secs(s))),
            Some(Duration::from_secs(30))
        );
    }

    #[test]
    fn every_kind_has_a_fail_closed_default_that_never_grants() {
        assert_eq!(PendingRequestKind::Permission.fail_closed_default(), "deny");
        assert_eq!(PendingRequestKind::Question.fail_closed_default(), "noAnswer");
        assert_eq!(PendingRequestKind::PlanApproval.fail_closed_default(), "reject");
        for kind in [
            PendingRequestKind::Permission,
            PendingRequestKind::Question,
            PendingRequestKind::PlanApproval,
        ] {
            let outcome = RequestOutcome::fail_closed(kind, NoAnswerReason::Timeout);
            assert!(
                !matches!(
                    outcome,
                    RequestOutcome::Permission { allow: true } | RequestOutcome::PlanApproval { approve: true }
                ),
                "{kind:?}: a fail-closed default must never grant"
            );
            assert!(
                !matches!(outcome, RequestOutcome::Question(AnswerOutcome::Answered(_))),
                "{kind:?}: a fail-closed default must never fabricate an answer"
            );
        }
    }
}
