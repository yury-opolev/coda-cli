//! Bounded schedules: one validation path, and pure admission/retirement
//! decisions.
//!
//! # Why this module exists
//! A schedule can now stop itself — after a deadline (`expiresAt`/`expiresIn`)
//! or after a number of runs (`maxRuns`). Two surfaces create schedules (the
//! `schedule_create` tool and `session/scheduleCreate`), and before this module
//! they validated separately and disagreed with each other. Everything a
//! definition needs to be *accepted* is decided here, once, by
//! [`build_draft`]; everything it needs to be *launched* is decided here, once,
//! by [`admit`].
//!
//! # Counting rule
//! `maxRuns` bounds **accepted launch attempts**, not successful runs. An
//! attempt counts as soon as the runner has accepted it and a task exists for
//! it — including a run that later fails in the model, the tool layer, or while
//! waiting for a concurrency slot. Only a launch the runner refuses outright
//! (no task registered, an immediate `Err`) is uncounted, because nothing ran.
//! Getting this backwards would let an unreliable environment retry a "run it
//! seven times" job forever.
//!
//! # Deadline rule
//! `expiresAt` is an *exclusive* boundary: an occurrence due exactly at the
//! deadline is not launched. A definition expires when the clock reaches the
//! deadline, not when its next boundary happens to fall beyond it, so an hourly
//! monitor with a Friday deadline is reported expired on Friday rather than at
//! its last Thursday tick.
//!
//! Every decision reads an injected `now`; nothing here calls `Utc::now()`.

use chrono::{DateTime, Duration as CDuration, Utc};
use serde_json::Value;

use super::cron_expression::CronExpression;
use super::schedule_recurrence::ScheduleRecurrence;
use super::scheduled_task::{
    ScheduleDefinitionDraft, ScheduleKind, ScheduleLiveState, ScheduleLiveStatus,
    ScheduleRetirementReason, ScheduledTask,
};

/// Shortest interval a recurring definition may use.
const MIN_INTERVAL_SECS: u64 = 60;

/// Longest relative `expiresIn` we resolve, so an absurd value fails loudly at
/// creation instead of overflowing into a meaningless absolute instant.
const MAX_RELATIVE_DAYS: i64 = 3650;

// ─────────────────────────────────────────────────────────────────────────────
// Duration parsing (shared by `every` and `expiresIn`)
// ─────────────────────────────────────────────────────────────────────────────

/// Parses the `<integer><unit>` duration spelling used by `every` and
/// `expiresIn`, where unit is `m`inutes, `h`ours or `d`ays.
///
/// `field` names the field in the error so one parser can serve both without
/// producing a message that talks about the wrong argument.
pub fn parse_unit_duration(field: &str, text: &str) -> Result<CDuration, String> {
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return Err(format!("'{field}' must be a duration such as 30m, 2h, or 1d."));
    }

    let mut chars = trimmed.chars();
    let unit = chars
        .next_back()
        .map(|c| c.to_ascii_lowercase())
        .filter(|c| matches!(c, 'm' | 'h' | 'd'))
        .ok_or_else(|| {
            format!("'{field}' must end with 'm' (minutes), 'h' (hours), or 'd' (days).")
        })?;

    let amount: i64 = chars
        .as_str()
        .trim()
        .parse()
        .ok()
        .filter(|&n: &i64| n > 0)
        .ok_or_else(|| {
            format!("'{field}' must be a positive whole number of minutes, hours, or days.")
        })?;

    let minutes_per_unit = match unit {
        'm' => 1i64,
        'h' => 60,
        _ => 24 * 60,
    };

    // Checked all the way: a caller-supplied count times a unit must not wrap.
    let minutes = amount
        .checked_mul(minutes_per_unit)
        .ok_or_else(|| format!("'{field}' is too large to represent."))?;
    if minutes > MAX_RELATIVE_DAYS * 24 * 60 {
        return Err(format!("'{field}' must be at most {MAX_RELATIVE_DAYS} days."));
    }
    CDuration::try_minutes(minutes)
        .ok_or_else(|| format!("'{field}' is too large to represent."))
}

// ─────────────────────────────────────────────────────────────────────────────
// Bounds validation
// ─────────────────────────────────────────────────────────────────────────────

/// Validated, absolute bounds for a definition.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ScheduleBounds {
    pub expires_at_utc: Option<DateTime<Utc>>,
    pub max_runs: Option<u32>,
}

impl ScheduleBounds {
    pub fn is_unbounded(&self) -> bool {
        self.expires_at_utc.is_none() && self.max_runs.is_none()
    }
}

