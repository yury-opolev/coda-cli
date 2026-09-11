//! Schedule runtime: watches [`ScheduledTaskStore`] and fires due definitions.
//!
//! Matches C# `Coda.Sdk/Scheduling/ScheduleRuntime.cs`.
//!
//! # Design
//! A single background tokio task is the only writer of runtime state.  It:
//! 1. Reconciles the store (adds new definitions, drops deleted-idle entries).
//! 2. Processes terminal callbacks received via an unbounded channel.
//! 3. Evaluates which definitions are due and launches agents.
//! 4. Waits for the earliest of: next due time (≤ 1 min), store change,
//!    terminal command, or cancellation.
//!
//! # Lifecycle events
//! The runtime emits [`ScheduleLifecycleEvent`] for each `Started`,
//! `Completed`, `Failed`, and `Stopped` transition.  Events are delivered
//! through an [`Arc<dyn ScheduleLifecycleSink>`].
//!
//! # Catch-up / overlap policy
//! - **Interval / Cron (recurring)**: `next_run_utc` is advanced to the next
//!   *future* boundary **before** launching — missed ticks are coalesced.
//! - **At (one-shot)**: record is kept until the single execution reaches
//!   terminal state, then removed (at-least-once semantics on restart).
//! - **Self-overlap**: a definition never runs concurrently with itself.  A
//!   second due tick while running transitions status → Pending.  On terminal,
//!   exactly one replacement is launched; further ticks only advance the
//!   next boundary.

use std::collections::HashMap;
use std::sync::{Arc, RwLock};
use std::time::Duration;

use chrono::{DateTime, Utc};
use tokio::sync::mpsc::{self, UnboundedSender};
use tokio_util::sync::CancellationToken;

use super::limits;
use super::schedule_recurrence::ScheduleRecurrence;
use super::scheduled_task::{
    ScheduleKind, ScheduleLiveState, ScheduleLiveStatus, ScheduleRetirement,
    ScheduleRetirementReason, ScheduleTerminalMetadata, ScheduleTerminalOutcome, ScheduledTask,
};
use super::scheduled_task_store::ScheduledTaskStore;
use crate::tasks::{TaskKind, TaskExecutionMode, TaskManager, TaskSnapshot};
use crate::tool::ScheduleOrigin;

// ─────────────────────────────────────────────────────────────────────────────
// Public types
// ─────────────────────────────────────────────────────────────────────────────

/// Live runtime status of a scheduled definition.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ScheduleRuntimeStatus {
    Idle,
    Running,
    Pending,
}

/// Point-in-time runtime state for one definition.
#[derive(Clone, Debug)]
pub struct ScheduleRuntimeState {
    pub status: ScheduleRuntimeStatus,
    pub active_task_id: Option<String>,
}

/// Immutable per-definition snapshot.
#[derive(Clone, Debug)]
pub struct ScheduleRuntimeSnapshot {
    pub definition_id: String,
    pub status: ScheduleRuntimeStatus,
    pub active_task_id: Option<String>,
}

/// A schedule lifecycle event emitted to the session sink.
#[derive(Clone, Debug)]
pub struct ScheduleLifecycleEvent {
    pub definition_id: String,
    pub definition_name: Option<String>,
    pub task_id: Option<String>,
    /// `"started"`, `"completed"`, `"failed"`, or `"stopped"`.
    pub state: String,
    pub timestamp: DateTime<Utc>,
    pub summary: Option<String>,
}

/// Sink that receives schedule lifecycle events.
pub trait ScheduleLifecycleSink: Send + Sync {
    fn publish(&self, event: ScheduleLifecycleEvent);
}

/// No-op lifecycle sink.
pub struct NullScheduleLifecycleSink;

impl ScheduleLifecycleSink for NullScheduleLifecycleSink {
    fn publish(&self, _: ScheduleLifecycleEvent) {}
}

