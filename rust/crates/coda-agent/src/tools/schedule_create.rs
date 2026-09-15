//! `schedule_create` — create a new scheduled task definition.
//!
//! The tool owns policy (who may create a schedule) and argument extraction;
//! every validation rule lives in [`crate::scheduling::limits::build_draft`],
//! which `session/scheduleCreate` calls too. Two creation surfaces with two
//! validators is how a bound ends up enforced on one path and ignored on the
//! other.

use async_trait::async_trait;
use chrono::Utc;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::scheduling::{build_draft, ScheduleCreateRequest};
use crate::tool::ToolContextServiceExt as _;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

pub struct ScheduleCreateTool;

#[async_trait]
impl Tool for ScheduleCreateTool {
    fn name(&self) -> &str {
        "schedule_create"
    }

    fn description(&self) -> &str {
        "Create a new scheduled task definition. Supply exactly one of 'every' (recurring \
         interval like '30m', '2h', '1d'), 'at' (one-shot ISO-8601 timestamp), or 'cron' \
         (five-field cron expression). Optionally bound it: 'maxRuns' stops the schedule \
         after that many runs have been started, and 'expiresAt' (absolute ISO-8601) or \
         'expiresIn' (relative, like '7d') stops it at a deadline. Without those the \
         schedule runs until deleted. Returns the new schedule id."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "prompt":{"type":"string","description":"The prompt to run on each firing"},
            "name":{"type":"string","description":"Optional human-readable label"},
            "every":{"type":"string","description":"Recurring interval: '30m', '2h', '1d'"},
            "at":{"type":"string","description":"One-shot ISO-8601 date-time"},
            "cron":{"type":"string","description":"Five-field cron expression"},
            "timeZone":{"type":"string","description":"IANA timezone id (for cron; default UTC)"},
            "maxRuns":{"type":"integer","minimum":1,"description":"Stop after this many runs have been started (a run that later fails still counts). Omit for unlimited."},
            "expiresAt":{"type":"string","description":"Absolute ISO-8601 deadline; no run starts at or after it. Mutually exclusive with expiresIn."},
            "expiresIn":{"type":"string","description":"Relative deadline from now: '30m', '2h', '7d'. Mutually exclusive with expiresAt."}
          },
          "required":["prompt"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        false
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        // POLICY: arbitrary schedule management is main-agent only. A trusted
        // scheduled-run origin does NOT grant create rights either — the only
        // origin-scoped self-action is the targetless `schedule_cancel_self`.
        // Keeping creation main-only is also what stops a bounded run from
        // cloning itself into an unbounded watch to escape its own budget.
        // Reject before any lookup or mutation.
        if ctx.caller_task_id.is_some() {
            return ToolResult::error("schedule_create is only available to the main agent.");
        }

        let store = match ctx.get_schedule_store() {
            Some(s) => s,
            None => return ToolResult::error("Schedule store is not available."),
        };

        let request = ScheduleCreateRequest {
            prompt: input.get("prompt").and_then(Value::as_str),
            name: input.get("name").and_then(Value::as_str),
            every: input.get("every").and_then(Value::as_str),
            at: input.get("at").and_then(Value::as_str),
            cron: input.get("cron").and_then(Value::as_str),
            time_zone: input.get("timeZone").and_then(Value::as_str),
            expires_at: input.get("expiresAt").and_then(Value::as_str),
            expires_in: input.get("expiresIn").and_then(Value::as_str),
            max_runs: input.get("maxRuns"),
        };

        let now = Utc::now();
        let draft = match build_draft(&request, now) {
            Ok(draft) => draft,
            Err(error) => return ToolResult::error(error),
        };

        let task = store.add(draft, now);
        let mut summary = format!("Schedule created: id={} kind={:?}", task.id, task.kind);
        if let Some(max) = task.max_runs {
            summary.push_str(&format!(" maxRuns={max}"));
        }
        if let Some(deadline) = task.expires_at_utc {
            summary.push_str(&format!(
                " expiresAt={}",
                deadline.format("%Y-%m-%dT%H:%M:%SZ")
            ));
        }
        if task.max_runs.is_none() && task.expires_at_utc.is_none() {
            summary.push_str(" (unbounded: runs until deleted)");
        }
        ToolResult::ok(summary)
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::scheduling::{ScheduleKind, ScheduledTaskStore};
    use std::sync::Arc;

    fn ctx(store: Arc<ScheduledTaskStore>) -> ToolContext {
        ToolContext::new(".").with_schedule_store(store)
    }

    fn ctx_with_caller(store: Arc<ScheduledTaskStore>, caller_id: &str) -> ToolContext {
        ToolContext::new(".")
            .with_schedule_store(store)
            .with_caller_task_id(caller_id)
    }

    async fn create(store: &Arc<ScheduledTaskStore>, input: Value) -> ToolOutcome {
        ScheduleCreateTool
            .execute(&input, &ctx(store.clone()), CancellationToken::new())
            .await
    }

    #[tokio::test]
    async fn create_interval_schedule() {
        let s = ScheduledTaskStore::new();
        let result = create(&s, serde_json::json!({"prompt": "run this", "every": "30m"})).await;
        assert!(!result.is_error, "{}", result.content);
        assert_eq!(s.items().len(), 1);
        assert_eq!(s.items()[0].kind, ScheduleKind::Interval);
    }

    #[tokio::test]
    async fn create_cron_schedule() {
        let s = ScheduledTaskStore::new();
        let result = create(
            &s,
            serde_json::json!({"prompt": "run this", "cron": "0 9 * * *", "timeZone": "UTC"}),
        )
        .await;
        assert!(!result.is_error, "{}", result.content);
        assert_eq!(s.items()[0].kind, ScheduleKind::Cron);
    }

    #[tokio::test]
    async fn reject_invalid_cron() {
        let s = ScheduledTaskStore::new();
        let result = create(&s, serde_json::json!({"prompt": "x", "cron": "not-a-cron"})).await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn reject_multiple_selectors() {
        let s = ScheduledTaskStore::new();
        let result = create(
            &s,
            serde_json::json!({"prompt": "x", "every": "1h", "cron": "* * * * *"}),
        )
        .await;
        assert!(result.is_error);
        assert!(result.content.contains("Exactly one"), "{}", result.content);
    }

    #[tokio::test]
    async fn reject_no_selector() {
        let s = ScheduledTaskStore::new();
        let result = create(&s, serde_json::json!({"prompt": "x"})).await;
        assert!(result.is_error);
    }

    // ── STAGE 1: bounds ──────────────────────────────────────────────────────

    /// The user's example: "run this every hour, but only seven times".
    #[tokio::test]
    async fn max_runs_is_stored_on_the_definition() {
        let s = ScheduledTaskStore::new();
        let result = create(
            &s,
            serde_json::json!({"prompt": "monitor", "every": "1h", "maxRuns": 7}),
        )
        .await;
        assert!(!result.is_error, "{}", result.content);
        let item = &s.items()[0];
        assert_eq!(item.max_runs, Some(7));
        assert_eq!(item.runs_started, 0);
        assert!(result.content.contains("maxRuns=7"), "{}", result.content);
    }

    /// A relative deadline is resolved to an absolute instant exactly once, at
    /// creation, so it cannot drift with later evaluations.
    #[tokio::test]
    async fn expires_in_is_resolved_to_an_absolute_deadline_at_creation() {
        let s = ScheduledTaskStore::new();
        let before = Utc::now();
        let result = create(
            &s,
            serde_json::json!({"prompt": "monitor", "every": "1h", "expiresIn": "7d"}),
        )
        .await;
        assert!(!result.is_error, "{}", result.content);
        let deadline = s.items()[0].expires_at_utc.expect("a deadline must be stored");
        assert!(deadline >= before + chrono::Duration::days(7));
        assert!(deadline <= Utc::now() + chrono::Duration::days(7));
    }

    #[tokio::test]
    async fn expires_at_is_accepted_as_an_absolute_instant() {
        let s = ScheduledTaskStore::new();
        let deadline = Utc::now() + chrono::Duration::days(30);
        let result = create(
            &s,
            serde_json::json!({
                "prompt": "monitor",
                "every": "1h",
                "expiresAt": deadline.to_rfc3339(),
            }),
        )
        .await;
        assert!(!result.is_error, "{}", result.content);
        assert_eq!(
            s.items()[0].expires_at_utc.unwrap().timestamp(),
            deadline.timestamp()
        );
    }

    #[tokio::test]
    async fn expires_at_and_expires_in_together_are_refused_and_nothing_is_created() {
        let s = ScheduledTaskStore::new();
        let result = create(
            &s,
            serde_json::json!({
                "prompt": "monitor",
                "every": "1h",
                "expiresAt": (Utc::now() + chrono::Duration::days(3)).to_rfc3339(),
                "expiresIn": "7d",
            }),
        )
        .await;
        assert!(result.is_error);
        assert!(result.content.contains("mutually exclusive"), "{}", result.content);
        assert!(s.items().is_empty(), "a rejected request must create nothing");
    }

    #[tokio::test]
    async fn a_past_deadline_is_refused_and_nothing_is_created() {
        let s = ScheduledTaskStore::new();
        let result = create(
            &s,
            serde_json::json!({
                "prompt": "monitor",
                "every": "1h",
                "expiresAt": (Utc::now() - chrono::Duration::days(1)).to_rfc3339(),
            }),
        )
        .await;
        assert!(result.is_error);
        assert!(s.items().is_empty());
    }

    #[tokio::test]
    async fn a_bad_run_budget_is_refused_and_nothing_is_created() {
        for bad in [
            serde_json::json!(0),
            serde_json::json!(-3),
            serde_json::json!(2.5),
            serde_json::json!("7"),
            serde_json::json!(4294967296u64),
        ] {
            let s = ScheduledTaskStore::new();
            let result = create(
                &s,
                serde_json::json!({"prompt": "monitor", "every": "1h", "maxRuns": bad}),
            )
            .await;
            assert!(result.is_error, "maxRuns={bad} must be refused");
            assert!(result.content.contains("maxRuns"), "{}", result.content);
            assert!(s.items().is_empty());
        }
    }

    /// Absent bounds must keep behaving exactly as before this stage.
    #[tokio::test]
    async fn an_unbounded_schedule_keeps_its_previous_defaults() {
        let s = ScheduledTaskStore::new();
        let result = create(&s, serde_json::json!({"prompt": "x", "every": "30m"})).await;
        assert!(!result.is_error, "{}", result.content);
        let item = &s.items()[0];
        assert_eq!(item.max_runs, None);
        assert_eq!(item.expires_at_utc, None);
        assert!(item.retirement.is_none());
        assert!(result.content.contains("unbounded"), "{}", result.content);
    }

    // ── POLICY: schedule management is main-agent only ───────────────────────

    /// A child (subagent) must be refused before any mutation, even though the
    /// schedule store is injected into its tool context.
    #[tokio::test]
    async fn child_caller_cannot_create_schedule_store_unchanged() {
        let s = ScheduledTaskStore::new();
        let result = ScheduleCreateTool
            .execute(
                &serde_json::json!({"prompt": "run this", "every": "30m"}),
                &ctx_with_caller(s.clone(), "task-0001"),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error, "children must be refused");
        assert!(
            s.items().is_empty(),
            "no schedule may be created on behalf of a child"
        );
    }

    /// Even a run stamped with a trusted scheduled origin (a nested child of a
    /// scheduled job) must not be able to create new arbitrary schedules —
    /// only the dedicated, targetless self-cancel may use its origin.
    #[tokio::test]
    async fn child_caller_with_schedule_origin_still_cannot_create() {
        use crate::tool::ScheduleOrigin;

        let s = ScheduledTaskStore::new();
        let ctx = ctx_with_caller(s.clone(), "task-0001")
            .with_schedule_origin(ScheduleOrigin::new("sched-1", None));
        let result = ScheduleCreateTool
            .execute(
                &serde_json::json!({"prompt": "run this", "every": "30m"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error, "trusted origin does not grant create rights");
        assert!(s.items().is_empty());
    }
}
