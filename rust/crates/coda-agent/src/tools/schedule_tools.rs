//! `schedule_list` — list all scheduled task definitions.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::scheduling::ScheduleKind;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

pub struct ScheduleListTool;

#[async_trait]
impl Tool for ScheduleListTool {
    fn name(&self) -> &str {
        "schedule_list"
    }

    fn description(&self) -> &str {
        "List all scheduled task definitions with their id, kind, next run time, and prompt."
    }

    fn input_schema_json(&self) -> &str {
        r#"{"type":"object","properties":{}}"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(&self, _input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        let store = match ctx.get_schedule_store() {
            Some(s) => s,
            None => return ToolResult::error("Schedule store is not available."),
        };

        let items = store.items();

        // Main agent (no caller identity) sees every definition, unchanged.
        let Some(caller_id) = ctx.caller_task_id.as_deref() else {
            return format_items(&items);
        };

        // POLICY: a child never gets full visibility. It may see AT MOST the
        // one definition it is currently running for (its trusted
        // scheduled-run origin) — never another job's prompt. The origin can
        // only come from the trusted `ToolContext`, never from tool
        // arguments or model output, so a forged JSON field has no effect.
        let origin = match &ctx.schedule_origin {
            Some(o) => o,
            None => {
                return ToolResult::error(
                    "schedule_list is not available to this task: no scheduled-run origin.",
                );
            }
        };

        // The caller identity itself must be verified against the task
        // manager; an unknown/unregistered/unverifiable caller must not gain
        // any rights just because a `schedule_origin` happens to be set.
        let mgr = match ctx.get_task_manager() {
            Some(m) => m,
            None => {
                return ToolResult::error(
                    "schedule_list is not available to this task: the calling task cannot be verified.",
                );
            }
        };
        if mgr.get(caller_id).is_none() {
            return ToolResult::error(
                "schedule_list is not available to this task: the calling task cannot be verified.",
            );
        }

        match items.iter().find(|t| t.id == origin.definition_id) {
            Some(t) => format_items(std::slice::from_ref(t)),
            None => ToolResult::ok("No scheduled tasks accessible to this task."),
        }
    }
}

/// Formats a list of scheduled definitions (or a single, origin-scoped one)
/// into the tool's text response. Shared by the main-agent full listing and
/// the child's own-origin-only listing so both paths render identically.
fn format_items(items: &[crate::scheduling::ScheduledTask]) -> ToolOutcome {
    if items.is_empty() {
        return ToolResult::ok("No scheduled tasks.");
    }

    let mut lines = Vec::new();
    for t in items {
        let kind = match t.kind {
            ScheduleKind::Interval => "interval",
            ScheduleKind::At => "at",
            ScheduleKind::Cron => "cron",
        };
        let label = t.name.as_deref().unwrap_or("(unnamed)");
        let next = t.next_run_utc.format("%Y-%m-%dT%H:%M:%SZ");
        lines.push(format!(
            "{} [{kind}] \"{label}\" next={next} — {}",
            t.id,
            t.prompt.chars().take(60).collect::<String>()
        ));
        if let Some(ref outcome) = t.last_terminal_outcome {
            lines.push(format!(
                "  last: {:?} at {}",
                outcome.outcome,
                outcome.completed_at_utc.format("%Y-%m-%dT%H:%M:%SZ")
            ));
        }
    }

    ToolResult::ok(lines.join("\n"))
}

/// `schedule_delete` — delete a scheduled task definition.
pub struct ScheduleDeleteTool;

#[async_trait]
impl Tool for ScheduleDeleteTool {
    fn name(&self) -> &str {
        "schedule_delete"
    }

