//! Live-turn `HistoryEntry` projection (§2.5 of the serve API implementation
//! plan).
//!
//! `Agent::run` mutates a *clone* of history and commits only at turn end, so
//! `session.history` during a turn is the pre-turn conversation. This module
//! rebuilds the in-flight turn as the same `HistoryEntry`/`HistoryBlock` DTOs
//! from the events the sink already sees (`session/prompt`'s accepted user
//! text, plus `AssistantText`/`Thinking`/`ToolCall`/`ToolResult` deltas), so a
//! client resyncing mid-turn does not depend on the ring and does not need to
//! wait for the agent's local clone to commit.
//!
//! # Cost model (I2)
//!
//! This accumulator lives inside `StateInner`, which is behind
//! `Mutex<Arc<StateInner>>` and copied-on-write. It therefore must be cheap
//! to *clone*, not just cheap to append to. Every piece of accumulated text is
//! stored as an `Arc<str>` chunk exactly as it arrived, and every finished
//! entry/block as an `Arc`, so a CoW clone copies pointers, never the
//! accumulated bytes. Nothing here re-materialises the full accumulated text
//! on a delta: the wire `Vec<HistoryEntry>` is built once per snapshot by
//! [`LiveTurnAccumulator::project`], **outside** the state lock, and is never
//! cached back into state.
//!
//! # Retention budget (S1)
//!
//! `liveBytesCap` is the **accounted retention budget** for the live turn,
//! and everything retained is charged against it — not only free text.
//! Each entry, each block and each delta chunk carries a fixed structural
//! charge on top of its payload, and identifiers (`callId`, `toolName`,
//! status labels) are charged at their real length. That keeps the entry,
//! block and chunk counts implicitly bounded too: nothing can be appended
//! "for free", so an empty-delta or empty-tool-result flood cannot grow the
//! projection without eventually setting `liveTruncated`.
//!
//! It is deliberately *not* advertised as an exact process-memory bound:
//! allocator overhead and `Arc` control blocks are not measured. It is an
//! honest upper bound on retained payload plus a fixed per-item constant, and
//! `Limits.liveBytesCap` documents exactly that.
//!
//! Scope note: this is the *foundational* live projection needed for a
//! correct `StateSnapshot.turn.liveEntries` — not the full `session/getHistory`
//! rich-history projection (Stage D), which additionally needs saved
//! transcripts, image metadata, and secret-deny-list sanitisation of
//! *committed* history. Known simplification here (documented, not hidden):
//! tool results are projected onto their own `"user"`-role entry per call
//! rather than batched exactly the way `coda_llm` batches them into one
//! request message — semantically equivalent content, different grouping.

use std::sync::Arc;

use coda_proto::history::{
    truncate_text, HistoryBlock, HistoryEntry, OMITTED_LIVE_BUDGET, OMITTED_TOO_LARGE,
};

/// Default cap on the total bytes of live-turn content retained (§2.5).
pub const DEFAULT_LIVE_BYTES_CAP: usize = 256 * 1024;

/// Per-block caps applied before the turn-wide byte budget.
const TOOL_INPUT_CAP: usize = 8 * 1024;
const TOOL_RESULT_CAP: usize = 16 * 1024;

/// Fixed structural charges (S1). Nothing may be appended for free: an empty
/// delta, an empty tool result or a bodyless block still costs its own
/// bookkeeping, which is what keeps entry/block/chunk counts bounded by the
/// same budget that bounds bytes.
const ENTRY_OVERHEAD: usize = 64;
const BLOCK_OVERHEAD: usize = 48;
const CHUNK_OVERHEAD: usize = 32;

/// Accumulates the in-flight turn as `HistoryEntry` DTOs from the same
/// events `ServeSink` already emits.
#[derive(Debug, Clone, Default)]
pub struct LiveTurnAccumulator {
    /// Finished entries, `Arc`-shared so a CoW clone is a pointer copy.
    entries: Vec<Arc<HistoryEntry>>,
    next_index: i64,
    /// Blocks already closed inside the still-open assistant entry.
    open_blocks: Vec<Arc<HistoryBlock>>,
    /// Chunks of the currently open assistant text block, exactly as the
    /// deltas arrived. Never concatenated until projection.
    open_text: Vec<Arc<str>>,
    /// Bytes the budget refused while *this* text block was open, so the
    /// block that was actually cut is the one that declares the omission
    /// (a turn-level `liveTruncated` cannot say which text is incomplete).
    open_text_omitted: usize,
    /// Chunks of the current reasoning burst, flushed into a
    /// `ReasoningSummary` block by `flush_thinking`.
    thinking: Vec<Arc<str>>,
    /// See [`Self::open_text_omitted`], for the current reasoning burst.
    thinking_omitted: usize,
    bytes_used: usize,
    bytes_cap: usize,
    pub truncated: bool,
    pub omitted_bytes: i64,
}

