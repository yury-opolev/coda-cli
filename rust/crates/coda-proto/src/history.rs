//! UI-safe history projection DTOs.
//!
//! `HistoryEntry` / `HistoryBlock` are the wire shape for one committed or
//! in-flight conversation turn. This module is deliberately foundational: it
//! exists now only so `TurnState.liveEntries` (see `crate::state`) can carry
//! the in-flight turn correctly. The full `session/getHistory` RPC, saved
//! transcript projection and the `state/live.rs` reconstruction service are
//! the next stage; nothing here reaches into `coda_llm::Message` (that
//! projection lives in `coda-serve`, which owns the LLM dependency).
//!
//! Secret deny-list (binding on every field below): never a
//! `Content::Thinking.signature`, never `RedactedThinking.data`, never image
//! base64, never a credential/header/token value. `HistoryBlock::Image` is
//! metadata only (`media_type`, `byte_length`) — the bytes themselves are
//! never carried on this DTO.
//!
//! No-silent-truncation rule (binding): every variant that can carry capped
//! free text also carries `omittedReason` + `fullLength`, set exactly when
//! the projection retained less than the original. Use
//! [`HistoryBlock::text_capped`] / [`HistoryBlock::reasoning_capped`] rather
//! than constructing those variants by hand, so the marker can never drift
//! away from the truncation it describes.

use serde::{Deserialize, Serialize};

/// What an entry *is*, independent of its wire `role`.
///
/// A provider conversation encodes tool results as **user-role** messages.
/// A client that renders `role == "user"` as "something the operator typed"
/// therefore invents user prompts that never happened. This discriminator
/// exists so that mistake is not possible: `role` stays exactly what the
/// provider protocol says, and `entryKind` says what it means.
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum HistoryEntryKind {
    /// Text the operator actually sent.
    UserPrompt,
    /// A user-role message carrying tool results (and possibly steering text
    /// appended at a delivery boundary). **Never** a new operator prompt.
    ToolResults,
    /// An assistant message.
    Assistant,
}