    fn description(&self) -> &str {
        "Delete a scheduled task definition by id. Does not stop an already-running execution."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "scheduleId":{"type":"string","description":"The schedule id to delete"}
          },
          "required":["scheduleId"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        false
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        // POLICY: arbitrary schedule management is main-agent only. A trusted
        // scheduled-run origin does NOT grant delete rights either — the only
        // origin-scoped self-action is a future, targetless self-cancel.
        // Reject before any lookup or mutation.
        if ctx.caller_task_id.is_some() {
            return ToolResult::error("schedule_delete is only available to the main agent.");
        }

        let id = match input.get("scheduleId").and_then(Value::as_str) {
            Some(id) => id,
            None => return ToolResult::error("Missing required 'scheduleId'."),
        };

        let store = match ctx.get_schedule_store() {
            Some(s) => s,
            None => return ToolResult::error("Schedule store is not available."),
        };

        if store.remove(id) {
            ToolResult::ok(format!("Schedule '{id}' deleted."))
        } else {
            ToolResult::error(format!("Schedule '{id}' not found."))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::scheduling::{ScheduleDefinitionDraft, ScheduledTaskStore};
    use crate::tasks::{TaskExecutionMode, TaskKind, TaskManager};
    use crate::tool::ScheduleOrigin;
    use std::sync::Arc;
    use std::time::Duration;

    fn ctx(store: Arc<ScheduledTaskStore>) -> ToolContext {
        ToolContext::new(".").with_schedule_store(store)
    }

    fn ctx_with_caller(store: Arc<ScheduledTaskStore>, caller_id: &str) -> ToolContext {
        ToolContext::new(".")
            .with_schedule_store(store)
            .with_caller_task_id(caller_id)
    }

    fn add_named(store: &Arc<ScheduledTaskStore>, prompt: &str) -> String {
        let draft = ScheduleDefinitionDraft {
            name: Some("test".into()),
            kind: ScheduleKind::Interval,
            prompt: prompt.into(),
            interval: Some(Duration::from_secs(3600)),
            at_utc: None,
            cron: None,
            time_zone_id: "UTC".into(),
            next_run_utc: chrono::Utc::now() + chrono::Duration::hours(1),
        };
        store.add(draft, chrono::Utc::now()).id
    }

    fn add(store: &Arc<ScheduledTaskStore>) -> String {
        add_named(store, "do something")
    }

    #[tokio::test]
    async fn list_empty_returns_message() {
        let s = ScheduledTaskStore::new();
        let result = ScheduleListTool
            .execute(&Value::Object(Default::default()), &ctx(s), CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("No scheduled"), "{}", result.content);
    }

    #[tokio::test]
    async fn list_shows_schedule() {
        let s = ScheduledTaskStore::new();
        let id = add(&s);
        let result = ScheduleListTool
            .execute(&Value::Object(Default::default()), &ctx(s), CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains(&id), "{}", result.content);
        assert!(result.content.contains("interval"), "{}", result.content);
    }

    #[tokio::test]
    async fn delete_known_schedule() {
        let s = ScheduledTaskStore::new();
        let id = add(&s);
        let result = ScheduleDeleteTool
            .execute(
                &serde_json::json!({"scheduleId": id}),
                &ctx(s.clone()),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert!(s.items().is_empty());
    }

    #[tokio::test]
    async fn delete_unknown_schedule_returns_error() {
        let s = ScheduledTaskStore::new();
        let result = ScheduleDeleteTool
            .execute(
                &serde_json::json!({"scheduleId": "nonexistent"}),
                &ctx(s),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    // ── POLICY: schedule management is main-agent only ───────────────────────

    /// A child (subagent) must be refused before any mutation, even though the
    /// schedule store is injected into its tool context.
    #[tokio::test]
    async fn child_caller_cannot_delete_schedule_store_unchanged() {
        let s = ScheduledTaskStore::new();
        let id = add(&s);
        let result = ScheduleDeleteTool
            .execute(
                &serde_json::json!({"scheduleId": id}),
                &ctx_with_caller(s.clone(), "task-0001"),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error, "children must be refused");
        assert_eq!(s.items().len(), 1, "the schedule must remain untouched");
    }

    /// Even a trusted scheduled-run origin must not grant delete rights —
    /// only a dedicated, targetless self-action (Stage 1) may act on its
    /// own origin.
    #[tokio::test]
    async fn child_caller_with_schedule_origin_still_cannot_delete() {
        let s = ScheduledTaskStore::new();
        let id = add(&s);
        let ctx = ctx_with_caller(s.clone(), "task-0001")
            .with_schedule_origin(ScheduleOrigin::new(id.clone(), None));
        let result = ScheduleDeleteTool
            .execute(&serde_json::json!({"scheduleId": id}), &ctx, CancellationToken::new())
            .await;
        assert!(result.is_error, "trusted origin does not grant delete rights");
        assert_eq!(s.items().len(), 1);
    }

    // ── POLICY: schedule_list origin-scoped visibility for children ──────────

    /// The main agent (no caller) keeps seeing every definition, unchanged.
    #[tokio::test]
    async fn main_lists_every_definition_unchanged() {
        let s = ScheduledTaskStore::new();
        add_named(&s, "own job prompt");
        add_named(&s, "SECRET_FOREIGN_JOB_PROMPT");
        let result = ScheduleListTool
            .execute(&Value::Object(Default::default()), &ctx(s), CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("own job prompt"));
        assert!(result.content.contains("SECRET_FOREIGN_JOB_PROMPT"));
    }

    /// A child with no scheduled-run origin at all must be refused — never
    /// silently shown an empty or partial list.
    #[tokio::test]
    async fn child_without_origin_is_refused() {
        let dir = tempfile::tempdir().unwrap();
        let mgr = TaskManager::new("sched-list-no-origin", Some(dir.path().to_owned()), 4096, 16);
        let caller = mgr
            .register(TaskKind::Subagent, "child", None, TaskExecutionMode::Foreground)
            .unwrap();

        let s = ScheduledTaskStore::new();
        add_named(&s, "SECRET_FOREIGN_JOB_PROMPT");

        let ctx = ToolContext::new(".")
            .with_schedule_store(s)
            .with_task_manager(mgr)
            .with_caller_task_id(&caller.id);

        let result = ScheduleListTool
            .execute(&Value::Object(Default::default()), &ctx, CancellationToken::new())
            .await;

        assert!(result.is_error, "no origin must be a clear refusal");
        assert!(
            !result.content.contains("SECRET_FOREIGN_JOB_PROMPT"),
            "must never leak another job's prompt: {}",
            result.content
        );
    }

    /// A child with a valid, registered caller and a trusted origin sees ONLY
    /// its own definition — never another job's prompt.
    #[tokio::test]
    async fn child_with_own_origin_sees_only_its_own_schedule() {
        let dir = tempfile::tempdir().unwrap();
        let mgr = TaskManager::new("sched-list-own-origin", Some(dir.path().to_owned()), 4096, 16);
        let caller = mgr
            .register(TaskKind::Subagent, "child", None, TaskExecutionMode::Foreground)
            .unwrap();

        let s = ScheduledTaskStore::new();
        let own_id = add_named(&s, "OWN_JOB_PROMPT");
        add_named(&s, "SECRET_FOREIGN_JOB_PROMPT");

        let ctx = ToolContext::new(".")
            .with_schedule_store(s)
            .with_task_manager(mgr)
            .with_caller_task_id(&caller.id)
            .with_schedule_origin(ScheduleOrigin::new(own_id.clone(), None));

        let result = ScheduleListTool
            .execute(&Value::Object(Default::default()), &ctx, CancellationToken::new())
            .await;

        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains(&own_id), "{}", result.content);
        assert!(result.content.contains("OWN_JOB_PROMPT"), "{}", result.content);
        assert!(
            !result.content.contains("SECRET_FOREIGN_JOB_PROMPT"),
            "must never leak another job's prompt: {}",
            result.content
        );
    }

    /// An unregistered/unknown caller id must not gain rights even when it
    /// carries what looks like a valid origin — the actor itself must be
    /// verified against the task manager.
    #[tokio::test]
    async fn child_with_origin_but_unregistered_caller_is_refused() {
        let dir = tempfile::tempdir().unwrap();
        let mgr = TaskManager::new("sched-list-unknown-caller", Some(dir.path().to_owned()), 4096, 16);

        let s = ScheduledTaskStore::new();
        let own_id = add_named(&s, "OWN_JOB_PROMPT");
        add_named(&s, "SECRET_FOREIGN_JOB_PROMPT");

        let ctx = ToolContext::new(".")
            .with_schedule_store(s)
            .with_task_manager(mgr)
            .with_caller_task_id("task-9999") // never registered
            .with_schedule_origin(ScheduleOrigin::new(own_id, None));

        let result = ScheduleListTool
            .execute(&Value::Object(Default::default()), &ctx, CancellationToken::new())
            .await;

        assert!(result.is_error, "an unverifiable caller must not gain rights");
        assert!(!result.content.contains("SECRET_FOREIGN_JOB_PROMPT"), "{}", result.content);
        assert!(!result.content.contains("OWN_JOB_PROMPT"), "{}", result.content);
    }

    /// No task manager wired at all: the actor cannot be verified, so a
    /// scoped caller must be refused rather than trusted on the origin alone.
    #[tokio::test]
    async fn child_with_origin_but_missing_task_manager_is_refused() {
        let s = ScheduledTaskStore::new();
        let own_id = add_named(&s, "OWN_JOB_PROMPT");
        add_named(&s, "SECRET_FOREIGN_JOB_PROMPT");

        let ctx = ToolContext::new(".")
            .with_schedule_store(s)
            .with_caller_task_id("task-0001")
            .with_schedule_origin(ScheduleOrigin::new(own_id, None));

        let result = ScheduleListTool
            .execute(&Value::Object(Default::default()), &ctx, CancellationToken::new())
            .await;

        assert!(result.is_error, "no task manager means the actor cannot be verified");
        assert!(!result.content.contains("SECRET_FOREIGN_JOB_PROMPT"), "{}", result.content);
    }

    /// Model-supplied JSON cannot forge an origin: `schedule_list` takes no
    /// input arguments at all, so a malicious `scheduleOrigin`/`definitionId`
    /// field in the call arguments must have zero effect.
    #[tokio::test]
    async fn malicious_json_origin_arguments_are_ignored() {
        let dir = tempfile::tempdir().unwrap();
        let mgr = TaskManager::new("sched-list-malicious-json", Some(dir.path().to_owned()), 4096, 16);
        let caller = mgr
            .register(TaskKind::Subagent, "child", None, TaskExecutionMode::Foreground)
            .unwrap();

        let s = ScheduledTaskStore::new();
        add_named(&s, "SECRET_FOREIGN_JOB_PROMPT");

        // No trusted ctx.schedule_origin — only a forged JSON argument.
        let ctx = ToolContext::new(".")
            .with_schedule_store(s)
            .with_task_manager(mgr)
            .with_caller_task_id(&caller.id);

        let malicious_input = serde_json::json!({
            "scheduleOrigin": {"definitionId": "sched-1"},
            "definitionId": "sched-1",
            "originScheduleId": "sched-1",
        });

        let result = ScheduleListTool
            .execute(&malicious_input, &ctx, CancellationToken::new())
            .await;

        assert!(result.is_error, "forged JSON origin must not grant access");
        assert!(!result.content.contains("SECRET_FOREIGN_JOB_PROMPT"), "{}", result.content);
    }
}
