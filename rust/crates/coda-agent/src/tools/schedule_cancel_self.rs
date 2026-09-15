//! `schedule_cancel_self` — a scheduled run retires its own definition.
//!
//! # Why this tool takes no target
//! The whole point of a bounded schedule is that it can stop when its own
//! condition is met ("watch the queue every hour until it drains"). The agent
//! that knows the condition is met is the one running *inside* the scheduled
//! job — but that agent is a child, and children are deliberately denied
//! `schedule_create` / `schedule_delete` so a run cannot manufacture or destroy
//! arbitrary automations.
//!
//! This tool is the single, narrow exception, and it is narrow by *shape*, not
//! by validation: it accepts no schedule id and no task id at all. There is
//! nothing for a model to put in the arguments that could point it at another
//! definition, at its parent, or at a sibling. The identity it acts on is
//! derived entirely from the trusted [`ScheduleOrigin`] the runtime stamped on
//! the run plus the caller's own registered task, neither of which is
//! model-supplied.
//!
//! # Who may call it
//! Only the **root** agent of a scheduled run: the task the schedule runtime
//! registered, which is `TaskKind::Scheduled` and has no parent. A nested
//! subagent inside a scheduled job inherits the origin (so it can *see* its own
//! definition) but is not the run, and must return its conclusion to the run
//! that spawned it instead. Least privilege: the deeper the agent, the less
//! reason it has to end an automation on everyone's behalf.

use async_trait::async_trait;
use chrono::Utc;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::scheduling::{sanitize_note, ScheduleRetirement, ScheduleRetirementReason};
use crate::tasks::{TaskActionResult, TaskKind};
use crate::tool::ToolContextServiceExt as _;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

/// One refusal wording for every failed authorization check.
///
/// A caller that is not a scheduled run learns only that it is not a scheduled
/// run — never whether the definition named by some origin exists, which would
/// turn this into an existence probe.
const NOT_A_SCHEDULED_RUN: &str = "schedule_cancel_self is only available to a scheduled run, \
     acting on its own schedule. If you are a subagent, return your conclusion to the task that \
     started you instead.";

pub struct ScheduleCancelSelfTool;

#[async_trait]
impl Tool for ScheduleCancelSelfTool {
    fn name(&self) -> &str {
        "schedule_cancel_self"
    }

