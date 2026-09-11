//! Rebuilding the visible conversation from `session/getHistory`.
//!
//! After a connect, a resume, a fork or a rewind the engine — not this client
//! — owns what the conversation *is*. The rich-history projection
//! (`HistoryEntry` / `HistoryBlock`) is how it says so, and this module turns
//! that into the same [`Block`]s the live event stream produces, so an old
//! conversation renders as a conversation rather than as a "restored N
//! messages" notice.
//!
//! The rules that are easy to get wrong, and are therefore pinned by tests
//! below:
//!
//! - **A user-role entry is not automatically an operator prompt.** A provider
//!   conversation encodes tool results as user-role messages, so replaying
//!   `role == "user"` as a prompt card invents prompts the operator never
//!   typed. [`HistoryEntryKind`] is the discriminator, and it is the only
//!   thing consulted.
//! - **A result belongs to one call.** Provider `callId`s are unique only
//!   within a request, so `(turnId, batchId, callId)` is the identity. A
//!   reused id across two batches must not let the second result rewrite the
//!   first call.
//! - **Grouping is a UI decision, not a batch decision.** The live reducer
//!   opens a new tool group whenever anything else intervenes, and keeps
//!   extending the open one otherwise. Now that canonical `batchId`s exist it
//!   would be easy — and wrong — to switch to one group per batch: a turn that
//!   ran twelve batches back to back would render as twelve collapsed groups
//!   where it used to render as one.
//! - **Nothing is invented.** No timestamp, no reasoning duration, no token
//!   count is fabricated for a historical entry; an omission marker is shown
//!   rather than hidden; a reasoning *signature* is never carried at all
//!   (the projection does not send one, and this never asks for one).

use coda_proto::history::{HistoryBlock, HistoryEntry, HistoryEntryKind};
use coda_proto::Correlation;
use coda_render::tool::{CallStatus, ToolActivity, ToolCall};

use crate::transcript::{ActivityKey, Block};

/// One hydrated conversation, plus anything the user should be told about it.
#[derive(Debug, Default)]
pub struct Hydration {
    pub blocks: Vec<Block>,
    /// Statements about what is *not* shown — an omitted block, a truncated
    /// live turn. Rendered as notices so an incomplete replay says so instead
    /// of looking complete.
    pub notices: Vec<String>,
}

/// The identity of a single tool call.
///
/// `call_id` alone is not it: providers reuse ids across requests, and the
/// live stream and a rehydrated history can hold the same id for two
/// genuinely different calls.
#[derive(Debug, Clone, PartialEq, Eq)]
struct CallKey {
    turn_id: Option<String>,
    batch_id: Option<String>,
    call_id: String,
}

struct Registered {
    key: CallKey,
    block: usize,
    call: usize,
    has_result: bool,
}

/// Turns committed entries (and optionally the in-flight turn) into blocks.
///
/// `live` is `TurnState.liveEntries` / `GetHistoryResult.liveEntries`: the
/// part of the conversation that is not committed yet. Passing both is safe
/// and is the documented reconstruction — `entries[..historyLength]` followed
/// by `liveEntries` never double-counts, because the fence moves only when the
/// live turn is cleared.
pub fn hydrate(entries: &[HistoryEntry], live: &[HistoryEntry], live_truncated: bool) -> Hydration {
    let mut out = Hydration::default();
    let mut registry: Vec<Registered> = Vec::new();

    for entry in entries {
        push_entry(entry, &mut out, &mut registry, false);
    }
    // Only the *last* live entry can still be receiving content, and within
    // it only the last block. Everything before is finished by the fact that
    // something followed it.
    let last_live = live.len().saturating_sub(1);
    for (index, entry) in live.iter().enumerate() {
        push_entry(entry, &mut out, &mut registry, index == last_live);
    }

    if live_truncated {
        out.notices.push(
            "Part of the running turn is not shown: the engine's live retention budget \
             was exceeded."
                .to_string(),
        );
    }
    out
}

