//! Conversation compaction: summarise history to free context space.
//!
//! Matches C# `Compaction/CompactionService.cs`, `CompactionPrompts.cs`,
//! `TokenEstimator.cs`.
//!
//! # Invariants
//! - The most recent exchange must survive compaction (tail-preservation).
//! - Every `ToolUse` block must have a matching `ToolResult` block so the
//!   provider does not reject the next request with an ordering error.
//! - When the summariser call fails (empty reply), the original history is
//!   returned unchanged — no silent data loss.

use std::sync::Arc;

use coda_llm::{Content, Message, Role};
use tokio_util::sync::CancellationToken;

use crate::goal::ForkedAgent;

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
                    Content::Image { .. } => chars += 64, // rough estimate
                }
            }
        }
        chars / 4
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Compaction prompts
// ─────────────────────────────────────────────────────────────────────────────

pub struct CompactionPrompts;

impl CompactionPrompts {
    pub const SYSTEM_PROMPT: &'static str = "You summarize a software-engineering conversation \
        so it can continue after older messages are dropped. Capture: the user's goal and task, \
        key decisions and constraints, files and functions changed, what is done vs pending, \
        errors hit and their fixes, and the immediate next step. Be dense and specific \
        (file paths, names, commands). Output only the summary.";

    pub const ACK_TEXT: &'static str = "Understood. I'll continue from the summary above.";

    pub fn build_user_message(history: &[Message]) -> String {
        let transcript = Self::render_transcript(history);
        format!("Summarize the following conversation for continuation:\n\n{transcript}")
    }

