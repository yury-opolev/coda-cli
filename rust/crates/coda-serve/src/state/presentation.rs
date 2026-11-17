//! The engine-owned presentation timeline of one live session: bounded
//! records (task completions, passive notifications) with stable item ids
//! and anchors relative to the committed conversation.
//!
//! Pure data. [`super::EngineState`] holds it behind an `Arc` inside
//! `StateInner` and mutates it only inside `transact`, so every change and the
//! `event/presentationRecord` announcing it are one transaction. Persistence
//! (`to_persisted` / `from_persisted`) is a plain JSON value; the host writes
//! it into the session transcript.

use std::collections::VecDeque;

use coda_proto::presentation::{
    PRESENTATION_SCHEMA_VERSION, PresentationAnchor, PresentationPayload, PresentationRecord,
    PresentationRelation, PresentationTimeline,
};
use serde_json::{Value, json};

/// Most records retained per timeline; the oldest are evicted first.
pub const MAX_RECORDS: usize = 256;
/// Most payload bytes (serialized JSON) retained per timeline.
pub const MAX_PAYLOAD_BYTES: usize = 256 * 1024;
/// Longest task instructions kept in a card.
pub const MAX_INSTRUCTIONS_CHARS: usize = 4096;
/// Longest task title kept in a card.
pub const MAX_TITLE_CHARS: usize = 200;
/// Defensive cap on notification text (the message bus already bounds it).
pub const MAX_NOTIFICATION_TEXT_CHARS: usize = 8192;

/// The on-disk format of [`PresentationStore::to_persisted`].
pub const PERSISTED_FORMAT: u64 = 1;

pub const DURABILITY_MEMORY: &str = "memory";
pub const DURABILITY_DURABLE: &str = "durable";
pub const DURABILITY_CORRUPT: &str = "corrupt";

#[derive(Clone, Debug)]
pub struct PresentationStore {
    timeline_id: String,
    next_position: u64,
    change_seq: u64,
    records: VecDeque<PresentationRecord>,
    payload_bytes: usize,
    evicted_before_seq: Option<u64>,
    durability: &'static str,
    /// The highest `change_seq` known to be on disk.
    durable_seq: Option<u64>,
}

/// What [`PresentationStore::accept_counting`] did.
#[derive(Debug, Default)]
pub struct Accepted {
    pub record: Option<PresentationRecord>,
    /// Whether `record` is an amendment of an existing item.
    pub amended: bool,
    pub evicted: u32,
}

fn payload_size(payload: &PresentationPayload) -> usize {
    serde_json::to_string(payload).map(|s| s.len()).unwrap_or(0)
}

fn cap_chars(text: &mut String, max: usize, marker: &str) {
    if text.chars().count() > max {
        let mut cut: String = text.chars().take(max).collect();
        cut.push_str(marker);
        *text = cut;
    }
}

/// Applies the payload caps, so a re-accepted payload compares equal to the
/// stored one.
fn bounded(mut payload: PresentationPayload) -> PresentationPayload {
    match &mut payload {
        PresentationPayload::TaskCompletion { title, instructions, .. } => {
            cap_chars(instructions, MAX_INSTRUCTIONS_CHARS, "\n[truncated]");
            if let Some(t) = title {
                cap_chars(t, MAX_TITLE_CHARS, "");
            }
        }
        PresentationPayload::Notification { text, .. } => {
            cap_chars(text, MAX_NOTIFICATION_TEXT_CHARS, "\n[truncated]");
        }
    }
    payload
}

impl PresentationStore {
    pub fn new(timeline_id: impl Into<String>) -> Self {
        Self {
            timeline_id: timeline_id.into(),
            next_position: 1,
            change_seq: 0,
            records: VecDeque::new(),
            payload_bytes: 0,
            evicted_before_seq: None,
            durability: DURABILITY_MEMORY,
            durable_seq: None,
        }
    }

    pub fn timeline_id(&self) -> &str {
        &self.timeline_id
    }

    pub fn change_seq(&self) -> u64 {
        self.change_seq
    }

