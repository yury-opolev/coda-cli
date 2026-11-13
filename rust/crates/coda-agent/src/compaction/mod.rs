//! Conversation compaction: summarise history to free context space.
//!
//! Matches C# `Compaction/CompactionService.cs`, `TokenEstimator.cs`.
//! Tail selection lives in [`tail`], summariser input rendering in [`render`],
//! the summariser seam in [`summarizer`], output allowance and fit in
//! [`budget`], the content-free account in [`report`] and the pre-compaction
//! store seam in [`checkpoint`].
//!
//! # Invariants
//! - The last two exchanges must survive compaction (tail-preservation).
//! - Every `ToolUse` block must have a matching `ToolResult` block so the
//!   provider does not reject the next request with an ordering error.
//! - History is replaced only by [`CompactionOutcome::Compacted`]: a
//!   truncated, empty, rejected, failed or cancelled summary, a result that
//!   would not fit, or a checkpoint that could not be saved all leave it
//!   untouched — no silent data loss.

pub mod budget;
pub mod checkpoint;
pub mod render;
pub mod report;
pub mod summarizer;
pub mod tail;

use std::sync::Arc;
use std::time::Instant;

use coda_llm::{Content, Message};
use tokio_util::sync::CancellationToken;

pub use budget::{
    max_summary_input_tokens, overflow_tokens, request_fits, summary_allowance, CompactionLimits,
    SummaryAllowance, WindowSource,
};
pub use checkpoint::{CheckpointMeta, CheckpointRef, CheckpointSink};
pub use report::CompactionReport;
pub use summarizer::{
    normalized_stop_reason, SummarizerError, Summarizer, SummaryCompletion, SummaryRequest,
    SummaryResponse,
};
pub use tail::compaction_tail_start;

// ─────────────────────────────────────────────────────────────────────────────
// Token estimator
// ─────────────────────────────────────────────────────────────────────────────

/// Rough token estimate: 4 characters ≈ 1 token.
///
/// Fast and model-independent.  The estimate should be treated as a lower bound
/// for planning trigger thresholds rather than as a precise budget.
pub struct TokenEstimator;

impl TokenEstimator {
    pub fn estimate(history: &[Message]) -> usize {
        let mut chars: usize = 0;
        for msg in history {
            for block in &msg.content {
                match block {
                    Content::Text(t) => chars += t.len(),
                    Content::ToolUse { name, input_json, .. } => {
                        chars += name.len() + input_json.len();
                    }
                    Content::ToolResult { content, .. } => {
                        chars += content.len();
                    }
                    Content::Thinking { text, .. } => chars += text.len(),
                    Content::RedactedThinking { data } => chars += data.len(),
                    // Providers bill an image at roughly 1,600 tokens; counting
                    // only its placeholder would hide it from the trigger.
                    Content::Image { .. } => chars += 6_400,
                }
            }
        }
        chars / 4
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CompactionService
// ─────────────────────────────────────────────────────────────────────────────

/// What started a compaction.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CompactionTriggerKind {
    Auto,
    Overflow,
    Manual,
}

impl CompactionTriggerKind {
    pub fn as_str(self) -> &'static str {
        match self {
            CompactionTriggerKind::Auto => "auto",
            CompactionTriggerKind::Overflow => "overflow",
            CompactionTriggerKind::Manual => "manual",
        }
    }
}

/// One compaction, on a stable snapshot of the history.
pub struct CompactionRequest<'a> {
    pub history: &'a [Message],
    /// A `PreCompact` hook's replacement for the summariser system prompt.
    pub instructions_override: Option<&'a str>,
    pub trigger: CompactionTriggerKind,
    pub limits: CompactionLimits,
}

/// The result of [`CompactionService::compact`]. Only `Compacted` carries a
/// history; the caller commits it.
#[derive(Debug)]
pub enum CompactionOutcome {
    /// `summary` is empty for a tail-only compaction (no summariser call).
    Compacted { history: Vec<Message>, summary: String, report: CompactionReport },
    NotCompacted { report: CompactionReport },
    Failed { report: CompactionReport },
    Cancelled { report: CompactionReport },
}

impl CompactionOutcome {
    pub fn report(&self) -> &CompactionReport {
        match self {
            CompactionOutcome::Compacted { report, .. }
            | CompactionOutcome::NotCompacted { report }
            | CompactionOutcome::Failed { report }
            | CompactionOutcome::Cancelled { report } => report,
        }
    }

    pub fn report_mut(&mut self) -> &mut CompactionReport {
        match self {
            CompactionOutcome::Compacted { report, .. }
            | CompactionOutcome::NotCompacted { report }
            | CompactionOutcome::Failed { report }
            | CompactionOutcome::Cancelled { report } => report,
        }
    }
}

/// Replaces the history ahead of the protected tail with a validated
/// checkpoint, as a transaction: nothing is returned for commit until the
/// summary is complete, the result fits the window and (when a sink is
/// configured) the pre-compaction history has been saved.
///
/// The resulting history is the checkpoint (user), its acknowledgement
/// (assistant) and the tail verbatim — the last two exchanges, plus older
/// whole exchanges while they fit the tail target. The tail holds the message
/// the user has just sent together with the exchange that gives it its
/// meaning, and it contains no unpaired `ToolUse`. A previous checkpoint at the
/// head is reconciled into the new one, never nested.
pub struct CompactionService {
    summarizer: Arc<dyn Summarizer>,
    checkpoint: Option<Arc<dyn CheckpointSink>>,
}

/// The history a successful compaction installs, rebuilt once more after the
/// checkpoint is saved so the installed text can name it.
struct Candidate<'h> {
    /// `None` for a tail-only compaction, which keeps `kept_head` as is.
    summary: Option<&'h str>,
    kept_head: &'h [Message],
    tail: &'h [Message],
    /// Tokens tail tool results must free (0 = none shortened).
    to_free: usize,
}

impl Candidate<'_> {
    fn build(&self, note: tail::CheckpointNote<'_>) -> (Vec<Message>, usize) {
        let checkpoint_name = match note {
            tail::CheckpointNote::Named(name) => Some(name),
            _ => None,
        };
        let mut history = match self.summary {
            Some(summary) => {
                vec![render::checkpoint_message(summary, checkpoint_name), render::ack_message()]
            }
            None => self.kept_head.to_vec(),
        };
        let offset = history.len();
        history.extend_from_slice(self.tail);
        let previewed = if self.to_free > 0 {
            tail::preview_tail_tool_results(&mut history[offset..], self.to_free, note)
        } else {
            0
        };
        (history, previewed)
    }
}