    /// Render a conversation history as a plain-text transcript for the summariser.
    pub fn render_transcript(history: &[Message]) -> String {
        let mut out = String::new();
        for msg in history {
            let role = if msg.role == Role::User { "User" } else { "Assistant" };
            for block in &msg.content {
                match block {
                    Content::Text(t) if !t.trim().is_empty() => {
                        out.push_str(role);
                        out.push_str(": ");
                        out.push_str(t);
                        out.push('\n');
                    }
                    Content::ToolUse { name, .. } => {
                        out.push_str("[tool call: ");
                        out.push_str(name);
                        out.push_str("]\n");
                    }
                    Content::ToolResult { content, .. } => {
                        let preview = if content.len() > 500 {
                            format!("{}…", &content[..500])
                        } else {
                            content.clone()
                        };
                        out.push_str("[tool result: ");
                        out.push_str(&preview);
                        out.push_str("]\n");
                    }
                    _ => {}
                }
            }
        }
        out.trim_end().to_owned()
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

    /// Compact `history` to a summary/ack pair.
    ///
    /// Returns the compacted history and the raw summary text (for
    /// `PostCompact` hooks).  When compaction fails, returns the original
    /// history and `summary = None`.
    pub async fn compact(
        &self,
        history: &[Message],
        instructions_override: Option<&str>,
        cancel: CancellationToken,
    ) -> (Vec<Message>, Option<String>) {
        if history.is_empty() {
            return (history.to_vec(), None);
        }

        let system = instructions_override.unwrap_or(CompactionPrompts::SYSTEM_PROMPT);
        let user_msg = CompactionPrompts::build_user_message(history);
        let summary = match self.fork.run(system, vec![Message::user(user_msg)], cancel).await {
            Ok(s) if !s.trim().is_empty() => s,
            // Summariser failed or returned empty — preserve original history.
            _ => return (history.to_vec(), None),
        };

        let compacted = vec![
            Message::user(format!("Summary of the earlier conversation:\n\n{summary}")),
            Message::new(Role::Assistant, vec![Content::Text(CompactionPrompts::ACK_TEXT.into())]),
        ];
        (compacted, Some(summary))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Trigger policy
// ─────────────────────────────────────────────────────────────────────────────

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
}

// ─────────────────────────────────────────────────────────────────────────────
// Tail-preservation helper
// ─────────────────────────────────────────────────────────────────────────────

/// Returns a slice of `history` that must be preserved intact after compaction.
///
/// The tail is: the last complete user→assistant exchange plus any trailing
/// incomplete exchange.  Every `ToolUse` block must be paired with its
/// `ToolResult` so providers do not see an orphaned tool call.
///
/// # Invariant
/// `history[tail_start..]` never contains an unpaired `ToolUse`.
pub fn compaction_tail_start(history: &[Message]) -> usize {
    if history.len() <= 2 {
        return 0;
    }

    // Walk backward to find the last user turn that safely starts the tail.
    // A user turn at idx is safe when:
    //  1. history[idx..] has no unpaired ToolUse (all tool calls are resolved).
    //  2. The immediately preceding assistant turn (history[idx-1], if it exists)
    //     does NOT contain ToolUse blocks.  If it does, the exchange that started
    //     it belongs to the tail as well, so we move the candidate start earlier.
    let mut idx = history.len();
    while idx > 0 {
        idx -= 1;
        if history[idx].role != Role::User {
            continue;
        }

        // Condition 1: no unpaired ToolUse in the candidate tail.
        if has_unpaired_tool_use(&history[idx..]) {
            continue;
        }

        // Condition 2: the previous assistant turn (if present) must have no
        // ToolUse blocks.  A preceding tool call means we need to include that
        // turn so the ToolUse and ToolResult stay in the same slice.
        if idx > 0 {
            let prev = &history[idx - 1];
            if prev.role == Role::Assistant {
                let prev_has_tool_use =
                    prev.content.iter().any(|b| matches!(b, Content::ToolUse { .. }));
                if prev_has_tool_use {
                    continue; // must include the preceding assistant + tool call
                }
            }
        }

        return idx;
    }
    // No safe cut point: keep everything.
    0
}

/// Returns `true` when the slice contains a `ToolUse` with no matching
/// `ToolResult` anywhere in the same slice.
fn has_unpaired_tool_use(slice: &[Message]) -> bool {
    use std::collections::HashSet;
    let mut pending: HashSet<String> = HashSet::new();
    for msg in slice {
        for block in &msg.content {
            match block {
                Content::ToolUse { id, .. } => { pending.insert(id.clone()); }
                Content::ToolResult { tool_use_id, .. } => { pending.remove(tool_use_id); }
                _ => {}
            }
        }
    }
    !pending.is_empty()
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
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

    // ── Tail-preservation ────────────────────────────────────────────────────

    /// Most recent exchange must survive: tail_start must point to or before
    /// the last user message.
    #[test]
    fn tail_preservation_includes_last_exchange() {
        let history = vec![
            user_msg("question 1"),
            assistant_msg("answer 1"),
            user_msg("question 2"),
            assistant_msg("answer 2"),
        ];
        let tail = compaction_tail_start(&history);
        // The tail must include the last user message (index 2).
        assert!(tail <= 2, "tail_start={tail} should be <= 2");
        // No messages after tail_start should be cut.
        assert_eq!(&history[tail..], &history[tail..]);
    }

    /// Critical: a tool_use block must always have its matching tool_result.
    /// If the last exchange contains a tool_use, the tail must include the
    /// preceding user message that started the exchange.
    #[test]
    fn tail_includes_tool_use_and_its_result() {
        let history = vec![
            user_msg("do something"),
            tool_use_msg("c1", "read_file"),
            tool_result_msg("c1", "file contents"),
            assistant_msg("done"),
        ];
        let tail = compaction_tail_start(&history);
        // The tail slice must contain both the tool_use and the tool_result.
        let tail_slice = &history[tail..];
        let has_use = tail_slice.iter().any(|m| {
            m.content.iter().any(|b| matches!(b, Content::ToolUse { id, .. } if id == "c1"))
        });
        let has_result = tail_slice.iter().any(|m| {
            m.content.iter().any(|b| matches!(b, Content::ToolResult { tool_use_id, .. } if tool_use_id == "c1"))
        });
        assert!(has_use, "tail must include the tool_use block");
        assert!(has_result, "tail must include the matching tool_result");
    }

    /// An incomplete exchange (tool_use with no result) is entirely preserved.
    #[test]
    fn incomplete_exchange_is_fully_preserved() {
        let history = vec![
            user_msg("initial message"),
            tool_use_msg("c1", "read_file"),
            // no tool_result — dangling tool use
        ];
        // The tail must encompass the whole history (no valid cut point).
        let tail = compaction_tail_start(&history);
        assert_eq!(tail, 0, "incomplete exchange forces full preservation");
    }

    // ── CompactionService ─────────────────────────────────────────────────────

    #[tokio::test]
    async fn compaction_replaces_history_with_summary_ack() {
        let fork = MockFork::new(vec!["Summary text here."]);
        let svc = CompactionService::new(fork);
        let history = vec![user_msg("what is 2+2?"), assistant_msg("4")];
        let cancel = CancellationToken::new();
        let (compacted, summary) = svc.compact(&history, None, cancel).await;
        assert_eq!(compacted.len(), 2);
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