fn push_entry(
    entry: &HistoryEntry,
    out: &mut Hydration,
    registry: &mut Vec<Registered>,
    live_tail: bool,
) {
    match entry.entry_kind {
        HistoryEntryKind::UserPrompt => push_user_entry(entry, out),
        // A user-role message carrying tool results is the *conversation
        // protocol*, not something the operator typed. Its results attach to
        // the calls that produced them; any text alongside them is steering
        // the operator really did send at a delivery boundary, so that — and
        // only that — becomes a user block.
        HistoryEntryKind::ToolResults => {
            for block in &entry.blocks {
                match block {
                    HistoryBlock::ToolResult { .. } => attach_result(block, out, registry),
                    HistoryBlock::Text { .. } => {
                        push_user_entry_block(block, entry.steering_message_id.as_deref(), out)
                    }
                    _ => {}
                }
            }
        }
        HistoryEntryKind::Assistant => push_assistant_entry(entry, out, registry, live_tail),
    }
}

fn push_user_entry(entry: &HistoryEntry, out: &mut Hydration) {
    let mut text = String::new();
    for block in &entry.blocks {
        match block {
            HistoryBlock::Text { text: body, omitted_reason, full_length } => {
                if !text.is_empty() {
                    text.push('\n');
                }
                text.push_str(body);
                if let Some(marker) = omission_marker(omitted_reason.as_deref(), *full_length) {
                    text.push_str(&marker);
                }
            }
            HistoryBlock::Image { media_type, byte_length } => {
                if !text.is_empty() {
                    text.push('\n');
                }
                text.push_str(&format!("[image {media_type}, {byte_length} bytes]"));
            }
            _ => {}
        }
    }
    if text.is_empty() {
        return;
    }
    out.blocks.push(Block::User {
        text,
        // Deliberately empty: the engine reports no wall-clock time for a
        // stored message, and stamping "now" on a message from last week
        // would be a fabricated fact rather than a missing one.
        timestamp: String::new(),
        pending: false,
        // Carried when the engine says this entry *is* a delivered steering
        // message: a client still holding its own copy of that message can
        // then recognise it here instead of appending it a second time.
        queue_id: entry.steering_message_id.clone(),
    });
}

fn push_user_entry_block(block: &HistoryBlock, steering_id: Option<&str>, out: &mut Hydration) {
    if let HistoryBlock::Text { text, omitted_reason, full_length } = block {
        if text.is_empty() {
            return;
        }
        let mut body = text.clone();
        if let Some(marker) = omission_marker(omitted_reason.as_deref(), *full_length) {
            body.push_str(&marker);
        }
        out.blocks.push(Block::User {
            text: body,
            timestamp: String::new(),
            pending: false,
            queue_id: steering_id.map(str::to_string),
        });
    }
}

fn push_assistant_entry(
    entry: &HistoryEntry,
    out: &mut Hydration,
    registry: &mut Vec<Registered>,
    live_tail: bool,
) {
    let last_block = entry.blocks.len().saturating_sub(1);
    for (index, block) in entry.blocks.iter().enumerate() {
        match block {
            HistoryBlock::Text { text, omitted_reason, full_length } => {
                let mut body = text.clone();
                if let Some(marker) = omission_marker(omitted_reason.as_deref(), *full_length) {
                    body.push_str(&marker);
                }
                if body.is_empty() {
                    continue;
                }
                out.blocks.push(Block::Assistant { text: body, complete: true });
            }
            HistoryBlock::ReasoningSummary { text, redacted, omitted_reason, full_length } => {
                let mut body = text.clone();
                if let Some(marker) = omission_marker(omitted_reason.as_deref(), *full_length) {
                    body.push_str(&marker);
                }
                // The trailing block of the in-flight turn is the burst the
                // model is reasoning through *now*: marking it done froze a
                // live row and stopped its clock on every re-read. Anything
                // the engine has already moved past is history.
                let live = live_tail && index == last_block && !*redacted;
                out.blocks.push(Block::Thinking {
                    text: body,
                    // Zero means "the clock never ran", which the renderer
                    // already treats as "say nothing" — a stored message
                    // carries no duration and inventing one would be a lie.
                    elapsed_ms: 0,
                    tokens: None,
                    complete: !live,
                    expanded: false,
                    done_at: None,
                });
                if *redacted {
                    out.notices.push(
                        "The provider withheld the text of one reasoning block.".to_string(),
                    );
                }
            }
            HistoryBlock::ToolCall {
                call_id,
                tool_name,
                input_json,
                input_omitted_reason,
                input_full_length,
                turn_id,
                batch_id,
            } => {
                let mut input = input_json.clone().unwrap_or_else(|| "{}".to_string());
                if let Some(marker) =
                    omission_marker(input_omitted_reason.as_deref(), *input_full_length)
                {
                    input.push_str(&marker);
                }
                let key = CallKey {
                    turn_id: turn_id.clone(),
                    batch_id: batch_id.clone(),
                    call_id: call_id.clone(),
                };
                let block_index = open_tool_group(out, &key);
                let Some(Block::Tools { activity, calls, .. }) = out.blocks.get_mut(block_index)
                else {
                    continue;
                };
                let mut call = ToolCall::new(tool_name, input);
                // Historical calls have already run; a call with no result is
                // one whose result genuinely never came back.
                call.status = CallStatus::Pending;
                activity.calls.push(call);
                calls.push(correlation(&key));
                registry.push(Registered {
                    key,
                    block: block_index,
                    call: activity.calls.len() - 1,
                    has_result: false,
                });
            }
            HistoryBlock::ToolResult { .. } => attach_result(block, out, registry),
            HistoryBlock::Image { media_type, byte_length } => {
                out.blocks.push(Block::Assistant {
                    text: format!("[image {media_type}, {byte_length} bytes]"),
                    complete: true,
                });
            }
        }
    }
}