impl LiveTurnAccumulator {
    pub fn new() -> Self {
        Self::with_cap(DEFAULT_LIVE_BYTES_CAP)
    }

    pub fn with_cap(bytes_cap: usize) -> Self {
        Self { bytes_cap, ..Default::default() }
    }

    fn charge(&mut self, len: usize) -> bool {
        if self.truncated {
            self.omitted_bytes += len as i64;
            return false;
        }
        if self.bytes_used + len > self.bytes_cap {
            self.truncated = true;
            self.omitted_bytes += len as i64;
            return false;
        }
        self.bytes_used += len;
        true
    }

    /// Charges bookkeeping for content already admitted (closing a block or
    /// an entry whose payload was charged when it arrived). It can push the
    /// accumulator over the cap — which correctly stops the *next* admission
    /// — but never retroactively discards what was already accepted.
    fn charge_structural(&mut self, len: usize) {
        self.bytes_used += len;
        if self.bytes_used > self.bytes_cap {
            self.truncated = true;
        }
    }

    fn push_entry(&mut self, role: &str, blocks: Vec<HistoryBlock>) {
        let index = self.next_index;
        self.next_index += 1;
        self.entries.push(Arc::new(HistoryEntry::new(index, role, blocks)));
    }

    /// The user prompt accepted by the host, projected as entry 0.
    ///
    /// An oversized prompt is **capped and marked**, never dropped: dropping
    /// it made a turn look like it had no operator prompt at all, and keeping
    /// it silently short made a truncated prompt look complete.
    pub fn push_user_text(&mut self, text: &str) {
        self.push_user_block(text, None, None);
    }

    /// Operator steering the engine has just delivered into this turn.
    ///
    /// The model received it *here*, between the assistant content before it
    /// and whatever the next request produces, so the open assistant entry is
    /// closed first and the message becomes its own entry in arrival order —
    /// exactly where the committed transcript will later show it.
    ///
    /// `upstream_full_length` is the queue's own `textLength` when the
    /// wire-facing steering cap already shortened `text`; the marker is
    /// carried through rather than re-derived, so a message that was cut once
    /// is never reported as complete.
    pub fn push_steering_text(
        &mut self,
        message_id: &str,
        text: &str,
        upstream_full_length: Option<i64>,
    ) {
        if text.is_empty() {
            return;
        }
        self.flush_assistant();
        self.push_user_block(text, upstream_full_length, Some(message_id));
    }

    fn push_user_block(
        &mut self,
        text: &str,
        upstream_full_length: Option<i64>,
        steering_id: Option<&str>,
    ) {
        if text.is_empty() {
            return;
        }
        const OVERHEAD: usize = ENTRY_OVERHEAD + BLOCK_OVERHEAD;
        if self.charge(OVERHEAD + text.len()) {
            self.push_user_entry(
                steering_id,
                HistoryBlock::Text {
                    text: text.to_string(),
                    omitted_reason: upstream_full_length.map(|_| OMITTED_TOO_LARGE.to_string()),
                    full_length: upstream_full_length,
                },
            );
            return;
        }
        // `charge` has already tripped the cap and counted the whole prompt
        // (payload plus its structural charge) as omitted. Retain whatever
        // prefix the remaining budget holds and correct the accounting for
        // everything that was in fact admitted, so `liveOmittedBytes` counts
        // only what was genuinely dropped.
        let room = self.bytes_cap.saturating_sub(self.bytes_used).saturating_sub(OVERHEAD);
        let (capped, _) = truncate_text(text, room);
        if capped.is_empty() {
            return;
        }
        self.bytes_used += OVERHEAD + capped.len();
        self.omitted_bytes -= (OVERHEAD + capped.len()) as i64;
        self.push_user_entry(
            steering_id,
            HistoryBlock::Text {
                text: capped,
                omitted_reason: Some(OMITTED_LIVE_BUDGET.to_string()),
                // What the *original* said, not what this projection was
                // handed: a message the steering cap had already shortened
                // must not have its length re-stated as the shortened one.
                full_length: Some(upstream_full_length.unwrap_or(text.len() as i64)),
            },
        );
    }

