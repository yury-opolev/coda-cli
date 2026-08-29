//! Computes the next UTC due time for recurring schedule definitions.
//!
//! - **Interval**: advance from the persisted `next_run_utc` boundary, coalescing
//!   missed ticks.
//! - **Cron**: evaluate in the definition's stored timezone, handling DST
//!   (spring-forward gaps are skipped; fall-back ambiguity resolves to the
//!   earlier UTC instant).
//! - **At**: one-shot; the persisted `next_run_utc` is authoritative.
//!
//! The search horizon for cron is 12 years, exceeding the worst-case valid-date
//! gap (Feb 29 across a non-leap century boundary).

use chrono::{DateTime, Duration as CDuration, NaiveDateTime, TimeZone, Utc};
use chrono::Timelike;
use chrono_tz::Tz;

use super::cron_expression::CronExpression;
use super::scheduled_task::{ScheduleKind, ScheduledTask};

const MAX_SEARCH_YEARS: i64 = 12;

pub struct ScheduleRecurrence;

impl ScheduleRecurrence {
    /// Returns the next UTC due time for a recurring definition strictly after
    /// `now_utc`. One-shot `At` definitions return `definition.next_run_utc`.
    pub fn advance_recurring_past(
        definition: &ScheduledTask,
        now_utc: DateTime<Utc>,
    ) -> Result<DateTime<Utc>, String> {
        match definition.kind {
            ScheduleKind::Interval => advance_interval(definition, now_utc),
            ScheduleKind::Cron => {
                let expr = definition.cron.as_deref().ok_or_else(|| {
                    format!("Definition '{}' has no cron expression.", definition.id)
                })?;
                let cron = CronExpression::parse(expr)?;
                let tz: Tz = definition.time_zone_id.parse().map_err(|_| {
                    format!(
                        "Definition '{}' references an unknown timezone '{}'.",
                        definition.id, definition.time_zone_id
                    )
                })?;
                next_cron_occurrence(&cron, now_utc, tz)
            }
            ScheduleKind::At => Ok(definition.next_run_utc),
        }
    }

    /// Returns the next UTC occurrence of `cron` strictly after `after_utc`,
    /// evaluated in `tz`. Nonexistent spring-forward minutes are skipped.
    /// Ambiguous fall-back minutes resolve to the larger offset (earlier UTC).
    pub fn next_cron_occurrence(
        cron: &CronExpression,
        after_utc: DateTime<Utc>,
        tz: Tz,
    ) -> Result<DateTime<Utc>, String> {
        next_cron_occurrence(cron, after_utc, tz)
    }
}

fn advance_interval(def: &ScheduledTask, now_utc: DateTime<Utc>) -> Result<DateTime<Utc>, String> {
    let interval = def.interval_duration().ok_or_else(|| {
        format!("Interval definition '{}' has no positive interval.", def.id)
    })?;
    if interval.is_zero() {
        return Err(format!(
            "Interval definition '{}' has no positive interval.",
            def.id
        ));
    }

    let boundary = def.next_run_utc;
    if boundary > now_utc {
        return Ok(boundary);
    }

    // Coalesce missed ticks: advance to the next future boundary.
    let elapsed = now_utc
        .signed_duration_since(boundary)
        .to_std()
        .unwrap_or_default();
    let steps = elapsed.as_secs_f64() / interval.as_secs_f64();
    let steps = steps.ceil() as u64;

    // Saturate to avoid u64 overflow when next_run_utc is very far in the past
    // (e.g. the record was persisted years ago with a short interval).
    const MAX_CATCHUP_SECS: u64 = 10 * 365 * 24 * 3600; // 10 years
    let delta_secs = interval.as_secs().saturating_mul(steps).min(MAX_CATCHUP_SECS);
    let delta = CDuration::seconds(delta_secs as i64);
    Ok(boundary + delta)
}

fn next_cron_occurrence(
    cron: &CronExpression,
    after_utc: DateTime<Utc>,
    tz: Tz,
) -> Result<DateTime<Utc>, String> {
    // Convert `after_utc` to local time in the target timezone.
    let after_local = after_utc.with_timezone(&tz);
    let start_local = after_local.naive_local();

    // Start one minute after the "after" wall-clock minute.
    let mut candidate = NaiveDateTime::new(
        start_local.date(),
        chrono::NaiveTime::from_hms_opt(start_local.hour(), start_local.minute(), 0).unwrap(),
    ) + CDuration::minutes(1);

    let limit = candidate + CDuration::days(MAX_SEARCH_YEARS * 366);

    while candidate <= limit {
        if !cron.matches_date(&candidate) {
            // Skip to the next day in a single step.
            candidate = candidate.date().and_hms_opt(0, 0, 0).unwrap() + CDuration::days(1);
            continue;
        }

        if !cron.matches_time(&candidate) {
            candidate += CDuration::minutes(1);
            continue;
        }

        // Check if this local minute exists in the timezone.
        match tz.from_local_datetime(&candidate) {
            chrono::LocalResult::None => {
                // Spring-forward gap: skip.
                candidate += CDuration::minutes(1);
                continue;
            }
            chrono::LocalResult::Single(dt) => {
                let utc = dt.with_timezone(&Utc);
                if utc <= after_utc {
                    candidate += CDuration::minutes(1);
                    continue;
                }
                return Ok(utc);
            }
            chrono::LocalResult::Ambiguous(earlier, later) => {
                // Fall-back: prefer the earlier UTC instant (larger offset).
                // Both map to the same local wall-clock time; we pick the one
                // that comes first in UTC (standard cron expectation).
                let utc = earlier.with_timezone(&Utc);
                if utc <= after_utc {
                    // Try the later UTC instant too.
                    let utc2 = later.with_timezone(&Utc);
                    if utc2 <= after_utc {
                        candidate += CDuration::minutes(1);
                        continue;
                    }
                    return Ok(utc2);
                }
                return Ok(utc);
            }
        }
    }

    Err(format!(
        "No occurrence found for cron '{}' within {MAX_SEARCH_YEARS} years of {after_utc}.",
        cron.expression
    ))
}