    pub fn durability(&self) -> &'static str {
        self.durability
    }

    pub fn len(&self) -> usize {
        self.records.len()
    }

    pub fn is_empty(&self) -> bool {
        self.records.is_empty()
    }

    /// Whether the newest change is already on disk.
    pub fn is_persisted(&self) -> bool {
        self.durable_seq.is_some_and(|seq| seq >= self.change_seq)
    }

    fn bump(&mut self) -> u64 {
        self.change_seq += 1;
        if self.durability == DURABILITY_DURABLE {
            self.durability = DURABILITY_MEMORY;
        }
        self.change_seq
    }

    /// Accepts an occurrence. See [`Self::accept_counting`].
    pub fn accept(
        &mut self,
        item_id: String,
        payload: PresentationPayload,
        anchor: PresentationAnchor,
        occurred_at: String,
    ) -> Option<PresentationRecord> {
        self.accept_counting(item_id, payload, anchor, occurred_at).record
    }

    /// Accepts an occurrence: idempotent for identical content, an in-place
    /// revision (same position and anchor) for different content, otherwise a
    /// new record at the end. Enforces the retention bounds afterwards.
    pub fn accept_counting(
        &mut self,
        item_id: String,
        payload: PresentationPayload,
        anchor: PresentationAnchor,
        occurred_at: String,
    ) -> Accepted {
        let payload = bounded(payload);
        if let Some(index) = self.records.iter().position(|r| r.item_id == item_id) {
            if self.records[index].payload == payload {
                return Accepted::default();
            }
            let seq = self.bump();
            let new_size = payload_size(&payload);
            let record = &mut self.records[index];
            let old_size = payload_size(&record.payload);
            record.revision += 1;
            record.change_seq = seq;
            record.payload = payload;
            let updated = record.clone();
            self.payload_bytes = self.payload_bytes - old_size.min(self.payload_bytes) + new_size;
            let evicted = self.enforce_bounds();
            return Accepted { record: Some(updated), amended: true, evicted };
        }
        let seq = self.bump();
        let position_seq = self.next_position;
        self.next_position += 1;
        self.payload_bytes += payload_size(&payload);
        let record = PresentationRecord {
            item_id,
            position_seq,
            revision: 1,
            change_seq: seq,
            occurred_at: Some(occurred_at),
            anchor,
            relation: PresentationRelation::Active,
            payload,
        };
        self.records.push_back(record.clone());
        let evicted = self.enforce_bounds();
        Accepted { record: Some(record), amended: false, evicted }
    }

    fn enforce_bounds(&mut self) -> u32 {
        let mut evicted = 0;
        while self.records.len() > 1
            && (self.records.len() > MAX_RECORDS || self.payload_bytes > MAX_PAYLOAD_BYTES)
        {
            let Some(oldest) = self.records.pop_front() else { break };
            self.payload_bytes = self.payload_bytes.saturating_sub(payload_size(&oldest.payload));
            let before = oldest.position_seq + 1;
            self.evicted_before_seq = Some(self.evicted_before_seq.map_or(before, |b| b.max(before)));
            evicted += 1;
        }
        evicted
    }

    fn mark(&mut self, index: usize) -> PresentationRecord {
        let seq = self.bump();
        let record = &mut self.records[index];
        record.change_seq = seq;
        record.clone()
    }

    /// Whether [`Self::resolve_turn`] would change anything.
    pub fn would_resolve(&self, turn_id: &str, rewritten: bool) -> bool {
        self.records.iter().any(|r| {
            r.anchor.turn_id.as_deref() == Some(turn_id)
                || (rewritten && r.anchor.turn_id.is_none() && r.relation == PresentationRelation::Active)
        })
    }

    /// The turn `turn_id` committed at `committed_len` entries: its pending
    /// anchors resolve to the turn's end. When the commit rewrote earlier
    /// history (auto-compaction inside the turn), every earlier active anchor
    /// is `compacted` first.
    pub fn resolve_turn(
        &mut self,
        turn_id: &str,
        committed_len: i64,
        rewritten: bool,
    ) -> Vec<PresentationRecord> {
        let mut changed = Vec::new();
        for index in 0..self.records.len() {
            let record = &mut self.records[index];
            let mut touched = false;
            if rewritten
                && record.anchor.turn_id.is_none()
                && record.relation == PresentationRelation::Active
            {
                record.relation = PresentationRelation::Compacted;
                touched = true;
            }
            if record.anchor.turn_id.as_deref() == Some(turn_id) {
                record.anchor = PresentationAnchor { after_entries: committed_len, turn_id: None };
                touched = true;
            }
            if touched {
                changed.push(self.mark(index));
            }
        }
        changed
    }

    /// A compaction replaced the conversation: settled active anchors no
    /// longer name entries of it. Pending ones resolve at their turn's end.
    pub fn compacted(&mut self) -> Vec<PresentationRecord> {
        self.relate_settled(PresentationRelation::Compacted, |_| true)
    }

    /// A rewind cut the conversation to `new_len` entries.
    pub fn rewound(&mut self, new_len: i64) -> Vec<PresentationRecord> {
        self.relate_settled(PresentationRelation::Rewound, |r| r.anchor.after_entries > new_len)
    }

    fn relate_settled(
        &mut self,
        relation: PresentationRelation,
        applies: impl Fn(&PresentationRecord) -> bool,
    ) -> Vec<PresentationRecord> {
        let mut changed = Vec::new();
        for index in 0..self.records.len() {
            let record = &mut self.records[index];
            if record.relation == PresentationRelation::Active
                && record.anchor.turn_id.is_none()
                && applies(record)
            {
                record.relation = relation;
                changed.push(self.mark(index));
            }
        }
        changed
    }

    /// A fork starts a new, empty timeline: it inherits no records.
    pub fn fork(&self, new_timeline_id: impl Into<String>) -> Self {
        Self::new(new_timeline_id)
    }

    pub fn snapshot(&self) -> PresentationTimeline {
        PresentationTimeline {
            schema_version: PRESENTATION_SCHEMA_VERSION,
            timeline_id: self.timeline_id.clone(),
            change_seq: self.change_seq,
            records: self.records.iter().cloned().collect(),
            evicted_before_seq: self.evicted_before_seq,
            durability: self.durability.to_owned(),
        }
    }

    pub fn to_persisted(&self) -> Value {
        json!({
            "format": PERSISTED_FORMAT,
            "timelineId": self.timeline_id,
            "nextPosition": self.next_position,
            "changeSeq": self.change_seq,
            "evictedBeforeSeq": self.evicted_before_seq,
            "records": self.records.iter().collect::<Vec<_>>(),
        })
    }

    /// Restores a timeline saved with a transcript of `loaded_len` entries.
    ///
    /// No member is a fresh timeline; an unreadable one is a fresh timeline
    /// reported as `corrupt` — never presented as an empty history. A pending
    /// turn anchor is settled where the turn started (that turn never
    /// committed); an anchor past the loaded conversation is `unknown`.
    pub fn from_persisted(value: Option<&Value>, loaded_len: i64, fresh_timeline_id: String) -> Self {
        let Some(value) = value else {
            return Self::new(fresh_timeline_id);
        };
        match Self::parse(value, loaded_len) {
            Some(store) => store,
            None => {
                let mut store = Self::new(fresh_timeline_id);
                store.durability = DURABILITY_CORRUPT;
                store
            }
        }
    }

    fn parse(value: &Value, loaded_len: i64) -> Option<Self> {
        if value.get("format")?.as_u64()? != PERSISTED_FORMAT {
            return None;
        }
        let timeline_id = value.get("timelineId")?.as_str()?.to_owned();
        let records: Vec<PresentationRecord> =
            serde_json::from_value(value.get("records")?.clone()).ok()?;
        if records.windows(2).any(|w| w[0].position_seq >= w[1].position_seq) {
            return None;
        }
        let max_position = records.last().map_or(0, |r| r.position_seq);
        let max_change = records.iter().map(|r| r.change_seq).max().unwrap_or(0);
        let next_position = value
            .get("nextPosition")
            .and_then(Value::as_u64)
            .unwrap_or(1)
            .max(max_position + 1);
        let change_seq = value.get("changeSeq").and_then(Value::as_u64).unwrap_or(0).max(max_change);
        let evicted_before_seq = value.get("evictedBeforeSeq").and_then(Value::as_u64);
        let mut store = Self {
            timeline_id,
            next_position,
            change_seq,
            records: VecDeque::new(),
            payload_bytes: 0,
            evicted_before_seq,
            durability: DURABILITY_DURABLE,
            durable_seq: Some(change_seq),
        };
        for mut record in records {
            record.anchor.turn_id = None;
            if record.relation == PresentationRelation::Active && record.anchor.after_entries > loaded_len {
                record.relation = PresentationRelation::Unknown;
            }
            store.payload_bytes += payload_size(&record.payload);
            store.records.push_back(record);
        }
        Some(store)
    }

    /// The write of the timeline as of `change_seq` succeeded.
    pub fn mark_durable(&mut self, change_seq: u64) {
        self.durable_seq = Some(self.durable_seq.map_or(change_seq, |d| d.max(change_seq)));
        if change_seq >= self.change_seq {
            self.durability = DURABILITY_DURABLE;
        }
    }

    pub fn mark_persist_failed(&mut self) {
        self.durability = DURABILITY_MEMORY;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn task(task_id: &str, title: &str) -> PresentationPayload {
        PresentationPayload::TaskCompletion {
            task_id: task_id.into(),
            status: "completed".into(),
            task_kind: Some("subagent".into()),
            title: Some(title.into()),
            instructions: "do it".into(),
        }
    }

    fn note(id: &str, text: &str) -> PresentationPayload {
        PresentationPayload::Notification {
            message_id: id.into(),
            label: "worker".into(),
            text: text.into(),
            context: None,
            source: "subagent".into(),
            task_id: None,
            schedule_definition_id: None,
        }
    }

    fn at(after: i64) -> PresentationAnchor {
        PresentationAnchor { after_entries: after, turn_id: None }
    }

    fn pending(after: i64, turn: &str) -> PresentationAnchor {
        PresentationAnchor { after_entries: after, turn_id: Some(turn.into()) }
    }

    fn accept(store: &mut PresentationStore, id: &str, payload: PresentationPayload, anchor: PresentationAnchor) -> Option<PresentationRecord> {
        store.accept(id.into(), payload, anchor, "2026-11-17T10:00:00Z".into())
    }

    #[test]
    fn identical_titles_from_different_runs_are_distinct() {
        let mut store = PresentationStore::new("tl");
        let a = accept(&mut store, "task:e1:t1", task("t1", "check"), at(2)).unwrap();
        let b = accept(&mut store, "task:e1:t2", task("t2", "check"), at(4)).unwrap();
        assert_eq!((a.position_seq, b.position_seq), (1, 2));
        assert_eq!(store.len(), 2);
    }

    #[test]
    fn a_reused_task_number_in_a_new_engine_is_a_new_item() {
        let mut store = PresentationStore::new("tl");
        accept(&mut store, "task:engineA:t1", task("t1", "x"), at(2)).unwrap();
        let again = accept(&mut store, "task:engineB:t1", task("t1", "x"), at(6)).unwrap();
        assert_eq!(again.position_seq, 2);
        assert_eq!(store.len(), 2);
    }

    #[test]
    fn a_duplicate_accept_is_idempotent() {
        let mut store = PresentationStore::new("tl");
        accept(&mut store, "notification:m1", note("m1", "hi"), at(2)).unwrap();
        let seq = store.change_seq();
        assert!(accept(&mut store, "notification:m1", note("m1", "hi"), at(9)).is_none());
        assert_eq!(store.change_seq(), seq);
        assert_eq!(store.snapshot().records[0].anchor, at(2));
    }

    #[test]
    fn a_revision_keeps_position_and_anchor() {
        let mut store = PresentationStore::new("tl");
        accept(&mut store, "notification:m0", note("m0", "first"), at(1)).unwrap();
        accept(&mut store, "notification:m1", note("m1", "hi"), at(2)).unwrap();
        let updated = accept(&mut store, "notification:m1", note("m1", "hi again"), at(9)).unwrap();
        assert_eq!(updated.position_seq, 2);
        assert_eq!(updated.revision, 2);
        assert_eq!(updated.change_seq, 3);
        assert_eq!(updated.anchor, at(2));
        assert_eq!(store.change_seq(), 3);
    }

    #[test]
    fn long_instructions_and_titles_are_capped() {
        let mut store = PresentationStore::new("tl");
        let payload = PresentationPayload::TaskCompletion {
            task_id: "t1".into(),
            status: "completed".into(),
            task_kind: None,
            title: Some("t".repeat(500)),
            instructions: "i".repeat(10_000),
        };
        let record = accept(&mut store, "task:e:t1", payload.clone(), at(0)).unwrap();
        let PresentationPayload::TaskCompletion { title, instructions, .. } = &record.payload else { panic!() };
        assert_eq!(title.as_ref().unwrap().chars().count(), MAX_TITLE_CHARS);
        assert!(instructions.ends_with("\n[truncated]"));
        assert_eq!(instructions.chars().count(), MAX_INSTRUCTIONS_CHARS + "\n[truncated]".len());
        assert!(accept(&mut store, "task:e:t1", payload, at(0)).is_none(), "the capped form is idempotent");
    }

    #[test]
    fn the_oldest_records_are_evicted_by_count() {
        let mut store = PresentationStore::new("tl");
        for i in 0..(MAX_RECORDS + 3) {
            accept(&mut store, &format!("notification:m{i}"), note(&format!("m{i}"), "x"), at(0));
        }
        let snap = store.snapshot();
        assert_eq!(snap.records.len(), MAX_RECORDS);
        assert_eq!(snap.records[0].position_seq, 4);
        assert_eq!(snap.evicted_before_seq, Some(4));
    }

    #[test]
    fn the_oldest_records_are_evicted_by_bytes() {
        let mut store = PresentationStore::new("tl");
        let big = "y".repeat(MAX_NOTIFICATION_TEXT_CHARS);
        let mut evicted = 0;
        for i in 0..40 {
            evicted += store
                .accept_counting(format!("notification:m{i}"), note(&format!("m{i}"), &big), at(0), String::new())
                .evicted;
        }
        assert!(evicted > 0);
        assert!(store.payload_bytes <= MAX_PAYLOAD_BYTES);
        let snap = store.snapshot();
        assert_eq!(snap.evicted_before_seq, Some(snap.records[0].position_seq));
        assert_eq!(snap.records.len() + evicted as usize, 40);
    }

    #[test]
    fn a_turn_commit_resolves_its_pending_anchors() {
        let mut store = PresentationStore::new("tl");
        accept(&mut store, "notification:a", note("a", "x"), at(2)).unwrap();
        accept(&mut store, "notification:b", note("b", "x"), pending(2, "t1")).unwrap();
        accept(&mut store, "notification:c", note("c", "x"), pending(2, "t-other")).unwrap();
        let changed = store.resolve_turn("t1", 6, false);
        assert_eq!(changed.len(), 1);
        assert_eq!(changed[0].item_id, "notification:b");
        assert_eq!(changed[0].anchor, at(6));
        assert_eq!(changed[0].change_seq, 4);
        let snap = store.snapshot();
        assert_eq!(snap.records[0].relation, PresentationRelation::Active);
        assert_eq!(snap.records[2].anchor, pending(2, "t-other"));
    }

    #[test]
    fn a_rewritten_turn_commit_compacts_earlier_anchors_first() {
        let mut store = PresentationStore::new("tl");
        accept(&mut store, "notification:a", note("a", "x"), at(2)).unwrap();
        accept(&mut store, "notification:b", note("b", "x"), pending(2, "t1")).unwrap();
        let changed = store.resolve_turn("t1", 3, true);
        assert_eq!(changed.len(), 2);
        let snap = store.snapshot();
        assert_eq!(snap.records[0].relation, PresentationRelation::Compacted);
        assert_eq!(snap.records[1].relation, PresentationRelation::Active);
        assert_eq!(snap.records[1].anchor, at(3));
    }

    #[test]
    fn compaction_marks_records_compacted() {
        let mut store = PresentationStore::new("tl");
        accept(&mut store, "notification:a", note("a", "x"), at(2)).unwrap();
        accept(&mut store, "notification:b", note("b", "x"), pending(4, "t1")).unwrap();
        let changed = store.compacted();
        assert_eq!(changed.len(), 1);
        assert_eq!(changed[0].relation, PresentationRelation::Compacted);
        assert!(store.compacted().is_empty(), "already compacted");
        assert_eq!(store.snapshot().records[1].relation, PresentationRelation::Active);
    }

    #[test]
    fn rewind_marks_later_records_rewound() {
        let mut store = PresentationStore::new("tl");
        accept(&mut store, "notification:a", note("a", "x"), at(2)).unwrap();
        accept(&mut store, "notification:b", note("b", "x"), at(4)).unwrap();
        accept(&mut store, "notification:c", note("c", "x"), at(6)).unwrap();
        let changed = store.rewound(4);
        assert_eq!(changed.len(), 1);
        assert_eq!(changed[0].item_id, "notification:c");
        assert_eq!(changed[0].relation, PresentationRelation::Rewound);
    }

    #[test]
    fn fork_starts_an_empty_timeline() {
        let mut store = PresentationStore::new("tl");
        accept(&mut store, "notification:a", note("a", "x"), at(2)).unwrap();
        let forked = store.fork("tl-2");
        let snap = forked.snapshot();
        assert_eq!(snap.timeline_id, "tl-2");
        assert!(snap.records.is_empty());
        assert_eq!(snap.change_seq, 0);
        assert_eq!(snap.durability, DURABILITY_MEMORY);
    }

    #[test]
    fn the_timeline_round_trips_through_its_persisted_form() {
        let mut store = PresentationStore::new("tl");
        accept(&mut store, "notification:a", note("a", "x"), at(2)).unwrap();
        accept(&mut store, "task:e:t1", task("t1", "y"), at(4)).unwrap();
        store.rewound(3);
        let value = store.to_persisted();
        assert_eq!(value["format"], 1);
        let restored = PresentationStore::from_persisted(Some(&value), 10, "fresh".into());
        let (before, after) = (store.snapshot(), restored.snapshot());
        assert_eq!(after.timeline_id, "tl");
        assert_eq!(after.records, before.records);
        assert_eq!(after.change_seq, before.change_seq);
        assert_eq!(after.durability, DURABILITY_DURABLE);
        let mut restored = restored;
        let next = accept(&mut restored, "notification:z", note("z", "x"), at(5)).unwrap();
        assert_eq!(next.position_seq, 3, "positions continue after a restore");
    }

    #[test]
    fn a_corrupt_member_is_reported_not_emptied() {
        for bad in [json!("garbage"), json!({ "format": 99, "timelineId": "x", "records": [] }), json!({ "format": 1, "timelineId": "x", "records": [{ "itemId": 3 }] })] {
            let store = PresentationStore::from_persisted(Some(&bad), 4, "fresh".into());
            let snap = store.snapshot();
            assert_eq!(snap.durability, DURABILITY_CORRUPT, "{bad}");
            assert!(snap.records.is_empty());
            assert_eq!(snap.timeline_id, "fresh");
        }
        let none = PresentationStore::from_persisted(None, 4, "fresh".into());
        assert_eq!(none.snapshot().durability, DURABILITY_MEMORY);
    }

    #[test]
    fn an_anchor_beyond_the_loaded_conversation_is_unknown_and_a_pending_turn_is_settled() {
        let mut store = PresentationStore::new("tl");
        accept(&mut store, "notification:a", note("a", "x"), at(2)).unwrap();
        accept(&mut store, "notification:b", note("b", "x"), at(8)).unwrap();
        accept(&mut store, "notification:c", note("c", "x"), pending(2, "t9")).unwrap();
        let restored = PresentationStore::from_persisted(Some(&store.to_persisted()), 4, "fresh".into());
        let snap = restored.snapshot();
        assert_eq!(snap.records[0].relation, PresentationRelation::Active);
        assert_eq!(snap.records[1].relation, PresentationRelation::Unknown);
        assert_eq!(snap.records[2].anchor, at(2));
        assert_eq!(snap.records[2].relation, PresentationRelation::Active);
    }

    #[test]
    fn durability_follows_what_was_written() {
        let mut store = PresentationStore::new("tl");
        accept(&mut store, "notification:a", note("a", "x"), at(2)).unwrap();
        let seq = store.change_seq();
        accept(&mut store, "notification:b", note("b", "x"), at(2)).unwrap();
        store.mark_durable(seq);
        assert_eq!(store.durability(), DURABILITY_MEMORY, "a newer change is not on disk yet");
        assert!(!store.is_persisted());
        store.mark_durable(store.change_seq());
        assert_eq!(store.durability(), DURABILITY_DURABLE);
        assert!(store.is_persisted());
        store.mark_persist_failed();
        assert_eq!(store.durability(), DURABILITY_MEMORY);
    }
}