    fn push_user_entry(&mut self, steering_id: Option<&str>, block: HistoryBlock) {
        let index = self.next_index;
        self.next_index += 1;
        let entry = HistoryEntry::new(index, "user", vec![block]);
        self.entries.push(Arc::new(match steering_id {
            Some(id) => entry.from_steering(id),
            None => entry,
        }));
    }

    /// An `AssistantText` delta — appended as its own chunk of the open
    /// assistant text block. A reasoning burst always precedes the text it
    /// produced, so any unflushed thinking is closed first.
    pub fn push_text_delta(&mut self, delta: &str) {
        if delta.is_empty() {
            return;
        }
        self.flush_thinking();
        if !self.charge(CHUNK_OVERHEAD + delta.len()) {
            self.open_text_omitted += delta.len();
            return;
        }
        self.open_text.push(Arc::from(delta));
    }

    /// A `Thinking` delta — buffered until `flush_thinking`.
    ///
    /// C5: reasoning bytes are charged against the same live budget as every
    /// other byte. Before this, an unbounded reasoning stream could grow
    /// `StateInner` without limit *and* without ever setting `liveTruncated`.
    pub fn push_thinking_delta(&mut self, delta: &str) {
        if delta.is_empty() {
            return;
        }
        if !self.charge(CHUNK_OVERHEAD + delta.len()) {
            self.thinking_omitted += delta.len();
            return;
        }
        self.thinking.push(Arc::from(delta));
    }

    /// `ThinkingComplete` — flushes the buffered thinking text as a
    /// `ReasoningSummary` block. Never carries the opaque provider signature
    /// (that never reaches this accumulator in the first place).
    pub fn flush_thinking(&mut self) {
        let Some(block) = self.pending_thinking_block() else { return };
        // Reasoning precedes any text produced from it; close the open text
        // block first so block order always matches arrival order.
        self.close_open_text();
        self.thinking.clear();
        self.thinking_omitted = 0;
        // The chunks were already charged; only the block wrapper is new.
        self.charge_structural(BLOCK_OVERHEAD);
        self.open_blocks.push(Arc::new(block));
    }

    fn close_open_text(&mut self) {
        let Some(block) = self.pending_text_block() else { return };
        self.open_text.clear();
        self.open_text_omitted = 0;
        self.charge_structural(BLOCK_OVERHEAD);
        self.open_blocks.push(Arc::new(block));
    }

    /// The still-open assistant text as a block, or `None` when nothing was
    /// retained *and* nothing was refused.
    fn pending_text_block(&self) -> Option<HistoryBlock> {
        if self.open_text.is_empty() && self.open_text_omitted == 0 {
            return None;
        }
        let text = concat(&self.open_text);
        let (omitted_reason, full_length) = omission_of(text.len(), self.open_text_omitted);
        Some(HistoryBlock::Text { text, omitted_reason, full_length })
    }

    /// The current reasoning burst as a block. See [`Self::pending_text_block`].
    fn pending_thinking_block(&self) -> Option<HistoryBlock> {
        if self.thinking.is_empty() && self.thinking_omitted == 0 {
            return None;
        }
        let text = concat(&self.thinking);
        let (omitted_reason, full_length) = omission_of(text.len(), self.thinking_omitted);
        Some(HistoryBlock::ReasoningSummary { text, redacted: false, omitted_reason, full_length })
    }

    /// A tool call — appended to the open assistant entry (tool_use blocks
    /// live on the assistant turn in `coda_llm`'s own message shape).
    ///
    /// Identifiers are charged at their real length: `callId` and `toolName`
    /// are retained just as much as the input is (S1).
    pub fn push_tool_call(
        &mut self,
        call_id: &str,
        batch_id: &str,
        turn_id: &str,
        tool_name: &str,
        input_json: &str,
    ) {
        let (capped, full_len) = truncate_text(input_json, TOOL_INPUT_CAP);
        if !self.charge(
            BLOCK_OVERHEAD + call_id.len() + batch_id.len() + turn_id.len()
                + tool_name.len() + capped.len(),
        ) {
            return;
        }
        self.flush_thinking();
        self.close_open_text();
        self.open_blocks.push(Arc::new(HistoryBlock::ToolCall {
            call_id: call_id.to_string(),
            tool_name: tool_name.to_string(),
            input_json: Some(capped),
            input_omitted_reason: full_len.map(|_| "tooLarge".to_string()),
            input_full_length: full_len,
            turn_id: Some(turn_id.to_string()),
            batch_id: Some(batch_id.to_string()),
        }));
    }

