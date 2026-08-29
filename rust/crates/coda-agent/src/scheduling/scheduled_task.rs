//! Scheduled task data types.

use std::time::Duration;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

// ── Enums ─────────────────────────────────────────────────────────────────────

/// The way a scheduled definition computes its due times.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ScheduleKind {
    /// Fixed recurring interval measured from schedule boundaries.
    Interval,
    /// One-shot execution at a specific UTC instant.
    At,
    /// Recurring five-field cron rule evaluated in a stored timezone.
    Cron,
}

/// Terminal result of a scheduled execution.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ScheduleTerminalOutcome {
    Succeeded,
    Failed,
    Stopped,
}

// ── Records ────────────────────────────────────────────────────────────────────

/// Last-known terminal outcome metadata for a scheduled definition.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct ScheduleTerminalMetadata {
    pub outcome: ScheduleTerminalOutcome,
    pub completed_at_utc: DateTime<Utc>,
    pub summary: Option<String>,
}

/// A persisted scheduled definition.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ScheduledTask {
    /// Persisted schema version.
    #[serde(rename = "schemaVersion")]
    pub schema_version: u32,
    pub id: String,
    pub name: Option<String>,
    pub kind: ScheduleKind,
    pub prompt: String,
    /// Recurring interval, when `kind == Interval`.
    #[serde(
        rename = "intervalSecs",
        skip_serializing_if = "Option::is_none",
        default
    )]
    pub interval: Option<f64>,
    /// One-shot UTC instant, when `kind == At`.
    #[serde(rename = "atUtc", skip_serializing_if = "Option::is_none", default)]
    pub at_utc: Option<DateTime<Utc>>,
    /// Normalized cron expression, when `kind == Cron`.
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub cron: Option<String>,
    /// Timezone the definition is interpreted in.
    #[serde(rename = "timeZoneId")]
    pub time_zone_id: String,
    #[serde(rename = "nextRunUtc")]
    pub next_run_utc: DateTime<Utc>,
    #[serde(rename = "createdAtUtc")]
    pub created_at_utc: DateTime<Utc>,
    #[serde(rename = "updatedAtUtc")]
    pub updated_at_utc: DateTime<Utc>,
    #[serde(rename = "lastTerminalOutcome", skip_serializing_if = "Option::is_none", default)]
    pub last_terminal_outcome: Option<ScheduleTerminalMetadata>,
}

impl ScheduledTask {
    pub const CURRENT_SCHEMA_VERSION: u32 = 2;

    pub fn interval_duration(&self) -> Option<Duration> {
        self.interval.map(Duration::from_secs_f64)
    }
}

/// A validated, normalized definition ready to persist.
pub struct ScheduleDefinitionDraft {
    pub name: Option<String>,
    pub kind: ScheduleKind,
    pub prompt: String,
    pub interval: Option<Duration>,
    pub at_utc: Option<DateTime<Utc>>,
    pub cron: Option<String>,
    pub time_zone_id: String,
    pub next_run_utc: DateTime<Utc>,
}

/// A snapshot of the store: the version plus a copied task list.
pub struct ScheduledTaskStoreSnapshot {
    pub version: u64,
    pub items: Vec<ScheduledTask>,
}
