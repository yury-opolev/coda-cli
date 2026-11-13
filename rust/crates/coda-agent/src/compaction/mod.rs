//! Conversation compaction: summarise history to free context space.
//!
//! Matches C# `Compaction/CompactionService.cs`, `TokenEstimator.cs`.
//! Tail selection lives in [`tail`], summariser input rendering in [`render`].
//!
//! # Invariants
//! - The last two exchanges must survive compaction (tail-preservation).
//! - Every `ToolUse` block must have a matching `ToolResult` block so the
//!   provider does not reject the next request with an ordering error.
//! - When the summariser call fails (empty reply), the original history is
//!   returned unchanged — no silent data loss.

pub mod render;
pub mod tail;

use std::sync::Arc;

use coda_llm::{Content, Message};
use tokio_util::sync::CancellationToken;

use crate::autonomy::ForkedAgent;

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

/// Summarises a conversation into a fresh, minimal two-message history.
///
/// The resulting history is:
/// 1. A user message containing the summary.
/// 2. A short assistant acknowledgement (so the next user turn keeps valid
///    user/assistant alternation).
///
/// When the summariser returns an empty string the original history is returned
/// unchanged — no silent data loss.
pub struct CompactionService {
    fork: Arc<dyn ForkedAgent>,
}

impl CompactionService {
    pub fn new(fork: Arc<dyn ForkedAgent>) -> Self {
        Self { fork }
    }

    /// Compact `history` to a summary/ack pair, followed by the preserved tail.
    ///
    /// Returns the compacted history and the raw summary text (for
    /// `PostCompact` hooks).  When compaction fails, returns the original
    /// history and `summary = None`.
    ///
    /// Only the messages *before* [`compaction_tail_start`] are summarised. The
    /// tail — the last two exchanges, plus older whole exchanges while they fit
    /// the tail target — is carried through verbatim, for two reasons. It
    /// holds the message the user has just sent together with the exchange
    /// that gives it its meaning, which an auto-compaction fired mid-turn would
    /// otherwise discard before the model ever saw it; and it is the slice
    /// guaranteed to contain no unpaired `ToolUse`, so the provider is never
    /// handed a tool call whose result was summarised away. A previous
    /// checkpoint at the head is reconciled into the new one, never nested.
    pub async fn compact(
        &self,
        history: &[Message],
        instructions_override: Option<&str>,
        cancel: CancellationToken,
    ) -> (Vec<Message>, Option<String>) {
        if history.is_empty() {
            return (history.to_vec(), None);
        }

        let (head, tail) = history.split_at(compaction_tail_start(history));
        let (previous, head) = render::split_previous_checkpoint(head);
        if head.is_empty() {
            // Everything is tail (or a previous checkpoint and its
            // acknowledgement): nothing new to summarise. Replacing it with a
            // summary of itself would shrink nothing and lose the exchange, so
            // report "not compacted" and let the caller's suppression wait for
            // more history.
            return (history.to_vec(), None);
        }

        let system = instructions_override.unwrap_or(render::SYSTEM_PROMPT);
        let Some(input) = render::render_summary_input(head, previous.as_deref(), usize::MAX)
        else {
            return (history.to_vec(), None);
        };
        let summary = match self
            .fork
            .run(system, vec![Message::user(input.user_message)], cancel)
            .await
        {
            Ok(s) if !s.trim().is_empty() => s,
            // Summariser failed or returned empty — preserve original history.
            _ => return (history.to_vec(), None),
        };

        let mut compacted = Vec::with_capacity(2 + tail.len());
        compacted.push(render::checkpoint_message(&summary, None));
        compacted.push(render::ack_message());
        compacted.extend_from_slice(tail);
        (compacted, Some(summary))
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
}

impl Default for CompactionPolicy {
    fn default() -> Self {
        Self { token_threshold: 50_000, proactive_ratio: 0.0 }
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
            ..Self::default()
        }
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
    use std::sync::Mutex;

    // ── Helpers ──────────────────────────────────────────────────────────────

    fn user_msg(text: &str) -> Message {
        Message::user(text)
    }

    fn assistant_msg(text: &str) -> Message {
        Message::assistant(text)
    }

