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
        if items.is_empty() {
            return ToolResult::ok("No scheduled tasks.");
        }

        let mut lines = Vec::new();
        for t in &items {
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
    use std::sync::Arc;
    use std::time::Duration;

    fn ctx(store: Arc<ScheduledTaskStore>) -> ToolContext {
        ToolContext::new(".").with_schedule_store(store)
    }

    fn add(store: &Arc<ScheduledTaskStore>) -> String {
        let draft = ScheduleDefinitionDraft {
            name: Some("test".into()),
            kind: ScheduleKind::Interval,
            prompt: "do something".into(),
            interval: Some(Duration::from_secs(3600)),
            at_utc: None,
            cron: None,
            time_zone_id: "UTC".into(),
            next_run_utc: chrono::Utc::now() + chrono::Duration::hours(1),
        };
        store.add(draft, chrono::Utc::now()).id
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
}
