//! The parked state (non-blocking runtime, Phase 2; proposal §6.3, §6.5).
//!
//! An agent whose turn ends while tasks it started are still running does not
//! finish and does not poll: it **parks**. No model call is in flight; the
//! loop sleeps until its inbox receives something — a pushed task completion,
//! a message from the user or its parent — and then runs the next turn with
//! that item delivered. A short debounce after the first wake-up item lets a
//! burst of completions arrive together, so they produce one turn rather than
//! one turn each.
//!
//! An agent with a [`ParkPolicy`] finishes when a turn ends with no live tasks
//! of its own (for a subagent, its final message is then its report). Agents
//! without a policy keep the old rule: the turn ends at the natural stop. The
//! interactive main conversation has no policy — between turns it is parked
//! by the serve main-inbox pump instead.
//!
//! Limits (decision D5): a maximum lifetime and a maximum time parked. When
//! either is reached the agent is woken once with a deadline notice, asked
//! for its (partial) report, and finishes at its next natural stop whatever
//! is still running. One that keeps working past [`DEADLINE_GRACE`] is
//! aborted.

use std::time::Duration;

use coda_llm::Message;
use tokio::time::Instant;
use tokio_util::sync::CancellationToken;

use crate::message::MessageBus;
use crate::steering::SteeringInbox;
use crate::tasks::TaskManager;

use super::AgentError;

/// Longest a subagent may run, parked or not (D5).
pub const SUBAGENT_MAX_LIFETIME: Duration = Duration::from_secs(2 * 60 * 60);
/// Longest a subagent may stay parked without any inbox item (D5).
pub const SUBAGENT_MAX_PARKED: Duration = Duration::from_secs(30 * 60);
/// Pause after the first wake-up item so a burst coalesces into one turn.
pub const WAKE_DEBOUNCE: Duration = Duration::from_secs(2);
/// How long an agent told its deadline was reached may keep working before
/// it is aborted.
pub const DEADLINE_GRACE: Duration = Duration::from_secs(5 * 60);

/// When and how an agent loop parks instead of ending its run.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ParkPolicy {
    /// The task whose children this agent waits for; `None` is the main agent.
    pub owner_task_id: Option<String>,
    pub max_lifetime: Option<Duration>,
    pub max_parked: Option<Duration>,
    pub debounce: Duration,
    pub deadline_grace: Duration,
}

impl ParkPolicy {
    /// The policy every subagent runs under.
    pub fn subagent(task_id: impl Into<String>) -> Self {
        Self {
            owner_task_id: Some(task_id.into()),
            max_lifetime: Some(SUBAGENT_MAX_LIFETIME),
            max_parked: Some(SUBAGENT_MAX_PARKED),
            debounce: WAKE_DEBOUNCE,
            deadline_grace: DEADLINE_GRACE,
        }
    }

    /// Headless `coda run`: the run must not end while work it started is
    /// still running, because the engine exits with it.
    pub fn headless_main() -> Self {
        Self {
            owner_task_id: None,
            max_lifetime: None,
            max_parked: Some(SUBAGENT_MAX_PARKED),
            debounce: WAKE_DEBOUNCE,
            deadline_grace: DEADLINE_GRACE,
        }
    }
}

/// Per-run bookkeeping for the limits.
pub(crate) struct ParkState {
    started: Instant,
    finishing_since: Option<Instant>,
}

impl ParkState {
    pub(crate) fn new() -> Self {
        Self { started: Instant::now(), finishing_since: None }
    }
}

/// What the loop does after a natural stop.
#[derive(Debug, PartialEq, Eq)]
pub(crate) enum ParkOutcome {
    /// Something arrived (or a deadline passed): run another turn.
    Resume,
    /// Nothing is left to wait for: the run ends.
    Finish,
}

/// Everything a parked loop watches.
pub(crate) struct Parker<'a> {
    pub policy: &'a ParkPolicy,
    pub tasks: &'a TaskManager,
    pub steering: Option<&'a SteeringInbox>,
    /// The main inbox, only for the main context.
    pub main_inbox: Option<&'a MessageBus>,
}