    /// An old exchange too large (~20k tokens) to join the protected tail, so
    /// the histories below have a head to summarise.
    fn bulky_early_exchange() -> Vec<Message> {
        vec![user_msg(&format!("early context {}", "e".repeat(80_000))), assistant_msg("noted")]
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

    // ── Mock ForkedAgent ─────────────────────────────────────────────────────

    struct MockFork {
        responses: Mutex<std::collections::VecDeque<String>>,
    }

    impl MockFork {
        fn new(responses: Vec<&str>) -> Arc<Self> {
            Arc::new(Self {
                responses: Mutex::new(responses.iter().map(|s| s.to_string()).collect()),
            })
        }
    }

    #[async_trait]
    impl ForkedAgent for MockFork {
        async fn run(
            &self,
            _system: &str,
            _messages: Vec<Message>,
            _cancel: CancellationToken,
        ) -> anyhow::Result<String> {
            Ok(self
                .responses
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or_default())
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
        let policy = CompactionPolicy { token_threshold: 2, proactive_ratio: 0.0 };
        let history = vec![user_msg("hello"), assistant_msg("world")];
        assert!(policy.should_compact(&history));
    }

    #[test]
    fn policy_silent_below_threshold() {
        let policy = CompactionPolicy { token_threshold: 100, proactive_ratio: 0.0 };
        let history = vec![user_msg("hi"), assistant_msg("ok")];
        assert!(!policy.should_compact(&history));
    }

    // ── CompactionService ─────────────────────────────────────────────────────

    #[tokio::test]
    async fn compaction_replaces_history_with_summary_ack() {
        let fork = MockFork::new(vec!["Summary text here."]);
        let svc = CompactionService::new(fork);
        // Something must lie before the protected tail (the last two
        // exchanges); a short history is all tail and has nothing to summarise.
        let mut history = bulky_early_exchange();
        history.extend([
            user_msg("what is 2+2?"),
            assistant_msg("4"),
            user_msg("and 3+3?"),
            assistant_msg("6"),
        ]);
        let cancel = CancellationToken::new();
        let (compacted, summary) = svc.compact(&history, None, cancel).await;
        assert!(compacted[0].role == Role::User);
        let first_text = match &compacted[0].content[0] {
            Content::Text(t) => t.clone(),
            _ => panic!("expected text"),
        };
        assert!(first_text.contains("Summary text here."), "summary must be in user msg");
        let ack = match &compacted[1].content[0] {
            Content::Text(t) => t.clone(),
            _ => panic!("expected text"),
        };
        assert!(ack.contains("Understood"), "ack text must be present");
        assert!(summary.is_some());
        assert!(compacted.len() < history.len() + 2, "compaction must shrink the history");
    }

    /// The message the user just sent is not something to summarise away.
    ///
    /// Auto-compaction fires *during* a turn, so the newest user message is
    /// typically the prompt being answered. Dropping it meant the model
    /// answered a summary of everything except the question it was asked.
    #[tokio::test]
    async fn compaction_keeps_the_newest_user_message_verbatim() {
        let fork = MockFork::new(vec!["Summary text here."]);
        let svc = CompactionService::new(fork);
        let mut history = bulky_early_exchange();
        history.extend([
            user_msg("old question"),
            assistant_msg("old answer"),
            user_msg("the question I just asked"),
        ]);
        let (compacted, summary) = svc.compact(&history, None, CancellationToken::new()).await;

        assert!(summary.is_some(), "there was earlier history to summarise");
        let survived = compacted.iter().any(|m| {
            m.role == Role::User
                && m.content.iter().any(|c| {
                    matches!(c, Content::Text(t) if t == "the question I just asked")
                })
        });
        assert!(survived, "the newest user message must survive: {compacted:?}");
    }

    /// A summarised-away `ToolResult` would leave its `ToolUse` orphaned, which
    /// providers reject outright — the turn fails rather than degrades.
    #[tokio::test]
    async fn compaction_never_orphans_a_tool_call_from_its_result() {
        let fork = MockFork::new(vec!["Summary text here."]);
        let svc = CompactionService::new(fork);
        let mut history = bulky_early_exchange();
        history.extend([
            user_msg("first"),
            tool_use_msg("call-0", "read"),
            tool_result_msg("call-0", "early"),
            assistant_msg("first answer"),
            user_msg("run the tool"),
            tool_use_msg("call-1", "grep"),
            tool_result_msg("call-1", "match"),
        ]);
        let (compacted, summary) = svc.compact(&history, None, CancellationToken::new()).await;
        assert!(summary.is_some(), "the bulky head must have been summarised");

        let uses: Vec<&str> = compacted
            .iter()
            .flat_map(|m| &m.content)
            .filter_map(|c| match c {
                Content::ToolUse { id, .. } => Some(id.as_str()),
                _ => None,
            })
            .collect();
        let results: Vec<&str> = compacted
            .iter()
            .flat_map(|m| &m.content)
            .filter_map(|c| match c {
                Content::ToolResult { tool_use_id, .. } => Some(tool_use_id.as_str()),
                _ => None,
            })
            .collect();
        for id in &uses {
            assert!(results.contains(id), "ToolUse {id} lost its result: {compacted:?}");
        }
        for id in &results {
            assert!(uses.contains(id), "ToolResult {id} lost its call: {compacted:?}");
        }
    }

    /// A lone exchange is entirely tail, so there is nothing ahead of it to
    /// summarise. Reporting "not compacted" is honest; replacing it with a
    /// summary of itself would shrink nothing and lose the exchange.
    #[tokio::test]
    async fn a_single_exchange_is_left_alone() {
        let fork = MockFork::new(vec!["Summary text here."]);
        let svc = CompactionService::new(fork);
        let history = vec![user_msg("what is 2+2?"), assistant_msg("4")];
        let (compacted, summary) = svc.compact(&history, None, CancellationToken::new()).await;
        assert_eq!(compacted, history, "a lone exchange must be returned untouched");
        assert!(summary.is_none(), "nothing was summarised");
    }

    #[tokio::test]
    async fn compaction_preserves_original_on_empty_summary() {
        // Summariser returns empty string — must not lose the history.
        let fork = MockFork::new(vec![""]);
        let svc = CompactionService::new(fork);
        let history = vec![user_msg("hi"), assistant_msg("hello")];
        let cancel = CancellationToken::new();
        let (compacted, summary) = svc.compact(&history, None, cancel).await;
        assert_eq!(compacted.len(), 2, "original history must be preserved");
        assert!(summary.is_none(), "no summary on failure");
    }

    #[tokio::test]
    async fn compaction_of_empty_history_is_noop() {
        let fork = MockFork::new(vec![]);
        let svc = CompactionService::new(fork);
        let cancel = CancellationToken::new();
        let (compacted, summary) = svc.compact(&[], None, cancel).await;
        assert!(compacted.is_empty());
        assert!(summary.is_none());
    }
}
