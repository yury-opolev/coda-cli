//! Content-free account of one compaction: every field is a number or a
//! closed label, so it can travel to diagnostics and clients without carrying
//! any conversation text.

/// Every `reason` a [`CompactionReport`] may carry.
pub const REASONS: &[&str] = &[
    "nothing_to_compact",
    "summary_truncated",
    "summary_empty",
    "summary_rejected",
    "summarizer_failed",
    "insufficient_room",
    "input_too_large",
    "protected_context_too_large",
    "checkpoint_write_failed",
    "history_changed",
    "cancelled",
];

#[derive(Debug, Clone, Default, PartialEq)]
pub struct CompactionReport {
    /// `"auto"`, `"overflow"` or `"manual"`.
    pub trigger: &'static str,
    /// `"compacted"`, `"not_compacted"`, `"failed"` or `"cancelled"`.
    pub outcome: &'static str,
    /// One of [`REASONS`].
    pub reason: Option<&'static str>,
    pub window_source: &'static str,
    pub context_window: u64,
    /// Filled by the caller.
    pub threshold: Option<u64>,
    /// The caller's full-request projection when it has one, else the history
    /// estimate.
    pub estimated_tokens: u64,
    /// The anchor's measured part (caller).
    pub measured_tokens: Option<u64>,
    pub messages_before: u32,
    pub messages_after: u32,
    pub tokens_before: u64,
    pub tokens_after: u64,
    pub tail_messages: u32,
    pub tail_tokens: u64,
    pub summary_allowance: Option<u32>,
    pub retry_allowance: Option<u32>,
    /// Summed over attempts.
    pub output_tokens: Option<u32>,
    pub stop_reason: Option<&'static str>,
    pub attempts: u32,
    pub duration_ms: u64,
    pub abbreviated_tool_outputs: u32,
    pub omitted_tool_outputs: u32,
    pub omitted_messages: u32,
    pub previewed_tail_results: u32,
    pub structured: Option<bool>,
    /// File name only, never a path.
    pub checkpoint: Option<String>,
}

/// `181234` → `"181k"`, `2400` → `"2.4k"`, `950` → `"950"`.
fn compact_count(n: u64) -> String {
    if n >= 10_000 {
        format!("{}k", (n + 500) / 1_000)
    } else if n >= 1_000 {
        format!("{:.1}k", n as f64 / 1_000.0)
    } else {
        n.to_string()
    }
}

impl CompactionReport {
    /// e.g. "Context compacted: 312 → 14 messages, ~181k → ~23k tokens
    /// (summary 2.4k tokens, 1 retry)."
    pub fn success_sentence(&self) -> String {
        let mut details = Vec::new();
        if let Some(out) = self.output_tokens.filter(|_| self.attempts > 0) {
            details.push(format!("summary {} tokens", compact_count(u64::from(out))));
        }
        if self.attempts > 1 {
            details.push("1 retry".to_owned());
        }
        if self.previewed_tail_results > 0 {
            details.push(format!("{} recent tool outputs shortened", self.previewed_tail_results));
        }
        let details =
            if details.is_empty() { String::new() } else { format!(" ({})", details.join(", ")) };
        format!(
            "Context compacted: {} → {} messages, ~{} → ~{} tokens{details}.",
            self.messages_before,
            self.messages_after,
            compact_count(self.tokens_before),
            compact_count(self.tokens_after),
        )
    }