fn saturating_u32(n: usize) -> u32 {
    u32::try_from(n).unwrap_or(u32::MAX)
}

fn settle(
    mut report: CompactionReport,
    started: Instant,
    outcome: &'static str,
    reason: &'static str,
) -> CompactionReport {
    report.outcome = outcome;
    report.reason = Some(reason);
    report.messages_after = report.messages_before;
    report.tokens_after = report.tokens_before;
    report.duration_ms = started.elapsed().as_millis() as u64;
    report
}

fn not_compacted(report: CompactionReport, started: Instant, reason: &'static str) -> CompactionOutcome {
    CompactionOutcome::NotCompacted { report: settle(report, started, "not_compacted", reason) }
}

fn failed(report: CompactionReport, started: Instant, reason: &'static str) -> CompactionOutcome {
    CompactionOutcome::Failed { report: settle(report, started, "failed", reason) }
}

fn cancelled(report: CompactionReport, started: Instant) -> CompactionOutcome {
    CompactionOutcome::Cancelled { report: settle(report, started, "cancelled", "cancelled") }
}

/// Summariser attempts: the initial allowance and at most one larger retry.
const MAX_SUMMARY_ATTEMPTS: u32 = 2;

impl CompactionService {
    pub fn new(summarizer: Arc<dyn Summarizer>) -> Self {
        Self { summarizer, checkpoint: None }
    }

    /// Persists the pre-compaction history before every commit.
    pub fn with_checkpoint_sink(mut self, sink: Arc<dyn CheckpointSink>) -> Self {
        self.checkpoint = Some(sink);
        self
    }

