//! Five-field cron expression parser and evaluator (minute hour dom month dow).
//!
//! Evaluation is timezone-neutral: [`CronExpression::matches`] tests a wall-clock
//! minute. Timezone conversion lives in [`super::schedule_recurrence`].
//!
//! **Invalid expressions are rejected with a descriptive error.** There is no
//! silent never-firing fallback — a malformed expression is a caller bug that must
//! surface immediately.

use std::collections::BTreeSet;

use chrono::{Datelike, Timelike};

/// A parsed, validated five-field cron expression.
#[derive(Clone, Debug)]
pub struct CronExpression {
    /// The normalized expression (single-space separated fields).
    pub expression: String,
    minutes: Vec<u32>,
    hours: Vec<u32>,
    days_of_month: Vec<u32>,
    months: Vec<u32>,
    days_of_week: Vec<u32>,
    dom_restricted: bool,
    dow_restricted: bool,
}

impl CronExpression {
    /// Parse a five-field cron expression. Returns `Err` with a descriptive
    /// message on any failure — never silently produces a never-firing expression.
    pub fn parse(expr: &str) -> Result<Self, String> {
        let trimmed = expr.trim();
        if trimmed.is_empty() {
            return Err("Expression must not be empty.".into());
        }
        let parts: Vec<&str> = trimmed.split_whitespace().collect();
        if parts.len() != 5 {
            return Err(format!(
                "Expected 5 fields (min hour dom month dow) but got {}.",
                parts.len()
            ));
        }

        let minutes = parse_field(parts[0], 0, 59)
            .map_err(|e| format!("minute field: {e}"))?;
        let hours = parse_field(parts[1], 0, 23)
            .map_err(|e| format!("hour field: {e}"))?;
        let days_of_month = parse_field(parts[2], 1, 31)
            .map_err(|e| format!("day-of-month field: {e}"))?;
        let months = parse_field(parts[3], 1, 12)
            .map_err(|e| format!("month field: {e}"))?;
        let days_of_week = parse_field(parts[4], 0, 6)
            .map_err(|e| format!("day-of-week field: {e}"))?;

        let normalized = parts.join(" ");
        let dom_restricted = parts[2].trim() != "*";
        let dow_restricted = parts[4].trim() != "*";

        Ok(Self {
            expression: normalized,
            minutes,
            hours,
            days_of_month,
            months,
            days_of_week,
            dom_restricted,
            dow_restricted,
        })
    }

    /// Returns `true` when `local_minute` matches this expression using standard
    /// cron day semantics (dom OR dow when both restricted; respective field when
    /// only one is restricted; every day when neither is restricted).
    pub fn matches(&self, local_minute: &chrono::NaiveDateTime) -> bool {
        self.matches_date(local_minute) && self.matches_time(local_minute)
    }

    /// Tests only the month and day fields.
    pub fn matches_date(&self, dt: &chrono::NaiveDateTime) -> bool {
        if !self.months.contains(&(dt.month())) {
            return false;
        }
        let dom_match = self.days_of_month.contains(&dt.day());
        let dow_match = self.days_of_week.contains(&(dt.weekday().num_days_from_sunday()));

        match (self.dom_restricted, self.dow_restricted) {
            (true, true) => dom_match || dow_match,
            (true, false) => dom_match,
            (false, true) => dow_match,
            (false, false) => true,
        }
    }

    /// Tests only the hour and minute fields.
    pub fn matches_time(&self, dt: &chrono::NaiveDateTime) -> bool {
        self.hours.contains(&dt.hour()) && self.minutes.contains(&dt.minute())
    }
}

// ── Field parser ──────────────────────────────────────────────────────────────

fn parse_field(field: &str, min: u32, max: u32) -> Result<Vec<u32>, String> {
    let mut set = BTreeSet::new();
    for element in field.split(',') {
        parse_element(element.trim(), min, max, &mut set)?;
    }
    if set.is_empty() {
        return Err(format!("Field '{field}' produced no values in range [{min},{max}]."));
    }
    Ok(set.into_iter().collect())
}

