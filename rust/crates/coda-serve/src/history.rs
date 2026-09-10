//! UI-safe projection of `coda_llm::Message` into history DTOs, plus the
//! bounded reads behind `session/getHistory` and `session/listSessions`.
//!
//! # Secret deny-list (§2.3, binding)
//!
//! A projected block **never** carries:
//!
//! - `Content::Thinking.signature` — the opaque provider token that must be
//!   replayed verbatim to the provider. It is a credential-shaped value with
//!   no display meaning, and it stays out of every DTO.
//! - `Content::RedactedThinking.data` — provider ciphertext. The block is
//!   reported as `reasoningSummary { text: "", redacted: true }` so a client
//!   knows reasoning happened and that it cannot be shown, rather than being
//!   handed the ciphertext or being told nothing happened.
//! - Image base64. `Image` becomes `{ mediaType, byteLength }`.
//!
//! Visible user text, tool inputs and tool results **are** carried: an
//! authorised UI needs them. They remain sensitive — they are never written
//! to diagnostics, which stay metadata-only.
//!
//! # Consistency boundary
//!
//! The live session's history is read under the **same** lock order the turn
//! commit uses (`HISTORY -> STATE`), so the committed prefix, the
//! `historyLength` fence, the `historyEpoch` and the live-turn projection in
//! one response always describe the same instant. This is deliberately not an
//! independent clone with its own cursor: a client concatenating
//! `entries[..historyLength]` with `liveEntries` cannot double-count at any
//! interleaving, including a turn completing mid-page.

use coda_llm::{Content, Message};
use coda_proto::history::{
    GetHistoryResult, HistoryBlock, HistoryEntry, SessionSummaryDto, truncate_text,
};

use crate::dispatch::RpcError;

/// Per-block caps for committed history. Oversized fields are truncated with
/// an explicit `omittedReason` + `fullLength`, never silently shortened.
pub const TOOL_INPUT_CAP: usize = 8 * 1024;
pub const TOOL_RESULT_CAP: usize = 16 * 1024;
pub const TEXT_BLOCK_CAP: usize = 64 * 1024;

/// Cap on a `session/listSessions` preview.
pub const PREVIEW_CAP: usize = 200;
/// Default and maximum page sizes. A client cannot ask the engine to
/// materialise an unbounded page.
pub const DEFAULT_HISTORY_LIMIT: i64 = 100;
pub const MAX_HISTORY_LIMIT: i64 = 500;
pub const DEFAULT_SESSION_LIMIT: i64 = 50;
pub const MAX_SESSION_LIMIT: i64 = 200;

/// Projects one committed message.
///
/// `index` is its absolute position in the conversation, which is what makes
/// paging safe: indices are stable within one `historyEpoch`, so a client can
/// reassemble pages without overlap detection.
pub fn project_message(index: i64, message: &Message) -> HistoryEntry {
    let role = message.role.as_str();
    let blocks = message.content.iter().filter_map(project_block).collect();
    HistoryEntry::new(index, role, blocks)
}

/// Projects a slice of committed history, numbering from `start_index`.
pub fn project_messages(start_index: i64, messages: &[Message]) -> Vec<HistoryEntry> {
    messages
        .iter()
        .enumerate()
        .map(|(offset, m)| project_message(start_index + offset as i64, m))
        .collect()
}

