//! `schedule_create` — create a new scheduled task definition.

use async_trait::async_trait;
use chrono::Utc;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::scheduling::{CronExpression, ScheduleDefinitionDraft, ScheduleKind};
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

pub struct ScheduleCreateTool;

#[async_trait]
impl Tool for ScheduleCreateTool {
    fn name(&self) -> &str {
        "schedule_create"
    }

    fn description(&self) -> &str {
        "Create a new scheduled task definition. Supply exactly one of 'every' (recurring \
         interval like '30m', '2h', '1d'), 'at' (one-shot ISO-8601 timestamp), or 'cron' \
         (five-field cron expression). Returns the new schedule id."
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
            "timeZone":{"type":"string","description":"IANA timezone id (for cron; default UTC)"}
          },
          "required":["prompt"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        false
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        let prompt = match input.get("prompt").and_then(Value::as_str) {
            Some(p) if !p.trim().is_empty() => p.trim().to_owned(),
            _ => return ToolResult::error("Missing required 'prompt'."),
        };

        let store = match ctx.get_schedule_store() {
            Some(s) => s,
            None => return ToolResult::error("Schedule store is not available."),
        };

        let name = input
            .get("name")
            .and_then(Value::as_str)
            .filter(|s| !s.trim().is_empty())
            .map(str::to_owned);

        let every = input.get("every").and_then(Value::as_str);
        let at = input.get("at").and_then(Value::as_str);
        let cron = input.get("cron").and_then(Value::as_str);
        let time_zone = input
            .get("timeZone")
            .and_then(Value::as_str)
            .unwrap_or("UTC");

        let selector_count = [every.is_some(), at.is_some(), cron.is_some()]
            .iter()
            .filter(|&&b| b)
            .count();
        if selector_count != 1 {
            return ToolResult::error(
                "schedule_create requires exactly one of 'every', 'at', or 'cron'.",
            );
        }

        let now = Utc::now();
        let draft = if let Some(every) = every {
            match parse_every(every, &prompt, name) {
                Ok(d) => d,
                Err(e) => return ToolResult::error(e),
            }
        } else if let Some(at_str) = at {
            match parse_at(at_str, &prompt, name) {
                Ok(d) => d,
                Err(e) => return ToolResult::error(e),
            }
        } else {
            let cron_str = cron.unwrap();
            match parse_cron(cron_str, &prompt, name, time_zone, now) {
                Ok(d) => d,
                Err(e) => return ToolResult::error(e),
            }
        };

        let task = store.add(draft, now);
        ToolResult::ok(format!(
            "Schedule created: id={} kind={:?}",
            task.id, task.kind
        ))
    }
}

fn parse_every(
    every: &str,
    prompt: &str,
    name: Option<String>,
) -> Result<ScheduleDefinitionDraft, String> {
    // Parse format: <number><unit> where unit is m, h, or d.
    let every = every.trim();
    let (n_str, unit) = every
        .char_indices()
        .rev()
        .find_map(|(i, c)| {
            if matches!(c, 'm' | 'h' | 'd' | 'M' | 'H' | 'D') {
                Some((&every[..i], c.to_ascii_lowercase()))
            } else {
                None
            }
        })
        .unwrap_or(("", ' '));

    let amount: u64 = n_str
        .parse()
        .ok()
        .filter(|&n: &u64| n > 0)
        .ok_or_else(|| "'every' must be an integer duration such as 30m, 2h, or 1d.".to_owned())?;

    let interval = match unit {
        'm' => std::time::Duration::from_secs(amount * 60),
        'h' => std::time::Duration::from_secs(amount * 3600),
        'd' => std::time::Duration::from_secs(amount * 86400),
        _ => return Err("'every' must end with 'm' (minutes), 'h' (hours), or 'd' (days).".into()),
    };

    if interval.as_secs() < 60 {
        return Err("'every' must be at least one minute.".into());
    }

    let now = Utc::now();
    let next_run = now + chrono::Duration::seconds(interval.as_secs() as i64);
    Ok(ScheduleDefinitionDraft {
        name,
        kind: ScheduleKind::Interval,
        prompt: prompt.to_owned(),
        interval: Some(interval),
        at_utc: None,
        cron: None,
        time_zone_id: "UTC".into(),
        next_run_utc: next_run,
    })
}