fn parse_element(element: &str, min: u32, max: u32, set: &mut BTreeSet<u32>) -> Result<(), String> {
    // Handle step: */n or a-b/n
    let (range_or_wild, step) = if let Some(slash_idx) = element.find('/') {
        let step_str = &element[slash_idx + 1..];
        let step: u32 = step_str.parse().map_err(|_| {
            format!("Invalid step value '{step_str}' in '{element}'; step must be a positive integer.")
        })?;
        if step < 1 {
            return Err(format!(
                "Invalid step value '{step_str}' in '{element}'; step must be a positive integer."
            ));
        }
        (&element[..slash_idx], Some(step))
    } else {
        (element, None)
    };

    let (from, to) = if range_or_wild == "*" {
        (min, max)
    } else if let Some(dash_idx) = range_or_wild.find('-') {
        let from_str = &range_or_wild[..dash_idx];
        let to_str = &range_or_wild[dash_idx + 1..];
        let from: u32 = from_str.parse().map_err(|_| {
            format!("Invalid range '{range_or_wild}'; expected integers on both sides of '-'.")
        })?;
        let to: u32 = to_str.parse().map_err(|_| {
            format!("Invalid range '{range_or_wild}'; expected integers on both sides of '-'.")
        })?;
        if from < min || from > max {
            return Err(format!("Range start {from} is out of [{min},{max}] in '{element}'."));
        }
        if to < min || to > max {
            return Err(format!("Range end {to} is out of [{min},{max}] in '{element}'."));
        }
        if from > to {
            return Err(format!(
                "Range start {from} must be <= range end {to} in '{element}'."
            ));
        }
        (from, to)
    } else {
        let n: u32 = range_or_wild.parse().map_err(|_| {
            format!("Invalid value '{range_or_wild}'; expected an integer, '*', or a range.")
        })?;
        if n < min || n > max {
            return Err(format!("Value {n} is out of [{min},{max}] in '{element}'."));
        }
        (n, n)
    };

    let increment = step.unwrap_or(1);
    let mut v = from;
    while v <= to {
        set.insert(v);
        match v.checked_add(increment) {
            Some(next) if next <= to => v = next,
            _ => break,
        }
    }

    Ok(())
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::NaiveDateTime;

    fn dt(y: i32, mo: u32, d: u32, h: u32, min: u32) -> NaiveDateTime {
        NaiveDateTime::new(
            chrono::NaiveDate::from_ymd_opt(y, mo, d).unwrap(),
            chrono::NaiveTime::from_hms_opt(h, min, 0).unwrap(),
        )
    }

    // ── parsing ────────────────────────────────────────────────────────────────

    #[test]
    fn parses_every_minute() {
        let c = CronExpression::parse("* * * * *").unwrap();
        assert_eq!(c.minutes.len(), 60);
        assert_eq!(c.hours.len(), 24);
    }

    #[test]
    fn parses_specific_values() {
        let c = CronExpression::parse("30 9 * * *").unwrap();
        assert_eq!(c.minutes, vec![30]);
        assert_eq!(c.hours, vec![9]);
    }

    #[test]
    fn parses_range() {
        let c = CronExpression::parse("0-29 * * * *").unwrap();
        assert_eq!(c.minutes.len(), 30);
    }

    #[test]
    fn parses_step() {
        let c = CronExpression::parse("*/15 * * * *").unwrap();
        assert_eq!(c.minutes, vec![0, 15, 30, 45]);
    }

    #[test]
    fn parses_list() {
        let c = CronExpression::parse("1,15,30 * * * *").unwrap();
        assert_eq!(c.minutes, vec![1, 15, 30]);
    }

    #[test]
    fn normalized_expression_uses_single_spaces() {
        let c = CronExpression::parse("  *   *  *  *  *  ").unwrap();
        assert_eq!(c.expression, "* * * * *");
    }

    // ── rejection ──────────────────────────────────────────────────────────────

    #[test]
    fn empty_expression_is_rejected() {
        assert!(CronExpression::parse("").is_err());
        assert!(CronExpression::parse("   ").is_err());
    }

    #[test]
    fn wrong_field_count_is_rejected() {
        assert!(CronExpression::parse("* * * *").is_err()); // 4 fields
        assert!(CronExpression::parse("* * * * * *").is_err()); // 6 fields
    }

    #[test]
    fn out_of_range_minute_is_rejected() {
        assert!(CronExpression::parse("60 * * * *").is_err());
    }

    #[test]
    fn out_of_range_hour_is_rejected() {
        assert!(CronExpression::parse("* 24 * * *").is_err());
    }

    #[test]
    fn out_of_range_dow_is_rejected() {
        assert!(CronExpression::parse("* * * * 7").is_err());
    }

    #[test]
    fn invalid_step_is_rejected() {
        assert!(CronExpression::parse("*/0 * * * *").is_err());
        assert!(CronExpression::parse("*/abc * * * *").is_err());
    }

    #[test]
    fn inverted_range_is_rejected() {
        assert!(CronExpression::parse("30-10 * * * *").is_err());
    }

    #[test]
    fn non_numeric_value_is_rejected() {
        assert!(CronExpression::parse("abc * * * *").is_err());
    }

    // ── matching ───────────────────────────────────────────────────────────────

    #[test]
    fn wildcard_matches_every_minute() {
        let c = CronExpression::parse("* * * * *").unwrap();
        assert!(c.matches(&dt(2024, 1, 1, 0, 0)));
        assert!(c.matches(&dt(2024, 12, 31, 23, 59)));
    }

    #[test]
    fn specific_minute_and_hour_matches() {
        let c = CronExpression::parse("30 9 * * *").unwrap();
        assert!(c.matches(&dt(2024, 6, 15, 9, 30)));
        assert!(!c.matches(&dt(2024, 6, 15, 9, 31)));
        assert!(!c.matches(&dt(2024, 6, 15, 10, 30)));
    }

    #[test]
    fn dom_only_restriction() {
        // First of every month at midnight.
        let c = CronExpression::parse("0 0 1 * *").unwrap();
        assert!(c.matches(&dt(2024, 3, 1, 0, 0)));
        assert!(!c.matches(&dt(2024, 3, 2, 0, 0)));
    }

    #[test]
    fn dow_only_restriction() {
        // Every Monday at noon (dow=1 for Monday in 0=Sunday convention).
        let c = CronExpression::parse("0 12 * * 1").unwrap();
        // 2024-01-01 is a Monday.
        assert!(c.matches(&dt(2024, 1, 1, 12, 0)));
        assert!(!c.matches(&dt(2024, 1, 2, 12, 0))); // Tuesday
    }

    #[test]
    fn both_dom_and_dow_restricted_uses_or_semantics() {
        // 15th of the month OR every Friday.
        let c = CronExpression::parse("0 0 15 * 5").unwrap();
        // 2024-01-15 is a Monday, but dom=15 matches.
        assert!(c.matches(&dt(2024, 1, 15, 0, 0)));
        // 2024-01-12 is a Friday, dow=5 matches.
        assert!(c.matches(&dt(2024, 1, 12, 0, 0)));
        // 2024-01-10 is a Wednesday and not the 15th — should not match.
        assert!(!c.matches(&dt(2024, 1, 10, 0, 0)));
    }

    #[test]
    fn month_restriction() {
        let c = CronExpression::parse("0 0 1 6 *").unwrap();
        assert!(c.matches(&dt(2024, 6, 1, 0, 0)));
        assert!(!c.matches(&dt(2024, 7, 1, 0, 0)));
    }
}