fn attach_result(block: &HistoryBlock, out: &mut Hydration, registry: &mut [Registered]) {
    let HistoryBlock::ToolResult {
        call_id,
        is_error,
        status,
        content,
        omitted_reason,
        full_length,
        turn_id,
        batch_id,
    } = block
    else {
        return;
    };
    let key = CallKey {
        turn_id: turn_id.clone(),
        batch_id: batch_id.clone(),
        call_id: call_id.clone(),
    };
    let Some(target) = locate(registry, &key) else {
        // A result with no call is not silently dropped, but neither is it
        // attached to whatever happens to be nearby: it is stated plainly.
        out.notices.push(format!(
            "A stored tool result for call {call_id} has no matching call in this history."
        ));
        return;
    };

    let mut body = content.clone().unwrap_or_default();
    if let Some(marker) = omission_marker(omitted_reason.as_deref(), *full_length) {
        body.push_str(&marker);
    }
    let (block_index, call_index) = (registry[target].block, registry[target].call);
    registry[target].has_result = true;
    if let Some(Block::Tools { activity, .. }) = out.blocks.get_mut(block_index) {
        if let Some(call) = activity.calls.get_mut(call_index) {
            call.result = Some(body);
            call.is_error = *is_error;
            call.status = stored_status(status.as_deref(), *is_error);
        }
    }
}

/// Finds the call a stored result belongs to.
///
/// Exact `(turnId, batchId, callId)` first. Only when the stored blocks carry
/// no correlation at all — a transcript written before correlation ids
/// existed — does it fall back to the newest still-unanswered call with that
/// id, which is the only defensible reading of a single-threaded turn.
fn locate(registry: &[Registered], key: &CallKey) -> Option<usize> {
    if let Some(index) = registry.iter().rposition(|r| r.key == *key && !r.has_result) {
        return Some(index);
    }
    if key.turn_id.is_some() || key.batch_id.is_some() {
        // The result named a turn/batch and no call in it matches. Attaching
        // it to a same-id call from a *different* batch is exactly the
        // provider-id-reuse bug this identity exists to prevent.
        return None;
    }
    registry
        .iter()
        .rposition(|r| r.key.call_id == key.call_id && !r.has_result && r.key.turn_id.is_none())
}

/// The block index of the tool group a new call belongs to.
///
/// Extends the tail group when the tail *is* one — the same UI boundary rule
/// the live reducer uses. Deliberately not "one group per `batchId`": a turn
/// that ran several batches back to back has always rendered as one group,
/// and canonical ids existing is not a reason to fragment it.
fn open_tool_group(out: &mut Hydration, key: &CallKey) -> usize {
    if matches!(out.blocks.last(), Some(Block::Tools { .. })) {
        return out.blocks.len() - 1;
    }
    out.blocks.push(Block::Tools {
        activity: ToolActivity { complete: true, ..ToolActivity::default() },
        key: ActivityKey {
            root_turn_id: key.turn_id.clone(),
            activity_id: key.batch_id.clone(),
        },
        calls: Vec::new(),
    });
    out.blocks.len() - 1
}

fn correlation(key: &CallKey) -> Correlation {
    Correlation {
        root_turn_id: key.turn_id.clone(),
        activity_id: key.batch_id.clone(),
        call_id: Some(key.call_id.clone()),
        source_id: Some(key.call_id.clone()),
        ..Default::default()
    }
}