fn project_block(block: &Content) -> Option<HistoryBlock> {
    match block {
        // Capped through the DTO constructor, so an oversized prompt is
        // always accompanied by `omittedReason`/`fullLength` and can never
        // be mistaken for the whole message.
        Content::Text(text) => Some(HistoryBlock::text_capped(text, TEXT_BLOCK_CAP)),
        // SECURITY: `signature` is deliberately not read. It is the opaque
        // provider token, not content.
        Content::Thinking { text, .. } => {
            Some(HistoryBlock::reasoning_capped(text, TEXT_BLOCK_CAP))
        }
        // SECURITY: `data` is provider ciphertext and is never carried. The
        // block is still reported so a client knows reasoning happened. Its
        // text is empty by policy, not by truncation, so it carries no
        // omission metadata — saying `tooLarge` here would be false.
        Content::RedactedThinking { .. } => Some(HistoryBlock::ReasoningSummary {
            text: String::new(),
            redacted: true,
            omitted_reason: None,
            full_length: None,
        }),
        Content::ToolUse { id, name, input_json, correlation } => {
            let (capped, full_len) = truncate_text(input_json, TOOL_INPUT_CAP);
            Some(HistoryBlock::ToolCall {
                call_id: id.clone(),
                tool_name: name.clone(),
                input_json: Some(capped),
                input_omitted_reason: full_len.map(|_| "tooLarge".to_string()),
                input_full_length: full_len,
                // Absent for transcripts written before correlation ids
                // existed. Omitted, never back-filled with a guess.
                turn_id: correlation.root_turn_id.clone(),
                batch_id: correlation.activity_id.clone(),
            })
        }
        Content::ToolResult { tool_use_id, content, is_error, correlation, status } => {
            let (capped, full_len) = truncate_text(content, TOOL_RESULT_CAP);
            Some(HistoryBlock::ToolResult {
                call_id: tool_use_id.clone(),
                is_error: *is_error,
                status: status.clone(),
                content: Some(capped),
                omitted_reason: full_len.map(|_| "tooLarge".to_string()),
                full_length: full_len,
                turn_id: correlation.root_turn_id.clone(),
                batch_id: correlation.activity_id.clone(),
            })
        }
        // SECURITY: metadata only — the base64 payload is never carried.
        Content::Image { media_type, base64 } => Some(HistoryBlock::Image {
            media_type: media_type.clone(),
            byte_length: decoded_byte_length(base64),
        }),
    }
}

/// The decoded size of a base64 payload, computed arithmetically so the
/// payload itself is never decoded into memory just to measure it.
fn decoded_byte_length(base64: &str) -> i64 {
    let len = base64.len() as i64;
    if len == 0 {
        return 0;
    }
    let padding = base64.bytes().rev().take_while(|b| *b == b'=').count() as i64;
    (len / 4) * 3 - padding
}

/// A bounded, capped preview of the first operator prompt in a transcript.
///
/// Tool-result messages are user-role too, so "the first user message" is not
/// necessarily anything the operator typed. This scans for a genuine prompt.
pub fn preview_of(messages: &[Message]) -> (String, bool) {
    let first_prompt = messages.iter().find(|m| {
        m.role == coda_llm::Role::User
            && !m.content.iter().any(|c| matches!(c, Content::ToolResult { .. }))
    });
    let text = first_prompt.map(Message::text).unwrap_or_default();
    let flattened: String = text.split_whitespace().collect::<Vec<_>>().join(" ");
    let (capped, full) = truncate_text(&flattened, PREVIEW_CAP);
    (capped, full.is_some())
}

/// Projects a `SessionTranscriptStore` summary into the wire DTO.
///
/// Never carries a filesystem path: a client must not be handed something it
/// could feed back as one.
pub fn project_summary(
    summary: &coda_agent::session::SessionSummary,
    is_current: bool,
) -> SessionSummaryDto {
    let flattened: String = summary.preview.split_whitespace().collect::<Vec<_>>().join(" ");
    let (preview, full) = truncate_text(&flattened, PREVIEW_CAP);
    SessionSummaryDto {
        session_id: summary.id.clone(),
        created_utc: summary.created_utc.to_rfc3339(),
        message_count: summary.message_count as i64,
        preview,
        preview_truncated: full.is_some(),
        is_current,
    }
}