/// Resolve `expiresAt` / `expiresIn` / `maxRuns` into absolute bounds.
///
/// `max_runs` arrives as raw JSON on both surfaces so that a non-integer,
/// negative, or out-of-range value is rejected with the *same* message
/// wherever it was supplied, rather than being silently coerced by one
/// deserializer and rejected by the other.
pub fn resolve_bounds(
    expires_at: Option<&str>,
    expires_in: Option<&str>,
    max_runs: Option<&Value>,
    now: DateTime<Utc>,
) -> Result<ScheduleBounds, String> {
    let expires_at = expires_at.map(str::trim).filter(|s| !s.is_empty());
    let expires_in = expires_in.map(str::trim).filter(|s| !s.is_empty());

    if expires_at.is_some() && expires_in.is_some() {
        return Err(
            "'expiresAt' and 'expiresIn' are mutually exclusive; supply at most one.".into(),
        );
    }

    let deadline = if let Some(text) = expires_at {
        let parsed = chrono::DateTime::parse_from_rfc3339(text).map_err(|_| {
            "'expiresAt' must be a valid ISO-8601 date-time with a timezone offset, \
             e.g. '2026-12-25T09:00:00Z'."
                .to_owned()
        })?;
        Some(parsed.with_timezone(&Utc))
    } else if let Some(text) = expires_in {
        let delta = parse_unit_duration("expiresIn", text)?;
        Some(
            now.checked_add_signed(delta)
                .ok_or_else(|| "'expiresIn' is too far in the future to represent.".to_owned())?,
        )
    } else {
        None
    };

    if let Some(deadline) = deadline {
        // The deadline is exclusive, so a deadline of exactly "now" could never
        // admit a single run. Accepting it would create a schedule that is dead
        // on arrival.
        if deadline <= now {
            return Err(
                "'expiresAt' must be in the future; a deadline at or before now would \
                 never allow a run."
                    .into(),
            );
        }
    }

    let max_runs = match max_runs {
        None | Some(Value::Null) => None,
        Some(value) => Some(parse_max_runs(value)?),
    };

    Ok(ScheduleBounds { expires_at_utc: deadline, max_runs })
}

fn parse_max_runs(value: &Value) -> Result<u32, String> {
    const MESSAGE: &str =
        "'maxRuns' must be a whole number of runs between 1 and 4294967295.";

    let number = value.as_number().ok_or_else(|| MESSAGE.to_owned())?;
    // `as_u64` rejects negatives and non-integers (including `7.5` and `1e9`
    // spelled as a float) without any lossy cast of our own.
    let runs = number.as_u64().ok_or_else(|| MESSAGE.to_owned())?;
    if runs == 0 || runs > u32::MAX as u64 {
        return Err(MESSAGE.to_owned());
    }
    Ok(runs as u32)
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared creation path
// ─────────────────────────────────────────────────────────────────────────────

/// Everything either creation surface can supply, before validation.
#[derive(Debug, Default, Clone)]
pub struct ScheduleCreateRequest<'a> {
    pub prompt: Option<&'a str>,
    pub name: Option<&'a str>,
    pub every: Option<&'a str>,
    pub at: Option<&'a str>,
    pub cron: Option<&'a str>,
    pub time_zone: Option<&'a str>,
    pub expires_at: Option<&'a str>,
    pub expires_in: Option<&'a str>,
    pub max_runs: Option<&'a Value>,
}