/// One block of content within a [`HistoryEntry`].
///
/// Every variant that can carry capped free text also carries the pair
/// (`omittedReason`, `fullLength`). They are present **exactly** when the
/// projection retained less than the original, so a client can always tell a
/// complete value from a shortened one. Both are omitted — never `null`,
/// never a fabricated length — when nothing was dropped.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(tag = "kind", rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum HistoryBlock {
    #[serde(rename_all = "camelCase")]
    Text {
        text: String,
        /// `"tooLarge"` (a per-block cap) or `"liveBudgetExceeded"` (the
        /// live-turn retention budget). Present exactly when `text` is
        /// shorter than what was actually said.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        omitted_reason: Option<String>,
        /// Byte length of the *original* text before any cap was applied.
        /// Present exactly when `text` was truncated.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        full_length: Option<i64>,
    },
    /// A reasoning summary — never the opaque provider signature/ciphertext.
    ///
    /// `redacted: true` means the provider withheld the text; that is a
    /// policy statement, not a truncation, so it carries no `fullLength`.
    #[serde(rename_all = "camelCase")]
    ReasoningSummary {
        text: String,
        redacted: bool,
        /// See [`HistoryBlock::Text::omitted_reason`].
        #[serde(default, skip_serializing_if = "Option::is_none")]
        omitted_reason: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        full_length: Option<i64>,
    },
    #[serde(rename_all = "camelCase")]
    ToolCall {
        call_id: String,
        tool_name: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        input_json: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        input_omitted_reason: Option<String>,
        /// Byte length of the *original* `input_json` before any cap was
        /// applied. Present exactly when `input_json` was truncated, so a
        /// client can say how much it is not seeing instead of assuming the
        /// capped string is the whole input (I8).
        #[serde(default, skip_serializing_if = "Option::is_none")]
        input_full_length: Option<i64>,
        /// The turn that produced this call, when the stored message
        /// recorded one. Provider `call_id`s are only unique within a
        /// request, so `(turn_id, batch_id, call_id)` is the identity a
        /// client should key on. Omitted — never invented — for transcripts
        /// written before correlation ids existed.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        turn_id: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        batch_id: Option<String>,
    },
    #[serde(rename_all = "camelCase")]
    ToolResult {
        call_id: String,
        is_error: bool,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        status: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        content: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        omitted_reason: Option<String>,
        /// Byte length of the *original* `content` before any cap was applied
        /// (I8). Present exactly when `content` was truncated.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        full_length: Option<i64>,
        /// See [`HistoryBlock::ToolCall::turn_id`].
        #[serde(default, skip_serializing_if = "Option::is_none")]
        turn_id: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        batch_id: Option<String>,
    },
    /// Metadata only — never a base64 payload (secret deny-list).
    #[serde(rename_all = "camelCase")]
    Image { media_type: String, byte_length: i64 },
}

    /// `omittedReason` when a per-block byte cap shortened a value.
    pub const OMITTED_TOO_LARGE: &str = "tooLarge";
    /// `omittedReason` when the live-turn retention budget shortened a value.
    pub const OMITTED_LIVE_BUDGET: &str = "liveBudgetExceeded";

    impl HistoryBlock {
        /// A text block capped at `max_bytes` on a UTF-8 boundary, with the
        /// marker fields set **exactly** when truncation happened.
        ///
        /// Constructing capped free text through this constructor is what makes
        /// "a truncated block always says so" a property of the type rather than
        /// a rule every call site has to remember.
        pub fn text_capped(text: &str, max_bytes: usize) -> Self {
            let (capped, full_length) = truncate_text(text, max_bytes);
            HistoryBlock::Text {
                text: capped,
                omitted_reason: full_length.map(|_| OMITTED_TOO_LARGE.to_string()),
                full_length,
            }
        }

        /// A reasoning summary capped at `max_bytes`. See [`Self::text_capped`].
        pub fn reasoning_capped(text: &str, max_bytes: usize) -> Self {
            let (capped, full_length) = truncate_text(text, max_bytes);
            HistoryBlock::ReasoningSummary {
                text: capped,
                redacted: false,
                omitted_reason: full_length.map(|_| OMITTED_TOO_LARGE.to_string()),
                full_length,
            }
        }
    }

/// One role-tagged entry (committed message or in-flight live turn content).
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct HistoryEntry {
    pub index: i64,
    /// `"user" | "assistant"` — exactly the provider protocol role.
    pub role: String,
    /// What this entry actually is. See [`HistoryEntryKind`]: a `user` role
    /// does **not** imply the operator typed anything.
    #[serde(default = "default_entry_kind")]
    pub entry_kind: HistoryEntryKind,
    pub blocks: Vec<HistoryBlock>,
}

fn default_entry_kind() -> HistoryEntryKind {
    HistoryEntryKind::UserPrompt
}