    /// A tool result — flushes the open assistant entry (if any), then
    /// appends its own `"user"`-role entry for the result (see module docs
    /// for the documented grouping simplification).
    pub fn push_tool_result(
        &mut self,
        call_id: &str,
        batch_id: Option<&str>,
        turn_id: &str,
        is_error: bool,
        content: &str,
        status: &str,
    ) {
        self.flush_assistant();
        let (capped, full_len) = truncate_text(content, TOOL_RESULT_CAP);
        if !self.charge(
            ENTRY_OVERHEAD + BLOCK_OVERHEAD + call_id.len() + turn_id.len()
                + batch_id.map_or(0, str::len) + status.len() + capped.len(),
        ) {
            return;
        }
        self.push_entry(
            "user",
            vec![HistoryBlock::ToolResult {
                call_id: call_id.to_string(),
                is_error,
                status: Some(status.to_string()),
                content: Some(capped),
                omitted_reason: full_len.map(|_| "tooLarge".to_string()),
                full_length: full_len,
                turn_id: Some(turn_id.to_string()),
                batch_id: batch_id.map(str::to_string),
            }],
        );
    }

    /// Flushes any open assistant blocks into a committed `HistoryEntry`.
    /// Safe to call when nothing is open (no-op).
    pub fn flush_assistant(&mut self) {
        self.flush_thinking();
        self.close_open_text();
        if self.open_blocks.is_empty() {
            return;
        }
        self.charge_structural(ENTRY_OVERHEAD);
        let blocks: Vec<HistoryBlock> =
            std::mem::take(&mut self.open_blocks).iter().map(|b| (**b).clone()).collect();
        self.push_entry("assistant", blocks);
    }

    /// Builds the wire `Vec<HistoryEntry>`, including any still-open
    /// assistant content (so a mid-stream snapshot shows partial text rather
    /// than nothing).
    ///
    /// This is the **only** place accumulated text is materialised, and it is
    /// called once per snapshot from outside the state lock — never per delta
    /// (I2).
    pub fn project(&self) -> Vec<HistoryEntry> {
        let mut out: Vec<HistoryEntry> = self.entries.iter().map(|e| (**e).clone()).collect();
        let pending_thinking = self.pending_thinking_block();
        let pending_text = self.pending_text_block();
        if self.open_blocks.is_empty() && pending_thinking.is_none() && pending_text.is_none() {
            return out;
        }
        let mut blocks: Vec<HistoryBlock> = self.open_blocks.iter().map(|b| (**b).clone()).collect();
        blocks.extend(pending_text);
        blocks.extend(pending_thinking);
        out.push(HistoryEntry::new(self.next_index, "assistant", blocks));
        out
    }

    /// The assistant text accumulated so far, in arrival order. This is the
    /// quantity the state/replay conformance property compares against the
    /// fold of the `event/assistantText` payloads the bus has published.
    pub fn assistant_text(&self) -> String {
        let mut out = String::new();
        for entry in &self.entries {
            if entry.role != "assistant" {
                continue;
            }
            for block in &entry.blocks {
                if let HistoryBlock::Text { text, .. } = block {
                    out.push_str(text);
                }
            }
        }
        for block in &self.open_blocks {
            if let HistoryBlock::Text { text, .. } = &**block {
                out.push_str(text);
            }
        }
        for chunk in &self.open_text {
            out.push_str(chunk);
        }
        out
    }
}

/// The `(omittedReason, fullLength)` pair for a block that retained
/// `retained` bytes and had `omitted` bytes refused by the live budget.
/// Both are `None` when nothing was refused — a complete block never carries
/// a marker.
fn omission_of(retained: usize, omitted: usize) -> (Option<String>, Option<i64>) {
    if omitted == 0 {
        return (None, None);
    }
    (Some(OMITTED_LIVE_BUDGET.to_string()), Some((retained + omitted) as i64))
}