/// Validate a creation request and produce a normalized draft.
///
/// This is the single validation path: the `schedule_create` tool and
/// `session/scheduleCreate` both call it, so a rule can never hold on one
/// surface and not the other.
pub fn build_draft(
    request: &ScheduleCreateRequest<'_>,
    now: DateTime<Utc>,
) -> Result<ScheduleDefinitionDraft, String> {
    let prompt = request
        .prompt
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .ok_or_else(|| "Missing required 'prompt'.".to_owned())?
        .to_owned();

    let name = request
        .name
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_owned);

    let every = request.every.map(str::trim).filter(|s| !s.is_empty());
    let at = request.at.map(str::trim).filter(|s| !s.is_empty());
    let cron = request.cron.map(str::trim).filter(|s| !s.is_empty());

    let selectors = [every.is_some(), at.is_some(), cron.is_some()]
        .into_iter()
        .filter(|&set| set)
        .count();
    if selectors != 1 {
        return Err("Exactly one of 'every', 'at', or 'cron' is required.".into());
    }

    let bounds = resolve_bounds(request.expires_at, request.expires_in, request.max_runs, now)?;

    let time_zone_id = request
        .time_zone
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .unwrap_or("UTC")
        .to_owned();

    let mut draft = if let Some(every) = every {
        let interval = parse_unit_duration("every", every)?;
        let secs = interval.num_seconds().max(0) as u64;
        if secs < MIN_INTERVAL_SECS {
            return Err("'every' must be at least one minute.".into());
        }
        let next_run_utc = now
            .checked_add_signed(interval)
            .ok_or_else(|| "'every' is too large to schedule.".to_owned())?;
        ScheduleDefinitionDraft {
            name,
            kind: ScheduleKind::Interval,
            prompt,
            interval: Some(std::time::Duration::from_secs(secs)),
            at_utc: None,
            cron: None,
            // An interval is measured in absolute time, never re-interpreted in
            // a calendar zone, so its stored zone stays UTC even when the
            // caller named one for display.
            time_zone_id: "UTC".to_owned(),
            next_run_utc,
            expires_at_utc: bounds.expires_at_utc,
            max_runs: bounds.max_runs,
        }
    } else if let Some(at) = at {
        let parsed = chrono::DateTime::parse_from_rfc3339(at).map_err(|_| {
            "'at' must be a valid ISO-8601 date-time with a timezone offset, \
             e.g. '2026-12-25T09:00:00+00:00'."
                .to_owned()
        })?;
        let at_utc = parsed.with_timezone(&Utc);
        ScheduleDefinitionDraft {
            name,
            kind: ScheduleKind::At,
            prompt,
            interval: None,
            at_utc: Some(at_utc),
            cron: None,
            time_zone_id: "UTC".to_owned(),
            next_run_utc: at_utc,
            expires_at_utc: bounds.expires_at_utc,
            max_runs: bounds.max_runs,
        }
    } else {
        let expression = CronExpression::parse(cron.expect("cron selector"))?;
        let tz: chrono_tz::Tz = time_zone_id
            .parse()
            .map_err(|_| format!("Unknown timezone '{time_zone_id}'."))?;
        let next_run_utc = ScheduleRecurrence::next_cron_occurrence(&expression, now, tz)?;
        ScheduleDefinitionDraft {
            name,
            kind: ScheduleKind::Cron,
            prompt,
            interval: None,
            at_utc: None,
            cron: Some(expression.expression),
            time_zone_id,
            next_run_utc,
            expires_at_utc: bounds.expires_at_utc,
            max_runs: bounds.max_runs,
        }
    };

    // A definition whose very first occurrence already falls outside its
    // deadline can never run. Refusing it at creation is far kinder than
    // accepting it and silently retiring it on the next loop iteration.
    if let Some(deadline) = draft.expires_at_utc {
        if draft.next_run_utc >= deadline {
            return Err(
                "The first occurrence falls at or after the expiry; nothing would ever run."
                    .into(),
            );
        }
    }
    // `at` definitions are one-shot: a run budget above one is meaningless but
    // harmless, so it is normalized rather than rejected.
    if draft.kind == ScheduleKind::At {
        draft.max_runs = draft.max_runs.map(|runs| runs.min(1));
    }

    Ok(draft)
}

// ─────────────────────────────────────────────────────────────────────────────
// Admission
// ─────────────────────────────────────────────────────────────────────────────

/// The decision for one prospective launch.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Admission {
    /// The definition may launch an occurrence now.
    Allow,
    /// The definition will never launch again, for this reason.
    Retire(ScheduleRetirementReason),
}

impl Admission {
    pub fn is_allowed(self) -> bool {
        matches!(self, Self::Allow)
    }

    pub fn retirement(self) -> Option<ScheduleRetirementReason> {
        match self {
            Self::Allow => None,
            Self::Retire(reason) => Some(reason),
        }
    }
}

/// Decide whether `definition` may launch an occurrence at `now`.
///
/// Pure: it reads only the definition and the supplied instant, so the same
/// decision is reachable from the launch path, the post-terminal relaunch
/// path, the advance-while-active path, and reconciliation without any of them
/// being able to drift.
pub fn admit(definition: &ScheduledTask, now: DateTime<Utc>) -> Admission {
    if let Some(retirement) = &definition.retirement {
        return Admission::Retire(retirement.reason);
    }
    if let Some(deadline) = definition.expires_at_utc {
        // Exclusive boundary: an occurrence due exactly at the deadline is out.
        if now >= deadline {
            return Admission::Retire(ScheduleRetirementReason::Expired);
        }
    }
    if let Some(max) = definition.max_runs {
        if definition.runs_started >= max {
            return Admission::Retire(ScheduleRetirementReason::RunLimit);
        }
    }
    Admission::Allow
}

