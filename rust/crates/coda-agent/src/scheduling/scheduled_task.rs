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

/// Why a bounded definition will never launch another occurrence.
///
/// Deliberately separate from [`ScheduleTerminalOutcome`]: the last *run*'s
/// outcome and the *definition*'s retirement answer different questions. A
/// definition can retire because its run budget is spent while its final run
/// still failed, and a run can succeed long after the definition expired.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ScheduleRetirementReason {
    /// `maxRuns` accepted launch attempts have been made.
    RunLimit,
    /// `expiresAtUtc` was reached.
    Expired,
    /// The scheduled run cancelled its own definition.
    Cancelled,
    /// The launch itself could not be accepted and retrying would spin.
    LaunchFailed,
}

impl ScheduleRetirementReason {
    /// The wire spelling used by `session/scheduleList`, `schedule_list` and
    /// the lifecycle events.
    pub fn as_wire(self) -> &'static str {
        match self {
            // A spent run budget is the *successful* end of a bounded job:
            // "run it seven times" finished, it did not break.
            Self::RunLimit => "completed",
            Self::Expired => "expired",
            Self::Cancelled => "cancelled",
            Self::LaunchFailed => "failed",
        }
    }
}

/// Record of the moment a definition stopped scheduling future work.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScheduleRetirement {
    pub reason: ScheduleRetirementReason,
    pub retired_at_utc: DateTime<Utc>,
    /// Short, sanitized note (e.g. a self-cancelling run's reason). Never a
    /// raw model string: callers clamp and strip it first.
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub note: Option<String>,
}

/// Live, in-process status of a definition.
///
/// This is runtime state, not configuration: it is owned by the schedule
/// runtime's loop, published into the store's ephemeral side table, and never
/// persisted. A fresh process owns no runs, so it starts `Idle` by definition.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ScheduleLiveStatus {
    /// Not running; will launch at `nextRunUtc` if still within its bounds.
    #[default]
    Idle,
    /// A run is in flight.
    Running,
    /// A run is in flight and exactly one replacement is queued behind it.
    Pending,
    /// A run is in flight but the definition has already exhausted its bounds:
    /// no further occurrence will ever be launched.
    Retiring,
    /// Future execution is quarantined after a launch or recurrence fault.
    Faulted,
}

/// Point-in-time live state for one definition.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct ScheduleLiveState {
    pub status: ScheduleLiveStatus,
    pub active_task_id: Option<String>,
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

    // ── Bounds ────────────────────────────────────────────────────────────────
    // These are *configuration and counters*, not runtime state, so they are
    // ordinary optional serde fields. Marking them `skip` would make a
    // persisted bounded definition come back unbounded — a stricter-looking
    // annotation that silently removes the user's limit.
    /// Hard deadline. No occurrence is launched at or after this instant.
    /// `None` means the definition never expires.
    #[serde(rename = "expiresAtUtc", skip_serializing_if = "Option::is_none", default)]
    pub expires_at_utc: Option<DateTime<Utc>>,
    /// Maximum number of *accepted* launch attempts. `None` means unlimited.
    #[serde(rename = "maxRuns", skip_serializing_if = "Option::is_none", default)]
    pub max_runs: Option<u32>,
    /// Monotonic count of accepted launch attempts, including runs that later
    /// failed. Never decremented, never reset by advancing or reconciling.
    #[serde(rename = "runsStarted", default)]
    pub runs_started: u32,
    /// Set once the definition can never launch again.
    #[serde(skip_serializing_if = "Option::is_none", default)]
    pub retirement: Option<ScheduleRetirement>,
}

impl ScheduledTask {
    pub const CURRENT_SCHEMA_VERSION: u32 = 3;

    pub fn interval_duration(&self) -> Option<Duration> {
        self.interval.map(Duration::from_secs_f64)
    }

    /// `true` once the definition has recorded a retirement.
    pub fn is_retired(&self) -> bool {
        self.retirement.is_some()
    }

    /// Remaining accepted launch attempts, or `None` when unlimited.
    pub fn runs_remaining(&self) -> Option<u32> {
        self.max_runs.map(|max| max.saturating_sub(self.runs_started))
    }
}

/// A validated, normalized definition ready to persist.
#[derive(Clone, Debug, PartialEq)]
pub struct ScheduleDefinitionDraft {
    pub name: Option<String>,
    pub kind: ScheduleKind,
    pub prompt: String,
    pub interval: Option<Duration>,
    pub at_utc: Option<DateTime<Utc>>,
    pub cron: Option<String>,
    pub time_zone_id: String,
    pub next_run_utc: DateTime<Utc>,
    /// Resolved once at creation: a relative `expiresIn` never survives as a
    /// relative value, so the deadline cannot drift with later evaluations.
    pub expires_at_utc: Option<DateTime<Utc>>,
    pub max_runs: Option<u32>,
}

/// A snapshot of the store: the version plus a copied task list.
pub struct ScheduledTaskStoreSnapshot {
    pub version: u64,
    pub items: Vec<ScheduledTask>,
    /// Ephemeral live state, keyed by definition id. Present only for
    /// definitions the runtime has touched in this process.
    pub live: std::collections::HashMap<String, ScheduleLiveState>,
}