    fn description(&self) -> &str {
        "Stop the recurring schedule that started THIS run, once its job is done (for example, \
         a watcher whose condition has finally been met). Takes no schedule id: it always acts \
         on your own schedule and can never affect another one. By default the current run \
         finishes normally and only future runs are cancelled; pass stopRunning=true to also \
         end this run immediately."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "stopRunning":{"type":"boolean","default":false,"description":"Also end the current run immediately instead of letting it finish."},
            "reason":{"type":"string","description":"Short note recorded with the cancellation."}
          }
        }"#
    }

    fn is_read_only(&self) -> bool {
        false
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        // 1. The run must carry a trusted scheduled origin. This is stamped by
        //    the schedule runtime and propagated by the subagent host; it is
        //    never parsed from tool arguments or model output.
        let Some(origin) = ctx.schedule_origin.as_ref() else {
            return ToolResult::error(NOT_A_SCHEDULED_RUN);
        };

        // 2. The caller must be a registered task. A context with an origin but
        //    no verifiable actor grants nothing.
        let Some(caller_id) = ctx.caller_task_id.as_deref() else {
            return ToolResult::error(NOT_A_SCHEDULED_RUN);
        };
        let Some(manager) = ctx.get_task_manager() else {
            return ToolResult::error(NOT_A_SCHEDULED_RUN);
        };
        let Some(caller) = manager.get(caller_id) else {
            return ToolResult::error(NOT_A_SCHEDULED_RUN);
        };

        // 3. The caller must be the scheduled run itself — the task the
        //    schedule runtime registered. `TaskKind::Scheduled` with no parent
        //    is that identity, and it comes from the task registry rather than
        //    from anything the model can influence. A nested subagent inherits
        //    the origin but is `TaskKind::Subagent` with a parent, so it is
        //    refused here.
        //
        //    Note this deliberately does NOT consult the schedule runtime's
        //    published view: a fast child can call this before the runtime has
        //    published its active task id, and refusing a legitimate self-cancel
        //    because of a publication race would be its own bug.
        if caller.kind != TaskKind::Scheduled || caller.parent_id.is_some() {
            return ToolResult::error(NOT_A_SCHEDULED_RUN);
        }

        let Some(store) = ctx.get_schedule_store() else {
            return ToolResult::error("Schedule store is not available.");
        };

        let stop_running = input
            .get("stopRunning")
            .and_then(Value::as_bool)
            .unwrap_or(false);
        let note = input
            .get("reason")
            .and_then(Value::as_str)
            .and_then(sanitize_note);

        let now = Utc::now();
        let outcome = store.update(&origin.definition_id, |definition| {
            match definition.retirement.as_ref() {
                // Repeated calls are idempotent: the first recorded reason wins
                // and is never relabelled by a later one.
                Some(existing) => CancelOutcome::AlreadyRetired(existing.reason),
                None => {
                    definition.retirement = Some(ScheduleRetirement {
                        reason: ScheduleRetirementReason::Cancelled,
                        retired_at_utc: now,
                        note: note.clone(),
                    });
                    definition.updated_at_utc = now;
                    CancelOutcome::Retired
                }
            }
        });

        let mut message = match outcome {
            // The definition is already gone (deleted by the main agent while
            // this run was working). Nothing to cancel, and nothing to
            // resurrect — reported as success because the caller's intent
            // ("do not run again") already holds.
            None => "This schedule no longer exists; no future runs were scheduled.".to_owned(),
            Some(CancelOutcome::AlreadyRetired(reason)) => format!(
                "This schedule was already retired ({}); no further runs will start.",
                reason.as_wire()
            ),
            Some(CancelOutcome::Retired) => {
                "This schedule is cancelled; no further runs will start.".to_owned()
            }
        };

        if stop_running {
            // Narrow self-stop: the only id passed is the caller's own,
            // already proved above. The token this cancels is the one the
            // schedule runner handed to this run, so the agent loop observes
            // it and the task reaches a truthful `Stopped` terminal state.
            match manager.request_self_stop(caller_id) {
                TaskActionResult::Ok => {
                    message.push_str(" This run is stopping now.");
                }
                _ => {
                    message.push_str(" This run was already finishing.");
                }
            }
        } else {
            message.push_str(" This run continues to completion.");
        }

        ToolResult::ok(message)
    }
}

enum CancelOutcome {
    Retired,
    AlreadyRetired(ScheduleRetirementReason),
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::scheduling::{
        ScheduleDefinitionDraft, ScheduleKind, ScheduledTaskStore,
    };
    use crate::tasks::{TaskExecutionMode, TaskManager};
    use crate::tool::ScheduleOrigin;
    use std::sync::Arc;
    use std::time::Duration;

    fn add(store: &Arc<ScheduledTaskStore>, prompt: &str) -> String {
        store
            .add(
                ScheduleDefinitionDraft {
                    name: Some("watcher".into()),
                    kind: ScheduleKind::Interval,
                    prompt: prompt.into(),
                    interval: Some(Duration::from_secs(3600)),
                    at_utc: None,
                    cron: None,
                    time_zone_id: "UTC".into(),
                    next_run_utc: Utc::now() + chrono::Duration::hours(1),
                    expires_at_utc: None,
                    max_runs: None,
                },
                Utc::now(),
            )
            .id
    }

    struct Fixture {
        store: Arc<ScheduledTaskStore>,
        manager: Arc<TaskManager>,
        own_id: String,
        foreign_id: String,
        run_task: String,
        _dir: tempfile::TempDir,
    }