/// The instant the runtime must wake for this definition, independent of any
/// due time.
///
/// A definition with an active run and a far-future next boundary still has to
/// be revisited at its deadline, otherwise an hourly monitor with a Friday
/// deadline would report itself live all weekend. Returns `None` when the
/// definition has no deadline left to observe.
pub fn deadline_wake(
    definition: &ScheduledTask,
    now: DateTime<Utc>,
) -> Option<DateTime<Utc>> {
    if definition.is_retired() {
        return None;
    }
    let deadline = definition.expires_at_utc?;
    (deadline > now).then_some(deadline)
}

/// Clamp and sanitize a model-supplied note before it reaches a stored record
/// or a lifecycle event.
/// Operational surfaces never carry a raw model string: control characters are
/// dropped and the result is clamped, so a note cannot forge log structure or
/// grow without bound.
pub fn sanitize_note(note: &str) -> Option<String> {
    const MAX_NOTE_CHARS: usize = 160;
    let cleaned: String = note
        .chars()
        .map(|c| if c.is_control() { ' ' } else { c })
        .collect::<String>()
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ");
    if cleaned.is_empty() {
        return None;
    }
    Some(cleaned.chars().take(MAX_NOTE_CHARS).collect())
}

// ─────────────────────────────────────────────────────────────────────────────
// Reported state
// ─────────────────────────────────────────────────────────────────────────────

