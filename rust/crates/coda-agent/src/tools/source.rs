//! Shared trusted-source resolution for tools that publish onto the engine
//! [`crate::message::MessageBus`] (`notify_user`, `ask_main`).
//!
//! Extracted verbatim from `notify_user`'s original inline resolver so both
//! tools share one identity-resolution path rather than drifting apart —
//! `notify_user`'s own behaviour and tests are unchanged by this move.

use crate::message::MessageSource;
use crate::tool::{ToolContext, ToolContextServiceExt as _};

/// Resolve the trusted [`MessageSource`] for this call, or a user-facing
/// refusal message when the identity cannot be positively established.
///
/// Fails closed: every branch that cannot prove a specific identity refuses
/// rather than defaulting to `Main`. Precedence:
///
/// 1. `ctx.schedule_origin` is set → this run was launched by the schedule
///    runtime (directly or as a nested child of one); the message is
///    attributed to that scheduled definition. The caller's own task id is
///    still independently verified against `ctx.get_task_manager()` — a
///    nested child of a scheduled run carries the same `schedule_origin` as
///    its ancestor but its own task id, and it is that own id that must be
///    attributed, never the root's.
/// 2. `ctx.caller_task_id` is set and resolves to a real, currently-known
///    task in `ctx.get_task_manager()` → this run is a subagent; the message
///    is attributed to that task's own label.
/// 3. `ctx.is_main_context` is `true` (and neither of the above applied) →
///    the trusted top-level conversation; attributed as `main`.
/// 4. Anything else (no manager wired, an unknown/foreign task id, no main
///    marker) is refused outright — **fails closed**.
pub(crate) fn resolve_trusted_source(ctx: &ToolContext) -> Result<MessageSource, String> {
    if let Some(origin) = &ctx.schedule_origin {
        let Some(task_id) = ctx.caller_task_id.clone() else {
            return Err(
                "Scheduled context is missing its own task id; refusing to attribute this \
                 notification."
                    .into(),
            );
        };
        let Some(manager) = ctx.get_task_manager() else {
            return Err(
                "No task manager is available to verify this scheduled run's identity; \
                 refusing to attribute this notification."
                    .into(),
            );
        };
        if manager.get(&task_id).is_none() {
            return Err(format!(
                "Task '{task_id}' is not known to the task manager; refusing to attribute this \
                 scheduled notification."
            ));
        }
        return Ok(MessageSource::ScheduledTask {
            definition_id: origin.definition_id.clone(),
            definition_name: origin.definition_name.clone(),
            task_id,
        });
    }

    if let Some(task_id) = &ctx.caller_task_id {
        let Some(manager) = ctx.get_task_manager() else {
            return Err(
                "No task manager is available to verify this subagent's identity; refusing to \
                 attribute this notification."
                    .into(),
            );
        };
        let Some(snapshot) = manager.get(task_id) else {
            return Err(format!(
                "Task '{task_id}' is not known to the task manager; refusing to attribute this \
                 notification."
            ));
        };
        return Ok(MessageSource::Subagent { task_id: task_id.clone(), label: snapshot.description });
    }

    if ctx.is_main_context {
        return Ok(MessageSource::Main);
    }

    Err(
        "This context has no verifiable identity (no schedule origin, no known caller task, and \
         no trusted main marker); refusing to send a notification rather than guessing who it is \
         from."
            .into(),
    )
}