    /// The marker wording used before the checkpoint (if any) is saved.
    fn pending_note(&self) -> tail::CheckpointNote<'static> {
        if self.checkpoint.is_some() {
            tail::CheckpointNote::Pending
        } else {
            tail::CheckpointNote::None
        }
    }

    pub async fn compact(
        &self,
        request: CompactionRequest<'_>,
        cancel: CancellationToken,
    ) -> CompactionOutcome {
        let started = Instant::now();
        let CompactionRequest { history, instructions_override, trigger, limits } = request;
        let tokens_before = TokenEstimator::estimate(history);
        let mut report = CompactionReport {
            trigger: trigger.as_str(),
            window_source: limits.window_source.as_str(),
            context_window: limits.context_window as u64,
            estimated_tokens: tokens_before as u64,
            messages_before: saturating_u32(history.len()),
            tokens_before: tokens_before as u64,
            ..CompactionReport::default()
        };
        if history.is_empty() {
            return not_compacted(report, started, "nothing_to_compact");
        }

        let start = compaction_tail_start(history);
        let (head, tail) = history.split_at(start);
        let (previous, head_rest) = render::split_previous_checkpoint(head);
        report.tail_messages = saturating_u32(tail.len());
        report.tail_tokens = TokenEstimator::estimate(tail) as u64;

        if head_rest.is_empty() {
            // Nothing new ahead of the protected exchanges. Summarising them
            // would lose their meaning, so the only possible relief is
            // shortening oversized tool output inside the tail.
            if request_fits(&limits, tokens_before) {
                return not_compacted(report, started, "nothing_to_compact");
            }
            let candidate = Candidate {
                summary: None,
                kept_head: head,
                tail,
                to_free: overflow_tokens(&limits, tokens_before),
            };
            let (shortened, previewed) = candidate.build(self.pending_note());
            if previewed == 0 || !request_fits(&limits, TokenEstimator::estimate(&shortened)) {
                return failed(report, started, "protected_context_too_large");
            }
            return self
                .commit(history, &candidate, String::new(), report, trigger, started, &cancel)
                .await;
        }

        let system = instructions_override.unwrap_or(render::SYSTEM_PROMPT).to_owned();
        let max_input = max_summary_input_tokens(&limits, system.len() / 4);
        let Some(input) = render::render_summary_input(head_rest, previous.as_deref(), max_input)
        else {
            return failed(report, started, "input_too_large");
        };
        report.abbreviated_tool_outputs = saturating_u32(input.abbreviated_tool_outputs);
        report.omitted_tool_outputs = saturating_u32(input.omitted_tool_outputs);
        report.omitted_messages = saturating_u32(input.omitted_messages);

        let Some(allowance) =
            summary_allowance(&limits, (system.len() + input.user_message.len()) / 4)
        else {
            return failed(report, started, "insufficient_room");
        };
        report.summary_allowance = Some(allowance.initial);
        report.retry_allowance = allowance.retry;

        let mut max_output_tokens = allowance.initial;
        let summary = loop {
            if cancel.is_cancelled() {
                return cancelled(report, started);
            }
            report.attempts += 1;
            let result = self
                .summarizer
                .summarize(
                    SummaryRequest {
                        system: system.clone(),
                        messages: vec![Message::user(input.user_message.clone())],
                        max_output_tokens,
                    },
                    cancel.clone(),
                )
                .await;
            // A reply that raced the cancellation is still discarded.
            if cancel.is_cancelled() {
                return cancelled(report, started);
            }
            let response = match result {
                Ok(response) => response,
                Err(SummarizerError::Cancelled) => return cancelled(report, started),
                Err(SummarizerError::Failed { .. }) => {
                    return failed(report, started, "summarizer_failed")
                }
            };
            report.output_tokens =
                Some(report.output_tokens.unwrap_or(0).saturating_add(response.usage.output_tokens));
            report.stop_reason = Some(normalized_stop_reason(response.stop_reason.as_deref()));
            match SummaryCompletion::classify(response.stop_reason.as_deref(), &response.text) {
                SummaryCompletion::Complete => break response.text,
                SummaryCompletion::Truncated => match allowance.retry {
                    Some(retry) if report.attempts < MAX_SUMMARY_ATTEMPTS => {
                        max_output_tokens = retry;
                    }
                    _ => return failed(report, started, "summary_truncated"),
                },
                SummaryCompletion::Empty => return failed(report, started, "summary_empty"),
                SummaryCompletion::Rejected(_) => {
                    return failed(report, started, "summary_rejected")
                }
            }
        };
        report.structured = Some(render::is_structured(&summary));

        let mut candidate = Candidate { summary: Some(&summary), kept_head: &[], tail, to_free: 0 };
        let (rebuilt, _) = candidate.build(self.pending_note());
        let rebuilt_tokens = TokenEstimator::estimate(&rebuilt);
        if !request_fits(&limits, rebuilt_tokens) {
            candidate.to_free = overflow_tokens(&limits, rebuilt_tokens);
            let (shortened, _) = candidate.build(self.pending_note());
            if !request_fits(&limits, TokenEstimator::estimate(&shortened)) {
                return failed(report, started, "protected_context_too_large");
            }
        }
        self.commit(history, &candidate, summary.clone(), report, trigger, started, &cancel).await
    }

    /// Saves the pre-compaction history (when a sink is configured) and only
    /// then hands back the candidate, rebuilt so it names the saved file. A
    /// cancellation before or during the save leaves the history untouched.
    #[allow(clippy::too_many_arguments)]
    async fn commit(
        &self,
        history: &[Message],
        candidate: &Candidate<'_>,
        summary: String,
        mut report: CompactionReport,
        trigger: CompactionTriggerKind,
        started: Instant,
        cancel: &CancellationToken,
    ) -> CompactionOutcome {
        if cancel.is_cancelled() {
            return cancelled(report, started);
        }
        let checkpoint_name = match &self.checkpoint {
            Some(sink) => {
                let meta = CheckpointMeta {
                    trigger: trigger.as_str(),
                    messages_before: history.len(),
                    estimated_tokens_before: report.tokens_before as usize,
                };
                let saved = sink.save(history, &meta).await;
                if cancel.is_cancelled() {
                    return cancelled(report, started);
                }
                match saved {
                    Ok(saved) => Some(saved.name),
                    Err(error) => {
                        tracing::warn!(%error, "pre-compaction checkpoint could not be saved");
                        return failed(report, started, "checkpoint_write_failed");
                    }
                }
            }
            None => None,
        };
        let note = match checkpoint_name.as_deref() {
            Some(name) => tail::CheckpointNote::Named(name),
            None => tail::CheckpointNote::None,
        };
        let (compacted, previewed) = candidate.build(note);
        report.previewed_tail_results = saturating_u32(previewed);
        report.checkpoint = checkpoint_name;
        report.outcome = "compacted";
        report.reason = None;
        report.messages_after = saturating_u32(compacted.len());
        report.tokens_after = TokenEstimator::estimate(&compacted) as u64;
        report.duration_ms = started.elapsed().as_millis() as u64;
        CompactionOutcome::Compacted { history: compacted, summary, report }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Trigger policy
// ─────────────────────────────────────────────────────────────────────────────

/// When to trigger proactive compaction.
///
/// Floor for the auto-compaction threshold, so a tiny or unknown window still
/// leaves room to work rather than compacting after every exchange.
pub const MIN_AUTO_COMPACT_THRESHOLD: usize = 8_000;

/// Context window assumed when the model's real one is unknown.
pub const DEFAULT_CONTEXT_WINDOW: usize = 200_000;

/// Output headroom reserved when the model's real output ceiling is unknown.
pub const FALLBACK_MAX_OUTPUT_TOKENS: usize = 8_192;

/// Fraction of the context window that may fill before compaction fires.
///
/// Deliberately well short of the window. [`TokenEstimator`] measures only the
/// message history — it cannot see the system prompt, the tool definitions, or
/// the MCP tool schemas, which are charged on every request and are easily tens
/// of thousands of tokens — and it rounds down at four characters per token. The
/// figure compared against this threshold is therefore a *lower bound* on the
/// real prompt, so a trigger set near the window would fire after the request
/// had already overflowed, which is the failure this is meant to pre-empt.
pub const DEFAULT_COMPACT_RATIO: f64 = 0.80;

/// How the auto-compaction threshold is chosen.
///
/// Three ways to express the same decision, in precedence order. All fields
/// default to "unset", which yields [`DEFAULT_COMPACT_RATIO`].
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct CompactionTrigger {
    /// An absolute token count. Any positive value outranks `ratio` and
    /// `free_tokens`; like them it is held to the floor and the reply cap,
    /// since a pin above the window would never fire.
    pub tokens: usize,
    /// Fraction of the window that may fill before compaction, `0 < r <= 1`.
    /// Anything outside that range (or non-finite) falls back to the default.
    pub ratio: Option<f64>,
    /// Tokens to leave free, as `window - free_tokens`. An alternative spelling
    /// of `ratio` for users who think in headroom rather than percentages.
    pub free_tokens: Option<usize>,
}

impl CompactionTrigger {
    /// A trigger that fills `ratio` of the window.
    pub fn ratio(ratio: f64) -> Self {
        Self { ratio: Some(ratio), ..Self::default() }
    }

    /// A trigger that leaves `free_tokens` of the window unused.
    pub fn free_tokens(free_tokens: usize) -> Self {
        Self { free_tokens: Some(free_tokens), ..Self::default() }
    }

    /// The fraction to apply, rejecting values a percentage cannot mean.
    fn effective_ratio(&self) -> f64 {
        match self.ratio {
            Some(r) if r.is_finite() && r > 0.0 && r <= 1.0 => r,
            _ => DEFAULT_COMPACT_RATIO,
        }
    }
}

/// When to trigger proactive compaction.
#[derive(Debug, Clone, Copy)]
pub struct CompactionPolicy {
    /// Estimated token count above which compaction fires. Default: 50_000.
    pub token_threshold: usize,
    /// Token ratio that triggers compaction within a goal run (reserved for
    /// future use; 0 = disabled). Matches C# `ProactiveCompactionTokenRatio`.
    pub proactive_ratio: f64,
    /// The window the threshold was derived from; `None` when unknown.
    pub context_window: Option<usize>,
    pub window_source: WindowSource,
    pub model_output_limit: Option<u32>,
}

impl Default for CompactionPolicy {
    fn default() -> Self {
        Self {
            token_threshold: 50_000,
            proactive_ratio: 0.0,
            context_window: None,
            window_source: WindowSource::Fallback,
            model_output_limit: None,
        }
    }
}

impl CompactionPolicy {
    /// Returns `true` when the history has grown past the threshold.
    pub fn should_compact(&self, history: &[Message]) -> bool {
        TokenEstimator::estimate(history) >= self.token_threshold
    }

    /// Derives the threshold from the active model's context window.
    ///
    /// A fixed threshold cannot be right for every model: 50k is reasonable
    /// against a 200k window and absurd against a 1.1M one, where it would
    /// summarise a conversation using four per cent of the space available.
    /// The threshold is therefore a *fraction* of the window, so it scales with
    /// the model instead of being a number that happens to suit one of them.
    ///
    /// `configured` is an explicit override: any positive value wins over the
    /// window fraction, which is how a user pins a threshold regardless of the
    /// model (still within the floor and the reply cap). `context_window`
    /// of `None` means the window is genuinely unknown, and falls back to the
    /// conservative default rather than assuming a large one.
    pub fn resolve_threshold(
        configured: usize,
        context_window: Option<usize>,
        output_reserve: Option<usize>,
    ) -> usize {
        Self::resolve_threshold_with(
            CompactionTrigger { tokens: configured, ..CompactionTrigger::default() },
            context_window,
            output_reserve,
        )
    }

    /// As [`Self::resolve_threshold`], but for a fully specified trigger.
    ///
    /// `output_reserve` is the room a *reply* needs — the `max_tokens` the loop
    /// actually requests, not the model's theoretical output ceiling. The two
    /// differ by an order of magnitude (a 200k-window model may advertise 64k of
    /// output while the loop asks for 4k), and reserving the ceiling would drag
    /// the trigger far below the configured fraction for every long-context
    /// model. It is applied as a *cap* rather than the primary rule, so it binds
    /// only on windows small enough that the fraction would not leave room to
    /// answer — an 8k window at 80% leaves 1.6k, which is not a reply.
    pub fn resolve_threshold_with(
        trigger: CompactionTrigger,
        context_window: Option<usize>,
        output_reserve: Option<usize>,
    ) -> usize {
        let window = context_window.unwrap_or(DEFAULT_CONTEXT_WINDOW);
        let reserve = output_reserve.unwrap_or(FALLBACK_MAX_OUTPUT_TOKENS);

        let derived = if trigger.tokens > 0 {
            trigger.tokens
        } else {
            match trigger.free_tokens {
                // Headroom that would leave less than the floor to work in is not a
                // budget, it is a mistake — honouring it would pin the threshold to
                // the floor and summarise every few exchanges for the rest of the
                // session. This is reachable without anyone typing a silly number:
                // the window is re-derived per turn from the active model, so a
                // value sized for a 1M-window model lands on a 128k one the moment
                // the model is switched. An unusable ratio falls back to the
                // default, so an unusable headroom does too.
                Some(free) if window.saturating_sub(free) >= MIN_AUTO_COMPACT_THRESHOLD => {
                    window - free
                }
                // `as usize` truncates, which is the right direction: rounding a
                // threshold up would spend headroom the ratio exists to protect.
                _ => (window as f64 * trigger.effective_ratio()) as usize,
            }
        };

        // A reserve at or above the window is not a reply budget, it is a
        // misconfiguration; ignoring it beats letting it drive the cap to zero.
        let cap = match window.saturating_sub(reserve) {
            0 => window,
            headroom => headroom,
        };
        // The floor must never exceed the cap, or it would hand back a
        // threshold larger than the window — which does not compact eagerly, it
        // never compacts at all.
        derived.clamp(MIN_AUTO_COMPACT_THRESHOLD.min(cap), cap)
    }

    /// A policy whose threshold is derived from the model's context window.
    pub fn for_context_window(context_window: Option<usize>) -> Self {
        Self::for_context_window_with(CompactionTrigger::default(), context_window, None)
    }

    /// As [`Self::for_context_window`], but for a configured trigger and a
    /// known reply reserve.
    pub fn for_context_window_with(
        trigger: CompactionTrigger,
        context_window: Option<usize>,
        output_reserve: Option<usize>,
    ) -> Self {
        Self {
            token_threshold: Self::resolve_threshold_with(
                trigger,
                context_window,
                output_reserve,
            ),
            context_window,
            ..Self::default()
        }
    }

    /// Where [`Self::context_window`] came from.
    pub fn with_window_source(mut self, source: WindowSource) -> Self {
        self.window_source = source;
        self
    }

    /// The model's own output ceiling, which bounds the summary allowance.
    pub fn with_model_output_limit(mut self, limit: Option<u32>) -> Self {
        self.model_output_limit = limit;
        self
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
    use coda_llm::Role;
    use std::collections::VecDeque;
    use std::sync::Mutex;

    // ── Helpers ──────────────────────────────────────────────────────────────

    fn user_msg(text: &str) -> Message {
        Message::user(text)
    }

    fn assistant_msg(text: &str) -> Message {
        Message::assistant(text)
    }

    fn tool_use_msg(id: &str, name: &str) -> Message {
        Message::new(
            Role::Assistant,
            vec![Content::ToolUse {
                id: id.into(),
                name: name.into(),
                input_json: "{}".into(),
                correlation: Default::default(),
            }],
        )
    }

    fn tool_result_msg(tool_use_id: &str, content: &str) -> Message {
        Message::new(
            Role::User,
            vec![Content::ToolResult {
                tool_use_id: tool_use_id.into(),
                content: content.into(),
                is_error: false,
                correlation: Default::default(),
                status: None,
            }],
        )
    }

    const HEADINGS: &str = "## Next Move\nx\n## Pending Decisions & Open Questions\nx\n## Objective\nx\n\
         ## Requirements & Constraints\nx\n## Decisions\nx\n## Work State\nx\n\
         ## Relevant Files & Identifiers\nx\n## Important Context\nx";

    /// Two bulky early exchanges, then the request §2 probe.
    fn long_history() -> Vec<Message> {
        vec![
            user_msg(&format!("first request {}", "f".repeat(40_000))),
            assistant_msg("first answer"),
            user_msg(&format!("second request {}", "s".repeat(40_000))),
            assistant_msg("second answer"),
            user_msg("Which formatting option should I apply?"),
            assistant_msg("Apply option B; leave option A unchanged."),
            user_msg("Yes, proceed."),
        ]
    }

    fn long_exchange(tag: &str) -> [Message; 2] {
        [user_msg(&format!("{tag} {}", "l".repeat(40_000))), assistant_msg(&format!("{tag} done"))]
    }

    fn limits(window: usize, model_output_limit: Option<u32>) -> CompactionLimits {
        CompactionLimits { context_window: window, model_output_limit, ..CompactionLimits::default() }
    }

    fn request<'a>(history: &'a [Message], limits: CompactionLimits) -> CompactionRequest<'a> {
        CompactionRequest {
            history,
            instructions_override: None,
            trigger: CompactionTriggerKind::Auto,
            limits,
        }
    }

    // ── Scripted fakes ───────────────────────────────────────────────────────

    struct ScriptedSummarizer {
        replies: Mutex<VecDeque<Result<SummaryResponse, SummarizerError>>>,
        /// (system, user text, max_output_tokens)
        seen: Mutex<Vec<(String, String, u32)>>,
        on_call: Option<Box<dyn Fn() + Send + Sync>>,
    }

    impl ScriptedSummarizer {
        fn new(replies: Vec<Result<SummaryResponse, SummarizerError>>) -> Arc<Self> {
            Arc::new(Self { replies: Mutex::new(replies.into()), seen: Mutex::default(), on_call: None })
        }

        fn with_on_call(
            replies: Vec<Result<SummaryResponse, SummarizerError>>,
            on_call: impl Fn() + Send + Sync + 'static,
        ) -> Arc<Self> {
            Arc::new(Self {
                replies: Mutex::new(replies.into()),
                seen: Mutex::default(),
                on_call: Some(Box::new(on_call)),
            })
        }

        fn budgets(&self) -> Vec<u32> {
            self.seen.lock().unwrap().iter().map(|s| s.2).collect()
        }

        fn calls(&self) -> usize {
            self.seen.lock().unwrap().len()
        }
    }

    #[async_trait]
    impl Summarizer for ScriptedSummarizer {
        async fn summarize(
            &self,
            request: SummaryRequest,
            _cancel: CancellationToken,
        ) -> Result<SummaryResponse, SummarizerError> {
            let user = request.messages.iter().map(Message::text).collect::<Vec<_>>().join("\n");
            self.seen.lock().unwrap().push((request.system, user, request.max_output_tokens));
            if let Some(on_call) = &self.on_call {
                on_call();
            }
            self.replies
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or(Err(SummarizerError::Failed { category: "exhausted" }))
        }
    }

    fn ok(text: &str, stop: &str, out: u32) -> Result<SummaryResponse, SummarizerError> {
        Ok(SummaryResponse {
            text: text.into(),
            stop_reason: Some(stop.into()),
            usage: coda_llm::Usage { output_tokens: out, ..Default::default() },
        })
    }

    struct FailingSink;

    #[async_trait]
    impl CheckpointSink for FailingSink {
        async fn save(&self, _: &[Message], _: &CheckpointMeta) -> anyhow::Result<CheckpointRef> {
            anyhow::bail!("disk full")
        }
    }

    struct RecordingSink(Mutex<Vec<usize>>);

    #[async_trait]
    impl CheckpointSink for RecordingSink {
        async fn save(&self, history: &[Message], meta: &CheckpointMeta) -> anyhow::Result<CheckpointRef> {
            assert_eq!(history.len(), meta.messages_before);
            self.0.lock().unwrap().push(meta.messages_before);
            Ok(CheckpointRef { name: "20261113T120000000Z.json".into() })
        }
    }

    fn has_text(history: &[Message], role: Role, text: &str) -> bool {
        history.iter().any(|m| {
            m.role == role && m.content.iter().any(|c| matches!(c, Content::Text(t) if t == text))
        })
    }

    fn checkpoint_count(history: &[Message]) -> usize {
        history.iter().filter(|m| m.text().contains(render::CHECKPOINT_OPEN)).count()
    }

    #[track_caller]
    fn expect_failed(outcome: &CompactionOutcome, reason: &str) {
        match outcome {
            CompactionOutcome::Failed { report } => {
                assert_eq!(report.outcome, "failed");
                assert_eq!(report.reason, Some(reason));
            }
            other => panic!("expected Failed({reason}), got {other:?}"),
        }
    }

    // ── TokenEstimator ───────────────────────────────────────────────────────

    #[test]
    fn estimate_counts_chars_divided_by_four() {
        let history = vec![user_msg("hello"), assistant_msg("world")];
        // "hello" = 5 chars + "world" = 5 chars = 10 → 10/4 = 2
        assert_eq!(TokenEstimator::estimate(&history), 2);
    }

    #[test]
    fn estimate_empty_history_is_zero() {
        assert_eq!(TokenEstimator::estimate(&[]), 0);
    }

    // ── CompactionPolicy ─────────────────────────────────────────────────────

    #[test]
    fn policy_fires_above_threshold() {
        let policy = CompactionPolicy { token_threshold: 2, ..CompactionPolicy::default() };
        let history = vec![user_msg("hello"), assistant_msg("world")];
        assert!(policy.should_compact(&history));
    }

    #[test]
    fn policy_silent_below_threshold() {
        let policy = CompactionPolicy { token_threshold: 100, ..CompactionPolicy::default() };
        let history = vec![user_msg("hi"), assistant_msg("ok")];
        assert!(!policy.should_compact(&history));
    }

    #[test]
    fn policy_keeps_the_window_it_was_derived_from() {
        let policy = CompactionPolicy::for_context_window_with(
            CompactionTrigger::default(),
            Some(1_000_000),
            None,
        )
        .with_window_source(WindowSource::Live)
        .with_model_output_limit(Some(64_000));
        assert_eq!(policy.context_window, Some(1_000_000));
        assert_eq!(policy.window_source, WindowSource::Live);
        assert_eq!(policy.model_output_limit, Some(64_000));
    }

    // ── Completion and budget ────────────────────────────────────────────────

    #[test]
    fn classifies_provider_stop_reasons() {
        use SummaryCompletion::*;
        let table: [(Option<&str>, &str, SummaryCompletion); 8] = [
            (Some("end_turn"), "x", Complete),
            (Some("stop"), "x", Complete),
            (Some("length"), "x", Truncated),
            (Some("max_output_tokens"), "", Truncated),
            (Some("incomplete"), "x", Truncated),
            (Some("end_turn"), " ", Empty),
            (Some("content_filter"), "x", Rejected("content_filter")),
            (None, "x", Rejected("unknown")),
        ];
        for (stop, text, expected) in table {
            assert_eq!(SummaryCompletion::classify(stop, text), expected, "{stop:?} / {text:?}");
        }
    }

    #[test]
    fn summary_budget_is_8k_initial() {
        let big = limits(1_000_000, None);
        assert_eq!(
            summary_allowance(&big, 10_000),
            Some(SummaryAllowance { initial: 8_192, retry: Some(32_768) })
        );
        assert_eq!(
            summary_allowance(&limits(1_000_000, Some(16_000)), 10_000),
            Some(SummaryAllowance { initial: 8_192, retry: Some(16_000) })
        );
        assert_eq!(
            summary_allowance(&limits(1_000_000, Some(8_192)), 10_000),
            Some(SummaryAllowance { initial: 8_192, retry: None })
        );
        // Room = 20_000 - 6_000 - 2_048 = 11_952.
        assert_eq!(
            summary_allowance(&limits(20_000, None), 6_000),
            Some(SummaryAllowance { initial: 8_192, retry: Some(11_952) })
        );
        // Room = 5_000 - 2_000 - 2_048 = 952 < 1_024.
        assert_eq!(summary_allowance(&limits(5_000, None), 2_000), None);
    }

    // ── CompactionService ────────────────────────────────────────────────────

    #[tokio::test]
    async fn probe_fixed_short_approval_keeps_action_target_verbatim() {
        let probe = vec![
            user_msg("old"),
            assistant_msg("old answer"),
            user_msg("Which option?"),
            assistant_msg("Apply option B; leave option A unchanged."),
            user_msg("Yes, proceed."),
        ];
        assert!(compaction_tail_start(&probe) <= 2);

        let history = long_history();
        let probe_start = history.len() - 5;
        assert!(compaction_tail_start(&history) - probe_start <= 2);

        let summarizer = ScriptedSummarizer::new(vec![ok(HEADINGS, "end_turn", 300)]);
        let svc = CompactionService::new(summarizer.clone());
        let outcome =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;

        let CompactionOutcome::Compacted { history: compacted, summary, report } = outcome else {
            panic!("expected Compacted, got {outcome:?}");
        };
        assert_eq!(summary, HEADINGS);
        assert!(compacted[0].text().contains("## Next Move"));
        assert!(has_text(&compacted, Role::User, "Which formatting option should I apply?"));
        assert!(has_text(&compacted, Role::Assistant, "Apply option B; leave option A unchanged."));
        assert!(has_text(&compacted, Role::User, "Yes, proceed."));
        assert_eq!(report.outcome, "compacted");
        assert_eq!(report.attempts, 1);
        assert_eq!(report.structured, Some(true));
        assert_eq!(report.messages_before, history.len() as u32);
        assert_eq!(report.messages_after, compacted.len() as u32);
        assert!(report.tokens_after < report.tokens_before);
    }

    #[tokio::test]
    async fn max_tokens_with_valid_headings_is_rejected_after_one_bounded_retry() {
        let history = long_history();
        let summarizer = ScriptedSummarizer::new(vec![
            ok(HEADINGS, "max_tokens", 8_192),
            ok(HEADINGS, "max_tokens", 32_768),
        ]);
        let svc = CompactionService::new(summarizer.clone());
        let outcome =
            svc.compact(request(&history, limits(1_000_000, None)), CancellationToken::new()).await;

        expect_failed(&outcome, "summary_truncated");
        let report = outcome.report();
        assert_eq!(report.attempts, 2);
        assert_eq!(report.output_tokens, Some(8_192 + 32_768));
        assert_eq!(report.stop_reason, Some("max_tokens"));
        assert_eq!(report.messages_after, report.messages_before);
        assert_eq!(summarizer.budgets(), vec![8_192, 32_768]);
    }

    #[tokio::test]
    async fn retry_succeeds_and_is_bounded_by_model_output_limit() {
        let history = long_history();
        let summarizer = ScriptedSummarizer::new(vec![
            ok(HEADINGS, "max_tokens", 8_192),
            ok(HEADINGS, "end_turn", 9_000),
        ]);
        let svc = CompactionService::new(summarizer.clone());
        let outcome = svc
            .compact(request(&history, limits(1_000_000, Some(16_000))), CancellationToken::new())
            .await;

        assert!(matches!(outcome, CompactionOutcome::Compacted { .. }), "{outcome:?}");
        assert_eq!(summarizer.budgets(), vec![8_192, 16_000]);
        assert_eq!(outcome.report().retry_allowance, Some(16_000));
        assert_eq!(outcome.report().attempts, 2);
    }

    #[tokio::test]
    async fn no_retry_when_no_larger_allowance_exists() {
        let history = long_history();
        let summarizer = ScriptedSummarizer::new(vec![
            ok(HEADINGS, "max_tokens", 8_192),
            ok(HEADINGS, "end_turn", 100),
        ]);
        let svc = CompactionService::new(summarizer.clone());
        let outcome = svc
            .compact(request(&history, limits(1_000_000, Some(8_192))), CancellationToken::new())
            .await;

        expect_failed(&outcome, "summary_truncated");
        assert_eq!(summarizer.calls(), 1);
        assert_eq!(outcome.report().retry_allowance, None);
    }

    #[tokio::test]
    async fn retry_is_bounded_by_remaining_room() {
        let history = long_history();
        let summarizer = ScriptedSummarizer::new(vec![
            ok(HEADINGS, "max_tokens", 8_192),
            ok(HEADINGS, "max_tokens", 9_000),
        ]);
        let svc = CompactionService::new(summarizer.clone());
        // The head renders to roughly 10k tokens; leave about 12k of room after it.
        let window = 10_000 + 12_000 + budget::COMPACTION_SAFETY_MARGIN;
        let outcome =
            svc.compact(request(&history, limits(window, None)), CancellationToken::new()).await;

        expect_failed(&outcome, "summary_truncated");
        let seen = summarizer.seen.lock().unwrap().clone();
        assert_eq!(seen.len(), 2);
        let input_tokens = (seen[0].0.len() + seen[0].1.len()) / 4;
        let allowance = summary_allowance(&limits(window, None), input_tokens).unwrap();
        assert_eq!(seen[0].2, 8_192);
        assert_eq!(Some(seen[1].2), allowance.retry);
        assert!(seen[1].2 < budget::SUMMARY_RETRY_MAX_OUTPUT_TOKENS);
        assert!(seen[1].2 > 8_192);
    }

    #[tokio::test]
    async fn empty_summary_fails_without_retry() {
        let history = long_history();
        let summarizer = ScriptedSummarizer::new(vec![ok("  ", "end_turn", 1), ok(HEADINGS, "end_turn", 1)]);
        let svc = CompactionService::new(summarizer.clone());
        let outcome =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;
        expect_failed(&outcome, "summary_empty");
        assert_eq!(summarizer.calls(), 1);
    }

    #[tokio::test]
    async fn unknown_stop_reason_fails() {
        let history = long_history();
        let reply = Ok(SummaryResponse { text: HEADINGS.into(), stop_reason: None, ..Default::default() });
        let summarizer = ScriptedSummarizer::new(vec![reply]);
        let svc = CompactionService::new(summarizer.clone());
        let outcome =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;
        expect_failed(&outcome, "summary_rejected");
        assert_eq!(outcome.report().stop_reason, Some("unknown"));
        assert_eq!(summarizer.calls(), 1);
    }

    #[tokio::test]
    async fn provider_error_fails() {
        let history = long_history();
        let summarizer =
            ScriptedSummarizer::new(vec![Err(SummarizerError::Failed { category: "server_error" })]);
        let svc = CompactionService::new(summarizer.clone());
        let outcome =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;
        expect_failed(&outcome, "summarizer_failed");
        assert_eq!(summarizer.calls(), 1);
    }

    #[tokio::test]
    async fn cancellation_returns_cancelled_and_keeps_history() {
        let history = long_history();
        let sink = Arc::new(RecordingSink(Mutex::default()));

        let summarizer = ScriptedSummarizer::new(vec![Err(SummarizerError::Cancelled)]);
        let svc = CompactionService::new(summarizer).with_checkpoint_sink(sink.clone());
        let outcome =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;
        let CompactionOutcome::Cancelled { report } = &outcome else {
            panic!("expected Cancelled, got {outcome:?}");
        };
        assert_eq!(report.outcome, "cancelled");
        assert_eq!(report.reason, Some("cancelled"));

        // Cancelled while the call was in flight, even though a reply arrived.
        let cancel = CancellationToken::new();
        let trip = cancel.clone();
        let summarizer = ScriptedSummarizer::with_on_call(vec![ok(HEADINGS, "end_turn", 1)], move || trip.cancel());
        let svc = CompactionService::new(summarizer).with_checkpoint_sink(sink.clone());
        let outcome = svc.compact(request(&history, limits(200_000, None)), cancel).await;
        assert!(matches!(outcome, CompactionOutcome::Cancelled { .. }), "{outcome:?}");

        // Cancelled before the call.
        let cancel = CancellationToken::new();
        cancel.cancel();
        let summarizer = ScriptedSummarizer::new(vec![ok(HEADINGS, "end_turn", 1)]);
        let svc = CompactionService::new(summarizer.clone()).with_checkpoint_sink(sink.clone());
        let outcome = svc.compact(request(&history, limits(200_000, None)), cancel).await;
        assert!(matches!(outcome, CompactionOutcome::Cancelled { .. }), "{outcome:?}");
        assert_eq!(summarizer.calls(), 0);

        assert!(sink.0.lock().unwrap().is_empty(), "a cancelled compaction writes no checkpoint");
    }

    #[tokio::test]
    async fn repeated_compactions_reconcile_one_checkpoint() {
        let history = long_history();
        let summarizer = ScriptedSummarizer::new(vec![
            ok("ID=TASK-42 DONE=parse PENDING=emit", "end_turn", 10),
            ok("ID=TASK-42 DONE=parse,emit PENDING=none", "end_turn", 10),
        ]);
        let svc = CompactionService::new(summarizer.clone());
        let first =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;
        let CompactionOutcome::Compacted { history: mut compacted, .. } = first else {
            panic!("first compaction must succeed: {first:?}");
        };
        for tag in ["third", "fourth", "fifth"] {
            compacted.extend(long_exchange(tag));
        }

        let second =
            svc.compact(request(&compacted, limits(200_000, None)), CancellationToken::new()).await;
        let CompactionOutcome::Compacted { history: twice, .. } = second else {
            panic!("second compaction must succeed: {second:?}");
        };

        let seen = summarizer.seen.lock().unwrap().clone();
        let second_input = &seen[1].1;
        assert_eq!(second_input.matches("<previous-checkpoint>").count(), 1);
        assert!(second_input.contains("TASK-42"));
        assert!(!second_input.contains(render::CHECKPOINT_OPEN), "the old checkpoint is not re-rendered");

        assert_eq!(checkpoint_count(&twice), 1);
        assert!(twice[0].text().contains("DONE=parse,emit"));
        assert!(!twice[0].text().contains("PENDING=emit"), "summaries are not concatenated");
    }

    #[tokio::test]
    async fn checkpoint_write_failure_keeps_old_context() {
        let history = long_history();
        let summarizer = ScriptedSummarizer::new(vec![ok(HEADINGS, "end_turn", 10)]);
        let svc = CompactionService::new(summarizer).with_checkpoint_sink(Arc::new(FailingSink));
        let outcome =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;
        expect_failed(&outcome, "checkpoint_write_failed");
        assert_eq!(outcome.report().checkpoint, None);
    }

    #[tokio::test]
    async fn checkpoint_is_written_before_commit_and_named() {
        let history = long_history();
        let sink = Arc::new(RecordingSink(Mutex::default()));
        let summarizer = ScriptedSummarizer::new(vec![ok(HEADINGS, "end_turn", 10)]);
        let svc = CompactionService::new(summarizer).with_checkpoint_sink(sink.clone());
        let outcome =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;

        let CompactionOutcome::Compacted { history: compacted, report, .. } = outcome else {
            panic!("expected Compacted, got {outcome:?}");
        };
        assert_eq!(*sink.0.lock().unwrap(), vec![history.len()]);
        assert!(compacted[0].text().contains("20261113T120000000Z.json"));
        assert_eq!(report.checkpoint.as_deref(), Some("20261113T120000000Z.json"));
    }

    #[tokio::test]
    async fn oversized_single_exchange_does_not_loop_or_lose_intent() {
        let history = oversized_single_exchange();
        let sink = Arc::new(RecordingSink(Mutex::default()));
        let summarizer = ScriptedSummarizer::new(vec![ok(HEADINGS, "end_turn", 10)]);
        let svc = CompactionService::new(summarizer.clone()).with_checkpoint_sink(sink.clone());
        let outcome =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;

        assert!(summarizer.calls() <= 2);
        let CompactionOutcome::Compacted { history: compacted, summary, report } = outcome else {
            panic!("expected a tail-only Compacted, got {outcome:?}");
        };
        assert!(has_text(&compacted, Role::User, "Please refactor module X; do not touch Y"));
        assert!(has_text(&compacted, Role::User, "go ahead"));
        assert!(summary.is_empty(), "tail-only compaction has no summary");
        assert_eq!(report.attempts, 0);
        assert_eq!(report.previewed_tail_results, 1);
        let result = first_tool_result(&compacted);
        assert!(result.contains("omitted during compaction"));
        assert!(result.contains("20261113T120000000Z.json"), "marker names the checkpoint");
        assert_eq!(sink.0.lock().unwrap().len(), 1, "checkpointed before commit");
    }

    fn oversized_single_exchange() -> Vec<Message> {
        vec![
            user_msg("Please refactor module X; do not touch Y"),
            tool_use_msg("c1", "read"),
            tool_result_msg("c1", &"r".repeat(900_000)),
            assistant_msg("Found it"),
            user_msg("go ahead"),
        ]
    }

    fn first_tool_result(history: &[Message]) -> String {
        history
            .iter()
            .flat_map(|m| &m.content)
            .find_map(|c| match c {
                Content::ToolResult { content, .. } => Some(content.clone()),
                _ => None,
            })
            .expect("the tool result stays paired")
    }

    #[tokio::test]
    async fn without_a_sink_the_marker_claims_no_checkpoint() {
        let history = oversized_single_exchange();
        let svc = CompactionService::new(ScriptedSummarizer::new(vec![]));
        let outcome =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;
        let CompactionOutcome::Compacted { history: compacted, report, .. } = outcome else {
            panic!("expected Compacted, got {outcome:?}");
        };
        assert_eq!(report.checkpoint, None);
        let result = first_tool_result(&compacted);
        assert!(result.contains("bytes of this tool output were omitted during compaction]"));
        assert!(!result.contains("checkpoint"), "no checkpoint exists: {}", &result[6_000..6_200]);
    }

    struct CancellingSink(CancellationToken, Mutex<usize>);

    #[async_trait]
    impl CheckpointSink for CancellingSink {
        async fn save(&self, _: &[Message], _: &CheckpointMeta) -> anyhow::Result<CheckpointRef> {
            *self.1.lock().unwrap() += 1;
            self.0.cancel();
            Ok(CheckpointRef { name: "late.json".into() })
        }
    }

    #[tokio::test]
    async fn cancellation_during_or_before_checkpoint_save_does_not_compact() {
        // Cancelled while the checkpoint was being written, on both paths.
        for history in [long_history(), oversized_single_exchange()] {
            let cancel = CancellationToken::new();
            let sink = Arc::new(CancellingSink(cancel.clone(), Mutex::new(0)));
            let summarizer = ScriptedSummarizer::new(vec![ok(HEADINGS, "end_turn", 10)]);
            let svc = CompactionService::new(summarizer).with_checkpoint_sink(sink.clone());
            let outcome = svc.compact(request(&history, limits(200_000, None)), cancel).await;
            assert!(matches!(outcome, CompactionOutcome::Cancelled { .. }), "{outcome:?}");
            assert_eq!(*sink.1.lock().unwrap(), 1);
        }

        // Cancelled before the tail-only path reaches the save.
        let cancel = CancellationToken::new();
        cancel.cancel();
        let sink = Arc::new(RecordingSink(Mutex::default()));
        let svc = CompactionService::new(ScriptedSummarizer::new(vec![])).with_checkpoint_sink(sink.clone());
        let history = oversized_single_exchange();
        let outcome = svc.compact(request(&history, limits(200_000, None)), cancel).await;
        assert!(matches!(outcome, CompactionOutcome::Cancelled { .. }), "{outcome:?}");
        assert!(sink.0.lock().unwrap().is_empty(), "no checkpoint is written after a cancel");
    }

    /// A summarised-away `ToolResult` would leave its `ToolUse` orphaned, which
    /// providers reject outright — the turn fails rather than degrades.
    #[tokio::test]
    async fn tool_pairs_survive_compaction() {
        let mut history = vec![
            user_msg(&format!("early context {}", "e".repeat(80_000))),
            assistant_msg("noted"),
        ];
        history.extend([
            user_msg("first"),
            tool_use_msg("call-0", "read"),
            tool_result_msg("call-0", "early"),
            assistant_msg("first answer"),
            user_msg("run the tool"),
            tool_use_msg("call-1", "grep"),
            tool_result_msg("call-1", "match"),
        ]);
        let svc = CompactionService::new(ScriptedSummarizer::new(vec![ok("Summary.", "end_turn", 5)]));
        let outcome =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;
        let CompactionOutcome::Compacted { history: compacted, .. } = outcome else {
            panic!("the bulky head must have been summarised: {outcome:?}");
        };

        let ids = |pick: fn(&Content) -> Option<&str>| -> Vec<String> {
            compacted.iter().flat_map(|m| &m.content).filter_map(pick).map(str::to_owned).collect()
        };
        let uses = ids(|c| match c {
            Content::ToolUse { id, .. } => Some(id.as_str()),
            _ => None,
        });
        let results = ids(|c| match c {
            Content::ToolResult { tool_use_id, .. } => Some(tool_use_id.as_str()),
            _ => None,
        });
        assert!(!uses.is_empty());
        for id in &uses {
            assert!(results.contains(id), "ToolUse {id} lost its result: {compacted:?}");
        }
        for id in &results {
            assert!(uses.contains(id), "ToolResult {id} lost its call: {compacted:?}");
        }
    }

    #[tokio::test]
    async fn nothing_to_compact_when_everything_is_protected() {
        let history =
            vec![user_msg("q1"), assistant_msg("a1"), user_msg("q2"), assistant_msg("a2")];
        let summarizer = ScriptedSummarizer::new(vec![ok(HEADINGS, "end_turn", 1)]);
        let svc = CompactionService::new(summarizer.clone());
        let outcome =
            svc.compact(request(&history, limits(200_000, None)), CancellationToken::new()).await;
        let CompactionOutcome::NotCompacted { report } = &outcome else {
            panic!("expected NotCompacted, got {outcome:?}");
        };
        assert_eq!(report.outcome, "not_compacted");
        assert_eq!(report.reason, Some("nothing_to_compact"));
        assert_eq!(summarizer.calls(), 0);

        let empty = svc.compact(request(&[], limits(200_000, None)), CancellationToken::new()).await;
        assert!(matches!(empty, CompactionOutcome::NotCompacted { .. }));
    }

    #[test]
    fn report_sentences_are_content_free() {
        let history = long_history();
        let texts: Vec<String> = history.iter().map(Message::text).collect();
        let mut sentences = vec![CompactionReport {
            messages_before: 7,
            messages_after: 5,
            tokens_before: 20_000,
            tokens_after: 9_000,
            output_tokens: Some(300),
            attempts: 1,
            ..CompactionReport::default()
        }
        .success_sentence()];
        for reason in report::REASONS {
            sentences.push(
                CompactionReport { reason: Some(reason), attempts: 2, ..CompactionReport::default() }
                    .failure_sentence(),
            );
        }
        for sentence in &sentences {
            for text in &texts {
                assert!(!sentence.contains(text.as_str()), "{sentence:?} leaks {text:?}");
            }
        }
    }
}