fn concat(chunks: &[Arc<str>]) -> String {
    let mut out = String::with_capacity(chunks.iter().map(|c| c.len()).sum());
    for c in chunks {
        out.push_str(c);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn user_text_is_entry_zero() {
        let mut acc = LiveTurnAccumulator::new();
        acc.push_user_text("hello");
        let entries = acc.project();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].index, 0);
        assert_eq!(entries[0].role, "user");
    }

    #[test]
    fn consecutive_text_deltas_coalesce_into_one_block() {
        let mut acc = LiveTurnAccumulator::new();
        acc.push_text_delta("Hello, ");
        acc.push_text_delta("world!");
        let entries = acc.project();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].blocks.len(), 1);
        assert!(matches!(&entries[0].blocks[0], HistoryBlock::Text { text, .. } if text == "Hello, world!"));
    }

    #[test]
    fn mid_stream_snapshot_shows_partial_open_text_not_nothing() {
        let mut acc = LiveTurnAccumulator::new();
        acc.push_text_delta("partial");
        let entries = acc.project();
        assert_eq!(entries.len(), 1, "an open, unflushed assistant entry must still be visible");
        assert!(matches!(&entries[0].blocks[0], HistoryBlock::Text { text, .. } if text == "partial"));
    }

    #[test]
    fn thinking_complete_flushes_a_reasoning_summary_never_a_signature() {
        let mut acc = LiveTurnAccumulator::new();
        acc.push_thinking_delta("considering ");
        acc.push_thinking_delta("options");
        acc.flush_thinking();
        acc.push_text_delta("done");
        acc.flush_assistant();
        let entries = acc.project();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].blocks.len(), 2);
        assert!(matches!(&entries[0].blocks[0], HistoryBlock::ReasoningSummary { text, redacted: false, .. } if text == "considering options"));
        let v = serde_json::to_value(&entries[0].blocks[0]).unwrap();
        assert!(v.get("signature").is_none());
    }

    // ── C5: reasoning is visible in the projection *before* it completes ──
    #[test]
    fn thinking_deltas_are_visible_in_a_snapshot_before_thinking_complete_arrives() {
        let mut acc = LiveTurnAccumulator::new();
        acc.push_thinking_delta("half a thought");
        let entries = acc.project();
        assert_eq!(entries.len(), 1, "an in-flight reasoning burst must be projected, not withheld");
        assert!(
            matches!(&entries[0].blocks[0], HistoryBlock::ReasoningSummary { text, .. } if text == "half a thought"),
            "a snapshot taken mid-reasoning must show the thoughts its cursor already covers"
        );
    }

    // ── C5: reasoning bytes are charged against the live budget ───────────
    #[test]
    fn reasoning_bytes_are_charged_against_the_live_budget_like_every_other_byte() {
        let mut acc = LiveTurnAccumulator::with_cap(32);
        acc.push_thinking_delta(&"t".repeat(1024));
        assert!(acc.truncated, "an unbounded reasoning stream must trip the advertised cap");
        assert!(acc.omitted_bytes > 0, "omitted reasoning bytes must be reported, not silently retained");
    }

    #[test]
    fn text_followed_by_reasoning_keeps_its_order_before_and_after_flush() {
        let mut acc = LiveTurnAccumulator::new();
        acc.push_text_delta("text first");
        acc.push_thinking_delta("then reasoning");
        let pending = acc.project();
        assert!(matches!(&pending[0].blocks[0], HistoryBlock::Text { text, .. } if text == "text first"));
        assert!(matches!(&pending[0].blocks[1], HistoryBlock::ReasoningSummary { text, .. } if text == "then reasoning"));
        acc.flush_assistant();
        assert_eq!(serde_json::to_value(pending).unwrap(), serde_json::to_value(acc.project()).unwrap());
    }

    #[test]
    fn turn_and_batch_identifiers_count_against_tool_payload_retention() {
        let id = "x".repeat(1024);
        let mut calls = LiveTurnAccumulator::with_cap(1024);
        calls.push_tool_call("call", &id, &id, "tool", "{}");
        assert!(calls.truncated);
        assert!(calls.project().is_empty());
        let mut results = LiveTurnAccumulator::with_cap(1024);
        results.push_tool_result("call", Some(&id), &id, false, "", "Succeeded");
        assert!(results.truncated);
        assert!(results.project().is_empty());
    }

    #[test]
    fn a_reasoning_burst_is_projected_before_the_text_it_produced() {
        let mut acc = LiveTurnAccumulator::new();
        acc.push_thinking_delta("thinking first");
        // No explicit flush: the text delta itself closes the burst.
        acc.push_text_delta("then answering");
        let entries = acc.project();
        assert_eq!(entries[0].blocks.len(), 2);
        assert!(matches!(&entries[0].blocks[0], HistoryBlock::ReasoningSummary { .. }));
        assert!(matches!(&entries[0].blocks[1], HistoryBlock::Text { text, .. } if text == "then answering"));
    }

    #[test]
    fn tool_result_flushes_open_assistant_content_first_then_appends_its_own_entry() {
        let mut acc = LiveTurnAccumulator::new();
        acc.push_text_delta("calling a tool");
        acc.push_tool_call("c1", "b1", "t1", "read_file", "{\"path\":\"a.txt\"}");
        acc.push_tool_result("c1", Some("b1"), "t1", false, "file contents", "Succeeded");
        let entries = acc.project();
        assert_eq!(entries.len(), 2, "assistant entry then tool-result entry");
        assert_eq!(entries[0].role, "assistant");
        assert_eq!(entries[0].blocks.len(), 2);
        assert_eq!(entries[1].role, "user");
        assert!(matches!(&entries[1].blocks[0], HistoryBlock::ToolResult { is_error: false, .. }));
    }

    #[test]
    fn committed_history_plus_live_entries_is_the_whole_conversation_no_duplication() {
        // Simulates: user entry, assistant text, tool call+result, more text.
        let mut acc = LiveTurnAccumulator::new();
        acc.push_user_text("do the thing");
        acc.push_text_delta("Sure, let me check.");
        acc.push_tool_call("c1", "b1", "t1", "read_file", "{}");
        acc.push_tool_result("c1", Some("b1"), "t1", false, "ok", "Succeeded");
        acc.push_text_delta("All done.");
        acc.flush_assistant();
        let entries = acc.project();
        // Indices must be contiguous and never repeat (no duplicate projection).
        let indices: Vec<i64> = entries.iter().map(|e| e.index).collect();
        assert_eq!(indices, vec![0, 1, 2, 3]);
    }

    #[test]
    fn oversized_content_is_explicitly_truncated_never_silently_dropped() {
        let mut acc = LiveTurnAccumulator::with_cap(16);
        acc.push_user_text("this text is definitely longer than sixteen bytes");
        assert!(acc.truncated);
        assert!(acc.omitted_bytes > 0);
    }

    #[test]
    fn reconstruction_completes_under_the_advertised_cap_with_explicit_omission() {
        // Once truncated, further pushes keep counting omitted bytes rather
        // than silently vanishing or panicking.
        let mut acc = LiveTurnAccumulator::with_cap(8);
        acc.push_text_delta("12345678901234567890");
        acc.push_text_delta("more content after the cap");
        assert!(acc.truncated);
        let omitted_after_first = acc.omitted_bytes;
        assert!(omitted_after_first > 0);
        acc.push_tool_call("c1", "b1", "t1", "t", "{}");
        assert!(acc.omitted_bytes > omitted_after_first, "every further omission must keep accumulating, not reset");
    }

    // ── I8: per-block caps declare the original byte length ───────────────
    #[test]
    fn an_oversized_tool_input_declares_its_original_byte_length() {
        let mut acc = LiveTurnAccumulator::new();
        let big = "z".repeat(TOOL_INPUT_CAP * 2);
        acc.push_tool_call("c1", "b1", "t1", "write", &big);
        let entries = acc.project();
        let HistoryBlock::ToolCall { input_json, input_omitted_reason, input_full_length, .. } =
            &entries[0].blocks[0]
        else {
            panic!("expected a tool call block");
        };
        assert_eq!(input_json.as_ref().unwrap().len(), TOOL_INPUT_CAP);
        assert_eq!(input_omitted_reason.as_deref(), Some("tooLarge"));
        assert_eq!(*input_full_length, Some(big.len() as i64));
    }

    #[test]
    fn an_oversized_tool_result_declares_its_original_byte_length() {
        let mut acc = LiveTurnAccumulator::new();
        let big = "r".repeat(TOOL_RESULT_CAP * 3);
        acc.push_tool_result("c1", Some("b1"), "t1", false, &big, "Succeeded");
        let entries = acc.project();
        let HistoryBlock::ToolResult { content, omitted_reason, full_length, .. } = &entries[0].blocks[0]
        else {
            panic!("expected a tool result block");
        };
        assert_eq!(content.as_ref().unwrap().len(), TOOL_RESULT_CAP);
        assert_eq!(omitted_reason.as_deref(), Some("tooLarge"));
        assert_eq!(*full_length, Some(big.len() as i64));
    }

    #[test]
    fn a_multibyte_cap_never_produces_invalid_utf8() {
        let mut acc = LiveTurnAccumulator::new();
        let big = "日本語".repeat(TOOL_RESULT_CAP); // 9 bytes per repeat
        acc.push_tool_result("c1", Some("b1"), "t1", false, &big, "Succeeded");
        let entries = acc.project();
        let HistoryBlock::ToolResult { content, full_length, .. } = &entries[0].blocks[0] else {
            panic!("expected a tool result block");
        };
        let content = content.as_ref().unwrap();
        assert!(content.len() <= TOOL_RESULT_CAP);
        assert!(content.is_char_boundary(content.len()));
        assert_eq!(*full_length, Some(big.len() as i64));
    }

    // ── I8 (review): free text truncated by the live budget says so ──────

    #[test]
    fn a_user_prompt_larger_than_the_live_budget_is_capped_and_declares_its_length() {
        // Dropping the whole entry made an enormous prompt look like a turn
        // with no prompt at all; keeping it silently capped made it look
        // complete. Neither is honest.
        let mut acc = LiveTurnAccumulator::with_cap(512);
        let big = "u".repeat(4096);
        acc.push_user_text(&big);
        let entries = acc.project();
        assert_eq!(entries.len(), 1, "the operator's prompt must still be visible");
        let v = serde_json::to_value(&entries[0].blocks[0]).unwrap();
        assert_eq!(v["kind"], "text");
        assert!(!v["text"].as_str().unwrap().is_empty());
        assert_eq!(
            v["fullLength"], big.len() as i64,
            "a truncated prompt must never be able to look complete: {v}"
        );
        assert_eq!(v["omittedReason"], "liveBudgetExceeded");
        assert!(acc.truncated, "the turn-level flag must agree with the block-level marker");
        // `liveOmittedBytes` counts what was genuinely dropped: the bytes the
        // budget refused, not the retained prefix and not the bookkeeping
        // charged for the entry that *was* admitted.
        let retained = v["text"].as_str().unwrap().len() as i64;
        assert_eq!(
            acc.omitted_bytes,
            big.len() as i64 - retained,
            "admitted bytes must not also be counted as omitted"
        );
    }

    #[test]
    fn assistant_text_cut_off_by_the_budget_marks_the_block_it_actually_cut() {
        let mut acc = LiveTurnAccumulator::with_cap(256);
        acc.push_text_delta(&"a".repeat(200));
        acc.push_text_delta(&"b".repeat(4096));
        acc.flush_assistant();
        let v = serde_json::to_value(&acc.project()[0].blocks[0]).unwrap();
        assert_eq!(v["kind"], "text");
        assert_eq!(v["omittedReason"], "liveBudgetExceeded");
        assert!(
            v["fullLength"].as_i64().unwrap() > v["text"].as_str().unwrap().len() as i64,
            "the declared original length must exceed what was retained: {v}"
        );
    }

    #[test]
    fn a_reasoning_summary_cut_off_by_the_budget_can_never_look_complete() {
        let mut acc = LiveTurnAccumulator::with_cap(256);
        acc.push_thinking_delta(&"t".repeat(100));
        acc.push_thinking_delta(&"t".repeat(8192));
        acc.flush_thinking();
        let v = serde_json::to_value(&acc.project()[0].blocks[0]).unwrap();
        assert_eq!(v["kind"], "reasoningSummary");
        assert_eq!(v["omittedReason"], "liveBudgetExceeded");
        assert_eq!(v["fullLength"], 8292);
    }

    #[test]
    fn an_untruncated_live_text_block_carries_no_length_metadata() {
        let mut acc = LiveTurnAccumulator::new();
        acc.push_user_text("a normal prompt");
        acc.push_text_delta("a normal reply");
        acc.flush_assistant();
        for entry in acc.project() {
            for block in &entry.blocks {
                let v = serde_json::to_value(block).unwrap();
                assert!(v.get("fullLength").is_none(), "absent metadata must be omitted: {v}");
                assert!(v.get("omittedReason").is_none());
            }
        }
    }

    // ── I2: a CoW clone must not copy the accumulated bytes ───────────────
    #[test]
    fn cloning_the_accumulator_shares_the_accumulated_text_rather_than_copying_it() {
        let mut acc = LiveTurnAccumulator::new();
        for i in 0..500 {
            acc.push_text_delta(&format!("chunk-{i} "));
        }
        let before: Vec<*const str> = acc.open_text.iter().map(Arc::as_ptr).collect();
        let copy = acc.clone();
        let after: Vec<*const str> = copy.open_text.iter().map(Arc::as_ptr).collect();
        assert_eq!(before, after, "a CoW clone must share text chunks, never reallocate them");
        assert_eq!(copy.assistant_text(), acc.assistant_text());
    }

    // ── S1: nothing is retained for free ─────────────────────────────────

    #[test]
    fn a_flood_of_empty_tool_results_cannot_grow_the_projection_without_bound() {
        // Every result here carries no content at all. Charging only text
        // bytes let this append entries, ids and status labels for ever
        // while still reporting `liveTruncated: false`.
        let mut acc = LiveTurnAccumulator::with_cap(4 * 1024);
        for i in 0..1000 {
            acc.push_tool_result(&format!("call-{i}"), Some("b1"), "t1", false, "", "Succeeded");
        }
        assert!(acc.truncated, "an empty-payload flood must still trip the advertised cap");
        assert!(
            acc.project().len() < 100,
            "the retained entry count must be bounded by the same budget that bounds bytes"
        );
    }

    #[test]
    fn identifiers_and_labels_are_charged_at_their_real_length() {
        let long_id = "c".repeat(2048);
        let long_name = "n".repeat(2048);
        let mut acc = LiveTurnAccumulator::with_cap(1024);
        acc.push_tool_call(&long_id, "b1", "t1", &long_name, "");
        assert!(
            acc.truncated,
            "a call id and tool name larger than the whole budget are retained bytes too"
        );
    }

    #[test]
    fn a_flood_of_tiny_deltas_is_bounded_by_the_per_chunk_charge() {
        // Each delta is one retained `Arc<str>` chunk. Charging only its
        // payload made a million one-byte deltas nearly free.
        let mut acc = LiveTurnAccumulator::with_cap(4 * 1024);
        for _ in 0..10_000 {
            acc.push_text_delta("x");
        }
        assert!(acc.truncated);
        assert!(
            acc.assistant_text().len() < 4 * 1024,
            "the number of retained chunks must be bounded, not just their payload"
        );
    }

    #[test]
    fn the_budget_still_admits_ordinary_streaming_well_inside_the_default_cap() {
        // The structural charges must not make a normal reply truncate: a
        // 400-delta answer with a couple of tool calls stays well inside the
        // advertised default.
        let mut acc = LiveTurnAccumulator::new();
        acc.push_user_text("a normal question");
        for i in 0..400 {
            acc.push_text_delta(&format!("token{i} "));
        }
        acc.push_tool_call("call-1", "b1", "t1", "read_file", "{\"path\":\"a.txt\"}");
        acc.push_tool_result("call-1", Some("b1"), "t1", false, "file contents", "Succeeded");
        assert!(!acc.truncated, "an ordinary streamed reply must not be truncated by bookkeeping");
    }

    #[test]
    fn assistant_text_matches_the_projected_text_exactly() {
        let mut acc = LiveTurnAccumulator::new();
        acc.push_user_text("ask");
        acc.push_text_delta("one ");
        acc.push_tool_call("c1", "b1", "t1", "t", "{}");
        acc.push_tool_result("c1", Some("b1"), "t1", false, "ok", "Succeeded");
        acc.push_text_delta("two");
        let projected: String = acc
            .project()
            .iter()
            .filter(|e| e.role == "assistant")
            .flat_map(|e| e.blocks.iter())
            .filter_map(|b| match b {
                HistoryBlock::Text { text, .. } => Some(text.clone()),
                _ => None,
            })
            .collect();
        assert_eq!(acc.assistant_text(), projected);
        assert_eq!(projected, "one two");
    }
}