/// Read-only view of the schedule runtime state.
pub trait ScheduleRuntimeView: Send + Sync {
    fn try_get_state(&self, schedule_id: &str) -> Option<ScheduleRuntimeState>;
    fn get_snapshot(&self) -> Vec<ScheduleRuntimeSnapshot>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Clock seam
// ─────────────────────────────────────────────────────────────────────────────

/// The runtime's source of "now".
///
/// Every scheduling *decision* (is a definition due? what is the next
/// boundary? when did this run reach a terminal state?) reads this seam rather
/// than `Utc::now()` directly, so tests can drive hours of schedule behaviour
/// deterministically without sleeping.
///
/// `TaskManager`'s own bookkeeping timestamps are deliberately *not* routed
/// through this trait: they record when a process actually ran, not when the
/// scheduler decided something.
pub trait ScheduleClock: Send + Sync {
    fn now(&self) -> DateTime<Utc>;
}

/// Wall-clock implementation; the default for every production runtime.
pub struct SystemClock;

impl ScheduleClock for SystemClock {
    fn now(&self) -> DateTime<Utc> {
        Utc::now()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ScheduledAgentRunner — launches one agent run per scheduled task
// ─────────────────────────────────────────────────────────────────────────────

/// One launch request produced by the schedule runtime.
///
/// Carries the definition identity alongside the prompt so the runner can
/// stamp the run's provenance ([`ScheduleOrigin`]). The origin travels with
/// the request through the subagent host into `ToolContext`; a later stage
/// uses it for self-cancellation, which is why it must come from the runtime
/// and never from the prompt text.
#[derive(Debug, Clone)]
pub struct ScheduledRun {
    pub definition_id: String,
    pub definition_name: Option<String>,
    pub prompt: String,
    pub description: String,
}

impl ScheduledRun {
    /// The trusted provenance stamp for this run.
    pub fn origin(&self) -> ScheduleOrigin {
        ScheduleOrigin::new(self.definition_id.clone(), self.definition_name.clone())
    }
}

/// Trait for launching a scheduled agent execution.
/// Implementors register a task with the manager, start the agent, and call
/// `on_terminal` once the run reaches a terminal state.
pub trait ScheduledAgentRunner: Send + Sync {
    /// Launch the agent.  Returns the assigned task id on success or an error
    /// message when registration / launch fails.
    fn start(
        &self,
        run: ScheduledRun,
        on_terminal: Arc<dyn Fn(TaskSnapshot) + Send + Sync>,
    ) -> Result<String, String>;
}

/// Real runner backed by a [`TaskManager`] and a [`SubagentFactory`].
pub struct TaskManagerRunner {
    task_manager: Arc<TaskManager>,
    subagent_factory: Arc<dyn crate::subagents::SubagentFactory>,
}

impl TaskManagerRunner {
    pub fn new(
        task_manager: Arc<TaskManager>,
        subagent_factory: Arc<dyn crate::subagents::SubagentFactory>,
    ) -> Arc<Self> {
        Arc::new(Self { task_manager, subagent_factory })
    }
}

impl ScheduledAgentRunner for TaskManagerRunner {
    fn start(
        &self,
        run: ScheduledRun,
        on_terminal: Arc<dyn Fn(TaskSnapshot) + Send + Sync>,
    ) -> Result<String, String> {
        let task = self.task_manager.register(
            TaskKind::Scheduled,
            &run.description,
            None,
            TaskExecutionMode::Background,
        )?;

        let task_id = task.id.clone();
        let task_cancel = task.cancel.clone();
        let factory = self.subagent_factory.clone();
        let mgr = self.task_manager.clone();
        let tid = task_id.clone();
        let sink = Arc::new(crate::events::NullSink);
        let origin = run.origin();
        let prompt = run.prompt;

        tokio::spawn(async move {
            // The scheduled run's own registered task is the child's caller
            // identity, and the definition id is its trusted origin.
            let request = crate::subagents::SubagentRequest::foreground(
                "general-purpose",
                prompt,
                tid.clone(),
                1,
            )
            .with_schedule_origin(Some(origin));

            let run_cancel = task_cancel.clone();
            match factory.spawn(request, sink, run_cancel).await {
                Ok(report) => {
                    mgr.complete(&tid, Some(report));
                }
                Err(e) => {
                    if task_cancel.is_cancelled() {
                        mgr.stop(&tid);
                    } else {
                        mgr.fail(&tid, Some(e));
                    }
                }
            }

            // Call the terminal callback with the final snapshot.
            if let Some(snap) = mgr.get(&tid) {
                on_terminal(snap);
            }
        });

        Ok(task_id)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Internal state
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum RuntimeStatus { Idle, Running, Pending }

struct Entry {
    definition: ScheduledTask,
    status: RuntimeStatus,
    active_task_id: Option<String>,
    /// Deleted from store while an agent was still running.  No replacement
    /// on terminal; the terminal is processed but the entry is removed.
    deleted: bool,
    /// Recurrence computation threw.  Quarantined until a new store revision
    /// appears for this definition.
    faulted: bool,
    /// A `retired` lifecycle event has already been emitted for this entry, so
    /// reconciliation and terminal processing cannot double-fire it.
    retirement_announced: bool,
}

struct TerminalCommand {
    definition_id: String,
    task_id: String,
    snapshot: TaskSnapshot,
}

const MAX_REEVALUATION: Duration = Duration::from_secs(60);

// ─────────────────────────────────────────────────────────────────────────────
// ScheduleRuntime
// ─────────────────────────────────────────────────────────────────────────────

pub struct ScheduleRuntime {
    _store: Arc<ScheduledTaskStore>,
    runner: Arc<dyn ScheduledAgentRunner>,
    lifecycle_sink: Arc<dyn ScheduleLifecycleSink>,
    commands_tx: UnboundedSender<TerminalCommand>,
    /// Thread-safe, published after every state change.
    view: Arc<RwLock<HashMap<String, ScheduleRuntimeState>>>,
    cancel: CancellationToken,
    /// Handle to the background loop task (set after `start()`).
    loop_handle: tokio::sync::Mutex<Option<tokio::task::JoinHandle<()>>>,
}

impl ScheduleRuntime {
    /// Runtime driven by the wall clock.
    pub fn new(
        store: Arc<ScheduledTaskStore>,
        runner: Arc<dyn ScheduledAgentRunner>,
        lifecycle_sink: Arc<dyn ScheduleLifecycleSink>,
    ) -> Arc<Self> {
        Self::new_with_clock(store, runner, lifecycle_sink, Arc::new(SystemClock))
    }

    /// Runtime driven by an injected clock.
    ///
    /// Tests use this to evaluate due-ness, recurrence advancement and
    /// terminal timestamps at arbitrary points in time without sleeping.  The
    /// loop still wakes on store-version notifications, so a test advances the
    /// clock and then touches the store to force a re-evaluation.
    pub fn new_with_clock(
        store: Arc<ScheduledTaskStore>,
        runner: Arc<dyn ScheduledAgentRunner>,
        lifecycle_sink: Arc<dyn ScheduleLifecycleSink>,
        clock: Arc<dyn ScheduleClock>,
    ) -> Arc<Self> {
        let (tx, rx) = mpsc::unbounded_channel();
        let cancel = CancellationToken::new();
        let view = Arc::new(RwLock::new(HashMap::new()));

        let this = Arc::new(Self {
            _store: store.clone(),
            runner,
            lifecycle_sink,
            commands_tx: tx,
            view: view.clone(),
            cancel: cancel.clone(),
            loop_handle: tokio::sync::Mutex::new(None),
        });

        // Store the receive end in the loop task via a move-closure.
        let loop_store = store;
        let loop_runner = this.runner.clone();
        let loop_sink = this.lifecycle_sink.clone();
        let loop_commands_tx = this.commands_tx.clone();
        let loop_view = view;
        let loop_cancel = cancel;

        let handle = tokio::spawn(run_loop(
            loop_store,
            loop_runner,
            loop_sink,
            rx,
            loop_commands_tx,
            loop_view,
            loop_cancel,
            clock,
        ));

        // Store handle — but we can't block here (async context), so we use
        // a oneshot channel to hand it over.
        let this_clone = this.clone();
        tokio::spawn(async move {
            *this_clone.loop_handle.lock().await = Some(handle);
        });

        this
    }

    /// Stop the runtime and wait for the loop task to exit.
    pub async fn shutdown(&self) {
        self.cancel.cancel();
        let handle = self.loop_handle.lock().await.take();
        if let Some(h) = handle {
            let _ = h.await;
        }
    }
}

impl ScheduleRuntimeView for ScheduleRuntime {
    fn try_get_state(&self, schedule_id: &str) -> Option<ScheduleRuntimeState> {
        self.view.read().unwrap().get(schedule_id).cloned()
    }

    fn get_snapshot(&self) -> Vec<ScheduleRuntimeSnapshot> {
        self.view
            .read()
            .unwrap()
            .iter()
            .map(|(id, s)| ScheduleRuntimeSnapshot {
                definition_id: id.clone(),
                status: s.status,
                active_task_id: s.active_task_id.clone(),
            })
            .collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Event loop (runs in a background tokio task)
// ─────────────────────────────────────────────────────────────────────────────

async fn run_loop(
    store: Arc<ScheduledTaskStore>,
    runner: Arc<dyn ScheduledAgentRunner>,
    sink: Arc<dyn ScheduleLifecycleSink>,
    mut commands_rx: tokio::sync::mpsc::UnboundedReceiver<TerminalCommand>,
    commands_tx: UnboundedSender<TerminalCommand>,
    view: Arc<RwLock<HashMap<String, ScheduleRuntimeState>>>,
    cancel: CancellationToken,
    clock: Arc<dyn ScheduleClock>,
) {
    let mut entries: HashMap<String, Entry> = HashMap::new();

    loop {
        if cancel.is_cancelled() {
            break;
        }

        // 1. Reconcile the store.
        let snapshot = store.get_snapshot();
        reconcile(&mut entries, &snapshot, &view, &store);

        // 2. Process queued terminal callbacks.
        while let Ok(cmd) = commands_rx.try_recv() {
            if cancel.is_cancelled() { break; }
            process_terminal(&mut entries, cmd, &store, &runner, &sink, &commands_tx, &view, &clock).await;
        }

        if cancel.is_cancelled() { break; }

        // 3. Evaluate due definitions.
        evaluate_due(&mut entries, &store, &runner, &sink, &commands_tx, &view, &cancel, &clock).await;

        if cancel.is_cancelled() { break; }

        // 4. Re-reconcile before parking (our own writes may have landed).
        let wait_snapshot = store.get_snapshot();
        reconcile(&mut entries, &wait_snapshot, &view, &store);

        // 5. Wait for next event.
        let now = clock.now();
        let delay = compute_delay(&entries, now);
        let observed_version = wait_snapshot.version;

        tokio::select! {
            _ = tokio::time::sleep(delay) => {}
            _ = store.wait_for_change(observed_version) => {}
            Some(cmd) = commands_rx.recv() => {
                // Re-queue and fall through so next iteration processes it.
                let _ = commands_tx.send(cmd);
            }
            _ = cancel.cancelled() => break,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Reconcile
// ─────────────────────────────────────────────────────────────────────────────

fn reconcile(
    entries: &mut HashMap<String, Entry>,
    snapshot: &crate::scheduling::ScheduledTaskStoreSnapshot,
    view: &Arc<RwLock<HashMap<String, ScheduleRuntimeState>>>,
    store: &Arc<ScheduledTaskStore>,
) {
    let mut seen = std::collections::HashSet::new();
    for definition in &snapshot.items {
        seen.insert(definition.id.clone());
        if let Some(entry) = entries.get_mut(&definition.id) {
            if entry.definition != *definition {
                // A retirement that arrived from outside (a self-cancelling
                // run) must not be treated as "a new revision may fix the
                // recurrence": clearing `faulted` there would put a quarantined
                // definition back into rotation.
                let newly_faultable = !definition.is_retired();
                entry.definition = definition.clone();
                if newly_faultable {
                    entry.faulted = false;
                }
            }
        } else {
            entries.insert(definition.id.clone(), Entry {
                definition: definition.clone(),
                status: RuntimeStatus::Idle,
                active_task_id: None,
                deleted: false,
                // A definition that arrives already retired (self-cancelled
                // between loop iterations) must never launch, so it is
                // announced as retired rather than rediscovered as fresh work.
                faulted: false,
                retirement_announced: definition.is_retired(),
            });
        }
    }

    for id in entries.keys().cloned().collect::<Vec<_>>() {
        if !seen.contains(&id) {
            let entry = entries.get_mut(&id).unwrap();
            if entry.status == RuntimeStatus::Idle {
                entries.remove(&id);
            } else {
                entry.deleted = true;
            }
        }
    }

    publish_view(entries, view, store);
}

// ─────────────────────────────────────────────────────────────────────────────
// EvaluateDue
// ─────────────────────────────────────────────────────────────────────────────

async fn evaluate_due(
    entries: &mut HashMap<String, Entry>,
    store: &Arc<ScheduledTaskStore>,
    runner: &Arc<dyn ScheduledAgentRunner>,
    sink: &Arc<dyn ScheduleLifecycleSink>,
    commands_tx: &UnboundedSender<TerminalCommand>,
    view: &Arc<RwLock<HashMap<String, ScheduleRuntimeState>>>,
    cancel: &CancellationToken,
    clock: &Arc<dyn ScheduleClock>,
) {
    let ids: Vec<String> = entries.keys().cloned().collect();

    for id in ids {
        if cancel.is_cancelled() { return; }
        let now = clock.now();

        let (status, next_run, deleted, faulted) = {
            let e = match entries.get(&id) {
                Some(e) => e,
                None => continue,
            };
            (e.status, e.definition.next_run_utc, e.deleted, e.faulted)
        };

        if deleted || faulted { continue; }

        // Bounds are evaluated on their own schedule, not on the definition's.
        // A deadline that passes while the next boundary is days away must
        // retire the definition *at the deadline*; waiting for the next tick
        // would leave a dead schedule advertising itself as live.
        let admission = {
            let e = entries.get(&id).unwrap();
            limits::admit(&e.definition, now)
        };
        if let Some(reason) = admission.retirement() {
            match status {
                // Nothing is in flight: the definition retires now.
                RuntimeStatus::Idle => {
                    retire(entries, &id, reason, now, None, store, sink, view);
                }
                // Work is still running. The default is that already-running
                // work finishes, so the definition is marked retiring and the
                // record is settled when that run reaches its terminal state.
                RuntimeStatus::Running | RuntimeStatus::Pending => {
                    if let Some(entry) = entries.get_mut(&id) {
                        // A queued replacement is no longer allowed to start.
                        entry.status = RuntimeStatus::Running;
                    }
                    publish_view(entries, view, store);
                }
            }
            continue;
        }

        let due = next_run <= now;
        match status {
            RuntimeStatus::Idle if due => {
                claim_and_launch(entries, &id, now, store, runner, sink, commands_tx, view).await;
            }
            RuntimeStatus::Running | RuntimeStatus::Pending if due => {
                let is_recurring = {
                    let e = entries.get(&id).unwrap();
                    is_recurring(&e.definition)
                };
                if is_recurring {
                    advance_while_active(entries, &id, now, store, view);
                }
            }
            _ => {}
        }
    }

    publish_view(entries, view, store);
}

// ─────────────────────────────────────────────────────────────────────────────
// Retirement
// ─────────────────────────────────────────────────────────────────────────────

/// Record that a definition will never launch again, and say so exactly once.
///
/// The write goes through `store.update` so it cannot clobber — or be clobbered
/// by — a concurrent counter bump or a self-cancellation that landed first. The
/// first recorded reason wins: a definition that cancelled itself is not
/// relabelled "expired" a moment later because its deadline also passed.
fn retire(
    entries: &mut HashMap<String, Entry>,
    id: &str,
    reason: ScheduleRetirementReason,
    now: DateTime<Utc>,
    note: Option<String>,
    store: &Arc<ScheduledTaskStore>,
    sink: &Arc<dyn ScheduleLifecycleSink>,
    view: &Arc<RwLock<HashMap<String, ScheduleRuntimeState>>>,
) {
    let recorded = store.update(id, |definition| {
        if definition.retirement.is_none() {
            definition.retirement = Some(ScheduleRetirement {
                reason,
                retired_at_utc: now,
                note,
            });
            definition.updated_at_utc = now;
        }
        definition.clone()
    });

    let Some(definition) = recorded else {
        // Deleted concurrently: there is nothing to retire and nothing to
        // resurrect.
        entries.remove(id);
        publish_view(entries, view, store);
        return;
    };

    let Some(entry) = entries.get_mut(id) else { return };
    entry.definition = definition.clone();
    entry.status = RuntimeStatus::Idle;
    entry.active_task_id = None;

    if !entry.retirement_announced {
        entry.retirement_announced = true;
        let effective = definition
            .retirement
            .as_ref()
            .map(|r| r.reason)
            .unwrap_or(reason);
        emit(
            sink,
            &definition,
            None,
            "retired",
            now,
            Some(format!("retired: {}", effective.as_wire())),
        );
    }
    publish_view(entries, view, store);
}

// ─────────────────────────────────────────────────────────────────────────────
// ClaimAndLaunch
// ─────────────────────────────────────────────────────────────────────────────

/// Claim the launch slot and start an occurrence.
///
/// The claim is a single atomic store mutation: it re-checks admission and
/// advances the recurrence boundary under the store lock, so a cancellation or
/// a deletion that lands between the due-evaluation and the launch is
/// linearized — either it wins and no run starts, or the claim wins and the
/// cancellation is observed on the next iteration. The runner is called
/// *after* the lock is released; nothing awaits or calls back into a runner
/// while the store is held.
async fn claim_and_launch(
    entries: &mut HashMap<String, Entry>,
    id: &str,
    now: DateTime<Utc>,
    store: &Arc<ScheduledTaskStore>,
    runner: &Arc<dyn ScheduledAgentRunner>,
    sink: &Arc<dyn ScheduleLifecycleSink>,
    commands_tx: &UnboundedSender<TerminalCommand>,
    view: &Arc<RwLock<HashMap<String, ScheduleRuntimeState>>>,
) {
    let definition = entries.get(id).unwrap().definition.clone();

    // Compute the next boundary outside the lock: recurrence evaluation is the
    // one part of this that can fail, and it must not fail with the store held.
    let advanced_next_run = if is_recurring(&definition) {
        match ScheduleRecurrence::advance_recurring_past(&definition, now) {
            Ok(next) => Some(next),
            Err(e) => {
                let entry = entries.get_mut(id).unwrap();
                entry.faulted = true;
                emit(sink, &definition, None, "failed", now, Some(format!("recurrence: {e}")));
                return;
            }
        }
    } else {
        None
    };

    let claim = store.update(id, |current| {
        // Re-check under the lock. The definition may have been cancelled,
        // expired or had its budget spent since the due evaluation.
        if let Some(reason) = limits::admit(current, now).retirement() {
            return Claim::Denied(reason);
        }
        if let Some(next) = advanced_next_run {
            current.next_run_utc = next;
            current.updated_at_utc = now;
        }
        Claim::Granted(current.clone())
    });

    let claimed = match claim {
        None => {
            // Deleted between evaluation and claim: never resurrect it.
            entries.remove(id);
            publish_view(entries, view, store);
            return;
        }
        Some(Claim::Denied(reason)) => {
            retire(entries, id, reason, now, None, store, sink, view);
            return;
        }
        Some(Claim::Granted(definition)) => definition,
    };

    entries.get_mut(id).unwrap().definition = claimed.clone();
    launch(entries, id, &claimed, now, store, runner, sink, commands_tx, view);
}

/// Outcome of an atomic launch claim.
enum Claim {
    Granted(ScheduledTask),
    Denied(ScheduleRetirementReason),
}

#[allow(clippy::too_many_arguments)]
fn launch(
    entries: &mut HashMap<String, Entry>,
    id: &str,
    definition: &ScheduledTask,
    now: DateTime<Utc>,
    store: &Arc<ScheduledTaskStore>,
    runner: &Arc<dyn ScheduledAgentRunner>,
    sink: &Arc<dyn ScheduleLifecycleSink>,
    commands_tx: &UnboundedSender<TerminalCommand>,
    view: &Arc<RwLock<HashMap<String, ScheduleRuntimeState>>>,
) {
    let definition_id = definition.id.clone();
    let def_clone = definition.clone();
    let runner_clone = runner.clone();
    let tx = commands_tx.clone();

    let on_terminal: Arc<dyn Fn(TaskSnapshot) + Send + Sync> = Arc::new(move |snap: TaskSnapshot| {
        let _ = tx.send(TerminalCommand {
            definition_id: definition_id.clone(),
            task_id: snap.id.clone(),
            snapshot: snap,
        });
    });

    let description = format!(
        "Scheduled: {}",
        def_clone.name.as_deref().unwrap_or(&def_clone.prompt)
    );

    let scheduled_run = ScheduledRun {
        definition_id: def_clone.id.clone(),
        definition_name: def_clone.name.clone(),
        prompt: def_clone.prompt.clone(),
        description,
    };

    match runner_clone.start(scheduled_run, on_terminal) {
        Ok(task_id) => {
            // The attempt was accepted: a task exists for it. It counts against
            // `maxRuns` from here on, even if the run later fails in the model,
            // in a tool, or while waiting for a concurrency slot — otherwise an
            // unreliable environment could retry a bounded job forever.
            let committed = store.update(id, |current| {
                current.runs_started = current.runs_started.saturating_add(1);
                current.updated_at_utc = now;
                current.clone()
            });

            let Some(entry) = entries.get_mut(id) else { return };
            if let Some(committed) = committed {
                entry.definition = committed;
            } else {
                // Deleted while the runner was accepting the launch. The run
                // itself is allowed to finish, but there is no definition left
                // to count against or to relaunch.
                entry.deleted = true;
            }
            entry.status = RuntimeStatus::Running;
            entry.active_task_id = Some(task_id.clone());
            let announced = entry.definition.clone();
            emit(sink, &announced, Some(&task_id), "started", now, None);
            publish_view(entries, view, store);
        }
        Err(e) => {
            // Refused outright: no task was registered, so nothing ran and the
            // attempt does not count against the budget.
            emit(sink, definition, None, "failed", now, Some(format!("launch: {e}")));
            if definition.kind == ScheduleKind::At {
                // A one-shot whose launch was refused has no future boundary to
                // advance to. Previously the entry was dropped from the map but
                // left in the store, so the very next reconcile rediscovered it
                // as fresh, overdue work and retried forever. Retire it in BOTH
                // places, with an explicit reason, so the failure is visible and
                // the loop cannot spin.
                retire(
                    entries,
                    id,
                    ScheduleRetirementReason::LaunchFailed,
                    now,
                    None,
                    store,
                    sink,
                    view,
                );
            } else {
                if let Some(entry) = entries.get_mut(id) {
                    entry.faulted = true; // prevent tight loop
                    entry.status = RuntimeStatus::Idle;
                    entry.active_task_id = None;
                }
                publish_view(entries, view, store);
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AdvanceWhileActive
// ─────────────────────────────────────────────────────────────────────────────

fn advance_while_active(
    entries: &mut HashMap<String, Entry>,
    id: &str,
    now: DateTime<Utc>,
    store: &Arc<ScheduledTaskStore>,
    view: &Arc<RwLock<HashMap<String, ScheduleRuntimeState>>>,
) {
    let definition = entries.get(id).unwrap().definition.clone();
    let next = match ScheduleRecurrence::advance_recurring_past(&definition, now) {
        Ok(n) => n,
        Err(e) => {
            let entry = entries.get_mut(id).unwrap();
            entry.faulted = true;
            tracing::warn!(id, error = %e, "schedule recurrence fault while active");
            return;
        }
    };

    // Advancing the boundary must never resurrect a retired definition or roll
    // back a counter, so it is a targeted field update rather than a blind
    // replace of a snapshot taken before the run started.
    let advanced = store.update(id, |current| {
        current.next_run_utc = next;
        current.updated_at_utc = now;
        current.clone()
    });

    let Some(advanced) = advanced else {
        let entry = entries.get_mut(id).unwrap();
        entry.deleted = true;
        return;
    };

    let entry = entries.get_mut(id).unwrap();
    entry.definition = advanced;
    // Running → Pending; Pending stays Pending. A definition that has already
    // exhausted its bounds never queues a replacement: it stays Running and is
    // settled when the in-flight run finishes.
    if limits::admit(&entry.definition, now).is_allowed() {
        entry.status = RuntimeStatus::Pending;
    }
    publish_view(entries, view, store);
}

// ─────────────────────────────────────────────────────────────────────────────
// ProcessTerminal
// ─────────────────────────────────────────────────────────────────────────────

async fn process_terminal(
    entries: &mut HashMap<String, Entry>,
    command: TerminalCommand,
    store: &Arc<ScheduledTaskStore>,
    runner: &Arc<dyn ScheduledAgentRunner>,
    sink: &Arc<dyn ScheduleLifecycleSink>,
    commands_tx: &UnboundedSender<TerminalCommand>,
    view: &Arc<RwLock<HashMap<String, ScheduleRuntimeState>>>,
    clock: &Arc<dyn ScheduleClock>,
) {
    let entry = match entries.get(&command.definition_id) {
        Some(e) => e,
        None => return, // Unknown / already removed
    };

    if entry.active_task_id.as_deref() != Some(&command.task_id) {
        return; // Stale terminal for a different task
    }

    let now = clock.now();
    let deleted = entry.deleted;
    let was_pending = entry.status == RuntimeStatus::Pending;

    let (kind_str, outcome) = map_terminal_status(&command.snapshot.status);
    let summary = command.snapshot.result.clone().or(command.snapshot.error.clone());

    if deleted {
        // Definition was removed while running: emit nothing, clean up.
        entries.remove(&command.definition_id);
        publish_view(entries, view, store);
        return;
    }

    // Self-retirement can land after reconciliation but before the terminal
    // callback. Consult the current store before removing a one-shot.
    let Some(definition) = store.update(&command.definition_id, |current| current.clone()) else {
        entries.remove(&command.definition_id);
        publish_view(entries, view, store);
        return;
    };
    if definition.kind == ScheduleKind::At && !definition.is_retired() {
        // One-shot: emit outcome, remove from store and entries.
        emit(sink, &definition, Some(&command.task_id), kind_str, now, summary.clone());
        store.remove(&definition.id);
        entries.remove(&command.definition_id);
        publish_view(entries, view, store);
        return;
    }

    // Recurring: persist terminal metadata, keep definition. A targeted update
    // rather than a blind replace, so a retirement or counter bump that landed
    // while the run was in flight survives.
    let updated = store.update(&command.definition_id, |current| {
        current.last_terminal_outcome = Some(ScheduleTerminalMetadata {
            outcome,
            completed_at_utc: now,
            summary: summary.clone(),
        });
        current.updated_at_utc = now;
        current.clone()
    });

    let Some(updated) = updated else {
        // Deleted concurrently.
        entries.remove(&command.definition_id);
        publish_view(entries, view, store);
        return;
    };

    let entry = entries.get_mut(&command.definition_id).unwrap();
    entry.definition = updated.clone();
    entry.status = RuntimeStatus::Idle;
    entry.active_task_id = None;
    emit(sink, &updated, Some(&command.task_id), kind_str, now, summary);

    // The run's outcome and the definition's fate are separate questions. The
    // run above may have succeeded while the definition is now out of budget,
    // or failed while the definition still has runs left.
    if let Some(reason) = limits::admit(&updated, now).retirement() {
        retire(entries, &command.definition_id, reason, now, None, store, sink, view);
        return;
    }

    if was_pending {
        // Launch one coalesced replacement; next_run_utc is already future.
        let def = entries.get(&command.definition_id).unwrap().definition.clone();
        launch(
            entries,
            &command.definition_id,
            &def,
            now,
            store,
            runner,
            sink,
            commands_tx,
            view,
        );
    }

    publish_view(entries, view, store);
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

fn is_recurring(definition: &ScheduledTask) -> bool {
    matches!(definition.kind, ScheduleKind::Interval | ScheduleKind::Cron)
}

fn compute_delay(entries: &HashMap<String, Entry>, now: DateTime<Utc>) -> Duration {
    let mut earliest = now + chrono::Duration::from_std(MAX_REEVALUATION).unwrap();
    for entry in entries.values() {
        if entry.deleted || entry.faulted { continue; }
        if entry.status == RuntimeStatus::Idle
            && entry.definition.next_run_utc < earliest
            && !entry.definition.is_retired()
        {
            earliest = entry.definition.next_run_utc;
        }
        // A deadline is a wake reason in its own right. Without this, a
        // definition whose next boundary is a year out — or one with a run in
        // flight, which contributes no due time at all — would keep advertising
        // itself as live long after it expired.
        if let Some(deadline) = limits::deadline_wake(&entry.definition, now) {
            if deadline < earliest {
                earliest = deadline;
            }
        }
    }
    let delta = (earliest - now).to_std().unwrap_or(MAX_REEVALUATION);
    delta.min(MAX_REEVALUATION)
}

/// Publish runtime state both to the in-process view and to the store's
/// ephemeral live table.
///
/// The store copy is what makes `schedule_list` and `session/scheduleList`
/// truthful: they can read a definition's real status without holding a handle
/// to the runtime. It is deliberately a side table, never a serialized field,
/// so a reload can never claim to own runs it does not have.
fn publish_view(
    entries: &HashMap<String, Entry>,
    view: &Arc<RwLock<HashMap<String, ScheduleRuntimeState>>>,
    store: &Arc<ScheduledTaskStore>,
) {
    let new_view: HashMap<String, ScheduleRuntimeState> = entries
        .iter()
        .map(|(id, e)| {
            (
                id.clone(),
                ScheduleRuntimeState {
                    status: match e.status {
                        RuntimeStatus::Idle => ScheduleRuntimeStatus::Idle,
                        RuntimeStatus::Running => ScheduleRuntimeStatus::Running,
                        RuntimeStatus::Pending => ScheduleRuntimeStatus::Pending,
                    },
                    active_task_id: e.active_task_id.clone(),
                },
            )
        })
        .collect();

    for (id, entry) in entries {
        store.set_live_state(id, live_state_of(entry));
    }
    // A definition the runtime no longer tracks owns no live state.
    for id in store.live_states().keys() {
        if !entries.contains_key(id) {
            store.set_live_state(id, ScheduleLiveState::default());
        }
    }

    *view.write().unwrap() = new_view;
}

/// The live status a reader should see for one entry.
///
/// `Retiring` is reported only while work is genuinely still in flight: a
/// definition at its limit with a run executing has not completed its work, and
/// calling it "completed" would be a lie a reader acts on.
fn live_state_of(entry: &Entry) -> ScheduleLiveState {
    let status = match entry.status {
        _ if entry.faulted => ScheduleLiveStatus::Faulted,
        RuntimeStatus::Idle => ScheduleLiveStatus::Idle,
        RuntimeStatus::Running | RuntimeStatus::Pending
            if entry.definition.is_retired() =>
        {
            ScheduleLiveStatus::Retiring
        }
        RuntimeStatus::Running => ScheduleLiveStatus::Running,
        RuntimeStatus::Pending => ScheduleLiveStatus::Pending,
    };
    ScheduleLiveState { status, active_task_id: entry.active_task_id.clone() }
}

fn emit(
    sink: &Arc<dyn ScheduleLifecycleSink>,
    definition: &ScheduledTask,
    task_id: Option<&str>,
    state: &str,
    timestamp: DateTime<Utc>,
    summary: Option<String>,
) {
    sink.publish(ScheduleLifecycleEvent {
        definition_id: definition.id.clone(),
        definition_name: definition.name.clone(),
        task_id: task_id.map(str::to_owned),
        state: state.to_owned(),
        timestamp,
        summary,
    });
}

fn map_terminal_status(
    status: &crate::tasks::TaskRunStatus,
) -> (&'static str, ScheduleTerminalOutcome) {
    use crate::tasks::TaskRunStatus;
    match status {
        TaskRunStatus::Completed => ("completed", ScheduleTerminalOutcome::Succeeded),
        TaskRunStatus::Failed => ("failed", ScheduleTerminalOutcome::Failed),
        TaskRunStatus::Stopped => ("stopped", ScheduleTerminalOutcome::Stopped),
        TaskRunStatus::Running => ("completed", ScheduleTerminalOutcome::Succeeded),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::scheduling::scheduled_task::ScheduleDefinitionDraft;
    use std::sync::atomic::{AtomicUsize, Ordering};

    // ── Mock runner ───────────────────────────────────────────────────────────

    /// A mock runner that records launches and allows tests to control completion.
    pub struct MockRunner {
        pub launch_count: Arc<AtomicUsize>,
        pub task_ids: std::sync::Mutex<Vec<String>>,
        /// Every `ScheduledRun` the runtime handed to this runner, in order.
        pub runs: std::sync::Mutex<Vec<ScheduledRun>>,
        /// When true, immediately call on_terminal with Completed status.
        pub auto_complete: bool,
        /// When set, calls on_terminal with Failed status.
        pub fail_on_launch: bool,
    }

    impl MockRunner {
        pub fn new() -> Arc<Self> {
            Arc::new(Self {
                launch_count: Arc::new(AtomicUsize::new(0)),
                task_ids: std::sync::Mutex::new(Vec::new()),
                runs: std::sync::Mutex::new(Vec::new()),
                auto_complete: true,
                fail_on_launch: false,
            })
        }

        /// Runner that never completes its launches (keeps tasks Running).
        pub fn never_completes() -> Arc<Self> {
            Arc::new(Self {
                launch_count: Arc::new(AtomicUsize::new(0)),
                task_ids: std::sync::Mutex::new(Vec::new()),
                runs: std::sync::Mutex::new(Vec::new()),
                auto_complete: false,
                fail_on_launch: false,
            })
        }
    }

    impl ScheduledAgentRunner for MockRunner {
        fn start(
            &self,
            run: ScheduledRun,
            on_terminal: Arc<dyn Fn(TaskSnapshot) + Send + Sync>,
        ) -> Result<String, String> {
            if self.fail_on_launch {
                return Err("launch failed".into());
            }
            self.runs.lock().unwrap().push(run);
            self.launch_count.fetch_add(1, Ordering::SeqCst);
            let task_id = format!("mock-task-{}", self.launch_count.load(Ordering::SeqCst));
            self.task_ids.lock().unwrap().push(task_id.clone());

            if self.auto_complete {
                let tid = task_id.clone();
                tokio::spawn(async move {
                    tokio::time::sleep(Duration::from_millis(10)).await;
                    on_terminal(make_snapshot(&tid, crate::tasks::TaskRunStatus::Completed));
                });
            }

            Ok(task_id)
        }
    }

    fn make_snapshot(id: &str, status: crate::tasks::TaskRunStatus) -> TaskSnapshot {
        use crate::tasks::{TaskExecutionMode, TaskKind};
        TaskSnapshot {
            id: id.to_owned(),
            parent_id: None,
            depth: 1,
            kind: TaskKind::Scheduled,
            description: "test".into(),
            status,
            mode: TaskExecutionMode::Background,
            version: 1,
            started_at: std::time::SystemTime::now(),
            ended_at: None,
            log_path: String::new(),
            result: Some("ok".into()),
            error: None,
            resolved_model: None,
        }
    }

    pub struct RecordingSink {
        pub events: std::sync::Mutex<Vec<ScheduleLifecycleEvent>>,
    }

    impl RecordingSink {
        pub fn new() -> Arc<Self> {
            Arc::new(Self { events: std::sync::Mutex::new(Vec::new()) })
        }
    }

    impl ScheduleLifecycleSink for RecordingSink {
        fn publish(&self, event: ScheduleLifecycleEvent) {
            self.events.lock().unwrap().push(event);
        }
    }

    fn draft_interval(secs: u64) -> ScheduleDefinitionDraft {
        let now = Utc::now();
        ScheduleDefinitionDraft {
            name: None,
            kind: ScheduleKind::Interval,
            prompt: "run me".into(),
            interval: Some(Duration::from_secs(secs)),
            at_utc: None,
            cron: None,
            time_zone_id: "UTC".into(),
            next_run_utc: now - chrono::Duration::seconds(1), // already due
            expires_at_utc: None,
            max_runs: None,
        }
    }

    fn draft_at(offset_ms: i64) -> ScheduleDefinitionDraft {
        let now = Utc::now();
        let when = now + chrono::Duration::milliseconds(offset_ms);
        ScheduleDefinitionDraft {
            name: None,
            kind: ScheduleKind::At,
            prompt: "run once".into(),
            interval: None,
            at_utc: Some(when),
            cron: None,
            time_zone_id: "UTC".into(),
            next_run_utc: when,
            expires_at_utc: None,
            max_runs: None,
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// An overdue interval definition fires once and advances next_run_utc.
    #[tokio::test]
    async fn interval_definition_fires_and_advances() {
        let store = ScheduledTaskStore::new();
        let t = store.add(draft_interval(3600), Utc::now());

        let runner = MockRunner::new();
        let sink = RecordingSink::new();
        let runtime = ScheduleRuntime::new(store.clone(), runner.clone(), sink.clone());

        // Wait for one launch.
        tokio::time::timeout(Duration::from_secs(2), async {
            while runner.launch_count.load(Ordering::SeqCst) == 0 {
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
        }).await.expect("should fire within 2s");

        // NextRunUtc must have advanced.
        let current = store.items();
        assert_eq!(current.len(), 1);
        assert!(current[0].next_run_utc > t.next_run_utc, "next_run_utc must advance");

        // Started event must have been emitted.
        let events = sink.events.lock().unwrap();
        assert!(events.iter().any(|e| e.state == "started"), "Started event must be emitted");

        runtime.shutdown().await;
    }

    /// A one-shot (At) definition fires once and is removed from the store.
    #[tokio::test]
    async fn at_definition_fires_once_then_removed() {
        let store = ScheduledTaskStore::new();
        store.add(draft_at(-10), Utc::now()); // already overdue

        let runner = MockRunner::new();
        let sink = RecordingSink::new();
        let runtime = ScheduleRuntime::new(store.clone(), runner.clone(), sink.clone());

        // Wait for completion.
        tokio::time::timeout(Duration::from_secs(2), async {
            while store.items().len() > 0 {
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
        }).await.expect("one-shot must complete and be removed");

        let events = sink.events.lock().unwrap();
        assert!(events.iter().any(|e| e.state == "started"), "Started must be emitted");
        assert!(events.iter().any(|e| e.state == "completed"), "Completed must be emitted");

        runtime.shutdown().await;
    }

    /// Recurring definition: same definition never runs concurrently.
    #[tokio::test]
    async fn recurring_definition_never_overlaps() {
        let store = ScheduledTaskStore::new();
        // Very short interval so multiple due ticks may arrive quickly.
        store.add(draft_interval(1), Utc::now());

        // Runner that doesn't auto-complete (keeps task running).
        let runner = MockRunner::never_completes();

        let sink = RecordingSink::new();
        let runtime = ScheduleRuntime::new(store.clone(), runner.clone(), sink.clone());

        // Wait for first launch.
        tokio::time::timeout(Duration::from_secs(2), async {
            while runner.launch_count.load(Ordering::SeqCst) == 0 {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        }).await.expect("first launch must happen");

        // Let multiple iterations pass; count must stay 1 (no overlap).
        tokio::time::sleep(Duration::from_millis(100)).await;
        let count_after = runner.launch_count.load(Ordering::SeqCst);
        assert_eq!(count_after, 1, "same definition must not overlap; concurrent launches detected");

        runtime.shutdown().await;
    }

    /// Deletion while running: the definition is removed from the store but
    /// the running task is not interrupted. No replacement is started.
    #[tokio::test]
    async fn delete_while_running_allows_task_to_finish() {
        let store = ScheduledTaskStore::new();
        let t = store.add(draft_interval(3600), Utc::now());

        // Runner that doesn't auto-complete.
        let runner = MockRunner::never_completes();

        let sink = RecordingSink::new();
        let runtime = ScheduleRuntime::new(store.clone(), runner.clone(), sink.clone());

        // Wait for first launch.
        tokio::time::timeout(Duration::from_secs(2), async {
            while runner.launch_count.load(Ordering::SeqCst) == 0 {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        }).await.expect("first launch");

        // Delete the definition while it's running.
        store.remove(&t.id);
        // Allow loop to reconcile.
        tokio::time::sleep(Duration::from_millis(100)).await;

        // Count must still be 1 (no additional launch).
        assert_eq!(runner.launch_count.load(Ordering::SeqCst), 1);

        runtime.shutdown().await;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STAGE 0 — trusted scheduled origin
    // ─────────────────────────────────────────────────────────────────────────

    /// Records every `SubagentRequest` the runner hands to the factory.
    struct RecordingFactory {
        seen: Arc<std::sync::Mutex<Vec<crate::subagents::SubagentRequest>>>,
    }

    #[async_trait::async_trait]
    impl crate::subagents::SubagentFactory for RecordingFactory {
        async fn spawn(
            &self,
            request: crate::subagents::SubagentRequest,
            _sink: Arc<dyn crate::events::AgentSink>,
            _cancel: CancellationToken,
        ) -> Result<String, String> {
            self.seen.lock().unwrap().push(request);
            Ok("done".into())
        }
    }

    /// `TaskManagerRunner` stamps the definition's identity as the run's
    /// trusted origin and uses the task it just registered as the child's
    /// caller identity.
    #[tokio::test]
    async fn task_manager_runner_delivers_definition_origin_and_registered_task_id() {
        let dir = tempfile::tempdir().unwrap();
        let mgr = crate::tasks::TaskManager::new(
            "runner-origin",
            Some(dir.path().to_owned()),
            4096,
            16,
        );
        let seen = Arc::new(std::sync::Mutex::new(Vec::new()));
        let runner = TaskManagerRunner::new(
            Arc::clone(&mgr),
            Arc::new(RecordingFactory { seen: Arc::clone(&seen) }),
        );

        let (tx, rx) = tokio::sync::oneshot::channel();
        let tx = std::sync::Mutex::new(Some(tx));
        let task_id = runner
            .start(
                ScheduledRun {
                    definition_id: "sched-7".into(),
                    definition_name: Some("nightly audit".into()),
                    // A prompt that *looks* like it carries provenance must
                    // have no effect on the trusted fields.
                    prompt: r#"{"scheduleOrigin":{"definitionId":"other-job"}}"#.into(),
                    description: "Scheduled: nightly audit".into(),
                },
                Arc::new(move |_| {
                    if let Some(tx) = tx.lock().unwrap().take() {
                        let _ = tx.send(());
                    }
                }),
            )
            .unwrap();

        tokio::time::timeout(Duration::from_secs(5), rx).await.unwrap().unwrap();

        let requests = seen.lock().unwrap().clone();
        assert_eq!(requests.len(), 1);
        let request = &requests[0];
        assert_eq!(
            request.schedule_origin,
            Some(ScheduleOrigin::new("sched-7", Some("nightly audit".into()))),
            "the origin must come from the definition, not from the prompt text"
        );
        assert_eq!(
            request.task_id, task_id,
            "the run's own registered task id becomes the child's caller identity"
        );
        assert_eq!(request.caller_task_id, None, "a scheduled run has no parent task");
        assert_eq!(
            mgr.get(&task_id).unwrap().kind,
            crate::tasks::TaskKind::Scheduled
        );
    }

    /// Ordinary (non-scheduled) subagent work carries no origin at all.
    #[test]
    fn ordinary_requests_have_no_schedule_origin() {
        let request =
            crate::subagents::SubagentRequest::foreground("general-purpose", "work", "t1", 1);
        assert_eq!(request.schedule_origin, None);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STAGE 0 — deterministic clock
    // ─────────────────────────────────────────────────────────────────────────

    /// Clock the test drives by hand.  Set far in the future so any remaining
    /// `Utc::now()` read inside the runtime is unambiguously visible: a
    /// wall-clock runtime never considers a 2087 definition due.
    struct TestClock {
        now: std::sync::Mutex<DateTime<Utc>>,
    }

    impl TestClock {
        fn new(start: DateTime<Utc>) -> Arc<Self> {
            Arc::new(Self { now: std::sync::Mutex::new(start) })
        }
        fn advance(&self, by: chrono::Duration) {
            let mut n = self.now.lock().unwrap();
            *n += by;
        }
    }

    impl ScheduleClock for TestClock {
        fn now(&self) -> DateTime<Utc> {
            *self.now.lock().unwrap()
        }
    }

    fn fake_start() -> DateTime<Utc> {
        "2087-03-01T00:00:00Z".parse::<DateTime<Utc>>().unwrap()
    }

    /// Due-evaluation, recurrence advancement and terminal metadata all read
    /// the injected clock.  Nothing here sleeps for a scheduling interval: the
    /// test advances the clock and pokes the store to wake the loop.
    #[tokio::test]
    async fn injected_clock_drives_due_evaluation_and_recurrence_advancement() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();

        let mut draft = draft_interval(3600);
        draft.next_run_utc = start - chrono::Duration::seconds(1); // due per fake clock
        let definition = store.add(draft, start);

        let runner = MockRunner::new();
        let sink = RecordingSink::new();
        let runtime = ScheduleRuntime::new_with_clock(
            store.clone(),
            runner.clone(),
            sink.clone(),
            clock.clone(),
        );

        tokio::time::timeout(Duration::from_secs(5), async {
            while runner.launch_count.load(Ordering::SeqCst) == 0 {
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        })
        .await
        .expect("a definition due per the injected clock must fire");

        // The advanced boundary is computed from the fake clock, not the wall
        // clock: an hourly definition evaluated at 2087-03-01T00:00 lands
        // inside the following hour.
        let advanced = store.items().into_iter().next().unwrap();
        assert!(
            advanced.next_run_utc > start
                && advanced.next_run_utc <= start + chrono::Duration::hours(1),
            "recurrence must advance from the injected now; got {} (fake now {start})",
            advanced.next_run_utc
        );
        assert_eq!(
            advanced.updated_at_utc, start,
            "the definition's update stamp must come from the injected clock"
        );

        // The runner completes, and process_terminal stamps the terminal
        // metadata with the injected clock too.
        tokio::time::timeout(Duration::from_secs(5), async {
            while store
                .items()
                .into_iter()
                .next()
                .and_then(|d| d.last_terminal_outcome)
                .is_none()
            {
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        })
        .await
        .expect("the terminal callback must be processed");

        let terminal = store
            .items()
            .into_iter()
            .next()
            .unwrap()
            .last_terminal_outcome
            .unwrap();
        assert_eq!(
            terminal.completed_at_utc, start,
            "the terminal timestamp must come from the injected clock"
        );
        assert_eq!(terminal.outcome, ScheduleTerminalOutcome::Succeeded);

        // Lifecycle events carry injected-clock timestamps as well.
        assert!(
            sink.events
                .lock()
                .unwrap()
                .iter()
                .all(|e| e.timestamp == start),
            "lifecycle timestamps must come from the injected clock"
        );

        // Move past the next boundary and wake the loop through the store's
        // existing version notification — no hour-long sleep required.
        clock.advance(chrono::Duration::hours(2));
        let current = store.items().into_iter().next().unwrap();
        assert!(store.replace(current), "poking the store must wake the loop");

        tokio::time::timeout(Duration::from_secs(5), async {
            while runner.launch_count.load(Ordering::SeqCst) < 2 {
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        })
        .await
        .expect("advancing the injected clock past the boundary must fire again");

        let after = store.items().into_iter().next().unwrap();
        assert!(
            after.next_run_utc > start + chrono::Duration::hours(2),
            "the second advance must also come from the injected clock; got {}",
            after.next_run_utc
        );

        // The definition identity reaches the runner on every launch.
        let runs = runner.runs.lock().unwrap();
        assert_eq!(runs.len(), 2);
        assert!(runs.iter().all(|r| r.definition_id == definition.id));
        assert!(runs.iter().all(|r| r.prompt == "run me"));

        drop(runs);
        runtime.shutdown().await;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STAGE 1 — bounded schedules
    // ─────────────────────────────────────────────────────────────────────────

    /// A runner the test drives by hand: it decides whether a launch is
    /// accepted, and decides *later* whether the accepted run succeeded.
    ///
    /// That separation is the whole point. `TaskManagerRunner` returns `Ok`
    /// once the task is registered — *before* it acquires a concurrency slot
    /// or talks to a model — so a run can be accepted and still fail. A mock
    /// that only ever completed successfully would let a wrong counting rule
    /// pass.
    pub struct ControlledRunner {
        accepted: std::sync::Mutex<Vec<(String, Arc<dyn Fn(TaskSnapshot) + Send + Sync>)>>,
        launch_count: AtomicUsize,
        refuse: std::sync::atomic::AtomicBool,
    }

    impl ControlledRunner {
        fn new() -> Arc<Self> {
            Arc::new(Self {
                accepted: std::sync::Mutex::new(Vec::new()),
                launch_count: AtomicUsize::new(0),
                refuse: std::sync::atomic::AtomicBool::new(false),
            })
        }

        fn refusing() -> Arc<Self> {
            let runner = Self::new();
            runner.refuse.store(true, Ordering::SeqCst);
            runner
        }

        fn launches(&self) -> usize {
            self.launch_count.load(Ordering::SeqCst)
        }

        fn in_flight(&self) -> usize {
            self.accepted.lock().unwrap().len()
        }

        /// Finish every in-flight run with `status`.
        fn finish_all(&self, status: crate::tasks::TaskRunStatus) {
            let runs: Vec<_> = self.accepted.lock().unwrap().drain(..).collect();
            for (task_id, on_terminal) in runs {
                on_terminal(make_snapshot(&task_id, status));
            }
        }
    }

    impl ScheduledAgentRunner for ControlledRunner {
        fn start(
            &self,
            _run: ScheduledRun,
            on_terminal: Arc<dyn Fn(TaskSnapshot) + Send + Sync>,
        ) -> Result<String, String> {
            if self.refuse.load(Ordering::SeqCst) {
                // Refused outright: no task is registered, nothing runs.
                return Err("no capacity".into());
            }
            let n = self.launch_count.fetch_add(1, Ordering::SeqCst) + 1;
            let task_id = format!("controlled-task-{n}");
            self.accepted.lock().unwrap().push((task_id.clone(), on_terminal));
            Ok(task_id)
        }
    }

    /// Wake the schedule loop without clobbering anything.
    ///
    /// The loop parks on the store version, so a test that advances the fake
    /// clock has to touch the store to force a re-evaluation. This is an
    /// atomic field update, not a blind `replace` of a snapshot that may
    /// already be stale — a blind poke could itself undo a retirement and make
    /// the test prove the opposite of what it claims.
    fn poke(store: &Arc<ScheduledTaskStore>, id: &str, now: DateTime<Utc>) {
        store.update(id, |task| task.updated_at_utc = now);
    }

    async fn wait_until(label: &str, mut condition: impl FnMut() -> bool) {
        tokio::time::timeout(Duration::from_secs(5), async {
            while !condition() {
                tokio::time::sleep(Duration::from_millis(2)).await;
            }
        })
        .await
        .unwrap_or_else(|_| panic!("timed out waiting for: {label}"));
    }

    /// Settle the loop: give it enough iterations to have done anything it
    /// was going to do, so an assertion of "nothing happened" means it.
    async fn settle() {
        tokio::time::sleep(Duration::from_millis(80)).await;
    }

    fn bounded_draft(
        secs: u64,
        expires_at: Option<DateTime<Utc>>,
        max_runs: Option<u32>,
        start: DateTime<Utc>,
    ) -> ScheduleDefinitionDraft {
        ScheduleDefinitionDraft {
            name: Some("bounded".into()),
            kind: ScheduleKind::Interval,
            prompt: "watch".into(),
            interval: Some(Duration::from_secs(secs)),
            at_utc: None,
            cron: None,
            time_zone_id: "UTC".into(),
            next_run_utc: start - chrono::Duration::seconds(1), // due immediately
            expires_at_utc: expires_at,
            max_runs,
        }
    }

    fn current(store: &Arc<ScheduledTaskStore>, id: &str) -> ScheduledTask {
        store.items().into_iter().find(|t| t.id == id).expect("definition must still exist")
    }

    // ── deadlines ─────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn each_definition_uses_a_fresh_admission_time() {
        struct AdvancingClock {
            start: DateTime<Utc>,
            calls: std::sync::atomic::AtomicUsize,
        }
        impl ScheduleClock for AdvancingClock {
            fn now(&self) -> DateTime<Utc> {
                let first = self.calls.fetch_add(1, std::sync::atomic::Ordering::SeqCst) == 0;
                self.start + chrono::Duration::milliseconds(if first { 0 } else { 2 })
            }
        }
        let start = fake_start();
        let deadline = start + chrono::Duration::milliseconds(1);
        let store = ScheduledTaskStore::new();
        for _ in 0..2 {
            store.add(bounded_draft(3600, Some(deadline), None, start), start);
        }
        let runner = ControlledRunner::new();
        let runner_port: Arc<dyn ScheduledAgentRunner> = runner.clone();
        let sink: Arc<dyn ScheduleLifecycleSink> = RecordingSink::new();
        let clock: Arc<dyn ScheduleClock> = Arc::new(AdvancingClock {
            start, calls: std::sync::atomic::AtomicUsize::new(0),
        });
        let view = Arc::new(RwLock::new(HashMap::new()));
        let (tx, _rx) = mpsc::unbounded_channel();
        let mut entries = HashMap::new();
        reconcile(&mut entries, &store.get_snapshot(), &view, &store);
        evaluate_due(
            &mut entries, &store, &runner_port, &sink, &tx, &view,
            &CancellationToken::new(), &clock,
        ).await;
        assert_eq!(runner.launches(), 1, "the later admission occurred after expiry");
    }

    /// The deadline retires the definition when the clock *reaches* it — not
    /// early because the next boundary would have fallen beyond it. An hourly
    /// monitor with a Friday deadline stays live until Friday.
    #[tokio::test]
    async fn a_definition_expires_at_its_deadline_not_at_its_last_fitting_tick() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();
        let deadline = start + chrono::Duration::days(7);
        let id = store
            .add(bounded_draft(3600, Some(deadline), None, start), start)
            .id;

        let runner = ControlledRunner::new();
        let sink = RecordingSink::new();
        let runtime =
            ScheduleRuntime::new_with_clock(store.clone(), runner.clone(), sink.clone(), clock.clone());

        wait_until("the first run to start", || runner.launches() >= 1).await;
        runner.finish_all(crate::tasks::TaskRunStatus::Completed);

        // Six days in: still live, still unretired.
        clock.advance(chrono::Duration::days(6));
        poke(&store, &id, clock.now());
        wait_until("the catch-up run", || runner.launches() >= 2).await;
        assert!(
            !current(&store, &id).is_retired(),
            "a definition six days before its deadline must still be live"
        );
        // Let the run finish so the definition is idle when the deadline lands:
        // the default is that in-flight work is never interrupted.
        runner.finish_all(crate::tasks::TaskRunStatus::Completed);
        settle().await;

        // At the deadline: retired, and nothing launches at or after it.
        clock.advance(chrono::Duration::days(1));
        poke(&store, &id, clock.now());
        wait_until("the deadline retirement", || current(&store, &id).is_retired()).await;

        let retirement = current(&store, &id).retirement.unwrap();
        assert_eq!(retirement.reason, ScheduleRetirementReason::Expired);
        assert_eq!(retirement.retired_at_utc, clock.now());

        let at_retirement = runner.launches();
        clock.advance(chrono::Duration::days(30));
        poke(&store, &id, clock.now());
        settle().await;
        assert_eq!(
            runner.launches(),
            at_retirement,
            "no occurrence may start at or after the expiry"
        );

        assert!(
            sink.events.lock().unwrap().iter().filter(|e| e.state == "retired").count() == 1,
            "the retirement must be announced exactly once"
        );

        runtime.shutdown().await;
    }

    /// A definition whose next boundary is far in the future, with a run still
    /// in flight, must still be revisited at its deadline. Without a
    /// deadline-driven wake the loop would sleep past it entirely.
    #[tokio::test]
    async fn the_loop_wakes_at_the_deadline_even_with_a_distant_next_run_and_active_work() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();
        let deadline = start + chrono::Duration::hours(2);
        // A daily interval: after the first launch the next boundary is ~24h
        // away, well past the 2h deadline.
        let id = store
            .add(bounded_draft(86_400, Some(deadline), None, start), start)
            .id;

        let runner = ControlledRunner::new();
        let sink = RecordingSink::new();
        let runtime =
            ScheduleRuntime::new_with_clock(store.clone(), runner.clone(), sink.clone(), clock.clone());

        wait_until("the first run to start", || runner.launches() == 1).await;
        assert!(current(&store, &id).next_run_utc > deadline, "next boundary is past the deadline");

        // The run is still in flight when the deadline passes.
        clock.advance(chrono::Duration::hours(2));
        poke(&store, &id, clock.now());
        settle().await;

        // Default: running work is not interrupted, but the definition is
        // already known to be retiring.
        assert_eq!(runner.in_flight(), 1, "the active run must not be cancelled by expiry");
        assert_eq!(
            crate::scheduling::reported_state(
                &current(&store, &id),
                &store.live_state(&id),
                clock.now()
            ),
            "retiring",
            "a definition past its deadline with work in flight is retiring, not completed"
        );

        runner.finish_all(crate::tasks::TaskRunStatus::Completed);
        wait_until("the retirement to settle", || current(&store, &id).is_retired()).await;
        assert_eq!(
            current(&store, &id).retirement.unwrap().reason,
            ScheduleRetirementReason::Expired
        );

        runtime.shutdown().await;
    }

    // ── run budgets ───────────────────────────────────────────────────────────

    /// "Daily, for a week" is seven accepted launches — and a run that fails
    /// still consumed one of them.
    #[tokio::test]
    async fn exactly_seven_accepted_launches_are_made_including_failed_runs() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();
        let id = store.add(bounded_draft(86_400, None, Some(7), start), start).id;

        let runner = ControlledRunner::new();
        let sink = RecordingSink::new();
        let runtime =
            ScheduleRuntime::new_with_clock(store.clone(), runner.clone(), sink.clone(), clock.clone());

        for day in 0..12 {
            wait_until(&format!("day {day} to settle"), || {
                runner.in_flight() > 0 || current(&store, &id).is_retired()
            })
            .await;
            // Alternate success and failure: both consume budget.
            let status = if day % 2 == 0 {
                crate::tasks::TaskRunStatus::Completed
            } else {
                crate::tasks::TaskRunStatus::Failed
            };
            runner.finish_all(status);
            clock.advance(chrono::Duration::days(1));
            poke(&store, &id, clock.now());
            settle().await;
        }

        assert_eq!(
            runner.launches(),
            7,
            "a run that failed still consumed budget; the schedule must stop at seven"
        );
        let definition = current(&store, &id);
        assert_eq!(definition.runs_started, 7);
        assert_eq!(
            definition.retirement.unwrap().reason,
            ScheduleRetirementReason::RunLimit
        );

        runtime.shutdown().await;
    }

    /// The counter is monotonic: advancing the boundary while active, and
    /// reconciling repeatedly, must never roll it back.
    #[tokio::test]
    async fn the_run_counter_is_never_reset_by_advancing_or_reconciling() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();
        let id = store.add(bounded_draft(3600, None, Some(5), start), start).id;

        let runner = ControlledRunner::new();
        let runtime = ScheduleRuntime::new_with_clock(
            store.clone(),
            runner.clone(),
            RecordingSink::new(),
            clock.clone(),
        );

        wait_until("the first run", || runner.launches() == 1).await;
        assert_eq!(current(&store, &id).runs_started, 1);

        // Several due ticks arrive while the run is still in flight: they only
        // advance the boundary and queue at most one replacement.
        for _ in 0..3 {
            clock.advance(chrono::Duration::hours(1));
            poke(&store, &id, clock.now());
            settle().await;
            assert_eq!(
                current(&store, &id).runs_started,
                1,
                "advancing while active must not touch the counter"
            );
        }

        runner.finish_all(crate::tasks::TaskRunStatus::Completed);
        wait_until("the coalesced replacement", || runner.launches() == 2).await;
        assert_eq!(current(&store, &id).runs_started, 2);

        runtime.shutdown().await;
    }

    /// The queued replacement is gated too: a definition that spends its last
    /// unit of budget on the *running* occurrence must not start the pending
    /// one when that occurrence finishes.
    #[tokio::test]
    async fn a_pending_replacement_is_refused_once_the_budget_is_spent() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();
        let id = store.add(bounded_draft(3600, None, Some(1), start), start).id;

        let runner = ControlledRunner::new();
        let sink = RecordingSink::new();
        let runtime =
            ScheduleRuntime::new_with_clock(store.clone(), runner.clone(), sink.clone(), clock.clone());

        wait_until("the only allowed run", || runner.launches() == 1).await;

        // A due tick lands while it runs: with budget left this would queue a
        // replacement.
        clock.advance(chrono::Duration::hours(2));
        poke(&store, &id, clock.now());
        settle().await;

        assert_eq!(
            crate::scheduling::reported_state(
                &current(&store, &id),
                &store.live_state(&id),
                clock.now()
            ),
            "retiring",
            "the last allowed run must report retiring, not completed, while it works"
        );

        runner.finish_all(crate::tasks::TaskRunStatus::Completed);
        wait_until("the retirement", || current(&store, &id).is_retired()).await;
        settle().await;

        assert_eq!(runner.launches(), 1, "the pending replacement must never start");
        let definition = current(&store, &id);
        assert_eq!(definition.runs_started, 1);
        assert_eq!(
            definition.retirement.unwrap().reason,
            ScheduleRetirementReason::RunLimit
        );
        // The run's own outcome stays a separate, honest fact.
        assert_eq!(
            definition.last_terminal_outcome.unwrap().outcome,
            ScheduleTerminalOutcome::Succeeded
        );

        runtime.shutdown().await;
    }

    // ── launch failures ───────────────────────────────────────────────────────

    /// A launch the runner refuses outright registers no task, so nothing ran
    /// and nothing may be charged to the budget.
    #[tokio::test]
    async fn a_refused_launch_does_not_consume_budget() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();
        let id = store.add(bounded_draft(3600, None, Some(3), start), start).id;

        let runner = ControlledRunner::refusing();
        let sink = RecordingSink::new();
        let runtime =
            ScheduleRuntime::new_with_clock(store.clone(), runner.clone(), sink.clone(), clock.clone());

        wait_until("the refusal to be reported", || {
            sink.events.lock().unwrap().iter().any(|e| e.state == "failed")
        })
        .await;
        settle().await;

        let definition = current(&store, &id);
        assert_eq!(definition.runs_started, 0, "nothing ran, so nothing was charged");
        assert!(!definition.is_retired(), "a refused launch is not a retirement for a recurring job");
        assert_eq!(
            limits::reported_state(&definition, &store.live_state(&id), clock.now()),
            "failed",
            "quarantined work must not be advertised as idle future work",
        );

        runtime.shutdown().await;
    }

    #[tokio::test]
    async fn a_one_shot_retired_after_reconcile_keeps_its_reason_after_completion() {
        let start = fake_start();
        let store = ScheduledTaskStore::new();
        let mut draft = bounded_draft(3600, None, None, start);
        draft.kind = ScheduleKind::At;
        draft.interval = None;
        draft.at_utc = Some(start);
        let id = store.add(draft, start).id;
        let runner = ControlledRunner::new();
        let runner_port: Arc<dyn ScheduledAgentRunner> = runner.clone();
        let sink: Arc<dyn ScheduleLifecycleSink> = RecordingSink::new();
        let clock: Arc<dyn ScheduleClock> = TestClock::new(start);
        let view = Arc::new(RwLock::new(HashMap::new()));
        let (tx, mut rx) = mpsc::unbounded_channel();
        let mut entries = HashMap::new();
        reconcile(&mut entries, &store.get_snapshot(), &view, &store);
        claim_and_launch(&mut entries, &id, start, &store, &runner_port, &sink, &tx, &view).await;

        // Self-retirement can land after the loop's snapshot and before its
        // terminal callback is drained. The store, not that snapshot, wins.
        store.update(&id, |definition| {
            definition.retirement = Some(ScheduleRetirement {
                reason: ScheduleRetirementReason::Cancelled,
                retired_at_utc: start,
                note: None,
            });
        }).unwrap();
        runner.finish_all(crate::tasks::TaskRunStatus::Completed);
        let terminal = tokio::time::timeout(Duration::from_secs(1), rx.recv()).await.unwrap().unwrap();
        process_terminal(&mut entries, terminal, &store, &runner_port, &sink, &tx, &view, &clock).await;
        let definition = current(&store, &id);
        assert_eq!(definition.retirement.unwrap().reason, ScheduleRetirementReason::Cancelled);
        assert!(definition.last_terminal_outcome.is_some());
    }

    /// A one-shot whose launch is refused used to be dropped from the runtime
    /// map but left in the store, so reconciliation rediscovered it as fresh,
    /// overdue work and retried it forever. It must now retire in both places
    /// with an explicit reason, and the loop must not spin.
    #[tokio::test]
    async fn a_refused_one_shot_retires_in_both_places_and_never_retries() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();
        let id = store
            .add(
                ScheduleDefinitionDraft {
                    name: None,
                    kind: ScheduleKind::At,
                    prompt: "once".into(),
                    interval: None,
                    at_utc: Some(start - chrono::Duration::seconds(1)),
                    cron: None,
                    time_zone_id: "UTC".into(),
                    next_run_utc: start - chrono::Duration::seconds(1),
                    expires_at_utc: None,
                    max_runs: None,
                },
                start,
            )
            .id;

        let runner = ControlledRunner::refusing();
        let sink = RecordingSink::new();
        let runtime =
            ScheduleRuntime::new_with_clock(store.clone(), runner.clone(), sink.clone(), clock.clone());

        wait_until("the one-shot to retire", || {
            store.items().iter().any(|t| t.id == id && t.is_retired())
        })
        .await;

        // Several reconcile cycles later it must still be retired, with no
        // retry storm.
        for _ in 0..5 {
            poke(&store, &id, clock.now());
            settle().await;
        }

        let definition = current(&store, &id);
        assert_eq!(
            definition.retirement.unwrap().reason,
            ScheduleRetirementReason::LaunchFailed,
            "the failure must be an explicit, visible state"
        );
        let failures = sink.events.lock().unwrap().iter().filter(|e| e.state == "failed").count();
        assert_eq!(failures, 1, "one refusal, one report — not a retry storm");
        let retirements =
            sink.events.lock().unwrap().iter().filter(|e| e.state == "retired").count();
        assert_eq!(retirements, 1, "the retirement must not double-fire");

        runtime.shutdown().await;
    }

    /// The same counting rule, proved against the **real** `TaskManagerRunner`
    /// rather than an idealized mock.
    ///
    /// `TaskManagerRunner::start` registers the task and returns `Ok`
    /// *before* the spawned worker acquires a concurrency slot or talks to a
    /// model, so a run can be accepted and then fail for reasons the scheduler
    /// never sees. Those runs must still consume budget: if they did not, a
    /// broken provider or an exhausted subagent pool would let a bounded job
    /// retry forever. A mock that reports `Err` for the same situation would
    /// let that bug through, which is why this test uses the production runner.
    #[tokio::test]
    async fn the_real_task_manager_runner_charges_runs_that_fail_after_acceptance() {
        /// Every spawn fails — exactly what an exhausted pool or a dead
        /// provider looks like from inside the worker, *after* `start`
        /// already returned `Ok`.
        struct AlwaysFailingFactory;

        #[async_trait::async_trait]
        impl crate::subagents::SubagentFactory for AlwaysFailingFactory {
            async fn spawn(
                &self,
                _request: crate::subagents::SubagentRequest,
                _sink: Arc<dyn crate::events::AgentSink>,
                _cancel: CancellationToken,
            ) -> Result<String, String> {
                Err("no capacity".into())
            }
        }

        let start = fake_start();
        let clock = TestClock::new(start);
        let dir = tempfile::tempdir().unwrap();
        let mgr = crate::tasks::TaskManager::new(
            "runner-counting",
            Some(dir.path().to_owned()),
            4096,
            64,
        );
        let store = ScheduledTaskStore::new();
        let id = store.add(bounded_draft(3600, None, Some(2), start), start).id;

        let runner = TaskManagerRunner::new(Arc::clone(&mgr), Arc::new(AlwaysFailingFactory));
        let sink = RecordingSink::new();
        let runtime =
            ScheduleRuntime::new_with_clock(store.clone(), runner, sink.clone(), clock.clone());

        // Drive well past two boundaries: if failed runs were uncounted this
        // would keep firing.
        for _ in 0..6 {
            clock.advance(chrono::Duration::hours(1));
            poke(&store, &id, clock.now());
            settle().await;
        }

        let definition = current(&store, &id);
        assert_eq!(
            definition.runs_started, 2,
            "a run accepted by the runner counts even though it failed afterwards"
        );
        assert_eq!(
            definition.retirement.expect("the budget is spent").reason,
            ScheduleRetirementReason::RunLimit
        );

        // The task manager really did register (and fail) exactly two runs —
        // the runner accepted them, which is what "accepted attempt" means.
        let scheduled: Vec<_> = mgr
            .list()
            .into_iter()
            .filter(|t| t.kind == crate::tasks::TaskKind::Scheduled)
            .collect();
        assert_eq!(scheduled.len(), 2, "two registered scheduled tasks, no more");
        assert!(
            scheduled
                .iter()
                .all(|t| t.status == crate::tasks::TaskRunStatus::Failed),
            "both accepted runs failed after acceptance"
        );

        // And the honest record of the last run is a failure, separate from
        // the definition's "run budget spent" retirement.
        assert_eq!(
            current(&store, &id).last_terminal_outcome.unwrap().outcome,
            ScheduleTerminalOutcome::Failed
        );

        runtime.shutdown().await;
    }

    // ── cancellation races ────────────────────────────────────────────────────

    /// A retirement recorded by someone else (the self-cancel tool) while the
    /// runtime holds an in-flight run must survive every later write: the
    /// terminal metadata update, the reconcile, and the boundary advance all
    /// go through targeted field updates precisely so they cannot resurrect it.
    #[tokio::test]
    async fn a_cancellation_during_a_run_is_never_undone_by_the_terminal_write() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();
        let id = store.add(bounded_draft(3600, None, Some(9), start), start).id;

        let runner = ControlledRunner::new();
        let runtime = ScheduleRuntime::new_with_clock(
            store.clone(),
            runner.clone(),
            RecordingSink::new(),
            clock.clone(),
        );

        wait_until("the run to start", || runner.launches() == 1).await;

        // Someone cancels the definition while the run is in flight.
        store.update(&id, |task| {
            task.retirement = Some(ScheduleRetirement {
                reason: ScheduleRetirementReason::Cancelled,
                retired_at_utc: clock.now(),
                note: Some("no longer needed".into()),
            });
        });

        runner.finish_all(crate::tasks::TaskRunStatus::Completed);
        settle().await;

        let definition = current(&store, &id);
        assert_eq!(
            definition.retirement.as_ref().unwrap().reason,
            ScheduleRetirementReason::Cancelled,
            "the terminal write must not overwrite or relabel the cancellation"
        );
        assert_eq!(
            definition.runs_started, 1,
            "the counter must not be rolled back by the terminal write"
        );
        assert!(
            definition.last_terminal_outcome.is_some(),
            "the run's own outcome is still recorded honestly"
        );

        // And it stays retired across further reconciles — no resurrection.
        for _ in 0..5 {
            clock.advance(chrono::Duration::hours(1));
            poke(&store, &id, clock.now());
            settle().await;
        }
        assert_eq!(runner.launches(), 1, "a cancelled definition must never launch again");
        assert_eq!(
            current(&store, &id).retirement.unwrap().reason,
            ScheduleRetirementReason::Cancelled
        );

        runtime.shutdown().await;
    }

    /// A definition cancelled while idle must not start even though its
    /// boundary is already overdue when the loop next looks at it.
    #[tokio::test]
    async fn a_definition_cancelled_before_its_due_tick_never_launches() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();

        // Not yet due, so the loop parks without launching.
        let mut draft = bounded_draft(3600, None, None, start);
        draft.next_run_utc = start + chrono::Duration::hours(1);
        let id = store.add(draft, start).id;

        let runner = ControlledRunner::new();
        let sink = RecordingSink::new();
        let runtime =
            ScheduleRuntime::new_with_clock(store.clone(), runner.clone(), sink.clone(), clock.clone());
        settle().await;
        assert_eq!(runner.launches(), 0);

        store.update(&id, |task| {
            task.retirement = Some(ScheduleRetirement {
                reason: ScheduleRetirementReason::Cancelled,
                retired_at_utc: clock.now(),
                note: None,
            });
        });

        // The boundary passes; a cancelled definition must stay silent.
        clock.advance(chrono::Duration::hours(2));
        poke(&store, &id, clock.now());
        settle().await;

        assert_eq!(runner.launches(), 0, "a cancelled definition must never launch");
        assert_eq!(
            crate::scheduling::reported_state(
                &current(&store, &id),
                &store.live_state(&id),
                clock.now()
            ),
            "cancelled"
        );

        runtime.shutdown().await;
    }

    /// Deleting a definition mid-launch must not resurrect it through the
    /// counter commit that follows the runner's acceptance.
    #[tokio::test]
    async fn a_definition_deleted_while_launching_is_not_resurrected_by_the_counter_commit() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();
        let id = store.add(bounded_draft(3600, None, Some(9), start), start).id;

        let runner = ControlledRunner::new();
        let runtime = ScheduleRuntime::new_with_clock(
            store.clone(),
            runner.clone(),
            RecordingSink::new(),
            clock.clone(),
        );

        wait_until("the run to start", || runner.launches() == 1).await;
        store.remove(&id);
        runner.finish_all(crate::tasks::TaskRunStatus::Completed);
        settle().await;

        assert!(
            store.items().iter().all(|t| t.id != id),
            "a deleted definition must never come back through a counter or terminal write"
        );
        assert!(store.live_states().is_empty(), "and it owns no stale live state");

        runtime.shutdown().await;
    }

    // ── unbounded definitions are unchanged ───────────────────────────────────

    /// The whole point of optional bounds: a definition without them behaves
    /// exactly as it did before this stage.
    #[tokio::test]
    async fn an_unbounded_definition_keeps_firing_and_never_retires() {
        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();
        let id = store.add(bounded_draft(3600, None, None, start), start).id;

        let runner = ControlledRunner::new();
        let runtime = ScheduleRuntime::new_with_clock(
            store.clone(),
            runner.clone(),
            RecordingSink::new(),
            clock.clone(),
        );

        for _ in 0..4 {
            wait_until("a run", || runner.in_flight() > 0).await;
            runner.finish_all(crate::tasks::TaskRunStatus::Completed);
            clock.advance(chrono::Duration::hours(1));
            poke(&store, &id, clock.now());
            settle().await;
        }

        assert!(runner.launches() >= 4, "an unbounded definition keeps firing");
        let definition = current(&store, &id);
        assert!(!definition.is_retired());
        assert_eq!(definition.max_runs, None);
        assert_eq!(definition.expires_at_utc, None);

        runtime.shutdown().await;
    }

    /// A clock jump must not produce a catch-up storm: missed ticks stay
    /// coalesced into a single run, and that run costs exactly one unit of
    /// budget.
    #[tokio::test]
    async fn a_clock_jump_coalesces_into_one_run_and_one_unit_of_budget() {        let start = fake_start();
        let clock = TestClock::new(start);
        let store = ScheduledTaskStore::new();
        let id = store.add(bounded_draft(3600, None, Some(20), start), start).id;

        let runner = ControlledRunner::new();
        let runtime = ScheduleRuntime::new_with_clock(
            store.clone(),
            runner.clone(),
            RecordingSink::new(),
            clock.clone(),
        );

        wait_until("the first run", || runner.launches() == 1).await;
        runner.finish_all(crate::tasks::TaskRunStatus::Completed);
        settle().await;

        // Jump a year: dozens of hourly ticks were missed.
        clock.advance(chrono::Duration::days(365));
        poke(&store, &id, clock.now());
        settle().await;

        assert_eq!(
            runner.launches(),
            2,
            "missed ticks must coalesce into one catch-up run, not a storm"
        );
        assert_eq!(current(&store, &id).runs_started, 2);

        runtime.shutdown().await;
    }
}