    /// One fixed sentence per reason; never content.
    pub fn failure_sentence(&self) -> String {
        let retried = if self.attempts > 1 { " after 1 retry" } else { "" };
        match self.reason {
            Some("nothing_to_compact") => {
                "Nothing older than the protected recent exchanges to compact; history unchanged."
                    .to_owned()
            }
            Some("summary_truncated") => format!(
                "Compaction failed: the summary hit its output limit{retried}; history unchanged."
            ),
            Some("summary_empty") => {
                "Compaction failed: the summariser returned an empty summary; history unchanged."
                    .to_owned()
            }
            Some("summary_rejected") => format!(
                "Compaction failed: the summariser stopped without a usable summary (stop reason: {}); history unchanged.",
                self.stop_reason.unwrap_or("unknown")
            ),
            Some("summarizer_failed") => {
                "Compaction failed: the summariser request failed; history unchanged.".to_owned()
            }
            Some("insufficient_room") => {
                "Compaction failed: the context window leaves too little room for a summary; history unchanged."
                    .to_owned()
            }
            Some("input_too_large") => {
                "Compaction failed: the conversation is too large to summarise within the context window; history unchanged."
                    .to_owned()
            }
            Some("protected_context_too_large") => {
                "Compaction failed: the protected recent exchanges alone do not fit the context window; history unchanged."
                    .to_owned()
            }
            Some("checkpoint_write_failed") => {
                "Compaction failed: the pre-compaction checkpoint could not be saved; history unchanged."
                    .to_owned()
            }
            Some("history_changed") => {
                "Compaction abandoned: the conversation changed while it was being compacted; history unchanged."
                    .to_owned()
            }
            Some("cancelled") => "Compaction cancelled; history unchanged.".to_owned(),
            _ => "Compaction failed; history unchanged.".to_owned(),
        }
    }

    /// Records the report in the ambient diagnostics context, if any.
    pub fn record_diagnostics(&self) {
        if let Some(ctx) = coda_diagnostics::current() {
            ctx.record(self.to_event());
        }
    }

    fn to_event(&self) -> coda_diagnostics::Event {
        coda_diagnostics::Event::Compaction {
            trigger: self.trigger,
            outcome: self.outcome,
            reason: self.reason,
            window_source: self.window_source,
            context_window: self.context_window,
            threshold: self.threshold,
            estimated_tokens: self.estimated_tokens,
            measured_tokens: self.measured_tokens,
            messages_before: self.messages_before,
            messages_after: self.messages_after,
            tokens_before: self.tokens_before,
            tokens_after: self.tokens_after,
            tail_messages: self.tail_messages,
            tail_tokens: self.tail_tokens,
            summary_allowance: self.summary_allowance,
            retry_allowance: self.retry_allowance,
            output_tokens: self.output_tokens,
            stop_reason: self.stop_reason,
            attempts: self.attempts,
            duration_ms: self.duration_ms,
            abbreviated_tool_outputs: self.abbreviated_tool_outputs,
            omitted_tool_outputs: self.omitted_tool_outputs,
            omitted_messages: self.omitted_messages,
            previewed_tail_results: self.previewed_tail_results,
            structured: self.structured,
            checkpoint_saved: self.checkpoint.is_some(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn success_sentence_reads_like_the_spec() {
        let report = CompactionReport {
            messages_before: 312,
            messages_after: 14,
            tokens_before: 181_234,
            tokens_after: 23_100,
            output_tokens: Some(2_400),
            attempts: 2,
            ..CompactionReport::default()
        };
        assert_eq!(
            report.success_sentence(),
            "Context compacted: 312 → 14 messages, ~181k → ~23k tokens (summary 2.4k tokens, 1 retry)."
        );
    }

    #[test]
    fn every_reason_has_its_own_failure_sentence() {
        let fallback = CompactionReport::default().failure_sentence();
        let mut seen = std::collections::HashSet::new();
        for reason in REASONS {
            let sentence =
                CompactionReport { reason: Some(reason), ..CompactionReport::default() }.failure_sentence();
            assert_ne!(sentence, fallback, "{reason} has no sentence of its own");
            assert!(seen.insert(sentence), "{reason} shares a sentence");
        }
    }

    #[test]
    fn diagnostics_event_carries_only_whether_a_checkpoint_was_saved() {
        let report = CompactionReport {
            checkpoint: Some("20261113T120000000Z.json".into()),
            ..CompactionReport::default()
        };
        let value = serde_json::to_value(report.to_event()).unwrap();
        assert_eq!(value["checkpoint_saved"], true);
        assert!(!value.to_string().contains("20261113T120000000Z"));
    }
}