impl HistoryEntry {
    /// Builds an entry, deriving [`HistoryEntryKind`] from the role and the
    /// blocks so no call site can forget it.
    pub fn new(index: i64, role: &str, blocks: Vec<HistoryBlock>) -> Self {
        let entry_kind = if role == "assistant" {
            HistoryEntryKind::Assistant
        } else if blocks.iter().any(|b| matches!(b, HistoryBlock::ToolResult { .. })) {
            HistoryEntryKind::ToolResults
        } else {
            HistoryEntryKind::UserPrompt
        };
        Self { index, role: role.to_string(), entry_kind, blocks }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// session/getHistory and session/listSessions
// ─────────────────────────────────────────────────────────────────────────────

/// A saved transcript, as listed by `session/listSessions`.
///
/// Only ids the engine itself validated, only the current workspace, never a
/// raw filesystem path: a client must never be handed something it could feed
/// back as a path.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SessionSummaryDto {
    pub session_id: String,
    pub created_utc: String,
    pub message_count: i64,
    /// Capped first-user-message preview.
    pub preview: String,
    pub preview_truncated: bool,
    /// `true` for the session this engine is currently running.
    pub is_current: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ListSessionsResult {
    pub sessions: Vec<SessionSummaryDto>,
    pub total_known: i64,
    /// `true` when `total_known` exceeded the (bounded) limit applied.
    pub truncated: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct GetHistoryResult {
    pub session_id: String,
    pub engine_instance_id: String,
    /// `true` when this is the session the engine is running, so `cursor`,
    /// `historyEpoch` and `liveEntries` are meaningful.
    pub is_live_session: bool,
    /// Present only for the live session. Absent (not `0`) for a saved
    /// transcript, which has no epoch of its own.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub history_epoch: Option<i64>,
    /// The event cursor this read is exact at — the same fence
    /// `session/getState` reports, read under the same lock. Present only
    /// for the live session.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cursor: Option<i64>,
    /// The committed/live fence at the instant of this read. A client
    /// reconstructs the conversation as
    /// `entries[..historyLength] ++ liveEntries` and cannot double-count.
    pub history_length: i64,
    pub entries: Vec<HistoryEntry>,
    /// Index to pass as the next `sinceIndex`.
    pub next_index: i64,
    pub total_known: i64,
    /// `true` when `nextIndex < totalKnown`: there is more to page.
    pub truncated: bool,
    /// The in-flight turn, projected with the same DTOs. Present only when
    /// `includeLive` was requested, this is the live session, and a turn is
    /// actually running.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub live_entries: Option<Vec<HistoryEntry>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub live_truncated: Option<bool>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub live_omitted_bytes: Option<i64>,
}

/// Truncates `text` to `max_bytes` on a UTF-8 boundary, returning the
/// truncated string and the original byte length when truncation happened.
///
/// Oversized content is never silently dropped: callers set an explicit
/// `omittedReason`/`fullLength` alongside this, per the plan's "no silent
/// truncation" rule for state/history DTOs.
pub fn truncate_text(text: &str, max_bytes: usize) -> (String, Option<i64>) {
    if text.len() <= max_bytes {
        return (text.to_string(), None);
    }
    let mut end = max_bytes;
    while end > 0 && !text.is_char_boundary(end) {
        end -= 1;
    }
    (text[..end].to_string(), Some(text.len() as i64))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn text_block_round_trips() {
        let entry = HistoryEntry::new(0, "assistant", vec![HistoryBlock::text_capped("hi", 1024)]);
        let v = serde_json::to_value(&entry).unwrap();
        assert_eq!(v["blocks"][0]["kind"], "text");
        assert_eq!(v["blocks"][0]["text"], "hi");
        assert_eq!(v["entryKind"], "assistant");
        let back: HistoryEntry = serde_json::from_value(v).unwrap();
        assert_eq!(back, entry);
    }

    // ── A user-role tool-result message is not an operator prompt ────────

    #[test]
    fn a_user_role_entry_carrying_tool_results_is_never_labelled_a_user_prompt() {
        let entry = HistoryEntry::new(
            3,
            "user",
            vec![HistoryBlock::ToolResult {
                call_id: "c1".into(),
                is_error: false,
                status: Some("Succeeded".into()),
                content: Some("file contents".into()),
                omitted_reason: None,
                full_length: None,
                turn_id: Some("t1".into()),
                batch_id: Some("b1".into()),
            }],
        );
        assert_eq!(entry.role, "user", "the provider role must be reported exactly");
        assert_eq!(
            entry.entry_kind,
            HistoryEntryKind::ToolResults,
            "a client must be able to tell this is not something the operator typed"
        );
        let v = serde_json::to_value(&entry).unwrap();
        assert_eq!(v["entryKind"], "toolResults");
    }

    #[test]
    fn a_mixed_user_entry_with_steering_text_and_tool_results_is_still_tool_results() {
        // The agent appends delivered steering text into the same user-role
        // message as tool results. That message must not be replayed as a
        // fresh prompt either.
        let entry = HistoryEntry::new(
            4,
            "user",
            vec![
                HistoryBlock::ToolResult {
                    call_id: "c1".into(),
                    is_error: false,
                    status: None,
                    content: Some("ok".into()),
                    omitted_reason: None,
                    full_length: None,
                    turn_id: None,
                    batch_id: None,
                },
                HistoryBlock::text_capped("actually, stop", 1024),
            ],
        );
        assert_eq!(entry.entry_kind, HistoryEntryKind::ToolResults);
    }

    #[test]
    fn a_genuine_operator_prompt_is_labelled_a_user_prompt() {
        let entry = HistoryEntry::new(0, "user", vec![HistoryBlock::text_capped("hello", 1024)]);
        assert_eq!(entry.entry_kind, HistoryEntryKind::UserPrompt);
    }

    #[test]
    fn correlation_metadata_is_omitted_when_a_stored_message_never_had_it() {
        // Old transcripts predate correlation ids. They must be reported as
        // absent, never back-filled with a plausible-looking value.
        let block = HistoryBlock::ToolCall {
            call_id: "legacy-1".into(),
            tool_name: "read_file".into(),
            input_json: Some("{}".into()),
            input_omitted_reason: None,
            input_full_length: None,
            turn_id: None,
            batch_id: None,
        };
        let v = serde_json::to_value(&block).unwrap();
        assert!(v.get("turnId").is_none());
        assert!(v.get("batchId").is_none());
    }

    #[test]
    fn reasoning_summary_never_carries_a_signature_field() {
        let block = HistoryBlock::ReasoningSummary {
            text: "thought about it".into(),
            redacted: false,
            omitted_reason: None,
            full_length: None,
        };
        let v = serde_json::to_value(&block).unwrap();
        assert!(v.get("signature").is_none());
        assert!(v.get("data").is_none());
    }

    // ── I8 (review): free-text variants declare their omissions too ───────

    #[test]
    fn a_truncated_text_block_round_trips_its_omission_metadata() {
        let original = "x".repeat(100_000);
        let (capped, full_len) = truncate_text(&original, 64 * 1024);
        let block = HistoryBlock::Text {
            text: capped,
            omitted_reason: full_len.map(|_| "tooLarge".to_string()),
            full_length: full_len,
        };
        let v = serde_json::to_value(&block).unwrap();
        assert_eq!(v["fullLength"], 100_000);
        assert_eq!(v["omittedReason"], "tooLarge");
        let back: HistoryBlock = serde_json::from_value(v).unwrap();
        assert_eq!(back, block);
    }

    #[test]
    fn an_untruncated_text_block_omits_its_omission_metadata() {
        let block =
            HistoryBlock::Text { text: "small".into(), omitted_reason: None, full_length: None };
        let v = serde_json::to_value(&block).unwrap();
        assert!(v.get("fullLength").is_none(), "absent metadata is omitted, never null");
        assert!(v.get("omittedReason").is_none());
        // A pre-review client that never sent the fields still deserialises.
        let legacy: HistoryBlock =
            serde_json::from_value(serde_json::json!({ "kind": "text", "text": "small" })).unwrap();
        assert_eq!(legacy, block);
    }

    #[test]
    fn a_truncated_reasoning_summary_round_trips_its_omission_metadata() {
        let block = HistoryBlock::ReasoningSummary {
            text: "half a thou".into(),
            redacted: false,
            omitted_reason: Some("liveBudgetExceeded".into()),
            full_length: Some(4096),
        };
        let v = serde_json::to_value(&block).unwrap();
        assert_eq!(v["fullLength"], 4096);
        assert_eq!(v["omittedReason"], "liveBudgetExceeded");
        assert!(v.get("signature").is_none(), "SECURITY: never the opaque token");
        let back: HistoryBlock = serde_json::from_value(v).unwrap();
        assert_eq!(back, block);
    }

    #[test]
    fn image_block_never_carries_base64() {
        let block = HistoryBlock::Image { media_type: "image/png".into(), byte_length: 4096 };
        let v = serde_json::to_value(&block).unwrap();
        assert!(v.get("base64").is_none());
        assert!(v.get("data").is_none());
        assert_eq!(v["byteLength"], 4096);
    }

    #[test]
    fn truncate_text_is_noop_under_the_cap() {
        let (s, full_len) = truncate_text("hello", 100);
        assert_eq!(s, "hello");
        assert!(full_len.is_none());
    }

    #[test]
    fn truncate_text_reports_full_length_when_over_the_cap() {
        let long = "x".repeat(200);
        let (s, full_len) = truncate_text(&long, 100);
        assert_eq!(s.len(), 100);
        assert_eq!(full_len, Some(200));
    }

    #[test]
    fn truncate_text_never_splits_a_utf8_boundary() {
        // "é" is 2 bytes in UTF-8; cap lands exactly mid-character.
        let text = "é".repeat(60); // 120 bytes
        let (s, full_len) = truncate_text(&text, 101);
        assert!(s.is_char_boundary(s.len()));
        assert!(full_len.is_some());
    }

    // ── I8: truncated blocks must declare the original byte length ────────

    #[test]
    fn a_truncated_tool_call_block_declares_the_original_byte_length() {
        let original = "x".repeat(9000);
        let (capped, full_len) = truncate_text(&original, 8 * 1024);
        let block = HistoryBlock::ToolCall {
            call_id: "c1".into(),
            tool_name: "read_file".into(),
            input_json: Some(capped),
            input_omitted_reason: full_len.map(|_| "tooLarge".to_string()),
            input_full_length: full_len,
            turn_id: None,
            batch_id: None,
        };
        let v = serde_json::to_value(&block).unwrap();
        assert_eq!(v["inputOmittedReason"], "tooLarge");
        assert_eq!(v["inputFullLength"], 9000, "a client must be told how much it is not seeing");
        assert_eq!(v["inputJson"].as_str().unwrap().len(), 8 * 1024);
        let back: HistoryBlock = serde_json::from_value(v).unwrap();
        assert_eq!(back, block);
    }

    #[test]
    fn a_truncated_tool_result_block_declares_the_original_byte_length() {
        let original = "abc".repeat(20_000);
        let (capped, full_len) = truncate_text(&original, 16 * 1024);
        let block = HistoryBlock::ToolResult {
            call_id: "c1".into(),
            is_error: false,
            status: Some("Succeeded".into()),
            content: Some(capped),
            omitted_reason: full_len.map(|_| "tooLarge".to_string()),
            full_length: full_len,
            turn_id: None,
            batch_id: None,
        };
        let v = serde_json::to_value(&block).unwrap();
        assert_eq!(v["fullLength"], 60_000);
        assert_eq!(v["omittedReason"], "tooLarge");
    }

    #[test]
    fn an_untruncated_block_omits_the_length_metadata_entirely() {
        let (capped, full_len) = truncate_text("small", 1024);
        let block = HistoryBlock::ToolResult {
            call_id: "c1".into(),
            is_error: false,
            status: None,
            content: Some(capped),
            omitted_reason: None,
            full_length: full_len,
            turn_id: None,
            batch_id: None,
        };
        let v = serde_json::to_value(&block).unwrap();
        assert!(v.get("fullLength").is_none(), "absent metadata must be omitted, never null");
        assert!(v.get("omittedReason").is_none());
    }

    #[test]
    fn a_cap_landing_mid_character_still_reports_the_true_original_byte_length() {
        // Multi-byte content capped mid-character: the retained text must be
        // valid UTF-8 and `fullLength` must be the true original byte length,
        // not the post-backtrack one.
        let original = "日本語テキスト".repeat(500); // 3 bytes per char
        let cap = 1000;
        let (capped, full_len) = truncate_text(&original, cap);
        assert!(capped.len() < cap + 4 && capped.len() <= cap);
        assert_eq!(full_len, Some(original.len() as i64));
        assert!(std::str::from_utf8(capped.as_bytes()).is_ok());
    }
}
