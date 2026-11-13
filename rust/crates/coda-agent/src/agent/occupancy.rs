//! How full the next request is: the provider's measured usage for the last
//! response, plus an estimate of what has been appended since.
//!
//! The measurement covers everything the provider counted — system prompt,
//! tool schemas, every message — so it is the honest base. It is only reused
//! while the request it measured is still the prefix of the next one: a
//! different model, system prompt or tool set, a shorter history or a changed
//! last message drops it, and the projection falls back to a full estimate
//! (messages + system prompt + tool schemas). Anchors live in memory only;
//! [`DefaultHasher`] is deterministic within a process, which is all they need.

use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::sync::Mutex;

use coda_llm::{Content, Message, ToolDefinition, Usage};

use crate::compaction::TokenEstimator;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct OccupancyAnchor {
    measured_tokens: usize,
    history_len: usize,
    last_message_hash: u64,
    fingerprint: u64,
}

/// The measured-usage anchor of one conversation; shared per session so it
/// survives turns.
#[derive(Debug, Default)]
pub struct OccupancyTracker {
    anchor: Mutex<Option<OccupancyAnchor>>,
}

/// The projected size of the next request, in estimated tokens.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Projection {
    /// What the trigger compares with its threshold.
    pub tokens: usize,
    /// The anchor's measured part, when a valid anchor was used.
    pub measured: Option<usize>,
    /// Messages + request overhead, estimated from scratch.
    pub full_estimate: usize,
}

impl OccupancyTracker {
    pub fn new() -> Self {
        Self::default()
    }

    /// Call right after the assistant message for the measured response was
    /// pushed, with the fingerprint of the request that produced it. Ignored
    /// when the usage reports no input.
    pub fn record(&self, history: &[Message], fingerprint: u64, usage: &Usage) {
        let input = usage.total_input_tokens() as usize;
        let Some(last) = history.last() else { return };
        if input == 0 {
            return;
        }
        *self.lock() = Some(OccupancyAnchor {
            measured_tokens: input + usage.output_tokens as usize,
            history_len: history.len(),
            last_message_hash: message_hash(last),
            fingerprint,
        });
    }

    pub fn project(&self, history: &[Message], fingerprint: u64, overhead_tokens: usize) -> Projection {
        let full_estimate = TokenEstimator::estimate(history) + overhead_tokens;
        let anchor = *self.lock();
        match anchor.filter(|a| anchor_is_valid(a, history, fingerprint)) {
            Some(a) => Projection {
                tokens: a.measured_tokens + TokenEstimator::estimate(&history[a.history_len..]),
                measured: Some(a.measured_tokens),
                full_estimate,
            },
            None => Projection { tokens: full_estimate, measured: None, full_estimate },
        }
    }