/// The single truthful state vocabulary for a definition, shared by the
/// `schedule_list` tool, `session/scheduleList` and the TUI browser.
///
/// Order matters: an in-flight run is reported before any retirement verdict,
/// because a definition at its limit with work still running is *retiring*,
/// not finished. Saying "completed" while the agent is mid-turn is the exact
/// dishonesty this vocabulary exists to prevent — and a hardcoded `"idle"`
/// (what the wire helper used to emit for everything) is the same lie for
/// every other state.
pub fn reported_state(
    definition: &ScheduledTask,
    live: &ScheduleLiveState,
    now: DateTime<Utc>,
) -> &'static str {
    match live.status {
        ScheduleLiveStatus::Faulted if live.active_task_id.is_some() => return "retiring",
        ScheduleLiveStatus::Retiring => return "retiring",
        ScheduleLiveStatus::Pending => return "pending",
        ScheduleLiveStatus::Running => {
            return if admit(definition, now).is_allowed() { "running" } else { "retiring" }
        }
        ScheduleLiveStatus::Idle | ScheduleLiveStatus::Faulted => {}
    }

    match admit(definition, now) {
        Admission::Allow if live.status == ScheduleLiveStatus::Faulted => "failed",
        Admission::Allow => "idle",
        Admission::Retire(reason) => reason.as_wire(),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::scheduling::{ScheduleRetirement, ScheduledTaskStore};
    use serde_json::json;

    fn now() -> DateTime<Utc> {
        "2087-03-01T00:00:00Z".parse().unwrap()
    }

    fn definition(bounds: ScheduleBounds, runs_started: u32) -> ScheduledTask {
        let store = ScheduledTaskStore::new();
        let task = store.add(
            ScheduleDefinitionDraft {
                name: None,
                kind: ScheduleKind::Interval,
                prompt: "watch".into(),
                interval: Some(std::time::Duration::from_secs(3600)),
                at_utc: None,
                cron: None,
                time_zone_id: "UTC".into(),
                next_run_utc: now(),
                expires_at_utc: bounds.expires_at_utc,
                max_runs: bounds.max_runs,
            },
            now(),
        );
        ScheduledTask { runs_started, ..task }
    }

    // ── parse_unit_duration ───────────────────────────────────────────────────

    #[test]
    fn unit_durations_accept_minutes_hours_and_days() {
        assert_eq!(parse_unit_duration("every", "30m").unwrap(), CDuration::minutes(30));
        assert_eq!(parse_unit_duration("every", "2h").unwrap(), CDuration::hours(2));
        assert_eq!(parse_unit_duration("expiresIn", "7d").unwrap(), CDuration::days(7));
        assert_eq!(parse_unit_duration("every", " 15M ").unwrap(), CDuration::minutes(15));
    }

    #[test]
    fn unit_durations_reject_junk_zero_negative_and_overflow() {
        for bad in ["", "  ", "abc", "30", "30s", "0m", "-5h", "1.5h", "9223372036854775807d"] {
            let error = parse_unit_duration("expiresIn", bad)
                .expect_err("{bad} must be rejected");
            assert!(
                error.contains("expiresIn"),
                "the error must name the field the caller supplied: {error}"
            );
        }
    }

    #[test]
    fn an_absurd_relative_duration_fails_loudly_rather_than_overflowing() {
        let error = parse_unit_duration("expiresIn", "40000d").unwrap_err();
        assert!(error.contains("at most"), "{error}");
    }

    // ── resolve_bounds ────────────────────────────────────────────────────────

    #[test]
    fn absent_bounds_are_unlimited() {
        let bounds = resolve_bounds(None, None, None, now()).unwrap();
        assert!(bounds.is_unbounded());
    }

    #[test]
    fn a_future_absolute_deadline_is_accepted_verbatim() {
        let bounds =
            resolve_bounds(Some("2087-03-08T00:00:00Z"), None, None, now()).unwrap();
        assert_eq!(
            bounds.expires_at_utc,
            Some("2087-03-08T00:00:00Z".parse::<DateTime<Utc>>().unwrap())
        );
    }

    #[test]
    fn a_relative_deadline_is_resolved_once_into_an_absolute_instant() {
        let bounds = resolve_bounds(None, Some("7d"), None, now()).unwrap();
        assert_eq!(bounds.expires_at_utc, Some(now() + CDuration::days(7)));
    }

    #[test]
    fn a_past_deadline_is_rejected() {
        let error = resolve_bounds(Some("2087-02-28T23:59:59Z"), None, None, now()).unwrap_err();
        assert!(error.contains("future"), "{error}");
    }

    /// The deadline is exclusive, so "expires exactly now" could never admit a
    /// run; accepting it would create a schedule that is dead on arrival.
    #[test]
    fn a_deadline_equal_to_now_is_rejected() {
        let error = resolve_bounds(Some("2087-03-01T00:00:00Z"), None, None, now()).unwrap_err();
        assert!(error.contains("future"), "{error}");
    }

    #[test]
    fn expires_at_and_expires_in_are_mutually_exclusive() {
        let error =
            resolve_bounds(Some("2087-03-08T00:00:00Z"), Some("7d"), None, now()).unwrap_err();
        assert!(error.contains("mutually exclusive"), "{error}");
    }

    #[test]
    fn an_invalid_date_is_rejected_safely() {
        for bad in ["not-a-date", "2087-13-45T00:00:00Z", "2087-03-08", "tomorrow"] {
            assert!(resolve_bounds(Some(bad), None, None, now()).is_err(), "{bad}");
        }
    }

    #[test]
    fn a_positive_run_budget_is_accepted() {
        let bounds = resolve_bounds(None, None, Some(&json!(7)), now()).unwrap();
        assert_eq!(bounds.max_runs, Some(7));
    }

    #[test]
    fn the_whole_u32_budget_is_available_without_an_arbitrary_cap() {
        let bounds = resolve_bounds(None, None, Some(&json!(u32::MAX)), now()).unwrap();
        assert_eq!(bounds.max_runs, Some(u32::MAX));
    }

    #[test]
    fn zero_negative_fractional_and_out_of_range_budgets_are_rejected() {
        for bad in [json!(0), json!(-1), json!(-7), json!(7.5), json!(4294967296u64),
                    json!("7"), json!(true), json!([7])] {
            let error = resolve_bounds(None, None, Some(&bad), now())
                .expect_err("must reject {bad}");
            assert!(error.contains("maxRuns"), "{error}");
        }
    }

    #[test]
    fn an_explicit_null_budget_means_unlimited() {
        let bounds = resolve_bounds(None, None, Some(&Value::Null), now()).unwrap();
        assert_eq!(bounds.max_runs, None);
    }

    #[test]
    fn both_bounds_may_be_supplied_together() {
        let bounds = resolve_bounds(None, Some("7d"), Some(&json!(7)), now()).unwrap();
        assert_eq!(bounds.max_runs, Some(7));
        assert_eq!(bounds.expires_at_utc, Some(now() + CDuration::days(7)));
    }

    // ── build_draft ───────────────────────────────────────────────────────────

    fn request<'a>() -> ScheduleCreateRequest<'a> {
        ScheduleCreateRequest { prompt: Some("check the build"), ..Default::default() }
    }

    #[test]
    fn an_hourly_monitor_bounded_by_a_week_is_accepted() {
        let draft = build_draft(
            &ScheduleCreateRequest { every: Some("1h"), expires_in: Some("7d"), ..request() },
            now(),
        )
        .unwrap();
        assert_eq!(draft.kind, ScheduleKind::Interval);
        assert_eq!(draft.next_run_utc, now() + CDuration::hours(1));
        assert_eq!(draft.expires_at_utc, Some(now() + CDuration::days(7)));
        assert_eq!(draft.max_runs, None);
    }

    /// The user's "daily for a week" is a run budget of seven, not a duration.
    #[test]
    fn a_daily_job_for_a_week_is_a_budget_of_seven() {
        let draft = build_draft(
            &ScheduleCreateRequest {
                cron: Some("0 9 * * *"),
                max_runs: Some(&json!(7)),
                ..request()
            },
            now(),
        )
        .unwrap();
        assert_eq!(draft.kind, ScheduleKind::Cron);
        assert_eq!(draft.max_runs, Some(7));
        assert_eq!(draft.expires_at_utc, None);
    }

    #[test]
    fn exactly_one_selector_is_still_required_with_bounds_present() {
        for selectors in [
            ScheduleCreateRequest { max_runs: Some(&json!(7)), ..request() },
            ScheduleCreateRequest {
                every: Some("1h"),
                cron: Some("* * * * *"),
                max_runs: Some(&json!(7)),
                ..request()
            },
        ] {
            let error = build_draft(&selectors, now()).unwrap_err();
            assert!(error.contains("Exactly one"), "{error}");
        }
    }

    #[test]
    fn a_definition_whose_first_run_is_past_the_deadline_is_refused() {
        let error = build_draft(
            &ScheduleCreateRequest {
                every: Some("2d"),
                expires_in: Some("1d"),
                ..request()
            },
            now(),
        )
        .unwrap_err();
        assert!(error.contains("nothing would ever run"), "{error}");
    }

    #[test]
    fn an_invalid_cron_or_timezone_is_rejected_before_any_bound_is_applied() {
        assert!(build_draft(
            &ScheduleCreateRequest { cron: Some("not-a-cron"), ..request() },
            now()
        )
        .is_err());
        assert!(build_draft(
            &ScheduleCreateRequest {
                cron: Some("0 9 * * *"),
                time_zone: Some("Mars/Olympus"),
                ..request()
            },
            now()
        )
        .is_err());
    }

    /// A cron definition keeps its IANA zone; an interval never acquires one,
    /// because a fixed interval is absolute time and is not re-interpreted
    /// across a DST boundary.
    #[test]
    fn a_cron_keeps_its_zone_while_an_interval_stays_utc() {
        let cron = build_draft(
            &ScheduleCreateRequest {
                cron: Some("0 9 * * *"),
                time_zone: Some("America/New_York"),
                ..request()
            },
            now(),
        )
        .unwrap();
        assert_eq!(cron.time_zone_id, "America/New_York");

        let interval = build_draft(
            &ScheduleCreateRequest {
                every: Some("24h"),
                time_zone: Some("America/New_York"),
                ..request()
            },
            now(),
        )
        .unwrap();
        assert_eq!(interval.time_zone_id, "UTC");
    }

    /// A daily cron in a DST-observing zone keeps its 09:00 wall-clock meaning
    /// across the spring-forward weekend; a 24h interval does not, which is
    /// exactly why the two are documented as different tools.
    #[test]
    fn a_daily_cron_across_a_dst_transition_stays_on_its_wall_clock_hour() {
        use chrono::{TimeZone, Timelike};

        let tz: chrono_tz::Tz = "America/New_York".parse().unwrap();
        // 2087-03-08 is the second Sunday in March: US spring-forward.
        let before = Utc.with_ymd_and_hms(2087, 3, 7, 15, 0, 0).unwrap();
        let draft = build_draft(
            &ScheduleCreateRequest {
                cron: Some("0 9 * * *"),
                time_zone: Some("America/New_York"),
                max_runs: Some(&json!(7)),
                ..request()
            },
            before,
        )
        .unwrap();
        let local = draft.next_run_utc.with_timezone(&tz);
        assert_eq!(
            (local.hour(), local.minute()),
            (9, 0),
            "the wall-clock hour must survive the transition; got {local}"
        );
    }

    #[test]
    fn a_one_shot_budget_is_normalized_to_a_single_run() {
        let draft = build_draft(
            &ScheduleCreateRequest {
                at: Some("2087-03-02T09:00:00Z"),
                max_runs: Some(&json!(9)),
                ..request()
            },
            now(),
        )
        .unwrap();
        assert_eq!(draft.max_runs, Some(1));
    }

    #[test]
    fn a_missing_prompt_is_rejected_on_the_shared_path() {
        let error = build_draft(
            &ScheduleCreateRequest { prompt: Some("   "), every: Some("1h"), ..Default::default() },
            now(),
        )
        .unwrap_err();
        assert!(error.contains("prompt"), "{error}");
    }

    #[test]
    fn an_unbounded_definition_keeps_todays_defaults() {
        let draft =
            build_draft(&ScheduleCreateRequest { every: Some("30m"), ..request() }, now()).unwrap();
        assert_eq!(draft.expires_at_utc, None);
        assert_eq!(draft.max_runs, None);
        assert_eq!(draft.interval, Some(std::time::Duration::from_secs(1800)));
    }

    #[test]
    fn a_sub_minute_interval_is_still_refused() {
        // The unit parser has no sub-minute unit, so this is about the floor
        // applying identically on the shared path.
        let error =
            build_draft(&ScheduleCreateRequest { every: Some("0m"), ..request() }, now())
                .unwrap_err();
        assert!(error.contains("every"), "{error}");
    }

    // ── admit ─────────────────────────────────────────────────────────────────

    #[test]
    fn an_unbounded_definition_is_always_admitted() {
        let task = definition(ScheduleBounds::default(), 9_999);
        assert_eq!(admit(&task, now() + CDuration::days(3650)), Admission::Allow);
    }

    #[test]
    fn a_definition_before_its_deadline_is_admitted() {
        let task = definition(
            ScheduleBounds { expires_at_utc: Some(now() + CDuration::days(7)), max_runs: None },
            0,
        );
        assert_eq!(admit(&task, now() + CDuration::days(6)), Admission::Allow);
    }

    /// The deadline is exclusive: an occurrence due exactly at it is out.
    #[test]
    fn a_definition_exactly_at_its_deadline_is_retired_not_admitted() {
        let deadline = now() + CDuration::days(7);
        let task = definition(
            ScheduleBounds { expires_at_utc: Some(deadline), max_runs: None },
            0,
        );
        assert_eq!(
            admit(&task, deadline),
            Admission::Retire(ScheduleRetirementReason::Expired)
        );
        assert_eq!(
            admit(&task, deadline + CDuration::seconds(1)),
            Admission::Retire(ScheduleRetirementReason::Expired)
        );
    }

    #[test]
    fn a_spent_run_budget_retires_the_definition() {
        let task = definition(ScheduleBounds { expires_at_utc: None, max_runs: Some(7) }, 7);
        assert_eq!(
            admit(&task, now()),
            Admission::Retire(ScheduleRetirementReason::RunLimit)
        );
    }

    #[test]
    fn the_last_allowed_run_is_still_admitted() {
        let task = definition(ScheduleBounds { expires_at_utc: None, max_runs: Some(7) }, 6);
        assert_eq!(admit(&task, now()), Admission::Allow);
    }

    /// Both bounds present: whichever binds first wins, and the reason is the
    /// one that actually stopped it.
    #[test]
    fn the_first_binding_limit_supplies_the_retirement_reason() {
        let deadline = now() + CDuration::days(7);
        let expired = definition(
            ScheduleBounds { expires_at_utc: Some(deadline), max_runs: Some(99) },
            3,
        );
        assert_eq!(
            admit(&expired, deadline),
            Admission::Retire(ScheduleRetirementReason::Expired)
        );

        let spent = definition(
            ScheduleBounds { expires_at_utc: Some(deadline), max_runs: Some(3) },
            3,
        );
        assert_eq!(
            admit(&spent, now()),
            Admission::Retire(ScheduleRetirementReason::RunLimit)
        );
    }

    /// Once retired, the recorded reason is authoritative and sticky — a later
    /// evaluation cannot re-derive a different one or re-admit the definition.
    #[test]
    fn a_recorded_retirement_is_sticky_and_keeps_its_reason() {
        let mut task = definition(ScheduleBounds::default(), 0);
        task.retirement = Some(ScheduleRetirement {
            reason: ScheduleRetirementReason::Cancelled,
            retired_at_utc: now(),
            note: None,
        });
        assert_eq!(
            admit(&task, now() + CDuration::days(365)),
            Admission::Retire(ScheduleRetirementReason::Cancelled)
        );
    }

    // ── deadline_wake ─────────────────────────────────────────────────────────

    #[test]
    fn a_deadline_supplies_a_wake_independent_of_the_next_run() {
        let deadline = now() + CDuration::hours(3);
        let mut task = definition(
            ScheduleBounds { expires_at_utc: Some(deadline), max_runs: None },
            0,
        );
        // Next boundary is a year out; the deadline is what matters.
        task.next_run_utc = now() + CDuration::days(365);
        assert_eq!(deadline_wake(&task, now()), Some(deadline));
    }

    #[test]
    fn a_passed_or_retired_deadline_asks_for_no_further_wake() {
        let deadline = now() + CDuration::hours(3);
        let task = definition(
            ScheduleBounds { expires_at_utc: Some(deadline), max_runs: None },
            0,
        );
        assert_eq!(deadline_wake(&task, deadline), None);

        let unbounded = definition(ScheduleBounds::default(), 0);
        assert_eq!(deadline_wake(&unbounded, now()), None);

        let mut retired = task.clone();
        retired.retirement = Some(ScheduleRetirement {
            reason: ScheduleRetirementReason::Cancelled,
            retired_at_utc: now(),
            note: None,
        });
        assert_eq!(deadline_wake(&retired, now()), None);
    }

    // ── sanitize_note ─────────────────────────────────────────────────────────

    #[test]
    fn notes_are_clamped_and_stripped_of_control_characters() {
        assert_eq!(sanitize_note("  all clear  ").as_deref(), Some("all clear"));
        assert_eq!(
            sanitize_note("line\nbreak\tand\r\nmore").as_deref(),
            Some("line break and more")
        );
        assert_eq!(sanitize_note("   ").as_deref(), None);
        let long = "x".repeat(500);
        assert_eq!(sanitize_note(&long).unwrap().chars().count(), 160);
    }

    // ── reported_state ────────────────────────────────────────────────────────

    fn live(status: ScheduleLiveStatus) -> ScheduleLiveState {
        ScheduleLiveState { status, active_task_id: Some("task-0001".into()) }
    }

    #[test]
    fn a_faulted_schedule_does_not_claim_its_active_run_has_finished() {
        let task = definition(ScheduleBounds::default(), 1);
        assert_eq!(reported_state(&task, &live(ScheduleLiveStatus::Faulted), now()), "retiring");
        let idle_fault = ScheduleLiveState { status: ScheduleLiveStatus::Faulted, active_task_id: None };
        assert_eq!(reported_state(&task, &idle_fault, now()), "failed");
    }

    #[test]
    fn an_idle_unbounded_definition_reports_idle() {
        let task = definition(ScheduleBounds::default(), 0);
        assert_eq!(reported_state(&task, &ScheduleLiveState::default(), now()), "idle");
    }

    #[test]
    fn a_live_run_is_reported_as_running_or_pending_not_idle() {
        let task = definition(ScheduleBounds::default(), 1);
        assert_eq!(reported_state(&task, &live(ScheduleLiveStatus::Running), now()), "running");
        assert_eq!(reported_state(&task, &live(ScheduleLiveStatus::Pending), now()), "pending");
    }

    /// The last allowed run, still executing. "completed" here would tell a
    /// reader the work is done while the agent is still mid-turn.
    #[test]
    fn a_run_that_has_spent_the_budget_reports_retiring_while_it_still_works() {
        let task = definition(ScheduleBounds { expires_at_utc: None, max_runs: Some(7) }, 7);
        assert_eq!(reported_state(&task, &live(ScheduleLiveStatus::Running), now()), "retiring");
        assert_eq!(reported_state(&task, &live(ScheduleLiveStatus::Retiring), now()), "retiring");
    }

    #[test]
    fn a_settled_definition_reports_why_it_will_never_run_again() {
        let spent = definition(ScheduleBounds { expires_at_utc: None, max_runs: Some(7) }, 7);
        assert_eq!(reported_state(&spent, &ScheduleLiveState::default(), now()), "completed");

        let deadline = now() + CDuration::hours(1);
        let expired = definition(
            ScheduleBounds { expires_at_utc: Some(deadline), max_runs: None },
            0,
        );
        assert_eq!(
            reported_state(&expired, &ScheduleLiveState::default(), deadline),
            "expired"
        );

        let mut cancelled = definition(ScheduleBounds::default(), 0);
        cancelled.retirement = Some(ScheduleRetirement {
            reason: ScheduleRetirementReason::Cancelled,
            retired_at_utc: now(),
            note: None,
        });
        assert_eq!(
            reported_state(&cancelled, &ScheduleLiveState::default(), now()),
            "cancelled"
        );

        let mut failed = definition(ScheduleBounds::default(), 0);
        failed.retirement = Some(ScheduleRetirement {
            reason: ScheduleRetirementReason::LaunchFailed,
            retired_at_utc: now(),
            note: None,
        });
        assert_eq!(reported_state(&failed, &ScheduleLiveState::default(), now()), "failed");
    }
}