    /// A scheduled root run, its own definition, and a foreign definition that
    /// must remain untouched no matter what the run does.
    fn fixture(label: &str) -> Fixture {
        let dir = tempfile::tempdir().unwrap();
        let manager = TaskManager::new(label, Some(dir.path().to_owned()), 4096, 16);
        let store = ScheduledTaskStore::new();
        let own_id = add(&store, "OWN_JOB_PROMPT");
        let foreign_id = add(&store, "FOREIGN_JOB_PROMPT");
        let run = manager
            .register(
                TaskKind::Scheduled,
                "Scheduled: watcher",
                None,
                TaskExecutionMode::Background,
            )
            .unwrap();
        Fixture {
            store,
            manager,
            own_id,
            foreign_id,
            run_task: run.id.clone(),
            _dir: dir,
        }
    }

    fn scheduled_ctx(f: &Fixture) -> ToolContext {
        ToolContext::new(".")
            .with_schedule_store(Arc::clone(&f.store))
            .with_task_manager(Arc::clone(&f.manager))
            .with_caller_task_id(&f.run_task)
            .with_schedule_origin(ScheduleOrigin::new(f.own_id.clone(), Some("watcher".into())))
    }

    async fn call(ctx: &ToolContext, input: Value) -> ToolOutcome {
        ScheduleCancelSelfTool
            .execute(&input, ctx, CancellationToken::new())
            .await
    }

    fn definition(f: &Fixture, id: &str) -> crate::scheduling::ScheduledTask {
        f.store.items().into_iter().find(|t| t.id == id).unwrap()
    }

    // ── happy path ────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn a_scheduled_root_run_retires_its_own_definition() {
        let f = fixture("cancel-self-own");
        let result = call(&scheduled_ctx(&f), serde_json::json!({})).await;

