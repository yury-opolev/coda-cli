//! The `/context` and `/cost` reports.
//!
//! Split from the event loop because it is presentation, not orchestration:
//! every function here is pure over [`Usage`], so what the reports *claim* can
//! be asserted without building an `App` — which needs a real engine.
//!
//! The reports keep two quantities visibly apart, because conflating them is
//! what made the old output misleading:
//!
//! - **The context window** is what one request carries. Each request re-sends
//!   the whole conversation, so the last request's input tokens are, near
//!   enough, what is in the window right now.
//! - **Session totals** are every request's tokens added up. They are what the
//!   session has cost. They pass the window size after a handful of turns and
//!   say nothing about how full it is.

use crate::render::glyphs;
use crate::state::Usage;

/// Width of the proportional bar, in cells.
const BAR_CELLS: usize = 40;

/// Renders the `/context` report.
///
/// `model` is the active model's label, or `None` when it is not yet known.
pub(super) fn context_report(usage: &Usage, model: Option<&str>) -> String {
    let mut out = String::from("Context usage\n");
    out.push_str(&format!("  model    {}\n", model.unwrap_or("(unknown)")));

    match (usage.last_input_tokens, usage.context_limit) {
        // Nothing has been measured. Saying "0%" here would be a claim, and a
        // false one: the window already holds the system prompt and the tool
        // definitions before a single message is sent.
        (0, _) => out.push_str("  window   (nothing measured yet — send a message first)\n"),
        (used, limit) if limit > 0 => {
            let percent = usage.percent_used().unwrap_or(0);
            out.push_str(&format!(
                "  window   {} of {} tokens ({percent}%)\n",
                thousands(used),
                thousands(limit),
            ));
            out.push_str(&format!("  {}\n", bar(percent)));
            out.push_str(&format!("  free     {} tokens\n", thousands((limit - used).max(0))));
        }
        // A measurement with no window to measure it against. Reported as the
        // bare figure rather than dropped, which is what the status bar has to
        // do for want of room.
        (used, _) => out.push_str(&format!(
            "  window   {} tokens in the last request (window size unknown)\n",
            thousands(used),
        )),
    }

    out.push_str(&format!("\n{}", session_totals(usage)));
    out
}

/// Renders the `/cost` report: session totals only.
pub(super) fn cost_report(usage: &Usage) -> String {
    session_totals(usage)
}

/// The cumulative block, shared by both reports and labelled so it cannot be
/// read as the window.
fn session_totals(usage: &Usage) -> String {
    let mut out = String::from("Session totals (every request)\n");
    out.push_str(&format!("  input    {} tokens\n", thousands(usage.input_tokens)));
    out.push_str(&format!("  output   {} tokens\n", thousands(usage.output_tokens)));
    out.push_str(&format!(
        "  total    {} tokens",
        thousands(usage.input_tokens + usage.output_tokens),
    ));
    // Only when the catalogue prices this model: "$0.00" on an unpriced model
    // reads as free rather than as unknown.
    if let Some(cost) = usage.estimated_cost() {
        out.push_str(&format!("\n  cost     ${cost:.2}"));
    }
    out
}

/// A proportional bar, `percent` full.
///
/// Rounds the filled length rather than truncating, but never shows an empty
/// bar for a non-zero percentage: a window that is 1% full is not empty, and a
/// bar that says it is would be the one thing the reader takes away.
fn bar(percent: u8) -> String {
    let percent = percent.min(100) as usize;
    let mut filled = (percent * BAR_CELLS).div_ceil(100);
    if percent > 0 {
        filled = filled.max(1);
    }
    let filled = filled.min(BAR_CELLS);
    format!(
        "{}{}",
        glyphs::BLOCK.repeat(filled),
        glyphs::BLOCK_EMPTY.repeat(BAR_CELLS - filled),
    )
}

