//! The cooperative safe point (planned restart).
//!
//! A subagent asked to suspend pauses only where its conversation is
//! complete: at the top of a loop iteration (no model stream in flight, the
//! last tool batch's results already in history) or while parked. There it
//! leaves a [`TaskCheckpoint`] of its history, turns `Suspended`, and waits —
//! making no model calls — until the request is withdrawn or the run is
//! cancelled.

use std::time::SystemTime;

use coda_llm::Message;
use tokio_util::sync::CancellationToken;

use crate::steering::SteeringInbox;
use crate::tasks::{TaskCheckpoint, TaskManager};

use super::AgentError;

/// Pauses `task_id` here when its suspension is requested. Returns whether
/// it was suspended (and has since been resumed); `Err(Cancelled)` when the
/// run is cancelled while suspended.
pub(crate) async fn safe_point(
    tasks: &TaskManager,
    task_id: &str,
    history: &[Message],
    steps_used: usize,
    parked: bool,
    steering: Option<&SteeringInbox>,
    cancel: &CancellationToken,
) -> Result<bool, AgentError> {
    if !tasks.is_suspension_requested(task_id) {
        return Ok(false);
    }
    let checkpoint = TaskCheckpoint {
        history: history.to_vec(),
        steps_used,
        parked,
        suspended_at: SystemTime::now(),
    };
    if !tasks.mark_suspended(task_id, checkpoint) {
        return Ok(false);
    }
    loop {
        let changed = tasks.suspend_signal().notified();
        tokio::pin!(changed);
        changed.as_mut().enable();
        if !tasks.is_suspension_requested(task_id) {
            break;
        }
        tokio::select! {
            biased;
            _ = cancel.cancelled() => return Err(AgentError::Cancelled),
            _ = &mut changed => {}
        }
    }
    if cancel.is_cancelled() {
        return Err(AgentError::Cancelled);
    }
    tasks.mark_resumed(task_id);
    if let Some(inbox) = steering {
        inbox.open_for_turn();
    }
    Ok(true)
}