fn stored_status(status: Option<&str>, is_error: bool) -> CallStatus {
    match status {
        Some("Failed") => CallStatus::Failed,
        Some("Cancelled") => CallStatus::Cancelled,
        Some("Skipped") => CallStatus::Skipped,
        Some("Succeeded") => CallStatus::Succeeded,
        _ if is_error => CallStatus::Failed,
        _ => CallStatus::Succeeded,
    }
}

/// The visible statement that a value was shortened.
///
/// Present exactly when the engine said so. Silence here would turn a capped
/// 200 KiB tool result into something indistinguishable from a complete one.
fn omission_marker(reason: Option<&str>, full_length: Option<i64>) -> Option<String> {
    let reason = reason?;
    let explanation = match reason {
        "tooLarge" => "too large",
        "liveBudgetExceeded" => "over the live retention budget",
        other => other,
    };
    Some(match full_length {
        Some(length) => format!("\n… [truncated: {explanation}; {length} bytes in full]"),
        None => format!("\n… [truncated: {explanation}]"),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_proto::history::HistoryBlock as B;

    fn text(body: &str) -> B {
        B::Text { text: body.into(), omitted_reason: None, full_length: None }
    }

    fn call(call_id: &str, name: &str, turn: Option<&str>, batch: Option<&str>) -> B {
        B::ToolCall {
            call_id: call_id.into(),
            tool_name: name.into(),
            input_json: Some("{}".into()),
            input_omitted_reason: None,
            input_full_length: None,
            turn_id: turn.map(str::to_string),
            batch_id: batch.map(str::to_string),
        }
    }

    fn result(call_id: &str, content: &str, turn: Option<&str>, batch: Option<&str>) -> B {
        B::ToolResult {
            call_id: call_id.into(),
            is_error: false,
            status: Some("Succeeded".into()),
            content: Some(content.into()),
            omitted_reason: None,
            full_length: None,
            turn_id: turn.map(str::to_string),
            batch_id: batch.map(str::to_string),
        }
    }

    fn tools(hydration: &Hydration) -> Vec<&ToolActivity> {
        hydration
            .blocks
            .iter()
            .filter_map(|b| match b {
                Block::Tools { activity, .. } => Some(activity),
                _ => None,
            })
            .collect()
    }

    fn users(hydration: &Hydration) -> Vec<&str> {
        hydration
            .blocks
            .iter()
            .filter_map(|b| match b {
                Block::User { text, .. } => Some(text.as_str()),
                _ => None,
            })
            .collect()
    }

    #[test]
    fn a_user_role_entry_carrying_tool_results_is_never_replayed_as_a_prompt() {
        // The bug this exists to prevent: a resumed conversation showing
        // "cat file.rs output" as something the operator typed.
        let entries = vec![
            HistoryEntry::new(0, "user", vec![text("read the file")]),
            HistoryEntry::new(1, "assistant", vec![call("c1", "read_file", Some("t1"), Some("b1"))]),
            HistoryEntry::new(2, "user", vec![result("c1", "file contents", Some("t1"), Some("b1"))]),
        ];
        let hydration = hydrate(&entries, &[], false);

        assert_eq!(users(&hydration), ["read the file"], "only the real prompt is a user block");
        let activity = tools(&hydration);
        assert_eq!(activity.len(), 1);
        assert_eq!(activity[0].calls[0].result.as_deref(), Some("file contents"));
    }

    #[test]
    fn a_reused_provider_call_id_in_a_later_batch_does_not_rewrite_the_earlier_call() {
        // Providers only guarantee call-id uniqueness within one request.
        let entries = vec![
            HistoryEntry::new(
                0,
                "assistant",
                vec![call("toolu_1", "read_file", Some("t1"), Some("b1"))],
            ),
            HistoryEntry::new(1, "user", vec![result("toolu_1", "first", Some("t1"), Some("b1"))]),
            HistoryEntry::new(2, "assistant", vec![text("now the second")]),
            HistoryEntry::new(
                3,
                "assistant",
                vec![call("toolu_1", "read_file", Some("t1"), Some("b2"))],
            ),
            HistoryEntry::new(4, "user", vec![result("toolu_1", "second", Some("t1"), Some("b2"))]),
        ];
        let hydration = hydrate(&entries, &[], false);
        let activity = tools(&hydration);
        assert_eq!(activity.len(), 2, "text between the batches splits the groups");
        assert_eq!(activity[0].calls[0].result.as_deref(), Some("first"));
        assert_eq!(activity[1].calls[0].result.as_deref(), Some("second"));
    }

    #[test]
    fn consecutive_batches_stay_one_group_now_that_canonical_ids_exist() {
        // The regression guard for "group by batchId": four batches in a row
        // with nothing between them are one group, exactly as the live
        // reducer has always rendered them.
        let entries = vec![HistoryEntry::new(
            0,
            "assistant",
            vec![
                call("c1", "read_file", Some("t1"), Some("b1")),
                call("c2", "read_file", Some("t1"), Some("b2")),
                call("c3", "read_file", Some("t1"), Some("b3")),
                call("c4", "read_file", Some("t1"), Some("b4")),
            ],
        )];
        let hydration = hydrate(&entries, &[], false);
        let activity = tools(&hydration);
        assert_eq!(activity.len(), 1, "one UI group, not one per batch");
        assert_eq!(activity[0].calls.len(), 4);
    }

    #[test]
    fn an_omission_marker_is_shown_rather_than_hidden() {
        let entries = vec![HistoryEntry::new(
            0,
            "assistant",
            vec![B::Text {
                text: "start of a very long answer".into(),
                omitted_reason: Some("tooLarge".into()),
                full_length: Some(120_000),
            }],
        )];
        let hydration = hydrate(&entries, &[], false);
        let body = match &hydration.blocks[0] {
            Block::Assistant { text, .. } => text.clone(),
            other => panic!("expected assistant text, got {other:?}"),
        };
        assert!(body.contains("truncated"), "{body}");
        assert!(body.contains("120000"), "the full length must be stated: {body}");
    }

    #[test]
    fn a_truncated_live_turn_says_so() {
        let hydration = hydrate(&[], &[], true);
        assert!(
            hydration.notices.iter().any(|n| n.contains("live retention budget")),
            "{:?}",
            hydration.notices
        );
    }

    #[test]
    fn the_running_turns_trailing_reasoning_stays_live_but_nothing_else_does() {
        // The engine projects the in-flight turn's reasoning as it streams.
        // Marking it done froze a live row and stopped its clock every time
        // the conversation was re-read; anything the turn has moved past —
        // and every committed entry — is history.
        let reasoning = |text: &str| B::ReasoningSummary {
            text: text.into(),
            redacted: false,
            omitted_reason: None,
            full_length: None,
        };
        let committed = vec![HistoryEntry::new(0, "assistant", vec![reasoning("last turn")])];
        let live = vec![
            HistoryEntry::new(1, "assistant", vec![reasoning("earlier burst"), text("an answer")]),
            HistoryEntry::new(2, "assistant", vec![reasoning("still thinking")]),
        ];
        let hydration = hydrate(&committed, &live, false);

        let bursts: Vec<(&str, bool)> = hydration
            .blocks
            .iter()
            .filter_map(|block| match block {
                Block::Thinking { text, complete, .. } => Some((text.as_str(), *complete)),
                _ => None,
            })
            .collect();
        assert_eq!(
            bursts,
            vec![("last turn", true), ("earlier burst", true), ("still thinking", false)]
        );
    }

    #[test]
    fn redacted_reasoning_is_never_left_open_for_text_that_can_never_arrive() {
        let live = vec![HistoryEntry::new(
            0,
            "assistant",
            vec![B::ReasoningSummary {
                text: String::new(),
                redacted: true,
                omitted_reason: None,
                full_length: None,
            }],
        )];
        let hydration = hydrate(&[], &live, false);
        assert!(matches!(hydration.blocks[0], Block::Thinking { complete: true, .. }));
    }

    #[test]
    fn historical_reasoning_carries_no_invented_clock_or_token_count() {
        let entries = vec![HistoryEntry::new(
            0,
            "assistant",
            vec![B::ReasoningSummary {
                text: "considered it".into(),
                redacted: false,
                omitted_reason: None,
                full_length: None,
            }],
        )];
        let hydration = hydrate(&entries, &[], false);
        match &hydration.blocks[0] {
            Block::Thinking { elapsed_ms, tokens, done_at, complete, .. } => {
                assert_eq!(*elapsed_ms, 0, "no fabricated duration");
                assert_eq!(*tokens, None, "no fabricated token count");
                assert_eq!(*done_at, None, "no fabricated wall clock");
                assert!(complete);
            }
            other => panic!("expected reasoning, got {other:?}"),
        }
    }

    #[test]
    fn a_stored_prompt_gets_no_fabricated_timestamp() {
        let entries = vec![HistoryEntry::new(0, "user", vec![text("last week")])];
        let hydration = hydrate(&entries, &[], false);
        match &hydration.blocks[0] {
            Block::User { timestamp, .. } => assert!(timestamp.is_empty()),
            other => panic!("expected a user block, got {other:?}"),
        }
    }

    #[test]
    fn steering_text_delivered_alongside_tool_results_stays_visible_as_the_users_own_message() {
        let entries = vec![
            HistoryEntry::new(0, "assistant", vec![call("c1", "read_file", Some("t1"), Some("b1"))]),
            HistoryEntry::new(
                1,
                "user",
                vec![result("c1", "contents", Some("t1"), Some("b1")), text("actually, stop")],
            ),
        ];
        let hydration = hydrate(&entries, &[], false);
        assert_eq!(users(&hydration), ["actually, stop"]);
    }

    #[test]
    fn a_legacy_transcript_without_correlation_ids_still_matches_by_call_id() {
        let entries = vec![
            HistoryEntry::new(0, "assistant", vec![call("legacy-1", "read_file", None, None)]),
            HistoryEntry::new(1, "user", vec![result("legacy-1", "contents", None, None)]),
        ];
        let hydration = hydrate(&entries, &[], false);
        assert_eq!(tools(&hydration)[0].calls[0].result.as_deref(), Some("contents"));
        assert!(hydration.notices.is_empty());
    }

    #[test]
    fn an_orphan_result_is_reported_rather_than_attached_to_the_nearest_call() {
        let entries = vec![
            HistoryEntry::new(0, "assistant", vec![call("c1", "read_file", Some("t1"), Some("b1"))]),
            HistoryEntry::new(1, "user", vec![result("c9", "who asked", Some("t1"), Some("b1"))]),
        ];
        let hydration = hydrate(&entries, &[], false);
        assert_eq!(tools(&hydration)[0].calls[0].result, None);
        assert!(hydration.notices.iter().any(|n| n.contains("c9")), "{:?}", hydration.notices);
    }

    #[test]
    fn a_delivered_steering_message_is_rebuilt_carrying_the_queue_id_that_identifies_it() {
        // A client that queued this message is holding its own copy and is
        // told separately that it was delivered. Without the id here it
        // cannot tell this block *is* that message, and has to choose between
        // showing the text twice and not showing it at all.
        let live = vec![
            HistoryEntry::new(0, "user", vec![text("start")]),
            HistoryEntry::new(1, "assistant", vec![text("half a reply")]),
            HistoryEntry::new(2, "user", vec![text("operator correction")]).from_steering("m1"),
        ];
        let hydration = hydrate(&[], &live, false);
        let ids: Vec<Option<&str>> = hydration
            .blocks
            .iter()
            .filter_map(|block| match block {
                Block::User { queue_id, .. } => Some(queue_id.as_deref()),
                _ => None,
            })
            .collect();
        assert_eq!(ids, [None, Some("m1")]);
        assert_eq!(users(&hydration), ["start", "operator correction"]);
    }

    #[test]
    fn steering_delivered_inside_a_tool_batch_keeps_its_queue_id_too() {
        let live = vec![
            HistoryEntry::new(0, "assistant", vec![call("c1", "read_file", Some("t1"), Some("b1"))]),
            HistoryEntry::new(
                1,
                "user",
                vec![result("c1", "contents", Some("t1"), Some("b1")), text("actually, stop")],
            )
            .from_steering("m2"),
        ];
        let hydration = hydrate(&[], &live, false);
        let ids: Vec<Option<&str>> = hydration
            .blocks
            .iter()
            .filter_map(|block| match block {
                Block::User { queue_id, .. } => Some(queue_id.as_deref()),
                _ => None,
            })
            .collect();
        assert_eq!(ids, [Some("m2")]);
    }

    #[test]
    fn the_live_turn_is_appended_after_the_committed_prefix() {
        let committed = vec![HistoryEntry::new(0, "user", vec![text("hello")])];
        let live = vec![HistoryEntry::new(1, "assistant", vec![text("hi back")])];
        let hydration = hydrate(&committed, &live, false);
        assert_eq!(hydration.blocks.len(), 2);
        assert!(matches!(hydration.blocks[0], Block::User { .. }));
        assert!(matches!(hydration.blocks[1], Block::Assistant { .. }));
    }
}