        assert!(!result.is_error, "{}", result.content);
        let own = definition(&f, &f.own_id);
        assert_eq!(
            own.retirement.unwrap().reason,
            ScheduleRetirementReason::Cancelled
        );
        assert!(
            !definition(&f, &f.foreign_id).is_retired(),
            "another definition must never be touched"
        );
    }

    /// The default must not interrupt the model mid-thought: cancelling future
    /// runs is a scheduling decision, not a reason to throw away this run's
    /// work.
    #[tokio::test]
    async fn by_default_the_current_run_is_left_alone() {
        let f = fixture("cancel-self-default");
        let task = f.manager.find_task(&f.run_task).unwrap();

        let result = call(&scheduled_ctx(&f), serde_json::json!({})).await;

        assert!(!result.is_error, "{}", result.content);
        assert!(
            !task.cancel.is_cancelled(),
            "the default must let the current run finish"
        );
        assert!(result.content.contains("continues to completion"), "{}", result.content);
    }

    /// `stopRunning` cancels the run's own token — the same token the schedule
    /// runner handed to the agent loop — so the run really stops rather than
    /// being reported as stopped.
    #[tokio::test]
    async fn stop_running_cancels_this_runs_own_token() {
        let f = fixture("cancel-self-stop");
        let task = f.manager.find_task(&f.run_task).unwrap();

        let result = call(&scheduled_ctx(&f), serde_json::json!({"stopRunning": true})).await;

        assert!(!result.is_error, "{}", result.content);
        assert!(
            task.cancel.is_cancelled(),
            "stopRunning must cancel the token the runner gave this run"
        );
        assert!(result.content.contains("stopping now"), "{}", result.content);
    }

    /// A generic `request_stop(self, Some(self))` is denied by design, so the
    /// self-stop path must not be built on it.
    #[tokio::test]
    async fn the_generic_stop_path_still_denies_a_task_naming_itself() {
        let f = fixture("cancel-self-generic-denied");
        assert_eq!(
            f.manager.request_stop(&f.run_task, Some(&f.run_task)),
            TaskActionResult::Denied,
            "the generic authorization model must keep excluding self"
        );
        assert_eq!(
            f.manager.request_self_stop(&f.run_task),
            TaskActionResult::Ok,
            "the narrow self-stop capability is what makes stopRunning work"
        );
    }

    #[tokio::test]
    async fn repeated_self_cancellation_is_idempotent_and_keeps_the_first_reason() {
        let f = fixture("cancel-self-idempotent");
        let ctx = scheduled_ctx(&f);

        let first = call(&ctx, serde_json::json!({"reason": "queue drained"})).await;
        let retired_at = definition(&f, &f.own_id).retirement.unwrap().retired_at_utc;

        let second = call(&ctx, serde_json::json!({"reason": "different story"})).await;

        assert!(!first.is_error && !second.is_error, "{}", second.content);
        let retirement = definition(&f, &f.own_id).retirement.unwrap();
        assert_eq!(retirement.reason, ScheduleRetirementReason::Cancelled);
        assert_eq!(retirement.retired_at_utc, retired_at, "the first record stands");
        assert_eq!(retirement.note.as_deref(), Some("queue drained"));
        assert!(second.content.contains("already retired"), "{}", second.content);
    }

    #[tokio::test]
    async fn a_reason_is_sanitized_and_clamped_before_it_is_recorded() {
        let f = fixture("cancel-self-reason");
        let noisy = format!("line\nbreak\ttab {}", "x".repeat(400));
        call(&scheduled_ctx(&f), serde_json::json!({"reason": noisy})).await;

        let note = definition(&f, &f.own_id).retirement.unwrap().note.unwrap();
        assert!(!note.contains('\n') && !note.contains('\t'), "{note}");
        assert!(note.chars().count() <= 160, "{}", note.chars().count());
    }

    /// The definition was deleted by the main agent while the run worked. The
    /// caller's intent already holds, and nothing may be resurrected.
    #[tokio::test]
    async fn cancelling_an_already_deleted_definition_creates_nothing() {
        let f = fixture("cancel-self-deleted");
        f.store.remove(&f.own_id);

        let result = call(&scheduled_ctx(&f), serde_json::json!({})).await;

        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains("no longer exists"), "{}", result.content);
        assert_eq!(f.store.items().len(), 1, "only the foreign definition remains");
        assert!(f.store.items().iter().all(|t| t.id != f.own_id));
    }

    // ── authorization ─────────────────────────────────────────────────────────

    /// Forged arguments cannot redirect the tool: it reads nothing from them
    /// but two flags, and the identity comes from the trusted context.
    #[tokio::test]
    async fn forged_target_arguments_are_ignored_entirely() {
        let f = fixture("cancel-self-forged");
        let forged = serde_json::json!({
            "scheduleId": f.foreign_id,
            "definitionId": f.foreign_id,
            "taskId": "task-0001",
            "scheduleOrigin": {"definitionId": f.foreign_id},
            "stopRunning": false,
        });

        let result = call(&scheduled_ctx(&f), forged).await;

        assert!(!result.is_error, "{}", result.content);
        assert!(
            definition(&f, &f.own_id).is_retired(),
            "the caller's own definition is the only thing it can affect"
        );
        assert!(
            !definition(&f, &f.foreign_id).is_retired(),
            "a forged id must never reach another definition"
        );
    }

    /// The main agent has no scheduled origin. It has `schedule_delete` for
    /// this; it must not fall into the self-cancel path.
    #[tokio::test]
    async fn a_run_with_no_scheduled_origin_is_refused() {
        let f = fixture("cancel-self-no-origin");
        let ctx = ToolContext::new(".")
            .with_schedule_store(Arc::clone(&f.store))
            .with_task_manager(Arc::clone(&f.manager))
            .with_caller_task_id(&f.run_task);

        let result = call(&ctx, serde_json::json!({})).await;

        assert!(result.is_error);
        assert!(!definition(&f, &f.own_id).is_retired());
        assert!(!definition(&f, &f.foreign_id).is_retired());
    }

    /// An origin with no verifiable actor behind it grants nothing.
    #[tokio::test]
    async fn an_unregistered_caller_is_refused_even_with_a_valid_origin() {
        let f = fixture("cancel-self-unknown-caller");
        let ctx = ToolContext::new(".")
            .with_schedule_store(Arc::clone(&f.store))
            .with_task_manager(Arc::clone(&f.manager))
            .with_caller_task_id("task-9999")
            .with_schedule_origin(ScheduleOrigin::new(f.own_id.clone(), None));

        let result = call(&ctx, serde_json::json!({})).await;

        assert!(result.is_error);
        assert!(!definition(&f, &f.own_id).is_retired());
    }

    #[tokio::test]
    async fn a_context_with_no_task_manager_is_refused() {
        let f = fixture("cancel-self-no-manager");
        let ctx = ToolContext::new(".")
            .with_schedule_store(Arc::clone(&f.store))
            .with_caller_task_id(&f.run_task)
            .with_schedule_origin(ScheduleOrigin::new(f.own_id.clone(), None));

        let result = call(&ctx, serde_json::json!({})).await;

        assert!(result.is_error);
        assert!(!definition(&f, &f.own_id).is_retired());
    }

    /// A nested subagent inside a scheduled job inherits the origin, but it is
    /// not the run. Least privilege: it reports back instead of ending an
    /// automation on everyone's behalf.
    #[tokio::test]
    async fn a_nested_subagent_of_a_scheduled_run_is_refused() {
        let f = fixture("cancel-self-nested");
        let child = f
            .manager
            .register(
                TaskKind::Subagent,
                "nested child",
                Some(&f.run_task),
                TaskExecutionMode::Foreground,
            )
            .unwrap();

        let ctx = ToolContext::new(".")
            .with_schedule_store(Arc::clone(&f.store))
            .with_task_manager(Arc::clone(&f.manager))
            .with_caller_task_id(&child.id)
            .with_schedule_origin(ScheduleOrigin::new(f.own_id.clone(), None));

        let result = call(&ctx, serde_json::json!({"stopRunning": true})).await;

        assert!(result.is_error, "a nested child must not retire the definition");
        assert!(result.content.contains("subagent"), "{}", result.content);
        assert!(!definition(&f, &f.own_id).is_retired());
        assert!(
            !f.manager.find_task(&f.run_task).unwrap().cancel.is_cancelled(),
            "and it must certainly not stop its parent's run"
        );
    }

    /// An ordinary background subagent that somehow carries an origin but is
    /// not a scheduled root is refused for the same reason.
    #[tokio::test]
    async fn an_ordinary_subagent_with_a_foreign_origin_is_refused() {
        let f = fixture("cancel-self-foreign-origin");
        let other = f
            .manager
            .register(
                TaskKind::Subagent,
                "unrelated worker",
                None,
                TaskExecutionMode::Background,
            )
            .unwrap();

        let ctx = ToolContext::new(".")
            .with_schedule_store(Arc::clone(&f.store))
            .with_task_manager(Arc::clone(&f.manager))
            .with_caller_task_id(&other.id)
            .with_schedule_origin(ScheduleOrigin::new(f.foreign_id.clone(), None));

        let result = call(&ctx, serde_json::json!({})).await;

        assert!(result.is_error);
        assert!(!definition(&f, &f.foreign_id).is_retired());
        assert!(!definition(&f, &f.own_id).is_retired());
    }

    /// Every refusal says the same thing, so a caller cannot use the wording to
    /// learn whether some other definition exists.
    #[tokio::test]
    async fn every_refusal_uses_the_same_wording() {
        let f = fixture("cancel-self-uniform-refusal");
        let child = f
            .manager
            .register(TaskKind::Subagent, "child", Some(&f.run_task), TaskExecutionMode::Foreground)
            .unwrap();

        let contexts = vec![
            ToolContext::new(".")
                .with_schedule_store(Arc::clone(&f.store))
                .with_task_manager(Arc::clone(&f.manager))
                .with_caller_task_id(&f.run_task),
            ToolContext::new(".")
                .with_schedule_store(Arc::clone(&f.store))
                .with_task_manager(Arc::clone(&f.manager))
                .with_caller_task_id("task-9999")
                .with_schedule_origin(ScheduleOrigin::new("nonexistent-definition", None)),
            ToolContext::new(".")
                .with_schedule_store(Arc::clone(&f.store))
                .with_task_manager(Arc::clone(&f.manager))
                .with_caller_task_id(&child.id)
                .with_schedule_origin(ScheduleOrigin::new(f.own_id.clone(), None)),
        ];

        for ctx in &contexts {
            let result = call(ctx, serde_json::json!({})).await;
            assert!(result.is_error);
            assert_eq!(result.content, NOT_A_SCHEDULED_RUN, "refusals must be indistinguishable");
        }
    }
}