impl Parker<'_> {
    fn inbox_has_any(&self) -> bool {
        self.steering.is_some_and(SteeringInbox::has_any)
            || self.main_inbox.is_some_and(MessageBus::has_pending_main)
    }

    async fn inbox_wait(&self) {
        let steering = async {
            match self.steering {
                Some(inbox) => inbox.wait_for_any().await,
                None => std::future::pending().await,
            }
        };
        let main = async {
            match self.main_inbox {
                Some(bus) => bus.wait_for_main().await,
                None => std::future::pending().await,
            }
        };
        tokio::select! {
            _ = steering => {}
            _ = main => {}
        }
    }

    fn reopen(&self) {
        if let Some(inbox) = self.steering {
            inbox.open_for_turn();
        }
    }

    /// Called at the top of every iteration: enforces the lifetime limit
    /// while the agent is working rather than parked.
    pub(crate) fn check_deadlines(
        &self,
        state: &mut ParkState,
        history: &mut Vec<Message>,
    ) -> Result<(), AgentError> {
        if let Some(since) = state.finishing_since {
            if since.elapsed() >= self.policy.deadline_grace {
                return Err(AgentError::Aborted {
                    reason: "its time limit was reached and it did not report within the grace period"
                        .into(),
                });
            }
            return Ok(());
        }
        if let Some(lifetime) = self.policy.max_lifetime {
            if state.started.elapsed() >= lifetime {
                self.deliver_deadline(state, history, "maximum lifetime", lifetime);
            }
        }
        Ok(())
    }

    fn deliver_deadline(
        &self,
        state: &mut ParkState,
        history: &mut Vec<Message>,
        what: &str,
        limit: Duration,
    ) {
        let live: Vec<String> = self
            .tasks
            .live_children(self.policy.owner_task_id.as_deref())
            .iter()
            .map(|t| t.id.clone())
            .collect();
        let running = if live.is_empty() {
            String::new()
        } else {
            format!(
                " Tasks still running ({}) will be stopped when you finish.",
                live.join(", ")
            )
        };
        history.push(Message::user(format!(
            "[deadline] You have reached your {what} ({} min). Do not start new work. Reply now \
             with your final report: what you completed, what you found, and what remains \
             unfinished.{running}",
            limit.as_secs() / 60
        )));
        state.finishing_since = Some(Instant::now());
    }

    /// Decides what happens after a turn ended naturally: finish, or park
    /// until an inbox item (or a deadline) wakes the agent for another turn.
    pub(crate) async fn at_natural_stop(
        &self,
        state: &mut ParkState,
        history: &mut Vec<Message>,
        cancel: &CancellationToken,
    ) -> Result<ParkOutcome, AgentError> {
        if state.finishing_since.is_some() {
            return Ok(ParkOutcome::Finish);
        }
        let parked_at = Instant::now();
        loop {
            let settled = self.tasks.settle_signal().notified();
            tokio::pin!(settled);
            settled.as_mut().enable();

            if self.inbox_has_any() {
                self.reopen();
                return Ok(ParkOutcome::Resume);
            }
            if self.tasks.live_children(self.policy.owner_task_id.as_deref()).is_empty() {
                // A child settles only after its completion is in the inbox,
                // so re-checking here cannot miss a report that raced in.
                if self.inbox_has_any() {
                    self.reopen();
                    return Ok(ParkOutcome::Resume);
                }
                return Ok(ParkOutcome::Finish);
            }

            // Parked: accept messages, make no model calls.
            self.reopen();
            let (limit_name, limit, remaining) = self.park_budget(state, parked_at);
            let expired = async {
                match remaining {
                    Some(d) => tokio::time::sleep(d).await,
                    None => std::future::pending().await,
                }
            };
            tokio::select! {
                biased;
                _ = cancel.cancelled() => return Err(AgentError::Cancelled),
                _ = self.inbox_wait() => {
                    tokio::select! {
                        _ = cancel.cancelled() => return Err(AgentError::Cancelled),
                        _ = tokio::time::sleep(self.policy.debounce) => {}
                    }
                    return Ok(ParkOutcome::Resume);
                }
                _ = &mut settled => continue,
                _ = expired => {
                    self.deliver_deadline(state, history, limit_name, limit);
                    return Ok(ParkOutcome::Resume);
                }
            }
        }
    }

    /// The nearer of the parked-time and lifetime limits, with its name.
    fn park_budget(
        &self,
        state: &ParkState,
        parked_at: Instant,
    ) -> (&'static str, Duration, Option<Duration>) {
        let parked = self
            .policy
            .max_parked
            .map(|d| ("maximum time waiting", d, d.saturating_sub(parked_at.elapsed())));
        let lifetime = self
            .policy
            .max_lifetime
            .map(|d| ("maximum lifetime", d, d.saturating_sub(state.started.elapsed())));
        match (parked, lifetime) {
            (Some(p), Some(l)) => {
                let nearer = if l.2 < p.2 { l } else { p };
                (nearer.0, nearer.1, Some(nearer.2))
            }
            (Some(x), None) | (None, Some(x)) => (x.0, x.1, Some(x.2)),
            (None, None) => ("", Duration::ZERO, None),
        }
    }
}