// ── Re-exported for testing ───────────────────────────────────────────────────
// (no public re-export needed; tested inline above)

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    fn utc(y: i32, mo: u32, d: u32, h: u32, min: u32) -> DateTime<Utc> {
        use chrono::TimeZone as _;
        Utc.with_ymd_and_hms(y, mo, d, h, min, 0).unwrap()
    }

    // ── interval recurrence ────────────────────────────────────────────────────

    fn interval_def(secs: f64, next_run: DateTime<Utc>) -> ScheduledTask {
        use super::super::scheduled_task::{ScheduledTask, ScheduleKind};
        ScheduledTask {
            schema_version: 2,
            id: "test-id".into(),
            name: None,
            kind: ScheduleKind::Interval,
            prompt: "test".into(),
            interval: Some(secs),
            at_utc: None,
            cron: None,
            time_zone_id: "UTC".into(),
            next_run_utc: next_run,
            created_at_utc: next_run,
            updated_at_utc: next_run,
            last_terminal_outcome: None,
        }
    }

    #[test]
    fn interval_returns_boundary_when_not_yet_due() {
        let next = utc(2024, 6, 1, 10, 0);
        let def = interval_def(3600.0, next);
        let now = utc(2024, 6, 1, 9, 30);
        let result = ScheduleRecurrence::advance_recurring_past(&def, now).unwrap();
        assert_eq!(result, next);
    }

    #[test]
    fn interval_advances_past_overdue_boundary() {
        let boundary = utc(2024, 6, 1, 10, 0);
        let def = interval_def(3600.0, boundary); // hourly
        let now = utc(2024, 6, 1, 11, 30); // 1.5 hours after boundary
        let result = ScheduleRecurrence::advance_recurring_past(&def, now).unwrap();
        // Expected next boundary: 10:00 + 2 hours = 12:00
        assert_eq!(result, utc(2024, 6, 1, 12, 0));
    }

    #[test]
    fn interval_missing_duration_is_error() {
        use super::super::scheduled_task::{ScheduledTask, ScheduleKind};
        let def = ScheduledTask {
            schema_version: 2,
            id: "bad".into(),
            name: None,
            kind: ScheduleKind::Interval,
            prompt: "x".into(),
            interval: None,
            at_utc: None,
            cron: None,
            time_zone_id: "UTC".into(),
            next_run_utc: utc(2024, 1, 1, 0, 0),
            created_at_utc: utc(2024, 1, 1, 0, 0),
            updated_at_utc: utc(2024, 1, 1, 0, 0),
            last_terminal_outcome: None,
        };
        assert!(ScheduleRecurrence::advance_recurring_past(&def, utc(2024, 1, 1, 0, 0)).is_err());
    }

    // ── cron recurrence ────────────────────────────────────────────────────────

    #[test]
    fn cron_next_occurrence_after_given_time() {
        let cron = CronExpression::parse("30 9 * * *").unwrap(); // 09:30 daily
        let after = utc(2024, 6, 1, 9, 30); // exactly at the minute
        let tz: Tz = "UTC".parse().unwrap();
        let result = next_cron_occurrence(&cron, after, tz).unwrap();
        // Strictly after → next day's 09:30
        assert_eq!(result, utc(2024, 6, 2, 9, 30));
    }

    #[test]
    fn cron_next_occurrence_before_given_time() {
        let cron = CronExpression::parse("30 9 * * *").unwrap(); // 09:30 daily
        let after = utc(2024, 6, 1, 8, 0);
        let tz: Tz = "UTC".parse().unwrap();
        let result = next_cron_occurrence(&cron, after, tz).unwrap();
        assert_eq!(result, utc(2024, 6, 1, 9, 30));
    }

    #[test]
    fn cron_unknown_timezone_is_error() {
        use super::super::scheduled_task::{ScheduledTask, ScheduleKind};
        let def = ScheduledTask {
            schema_version: 2,
            id: "bad-tz".into(),
            name: None,
            kind: ScheduleKind::Cron,
            prompt: "x".into(),
            interval: None,
            at_utc: None,
            cron: Some("* * * * *".into()),
            time_zone_id: "Not/ATimezone".into(),
            next_run_utc: utc(2024, 1, 1, 0, 0),
            created_at_utc: utc(2024, 1, 1, 0, 0),
            updated_at_utc: utc(2024, 1, 1, 0, 0),
            last_terminal_outcome: None,
        };
        assert!(ScheduleRecurrence::advance_recurring_past(&def, utc(2024, 1, 1, 0, 0)).is_err());
    }

    #[test]
    fn cron_with_timezone_offset() {
        // 09:00 US/Eastern = 14:00 UTC (EST, UTC-5).
        let cron = CronExpression::parse("0 9 * * *").unwrap();
        let tz: Tz = "America/New_York".parse().unwrap();
        let after = utc(2024, 1, 1, 10, 0); // 05:00 Eastern
        let result = next_cron_occurrence(&cron, after, tz).unwrap();
        // 09:00 Eastern on 2024-01-01 is 14:00 UTC.
        assert_eq!(result, utc(2024, 1, 1, 14, 0));
    }
}