/// Applies the default page size when the client did not ask for one.
///
/// A supplied `limit` has already been validated and clamped by
/// `GetHistoryParams::normalise` / `ListSessionsParams::normalise` at the
/// dispatch boundary — `0`, a negative value and a non-integer are typed
/// `-32602`s there, never a silent reinterpretation here. The `max` is
/// re-applied defensively so a direct caller cannot bypass the ceiling.
pub fn clamp_limit(requested: Option<i64>, default: i64, max: i64) -> i64 {
    match requested {
        Some(n) if n > 0 => n.min(max),
        _ => default,
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// session/getHistory
// ─────────────────────────────────────────────────────────────────────────────

/// The committed slice a single `session/getHistory` call needs, plus the
/// total it was sliced from.
///
/// Taken under the HISTORY lock so an unbounded conversation is never cloned
/// wholesale just to answer one bounded page.
pub struct CommittedPage {
    pub total: i64,
    pub start: i64,
    pub messages: Vec<coda_llm::Message>,
}

/// The `[start, end)` window a request asks for, clamped into the real range.
///
/// A `sinceIndex` past the end yields an empty final page, and a negative one
/// is clamped to zero — neither panics and neither wraps.
pub fn page_bounds(p: &crate::dispatch::GetHistoryParams, total: i64) -> (i64, i64) {
    let limit = clamp_limit(p.limit, DEFAULT_HISTORY_LIMIT, MAX_HISTORY_LIMIT);
    let start = p.since_index.unwrap_or(0).max(0).min(total);
    (start, (start + limit).min(total))
}

/// Builds the live-session response from a *single* consistent read.
///
/// `page` and `view` must have been taken together under the HISTORY lock
/// (see the module docs): `page.total` is the committed fence read as ground
/// truth at the same instant `view` was projected, so
/// `entries[..historyLength] ++ liveEntries` is exact at `view.cursor`.
pub fn build_live_result(
    p: &crate::dispatch::GetHistoryParams,
    page: &CommittedPage,
    view: &crate::state::HistoryView,
) -> Result<GetHistoryResult, RpcError> {
    if let Some(expected) = &p.engine_instance_id {
        if expected != &view.engine_instance_id {
            return Err(RpcError::instance_changed(format!(
                "this engine is instance {}; history indices from another process are not \
                 comparable — re-snapshot",
                view.engine_instance_id
            )));
        }
    }
    if let Some(requested) = p.history_epoch {
        if requested != view.history_epoch {
            return Err(RpcError::stale_epoch(requested, view.history_epoch));
        }
    }
    if let Some(expected) = p.expected_history_length {
        if expected != page.total {
            return Err(RpcError::history_fence_moved(expected, page.total));
        }
    }

    let entries = project_messages(page.start, &page.messages);
    let next_index = page.start + entries.len() as i64;

    let include_live = p.include_live.unwrap_or(false);
    let live = if include_live { view.live.as_ref() } else { None };

    Ok(GetHistoryResult {
        session_id: view.session_id.clone(),
        engine_instance_id: view.engine_instance_id.clone(),
        is_live_session: true,
        history_epoch: Some(view.history_epoch),
        cursor: Some(view.cursor),
        // The fence is the committed length read as ground truth under the
        // same lock, not a separately-tracked counter that could drift.
        history_length: page.total,
        entries,
        next_index,
        total_known: page.total,
        truncated: next_index < page.total,
        live_entries: live.map(|l| l.entries.clone()),
        live_truncated: live.map(|l| l.truncated),
        live_omitted_bytes: live.map(|l| l.omitted_bytes),
    })
}

/// Builds the response for a saved transcript.
///
/// A saved session has no epoch, no cursor and no live turn of its own, so
/// those fields are **absent** rather than reported as `0`/empty — a client
/// must not be able to mistake a stored file for the running conversation.
///
/// Unlike the live path this necessarily holds the whole file in memory: the
/// store's unit of work is a transcript, and re-reading it per page would be
/// worse. The *response* is still bounded by `limit`.
pub fn build_saved_result(
    p: &crate::dispatch::GetHistoryParams,
    session_id: &str,
    engine_instance_id: &str,
    messages: &[Message],
) -> Result<GetHistoryResult, RpcError> {
    if p.history_epoch.is_some() {
        return Err(RpcError::invalid_params(
            "historyEpoch applies to the live session only; a saved transcript has no epoch",
        ));
    }
    if p.include_live.unwrap_or(false) {
        return Err(RpcError::invalid_params(
            "includeLive applies to the live session only; a saved transcript has no in-flight turn",
        ));
    }
    let total = messages.len() as i64;
    let (start, end) = page_bounds(p, total);

    Ok(GetHistoryResult {
        session_id: session_id.to_string(),
        engine_instance_id: engine_instance_id.to_string(),
        is_live_session: false,
        history_epoch: None,
        cursor: None,
        history_length: total,
        entries: project_messages(start, &messages[start as usize..end as usize]),
        next_index: end,
        total_known: total,
        truncated: end < total,
        live_entries: None,
        live_truncated: None,
        live_omitted_bytes: None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_llm::{Correlation, Role};

    fn thinking_with_signature() -> Message {
        Message::new(
            Role::Assistant,
            vec![Content::Thinking {
                text: "I should check the file".into(),
                signature: Some("SIGNATURE-DO-NOT-LEAK-abc123".into()),
            }],
        )
    }

    // ── Secret deny-list ─────────────────────────────────────────────────

    #[test]
    fn a_thinking_block_never_carries_its_provider_signature() {
        let entry = project_message(0, &thinking_with_signature());
        let json = serde_json::to_string(&entry).unwrap();
        assert!(
            !json.contains("SIGNATURE-DO-NOT-LEAK"),
            "SECURITY: the opaque provider signature must never reach a DTO: {json}"
        );
        assert_eq!(
            entry.blocks[0],
            HistoryBlock::ReasoningSummary {
                text: "I should check the file".into(),
                redacted: false,
                omitted_reason: None,
                full_length: None,
            }
        );
    }

    #[test]
    fn redacted_thinking_is_reported_as_redacted_without_its_ciphertext() {
        let msg = Message::new(
            Role::Assistant,
            vec![Content::RedactedThinking { data: "CIPHERTEXT-DO-NOT-LEAK-xyz".into() }],
        );
        let entry = project_message(0, &msg);
        let json = serde_json::to_string(&entry).unwrap();
        assert!(!json.contains("CIPHERTEXT-DO-NOT-LEAK"), "SECURITY: {json}");
        assert_eq!(
            entry.blocks[0],
            HistoryBlock::ReasoningSummary {
                text: String::new(),
                redacted: true,
                omitted_reason: None,
                full_length: None,
            },
            "the client must learn reasoning happened and that it cannot be shown"
        );
    }

    #[test]
    fn an_image_is_reduced_to_metadata_and_never_carries_base64() {
        // 12 base64 chars, no padding → 9 decoded bytes.
        let msg = Message::new(
            Role::User,
            vec![Content::Image {
                media_type: "image/png".into(),
                base64: "QUJDREVGR0hJSktM".into(),
            }],
        );
        let entry = project_message(0, &msg);
        let json = serde_json::to_string(&entry).unwrap();
        assert!(!json.contains("QUJDREVGR0hJSktM"), "SECURITY: base64 must never be carried: {json}");
        match &entry.blocks[0] {
            HistoryBlock::Image { media_type, byte_length } => {
                assert_eq!(media_type, "image/png");
                assert_eq!(*byte_length, 12);
            }
            other => panic!("expected an image block, got {other:?}"),
        }
    }

    #[test]
    fn base64_padding_is_accounted_for_in_the_reported_byte_length() {
        assert_eq!(decoded_byte_length("QUJD"), 3);
        assert_eq!(decoded_byte_length("QUJDRA=="), 4);
        assert_eq!(decoded_byte_length("QUJDREU="), 5);
        assert_eq!(decoded_byte_length(""), 0);
    }

    // ── Truncation is explicit ───────────────────────────────────────────

    #[test]
    fn an_oversized_tool_result_declares_its_true_original_length() {
        let big = "z".repeat(TOOL_RESULT_CAP + 4242);
        let msg = Message::new(
            Role::User,
            vec![Content::ToolResult {
                tool_use_id: "c1".into(),
                content: big.clone(),
                is_error: false,
                correlation: Correlation::default(),
                status: Some("Succeeded".into()),
            }],
        );
        let entry = project_message(7, &msg);
        match &entry.blocks[0] {
            HistoryBlock::ToolResult { content, omitted_reason, full_length, .. } => {
                assert_eq!(content.as_ref().unwrap().len(), TOOL_RESULT_CAP);
                assert_eq!(omitted_reason.as_deref(), Some("tooLarge"));
                assert_eq!(*full_length, Some(big.len() as i64));
            }
            other => panic!("expected a tool result, got {other:?}"),
        }
    }

    // ── I8 (review): free text is capped too, and must say so ────────────

    #[test]
    fn an_oversized_user_text_block_declares_its_true_original_length() {
        // A 64 KiB+ prompt was capped and then reported as if it were the
        // whole message: no `omittedReason`, no `fullLength`. A client had no
        // way to tell a complete prompt from a truncated one.
        let big = "u".repeat(TEXT_BLOCK_CAP + 1234);
        let msg = Message::new(Role::User, vec![Content::Text(big.clone())]);
        let v = serde_json::to_value(project_message(0, &msg)).unwrap();
        assert_eq!(v["blocks"][0]["kind"], "text");
        assert_eq!(v["blocks"][0]["text"].as_str().unwrap().len(), TEXT_BLOCK_CAP);
        assert_eq!(
            v["blocks"][0]["fullLength"], big.len() as i64,
            "a truncated prompt must never be able to look complete: {v}"
        );
        assert_eq!(v["blocks"][0]["omittedReason"], "tooLarge");
    }

    #[test]
    fn an_oversized_reasoning_summary_declares_its_true_original_length() {
        let big = "r".repeat(TEXT_BLOCK_CAP * 2);
        let msg = Message::new(
            Role::Assistant,
            vec![Content::Thinking { text: big.clone(), signature: Some("SIG-DO-NOT-LEAK".into()) }],
        );
        let v = serde_json::to_value(project_message(0, &msg)).unwrap();
        assert_eq!(v["blocks"][0]["kind"], "reasoningSummary");
        assert_eq!(v["blocks"][0]["fullLength"], big.len() as i64);
        assert_eq!(v["blocks"][0]["omittedReason"], "tooLarge");
        assert!(v["blocks"][0].get("signature").is_none(), "SECURITY: {v}");
    }

    #[test]
    fn an_untruncated_text_block_omits_the_length_metadata_entirely() {
        let msg = Message::new(Role::User, vec![Content::Text("short".into())]);
        let v = serde_json::to_value(project_message(0, &msg)).unwrap();
        assert!(
            v["blocks"][0].get("fullLength").is_none(),
            "absent metadata must be omitted, never null or a fake length: {v}"
        );
        assert!(v["blocks"][0].get("omittedReason").is_none());
    }

    #[test]
    fn a_redacted_reasoning_block_is_not_reported_as_truncated() {
        // Its text is empty *by policy*, not because a cap was hit. Marking it
        // `tooLarge` would be a different (and false) statement.
        let msg = Message::new(
            Role::Assistant,
            vec![Content::RedactedThinking { data: "CIPHERTEXT".into() }],
        );
        let v = serde_json::to_value(project_message(0, &msg)).unwrap();
        assert_eq!(v["blocks"][0]["redacted"], true);
        assert!(v["blocks"][0].get("fullLength").is_none());
        assert!(v["blocks"][0].get("omittedReason").is_none());
    }

    #[test]
    fn a_capped_multibyte_text_block_stays_valid_utf8_and_reports_the_pre_cap_length() {
        let big = "日本語".repeat(TEXT_BLOCK_CAP); // 9 bytes per repeat
        let msg = Message::new(Role::User, vec![Content::Text(big.clone())]);
        let v = serde_json::to_value(project_message(0, &msg)).unwrap();
        let text = v["blocks"][0]["text"].as_str().unwrap();
        assert!(text.len() <= TEXT_BLOCK_CAP);
        assert!(text.is_char_boundary(text.len()));
        assert_eq!(v["blocks"][0]["fullLength"], big.len() as i64);
        assert_eq!(v["blocks"][0]["omittedReason"], "tooLarge");
    }

    #[test]
    fn a_multi_byte_cap_never_splits_a_character() {
        // Every char is 3 bytes, so the cap lands mid-character.
        let text = "日".repeat(TOOL_INPUT_CAP);
        let msg = Message::new(
            Role::Assistant,
            vec![Content::ToolUse {
                id: "c1".into(),
                name: "write_file".into(),
                input_json: text.clone(),
                correlation: Correlation::default(),
            }],
        );
        let entry = project_message(0, &msg);
        match &entry.blocks[0] {
            HistoryBlock::ToolCall { input_json, input_full_length, .. } => {
                let s = input_json.as_ref().unwrap();
                assert!(s.len() <= TOOL_INPUT_CAP);
                assert!(std::str::from_utf8(s.as_bytes()).is_ok());
                assert_eq!(*input_full_length, Some(text.len() as i64));
            }
            other => panic!("expected a tool call, got {other:?}"),
        }
    }

    // ── Correlation metadata: carried when known, omitted when not ───────

    #[test]
    fn correlation_ids_are_carried_when_the_stored_message_has_them() {
        let msg = Message::new(
            Role::Assistant,
            vec![Content::ToolUse {
                id: "call-1".into(),
                name: "read_file".into(),
                input_json: "{}".into(),
                correlation: Correlation {
                    root_turn_id: Some("turn-9".into()),
                    activity_id: Some("batch-3".into()),
                    source_id: Some("call-1".into()),
                },
            }],
        );
        match &project_message(0, &msg).blocks[0] {
            HistoryBlock::ToolCall { turn_id, batch_id, call_id, .. } => {
                assert_eq!(turn_id.as_deref(), Some("turn-9"));
                assert_eq!(batch_id.as_deref(), Some("batch-3"));
                assert_eq!(call_id, "call-1");
            }
            other => panic!("expected a tool call, got {other:?}"),
        }
    }

    #[test]
    fn an_old_transcript_without_correlation_ids_omits_them_rather_than_inventing_them() {
        let msg = Message::new(
            Role::Assistant,
            vec![Content::ToolUse {
                id: "legacy".into(),
                name: "read_file".into(),
                input_json: "{}".into(),
                correlation: Correlation::default(),
            }],
        );
        match &project_message(0, &msg).blocks[0] {
            HistoryBlock::ToolCall { turn_id, batch_id, .. } => {
                assert!(turn_id.is_none());
                assert!(batch_id.is_none());
            }
            other => panic!("expected a tool call, got {other:?}"),
        }
    }

    // ── Roles and entry kinds ────────────────────────────────────────────

    #[test]
    fn a_user_role_tool_result_message_is_never_labelled_an_operator_prompt() {
        let msg = Message::new(
            Role::User,
            vec![Content::ToolResult {
                tool_use_id: "c1".into(),
                content: "ok".into(),
                is_error: false,
                correlation: Correlation::default(),
                status: None,
            }],
        );
        let entry = project_message(2, &msg);
        assert_eq!(entry.role, "user");
        assert_eq!(entry.entry_kind, coda_proto::history::HistoryEntryKind::ToolResults);
    }

    #[test]
    fn absolute_indices_are_stable_across_pages() {
        let msgs = vec![Message::user("a"), Message::assistant("b"), Message::user("c")];
        let page = project_messages(1, &msgs[1..]);
        assert_eq!(page.iter().map(|e| e.index).collect::<Vec<_>>(), vec![1, 2]);
    }

    // ── Previews ─────────────────────────────────────────────────────────

    #[test]
    fn a_preview_skips_tool_result_messages_and_finds_the_real_prompt() {
        let msgs = vec![
            Message::new(
                Role::User,
                vec![Content::ToolResult {
                    tool_use_id: "c".into(),
                    content: "tool output that is not a prompt".into(),
                    is_error: false,
                    correlation: Correlation::default(),
                    status: None,
                }],
            ),
            Message::user("the real question"),
        ];
        let (preview, truncated) = preview_of(&msgs);
        assert_eq!(preview, "the real question");
        assert!(!truncated);
    }

    #[test]
    fn a_long_preview_is_capped_and_says_so() {
        let msgs = vec![Message::user("w ".repeat(400))];
        let (preview, truncated) = preview_of(&msgs);
        assert!(preview.len() <= PREVIEW_CAP);
        assert!(truncated);
    }

    #[test]
    fn an_empty_transcript_previews_as_empty_rather_than_panicking() {
        let (preview, truncated) = preview_of(&[]);
        assert_eq!(preview, "");
        assert!(!truncated);
    }

    // ── Bounds ───────────────────────────────────────────────────────────

    #[test]
    fn a_client_cannot_ask_for_an_unbounded_page() {
        assert_eq!(clamp_limit(Some(i64::MAX), DEFAULT_HISTORY_LIMIT, MAX_HISTORY_LIMIT), MAX_HISTORY_LIMIT);
        assert_eq!(clamp_limit(Some(0), DEFAULT_HISTORY_LIMIT, MAX_HISTORY_LIMIT), DEFAULT_HISTORY_LIMIT);
        assert_eq!(clamp_limit(Some(-5), DEFAULT_HISTORY_LIMIT, MAX_HISTORY_LIMIT), DEFAULT_HISTORY_LIMIT);
        assert_eq!(clamp_limit(None, DEFAULT_HISTORY_LIMIT, MAX_HISTORY_LIMIT), DEFAULT_HISTORY_LIMIT);
        assert_eq!(clamp_limit(Some(7), DEFAULT_HISTORY_LIMIT, MAX_HISTORY_LIMIT), 7);
    }

    // ── getHistory: fences, paging and the no-duplicate contract ─────────

    use crate::dispatch::GetHistoryParams;
    use crate::state::{HistoryView, LiveView};

    fn conversation(n: usize) -> Vec<Message> {
        (0..n)
            .map(|i| {
                if i % 2 == 0 {
                    Message::user(format!("prompt {i}"))
                } else {
                    Message::assistant(format!("reply {i}"))
                }
            })
            .collect()
    }

    fn view(committed: &[Message], live: Option<LiveView>) -> HistoryView {
        HistoryView {
            engine_instance_id: "engine-a".into(),
            session_id: "s1".into(),
            history_epoch: 3,
            history_length: committed.len() as i64,
            cursor: 42,
            live,
        }
    }

    /// Slices the committed conversation exactly as the host does under the
    /// HISTORY lock, so the tests exercise the real call shape.
    fn page_of(p: &GetHistoryParams, committed: &[Message]) -> CommittedPage {
        let total = committed.len() as i64;
        let (start, end) = page_bounds(p, total);
        CommittedPage {
            total,
            start,
            messages: committed[start as usize..end as usize].to_vec(),
        }
    }

    fn live_view() -> LiveView {
        LiveView {
            entries: vec![HistoryEntry::new(
                0,
                "user",
                vec![HistoryBlock::text_capped("in flight", TEXT_BLOCK_CAP)],
            )],
            truncated: false,
            omitted_bytes: 0,
        }
    }

    #[test]
    fn a_stale_history_epoch_is_a_typed_error_not_a_partially_valid_page() {
        let committed = conversation(4);
        let p = GetHistoryParams { history_epoch: Some(2), ..Default::default() };
        let err = build_live_result(&p, &page_of(&p, &committed), &view(&committed, None))
            .expect_err("a stale epoch must not be answered");
        assert_eq!(err.code, coda_proto::messages::error_code::STALE_EPOCH);
        assert!(err.message.contains("stale"));
    }

    #[test]
    fn a_matching_history_epoch_is_accepted() {
        let committed = conversation(4);
        let p = GetHistoryParams { history_epoch: Some(3), ..Default::default() };
        let r = build_live_result(&p, &page_of(&p, &committed), &view(&committed, None)).unwrap();
        assert_eq!(r.history_epoch, Some(3));
    }

    #[test]
    fn a_handle_from_another_engine_instance_is_refused() {
        let committed = conversation(2);
        let p = GetHistoryParams { engine_instance_id: Some("engine-b".into()), ..Default::default() };
        let err = build_live_result(&p, &page_of(&p, &committed), &view(&committed, None)).expect_err("must refuse");
        assert_eq!(err.code, crate::dispatch::error_code::INSTANCE_CHANGED);
    }

    #[test]
    fn a_fence_that_moved_between_pages_is_refused_rather_than_duplicating_content() {
        // The client saw 4 committed messages on page 1; a turn then committed
        // two more. Continuing blindly would interleave two conversations.
        let committed = conversation(6);
        let p = GetHistoryParams {
            expected_history_length: Some(4),
            since_index: Some(4),
            ..Default::default()
        };
        let err = build_live_result(&p, &page_of(&p, &committed), &view(&committed, None)).expect_err("must refuse");
        assert_eq!(err.code, coda_proto::messages::error_code::HISTORY_FENCE_MOVED);
    }

    #[test]
    fn paging_reconstructs_the_whole_conversation_exactly_once() {
        let committed = conversation(11);
        let mut seen: Vec<HistoryEntry> = Vec::new();
        let mut since = 0i64;
        loop {
            let p = GetHistoryParams {
                since_index: Some(since),
                limit: Some(4),
                expected_history_length: Some(11),
                ..Default::default()
            };
            let r = build_live_result(&p, &page_of(&p, &committed), &view(&committed, None)).unwrap();
            seen.extend(r.entries.clone());
            since = r.next_index;
            if !r.truncated {
                break;
            }
        }
        assert_eq!(seen.len(), 11, "every entry exactly once");
        assert_eq!(
            seen.iter().map(|e| e.index).collect::<Vec<_>>(),
            (0..11).collect::<Vec<_>>(),
            "absolute indices, gapless, no duplicates"
        );
    }

    #[test]
    fn the_live_turn_starts_exactly_where_the_committed_fence_ends() {
        let committed = conversation(5);
        let p = GetHistoryParams { include_live: Some(true), ..Default::default() };
        let r = build_live_result(&p, &page_of(&p, &committed), &view(&committed, Some(live_view()))).unwrap();
        assert_eq!(r.history_length, 5);
        assert_eq!(r.total_known, 5, "the in-flight turn is never counted as committed");
        assert_eq!(r.live_entries.as_ref().unwrap().len(), 1);
        assert_eq!(r.live_truncated, Some(false));
    }

    #[test]
    fn live_entries_are_omitted_entirely_unless_requested() {
        let committed = conversation(2);
        let r = build_live_result(&GetHistoryParams::default(), &page_of(&GetHistoryParams::default(), &committed), &view(&committed, Some(live_view())))
            .unwrap();
        assert!(r.live_entries.is_none());
        assert!(r.live_truncated.is_none(), "absent, never a misleading `false`");
    }

    #[test]
    fn an_idle_engine_reports_no_live_entries_even_when_asked() {
        let committed = conversation(2);
        let p = GetHistoryParams { include_live: Some(true), ..Default::default() };
        let r = build_live_result(&p, &page_of(&p, &committed), &view(&committed, None)).unwrap();
        assert!(r.live_entries.is_none());
    }

    #[test]
    fn a_since_index_past_the_end_returns_an_empty_final_page_rather_than_panicking() {
        let committed = conversation(3);
        let p = GetHistoryParams { since_index: Some(99), ..Default::default() };
        let r = build_live_result(&p, &page_of(&p, &committed), &view(&committed, None)).unwrap();
        assert!(r.entries.is_empty());
        assert_eq!(r.next_index, 3);
        assert!(!r.truncated);
    }

    #[test]
    fn a_negative_since_index_is_clamped_rather_than_wrapping() {
        let committed = conversation(3);
        let p = GetHistoryParams { since_index: Some(-10), ..Default::default() };
        let r = build_live_result(&p, &page_of(&p, &committed), &view(&committed, None)).unwrap();
        assert_eq!(r.entries.first().map(|e| e.index), Some(0));
    }

    // ── Saved transcripts ────────────────────────────────────────────────

    #[test]
    fn a_saved_transcript_has_no_epoch_or_cursor_of_its_own() {
        let saved = conversation(3);
        let r = build_saved_result(&GetHistoryParams::default(), "old-1", "engine-a", &saved).unwrap();
        assert!(!r.is_live_session);
        assert!(r.history_epoch.is_none(), "a stored file has no epoch — absent, not 0");
        assert!(r.cursor.is_none());
        assert_eq!(r.history_length, 3);
        assert_eq!(r.entries.len(), 3);
    }

    #[test]
    fn asking_a_saved_transcript_for_live_state_is_refused_rather_than_answered_emptily() {
        let saved = conversation(1);
        let p = GetHistoryParams { include_live: Some(true), ..Default::default() };
        assert!(build_saved_result(&p, "old-1", "engine-a", &saved).is_err());
        let p = GetHistoryParams { history_epoch: Some(0), ..Default::default() };
        assert!(build_saved_result(&p, "old-1", "engine-a", &saved).is_err());
    }

    #[test]
    fn a_saved_transcript_pages_with_the_same_bounds() {
        let saved = conversation(9);
        let p = GetHistoryParams { limit: Some(4), since_index: Some(4), ..Default::default() };
        let r = build_saved_result(&p, "old-1", "engine-a", &saved).unwrap();
        assert_eq!(r.entries.iter().map(|e| e.index).collect::<Vec<_>>(), vec![4, 5, 6, 7]);
        assert!(r.truncated);
        assert_eq!(r.next_index, 8);
    }
}
