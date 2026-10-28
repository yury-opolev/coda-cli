//! The `/context` and `/cost` reports.
//!
//! Split from the event loop because it is presentation, not orchestration:
//! every function here is pure over [`Usage`], so what the reports *claim* can
//! be asserted without building an `App` — which needs a real engine.
//!
//! The reports keep three quantities visibly apart, because conflating them is
//! what made the old output misleading:
//!
//! - **The context window** is what one request carries. Each request re-sends
//!   the whole conversation — cached or not, it all occupies the window — so
//!   the last request's total input is what is in the window right now.
//! - **Session totals** are every request's tokens added up, cached input
//!   included. They pass the window size after a handful of turns and say
//!   nothing about how full it is.
//! - **The cost estimate** prices each category at its own rate; cache reads
//!   cost a fraction of uncached input.

use coda_proto::state::UsagePair;

use crate::render::glyphs;
use crate::state::{ContextReading, Usage};

/// Width of the proportional bar, in cells.
const BAR_CELLS: usize = 40;

/// Renders the `/context` report.
///
/// `model` is the active model's label, or `None` when it is not yet known.
pub(super) fn context_report(usage: &Usage, model: Option<&str>) -> String {
    let mut out = String::from("Context usage\n");
    out.push_str(&format!("  model    {}\n", model.unwrap_or("(unknown)")));

    let used = usage.last_input_tokens;
    match (usage.context(), usage.context_limit) {
        // Nothing has been measured. Saying "0%" here would be a claim, and a
        // false one: the window already holds the system prompt and the tool
        // definitions before a single message is sent.
        _ if used <= 0 => out.push_str("  window   (nothing measured yet — send a message first)\n"),
        (Some(reading), limit) => {
            let label = reading.label().unwrap_or_else(|| "<1%".to_owned());
            out.push_str(&format!(
                "  window   {} of {} tokens ({label})\n",
                thousands(used),
                thousands(limit),
            ));
            out.push_str(&format!("  {}\n", bar(usage.percent_used().unwrap_or(0))));
            out.push_str(&format!("  free     {} tokens\n", thousands((limit - used).max(0))));
            match reading {
                ContextReading::Current(_) => {}
                ContextReading::Stale(_) => out.push_str(
                    "  (last measured before the conversation or model changed; \
                     the next request measures it again)\n",
                ),
                ContextReading::AtLeast(_) => out.push_str(
                    "  (at least: this engine did not report cached input, which also \
                     occupies the window)\n",
                ),
            }
        }
        // A measurement with no window to measure it against. Reported as the
        // bare figure rather than dropped, which is what the status bar has to
        // do for want of room.
        (None, _) => out.push_str(&format!(
            "  window   {} tokens in the last request (window size unknown)\n",
            thousands(used),
        )),
    }
    if let Some(last) = usage.last.filter(UsagePair::reports_cache) {
        out.push_str(&format!(
            "  last request: {} uncached, {} cache read, {} cache write\n",
            thousands(last.input_tokens),
            thousands(last.cache_read()),
            thousands(last.cache_write()),
        ));
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
    let mut out = String::from("Session totals (every request, cached input included)\n");
    out.push_str(&format!("  input    {} tokens\n", thousands(usage.input_tokens)));
    if let Some(session) = usage.session.filter(UsagePair::reports_cache) {
        out.push_str(&format!("    uncached      {}\n", thousands(session.input_tokens)));
        out.push_str(&format!("    cache read    {}\n", thousands(session.cache_read())));
        out.push_str(&format!(
            "    cache write   {} (5m {}, 1h {})\n",
            thousands(session.cache_write()),
            thousands(session.cache_write_5m()),
            thousands(session.cache_write_1h()),
        ));
    }
    out.push_str(&format!("  output   {} tokens\n", thousands(usage.output_tokens)));
    out.push_str(&format!(
        "  total    {} tokens",
        thousands(usage.input_tokens + usage.output_tokens),
    ));
    if usage.by_model.len() > 1 {
        out.push_str("\n  by model");
        for entry in &usage.by_model {
            out.push_str(&format!(
                "\n    {}  {} in, {} out",
                entry.model,
                thousands(entry.usage.total_input()),
                thousands(entry.usage.output_tokens),
            ));
        }
    }
    if let Some(cost) = usage.cost {
        if cost.complete {
            out.push_str(&format!("\n  cost     ${:.2} (estimate)", cost.total_usd));
        } else if cost.total_usd > 0.0 {
            out.push_str(&format!(
                "\n  cost     at least ${:.2} (some usage has no known price or was not recorded)",
                cost.total_usd,
            ));
        } else {
            // "$0.00" on an unpriced model reads as free rather than unknown.
            out.push_str("\n  cost     unknown (no known price for this usage)");
        }
        if cost.total_usd > 0.0 {
            out.push_str(&format!(
                "\n    uncached ${:.2}, cache read ${:.2}, cache write ${:.2}, output ${:.2}",
                cost.input_usd, cost.cache_read_usd, cost.cache_write_usd, cost.output_usd,
            ));
        }
    }
    if usage.totals_partial() {
        out.push_str(
            "\n  (partial: this session was saved before usage was recorded, so these \
             totals cover only the requests measured since)",
        );
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
    use coda_proto::state::{CostEstimate, ModelUsage, UsageCoverage};

    fn usage() -> Usage {
        let last = UsagePair::from_counts(0, 40_000, 0, 0, 500);
        Usage {
            input_tokens: 900_000,
            output_tokens: 20_000,
            last_input_tokens: 40_000,
            context_limit: 200_000,
            session: Some(UsagePair::from_counts(100_000, 780_000, 15_000, 5_000, 20_000)),
            last: Some(last),
            coverage: Some(UsageCoverage::Complete),
            ..Usage::default()
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

    /// The cumulative total is nearly five times the window, and must never
    /// be presented as the window's contents.
    #[test]
    fn the_report_separates_the_window_from_the_session_total() {
        let report = context_report(&usage(), Some("claude-opus-5"));

        assert!(report.contains("40,000 of 200,000 tokens (20%)"), "{report}");
        assert!(report.contains("free     160,000 tokens"), "{report}");
        assert!(report.contains("last request: 0 uncached, 40,000 cache read"), "{report}");
        assert!(report.contains("Session totals (every request"), "{report}");
        assert!(report.contains("input    900,000 tokens"), "{report}");
        assert!(report.contains("cache read    780,000"), "{report}");
        assert!(report.contains("cache write   20,000 (5m 15,000, 1h 5,000)"), "{report}");
        assert!(report.contains("total    920,000 tokens"), "{report}");
        assert!(report.contains("claude-opus-5"), "{report}");
        assert!(!report.contains("partial"), "{report}");
    }

    #[test]
    fn a_small_window_reads_below_one_percent_with_a_visible_bar() {
        let usage = Usage { last_input_tokens: 1_000, context_limit: 1_000_000, ..usage() };
        let report = context_report(&usage, Some("m"));
        assert!(report.contains("(<1%)"), "{report}");
        assert!(report.contains(glyphs::BLOCK), "{report}");
    }

    #[test]
    fn a_stale_measurement_is_labelled_as_the_last_known_one() {
        let usage = Usage { context_stale: true, ..usage() };
        let report = context_report(&usage, Some("m"));
        assert!(report.contains("(~20%)"), "{report}");
        assert!(report.contains("last measured before"), "{report}");
    }

    #[test]
    fn nothing_measured_is_reported_as_unknown_not_as_zero_percent() {
        let usage = Usage { last_input_tokens: 0, last: None, ..usage() };
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
    fn cost_is_the_engines_estimate_and_says_when_it_is_incomplete() {
        let unpriced = context_report(&usage(), Some("m"));
        assert!(!unpriced.contains("cost"), "no estimate, no line: {unpriced}");

        let estimate = CostEstimate {
            total_usd: 3.0,
            input_usd: 0.5,
            cache_read_usd: 0.39,
            cache_write_usd: 0.11,
            output_usd: 2.0,
            complete: true,
        };
        let priced = Usage { cost: Some(estimate), ..usage() };
        let report = cost_report(&priced);
        assert!(report.contains("cost     $3.00 (estimate)"), "{report}");
        assert!(report.contains("cache read $0.39"), "{report}");

        let partial = Usage { cost: Some(CostEstimate { complete: false, ..estimate }), ..usage() };
        assert!(cost_report(&partial).contains("at least $3.00"));

        let unknown =
            Usage { cost: Some(CostEstimate { complete: false, ..CostEstimate::default() }), ..usage() };
        let report = cost_report(&unknown);
        assert!(report.contains("cost     unknown"), "{report}");
        assert!(!report.contains("$0.00"), "unknown is not free: {report}");
    }

    #[test]
    fn partial_totals_say_what_they_cover() {
        let usage = Usage { coverage: Some(UsageCoverage::Partial), ..usage() };
        let report = cost_report(&usage);
        assert!(report.contains("partial"), "{report}");
        assert!(report.contains("cover only the requests measured since"), "{report}");
    }

    #[test]
    fn a_session_on_several_models_is_broken_down_by_model() {
        let usage = Usage {
            by_model: vec![
                ModelUsage { model: "a".into(), usage: UsagePair::from_counts(1, 2, 0, 0, 3) },
                ModelUsage { model: "b".into(), usage: UsagePair::from_counts(10, 0, 0, 0, 5) },
            ],
            ..usage()
        };
        let report = cost_report(&usage);
        assert!(report.contains("a  3 in, 3 out"), "{report}");
        assert!(report.contains("b  10 in, 5 out"), "{report}");
    }

    #[test]
    fn the_cost_report_is_the_session_block_alone() {
        let report = cost_report(&usage());
        assert!(report.starts_with("Session totals"), "{report}");
        assert!(!report.contains("window"), "{report}");
    }
}