    pub fn invalidate(&self) {
        *self.lock() = None;
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Option<OccupancyAnchor>> {
        self.anchor.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

fn anchor_is_valid(anchor: &OccupancyAnchor, history: &[Message], fingerprint: u64) -> bool {
    anchor.fingerprint == fingerprint
        && anchor.history_len > 0
        && history.len() >= anchor.history_len
        && message_hash(&history[anchor.history_len - 1]) == anchor.last_message_hash
}

/// Stable hash of model + system prompt + every tool's name, description and
/// schema.
pub fn request_fingerprint(model: &str, system: Option<&str>, tools: &[ToolDefinition]) -> u64 {
    let mut h = DefaultHasher::new();
    model.hash(&mut h);
    system.hash(&mut h);
    tools.len().hash(&mut h);
    for t in tools {
        t.name.hash(&mut h);
        t.description.hash(&mut h);
        t.input_schema_json.hash(&mut h);
    }
    h.finish()
}

/// Estimated tokens of the system prompt and tool schemas: their characters
/// over four, like [`TokenEstimator`].
pub fn request_overhead_tokens(system: Option<&str>, tools: &[ToolDefinition]) -> usize {
    let chars = system.map_or(0, str::len)
        + tools
            .iter()
            .map(|t| t.name.len() + t.description.len() + t.input_schema_json.len())
            .sum::<usize>();
    chars / 4
}

fn message_hash(m: &Message) -> u64 {
    let mut h = DefaultHasher::new();
    m.role.as_str().hash(&mut h);
    m.content.len().hash(&mut h);
    for block in &m.content {
        match block {
            Content::Text(t) => (0u8, t).hash(&mut h),
            Content::ToolUse { id, name, input_json, .. } => (1u8, id, name, input_json).hash(&mut h),
            Content::ToolResult { tool_use_id, content, is_error, status, .. } => {
                (2u8, tool_use_id, content, is_error, status).hash(&mut h)
            }
            Content::Image { media_type, base64 } => (3u8, media_type, base64).hash(&mut h),
            Content::Thinking { text, signature } => (4u8, text, signature).hash(&mut h),
            Content::RedactedThinking { data } => (5u8, data).hash(&mut h),
        }
    }
    h.finish()
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_llm::Role;

    fn tool_result(chars: usize) -> Message {
        Message::new(
            Role::User,
            vec![Content::ToolResult {
                tool_use_id: "call-1".into(),
                content: "r".repeat(chars),
                is_error: false,
                correlation: Default::default(),
                status: None,
            }],
        )
    }

    fn usage() -> Usage {
        Usage { input_tokens: 100_000, cache_read_tokens: 20_000, output_tokens: 3_000, ..Usage::ZERO }
    }

    fn anchored() -> (OccupancyTracker, Vec<Message>, u64) {
        let fp = request_fingerprint("model", Some("system"), &[]);
        let tracker = OccupancyTracker::new();
        let mut history = vec![Message::user("u".repeat(8_000))];
        history.push(Message::assistant("a".repeat(2_000)));
        tracker.record(&history, fp, &usage());
        (tracker, history, fp)
    }

    #[test]
    fn large_system_prompt_and_tool_schemas_count() {
        let system = "s".repeat(200_000);
        let tools: Vec<ToolDefinition> = (0..50)
            .map(|i| ToolDefinition::new(format!("tool_{i}"), "describes it", "x".repeat(4_000)))
            .collect();
        let history = vec![Message::user("h".repeat(10_000))];
        let fp = request_fingerprint("model", Some(&system), &tools);
        let overhead = request_overhead_tokens(Some(&system), &tools);

        let schema_chars: usize =
            tools.iter().map(|t| t.name.len() + t.description.len() + 4_000).sum();
        assert_eq!(overhead, (200_000 + schema_chars) / 4);
        assert!(overhead > 50_000 + 50_000, "system and schemas both count");

        let p = OccupancyTracker::new().project(&history, fp, overhead);
        assert_eq!(p.tokens, 2_500 + overhead);
        assert_eq!(p.measured, None);
        assert_eq!(p.full_estimate, p.tokens);
    }

    #[test]
    fn measured_anchor_plus_growth() {
        let (tracker, mut history, fp) = anchored();
        history.push(tool_result(4_000));

        let p = tracker.project(&history, fp, 1);
        assert_eq!(p.measured, Some(123_000));
        assert_eq!(p.tokens, 123_000 + 1_000, "the measured response is not counted twice");
    }

    #[test]
    fn config_change_rebases() {
        let (tracker, mut history, _) = anchored();
        history.push(tool_result(4_000));
        let tools = [ToolDefinition::new("new_tool", "d", "{}")];
        let other = request_fingerprint("model", Some("system"), &tools);
        let overhead = request_overhead_tokens(Some("system"), &tools);

        let p = tracker.project(&history, other, overhead);
        assert_eq!(p.measured, None);
        assert_eq!(p.tokens, TokenEstimator::estimate(&history) + overhead);
    }

    #[test]
    fn history_replacement_invalidates() {
        let (tracker, history, fp) = anchored();

        let shorter = &history[..1];
        let p = tracker.project(shorter, fp, 7);
        assert_eq!((p.measured, p.tokens), (None, TokenEstimator::estimate(shorter) + 7));

        let mut replaced = history.clone();
        replaced[1] = Message::assistant("a different reply");
        let p = tracker.project(&replaced, fp, 7);
        assert_eq!((p.measured, p.tokens), (None, TokenEstimator::estimate(&replaced) + 7));

        tracker.invalidate();
        assert_eq!(tracker.project(&history, fp, 7).measured, None);
    }

    #[test]
    fn zero_usage_is_not_an_anchor() {
        let fp = request_fingerprint("model", None, &[]);
        let tracker = OccupancyTracker::new();
        let history = vec![Message::user("u"), Message::assistant("a")];
        tracker.record(&history, fp, &Usage::ZERO);
        assert_eq!(tracker.project(&history, fp, 0).measured, None);
    }
}