fn parse_at(at: &str, prompt: &str, name: Option<String>) -> Result<ScheduleDefinitionDraft, String> {
    let dt = chrono::DateTime::parse_from_rfc3339(at.trim())
        .map_err(|_| format!("'at' must be a valid ISO-8601 date-time with timezone offset, e.g. '2024-12-25T09:00:00+00:00'."))?;
    let utc = dt.with_timezone(&Utc);
    Ok(ScheduleDefinitionDraft {
        name,
        kind: ScheduleKind::At,
        prompt: prompt.to_owned(),
        interval: None,
        at_utc: Some(utc),
        cron: None,
        time_zone_id: "UTC".into(),
        next_run_utc: utc,
    })
}

fn parse_cron(
    cron: &str,
    prompt: &str,
    name: Option<String>,
    time_zone_id: &str,
    now: chrono::DateTime<Utc>,
) -> Result<ScheduleDefinitionDraft, String> {
    let expr = CronExpression::parse(cron)?;

    // Validate timezone.
    let tz: chrono_tz::Tz = time_zone_id
        .parse()
        .map_err(|_| format!("Unknown timezone '{time_zone_id}'."))?;

    let next_run = crate::scheduling::ScheduleRecurrence::next_cron_occurrence(&expr, now, tz)?;

    Ok(ScheduleDefinitionDraft {
        name,
        kind: ScheduleKind::Cron,
        prompt: prompt.to_owned(),
        interval: None,
        at_utc: None,
        cron: Some(expr.expression),
        time_zone_id: time_zone_id.to_owned(),
        next_run_utc: next_run,
    })
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::scheduling::ScheduledTaskStore;
    use std::sync::Arc;

    fn ctx(store: Arc<ScheduledTaskStore>) -> ToolContext {
        ToolContext::new(".").with_schedule_store(store)
    }

    #[tokio::test]
    async fn create_interval_schedule() {
        let s = ScheduledTaskStore::new();
        let result = ScheduleCreateTool
            .execute(
                &serde_json::json!({"prompt": "run this", "every": "30m"}),
                &ctx(s.clone()),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert_eq!(s.items().len(), 1);
        assert_eq!(s.items()[0].kind, ScheduleKind::Interval);
    }

    #[tokio::test]
    async fn create_cron_schedule() {
        let s = ScheduledTaskStore::new();
        let result = ScheduleCreateTool
            .execute(
                &serde_json::json!({"prompt": "run this", "cron": "0 9 * * *", "timeZone": "UTC"}),
                &ctx(s.clone()),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert_eq!(s.items()[0].kind, ScheduleKind::Cron);
    }

    #[tokio::test]
    async fn reject_invalid_cron() {
        let s = ScheduledTaskStore::new();
        let result = ScheduleCreateTool
            .execute(
                &serde_json::json!({"prompt": "x", "cron": "not-a-cron"}),
                &ctx(s),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn reject_multiple_selectors() {
        let s = ScheduledTaskStore::new();
        let result = ScheduleCreateTool
            .execute(
                &serde_json::json!({"prompt": "x", "every": "1h", "cron": "* * * * *"}),
                &ctx(s),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
        assert!(result.content.contains("exactly one"), "{}", result.content);
    }

    #[tokio::test]
    async fn reject_no_selector() {
        let s = ScheduledTaskStore::new();
        let result = ScheduleCreateTool
            .execute(
                &serde_json::json!({"prompt": "x"}),
                &ctx(s),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }
}