/// Groups digits in threes, so six- and seven-figure token counts can be told
/// apart at a glance.
fn thousands(n: i64) -> String {
    let negative = n < 0;
    let digits = n.unsigned_abs().to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3 + 1);
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i) % 3 == 0 {
            out.push(',');
        }
        out.push(c);
    }
    if negative { format!("-{out}") } else { out }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn usage() -> Usage {
        Usage {
            input_tokens: 900_000,
            output_tokens: 20_000,
            last_input_tokens: 40_000,
            context_limit: 200_000,
            price_per_million: None,
        }
    }

    #[test]
    fn thousands_groups_digits() {
        assert_eq!(thousands(0), "0");
        assert_eq!(thousands(42), "42");
        assert_eq!(thousands(1_000), "1,000");
        assert_eq!(thousands(200_000), "200,000");
        assert_eq!(thousands(1_048_576), "1,048,576");
        assert_eq!(thousands(-1_500), "-1,500");
    }

    #[test]
    fn the_bar_is_proportional_and_always_its_full_width() {
        for percent in [0u8, 1, 25, 50, 99, 100] {
            let rendered = bar(percent);
            assert_eq!(
                rendered.chars().count(),
                BAR_CELLS,
                "a {percent}% bar must still be {BAR_CELLS} cells wide",
            );
        }
        assert_eq!(bar(0).matches(glyphs::BLOCK).count(), 0);
        assert_eq!(bar(100).matches(glyphs::BLOCK).count(), BAR_CELLS);
        assert_eq!(bar(50).matches(glyphs::BLOCK).count(), BAR_CELLS / 2);
    }

    /// A window that is barely used is not an empty window.
    #[test]
    fn a_non_zero_percentage_always_shows_at_least_one_filled_cell() {
        assert!(bar(1).starts_with(glyphs::BLOCK), "1% must not render as empty");
    }

    /// The whole point of the rewrite: the cumulative total is nearly five
    /// times the window, and must never be presented as the window's contents.
    #[test]
    fn the_report_separates_the_window_from_the_session_total() {
        let report = context_report(&usage(), Some("claude-opus-5"));

        assert!(report.contains("40,000 of 200,000 tokens (20%)"), "{report}");
        assert!(report.contains("free     160,000 tokens"), "{report}");
        assert!(report.contains("Session totals (every request)"), "{report}");
        assert!(report.contains("input    900,000 tokens"), "{report}");
        assert!(report.contains("total    920,000 tokens"), "{report}");
        assert!(report.contains("claude-opus-5"), "{report}");
    }

    #[test]
    fn nothing_measured_is_reported_as_unknown_not_as_zero_percent() {
        let usage = Usage { last_input_tokens: 0, ..usage() };
        let report = context_report(&usage, Some("m"));
        assert!(report.contains("nothing measured yet"), "{report}");
        assert!(!report.contains("(0%)"), "an unmeasured window is not an empty one: {report}");
    }

    #[test]
    fn an_unknown_window_still_reports_what_was_measured() {
        let usage = Usage { context_limit: 0, ..usage() };
        let report = context_report(&usage, None);
        assert!(report.contains("40,000 tokens in the last request"), "{report}");
        assert!(report.contains("window size unknown"), "{report}");
        assert!(report.contains("(unknown)"), "the model is unknown too: {report}");
    }

    #[test]
    fn cost_is_shown_only_when_the_model_is_priced() {
        let unpriced = context_report(&usage(), Some("m"));
        assert!(!unpriced.contains("cost"), "an unpriced model must not read as free: {unpriced}");

        let priced = Usage { price_per_million: Some((3.0, 15.0)), ..usage() };
        let report = context_report(&priced, Some("m"));
        // 900k in at $3/M = $2.70, 20k out at $15/M = $0.30.
        assert!(report.contains("cost     $3.00"), "{report}");
    }

    #[test]
    fn the_cost_report_is_the_session_block_alone() {
        let report = cost_report(&usage());
        assert!(report.starts_with("Session totals"), "{report}");
        assert!(!report.contains("window"), "{report}");
    }
}
