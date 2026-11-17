//! Where blocks that are not the conversation itself belong in the
//! transcript.
//!
//! A history read rebuilds the conversation; it says nothing about the
//! completion cards, notifications, local notices or command output shown
//! between its entries. Before this module those were re-appended after the
//! rebuilt conversation, so every refresh moved old records to the bottom
//! and made them look new. Now every such block carries a [`Placement`]
//! naming the conversation position it belongs to, and a rebuild puts it
//! back there. A block whose position cannot be known is put in an explicit
//! historical area at the top, never at the live tail.
//!
//! Positions are counted in committed conversation entries (`afterEntries`):
//! Coda's history is append-only between rewrites, and every rewrite bumps
//! the history epoch, so `(epoch, entries)` names a stable point.

use coda_proto::presentation::{PresentationAnchor, PresentationRelation};

/// What this client knows about the conversation right now.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Fence {
    pub epoch: i64,
    /// Committed entries in the engine's history.
    pub committed: i64,
    /// A turn is running: anything created now belongs at that turn's end.
    pub turn_running: bool,
}

/// A conversation position.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Anchor {
    /// Before the whole conversation (the banner, launch notices).
    Start,
    /// After the first `after` committed entries. `epoch` is `None` for an
    /// engine-maintained anchor, which the engine keeps current across
    /// compaction and rewind itself.
    Entries { epoch: Option<i64>, after: i64 },
    /// At the end of the turn running in `epoch`, not yet known.
    TurnEnd { epoch: i64 },
    /// Its position in the conversation is not known.
    Historical,
}

impl Anchor {
    /// An engine record's anchor.
    pub fn from_engine(anchor: &PresentationAnchor, fence: Fence) -> Self {
        if anchor.turn_id.is_some() {
            Anchor::TurnEnd { epoch: fence.epoch }
        } else {
            Anchor::Entries { epoch: None, after: anchor.after_entries }
        }
    }
}

/// What kind of block this is, as far as rebuilding is concerned.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Placement {
    /// Part of the conversation: replaced by every history read.
    Conversation,
    /// A statement about the current view (an omission warning): replaced
    /// with that view.
    View,
    /// A record of something that happened (a completion card, a
    /// notification), identified by `item_id`.
    Record(RecordPlacement),
    /// This terminal's own output, anchored where it was created.
    Local { anchor: Anchor, order: Order },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RecordPlacement {
    pub item_id: String,
    /// `Some` for an engine record: the engine's acceptance order.
    pub position_seq: Option<u64>,
    pub revision: u32,
    pub relation: PresentationRelation,
    pub anchor: Anchor,
    pub order: Order,
}

/// Ordering among blocks that share a conversation position.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Order {
    /// Creation order on this client.
    pub seq: u64,
    /// The highest engine record position already shown when this block was
    /// created: it goes after those records and before any later one.
    pub seen: u64,
}

impl Placement {
    pub fn anchor(&self) -> Option<&Anchor> {
        match self {
            Placement::Record(record) => Some(&record.anchor),
            Placement::Local { anchor, .. } => Some(anchor),
            Placement::Conversation | Placement::View => None,
        }
    }

    pub fn anchor_mut(&mut self) -> Option<&mut Anchor> {
        match self {
            Placement::Record(record) => Some(&mut record.anchor),
            Placement::Local { anchor, .. } => Some(anchor),
            Placement::Conversation | Placement::View => None,
        }
    }

    pub fn is_retained(&self) -> bool {
        matches!(self, Placement::Record(_) | Placement::Local { .. })
    }

    /// The key that orders blocks at one position: engine records by their
    /// position, everything else after the records it had seen.
    pub fn order_key(&self) -> (u64, u8, u64) {
        match self {
            Placement::Record(RecordPlacement { position_seq: Some(position), order, .. }) => {
                (*position, 0, order.seq)
            }
            Placement::Record(RecordPlacement { order, .. }) | Placement::Local { order, .. } => {
                (order.seen, 1, order.seq)
            }
            Placement::Conversation | Placement::View => (u64::MAX, 2, u64::MAX),
        }
    }

    fn relation(&self) -> PresentationRelation {
        match self {
            Placement::Record(record) => record.relation,
            _ => PresentationRelation::Active,
        }
    }
}

/// Where each committed entry of a rebuilt conversation ends.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ConversationLayout {
    pub epoch: i64,
    /// Committed entries in the whole history (not just this page).
    pub committed: i64,
    /// Index of the first loaded entry; earlier entries are not loaded.
    pub first_index: i64,
    /// `(entry index, block count after it)` for every loaded committed
    /// entry, in order.
    pub entry_ends: Vec<(i64, usize)>,
    /// Block count before the live (uncommitted) turn's blocks.
    pub live_start: usize,
}

/// Where one retained block goes in a rebuild.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum Slot {
    /// Before everything.
    Top,
    /// In the historical area, after [`Slot::Top`] and before the
    /// conversation.
    Historical,
    /// Before conversation block `n` (`n` may be the conversation length).
    At(usize),
}

/// Resolves a retained block's slot against a rebuilt conversation of
/// `conversation_len` blocks (without its streaming tail).
pub fn slot(placement: &Placement, layout: Option<&ConversationLayout>, conversation_len: usize) -> Slot {
    let Some(anchor) = placement.anchor() else {
        return Slot::Historical;
    };
    if placement.relation() != PresentationRelation::Active {
        return Slot::Historical;
    }
    let Some(layout) = layout else {
        // Without a layout no conversation position can be resolved.
        return if *anchor == Anchor::Start { Slot::Top } else { Slot::Historical };
    };
    match anchor {
        Anchor::Start => Slot::Top,
        Anchor::Historical => Slot::Historical,
        Anchor::TurnEnd { epoch } if *epoch == layout.epoch => Slot::At(conversation_len),
        Anchor::TurnEnd { .. } => Slot::Historical,
        Anchor::Entries { epoch: Some(epoch), .. } if *epoch != layout.epoch => Slot::Historical,
        Anchor::Entries { after, .. } => {
            let after = *after;
            if after < layout.first_index || after > layout.committed {
                return Slot::Historical;
            }
            if after == layout.committed {
                // After every committed entry: before a live turn, if any.
                return Slot::At(layout.live_start.min(conversation_len));
            }
            let end = layout
                .entry_ends
                .iter()
                .take_while(|(index, _)| *index < after)
                .last()
                .map_or(0, |(_, end)| *end);
            Slot::At(end.min(conversation_len))
        }
    }
}

/// A bounded set of item ids, oldest forgotten first.
#[derive(Debug, Default)]
pub struct SeenItems {
    ids: std::collections::HashSet<String>,
    order: std::collections::VecDeque<String>,
}

impl SeenItems {
    pub const CAPACITY: usize = 1024;

    /// Remembers `id`, reporting whether it is new.
    pub fn insert(&mut self, id: &str) -> bool {
        if self.ids.contains(id) {
            return false;
        }
        if self.order.len() == Self::CAPACITY {
            if let Some(oldest) = self.order.pop_front() {
                self.ids.remove(&oldest);
            }
        }
        self.ids.insert(id.to_string());
        self.order.push_back(id.to_string());
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn seen_items_are_bounded() {
        let mut seen = SeenItems::default();
        assert!(seen.insert("a"));
        assert!(!seen.insert("a"));
        for n in 0..SeenItems::CAPACITY {
            seen.insert(&n.to_string());
        }
        assert!(seen.insert("a"), "the oldest id was forgotten to stay bounded");
    }

    fn layout() -> ConversationLayout {
        // Entries 2..6 loaded (0 and 1 paged out), each one block; 6
        // committed entries; then a live turn.
        ConversationLayout {
            epoch: 4,
            committed: 6,
            first_index: 2,
            entry_ends: vec![(2, 1), (3, 2), (4, 3), (5, 4)],
            live_start: 4,
        }
    }

    fn local(anchor: Anchor) -> Placement {
        Placement::Local { anchor, order: Order::default() }
    }

    #[test]
    fn an_anchor_resolves_after_the_entries_it_names() {
        let l = layout();
        assert_eq!(slot(&local(Anchor::Entries { epoch: Some(4), after: 4 }), Some(&l), 6), Slot::At(2));
        assert_eq!(slot(&local(Anchor::Entries { epoch: None, after: 2 }), Some(&l), 6), Slot::At(0));
        assert_eq!(slot(&local(Anchor::Entries { epoch: None, after: 6 }), Some(&l), 6), Slot::At(4));
    }

    #[test]
    fn an_anchor_outside_the_loaded_page_or_epoch_is_historical() {
        let l = layout();
        assert_eq!(slot(&local(Anchor::Entries { epoch: None, after: 1 }), Some(&l), 6), Slot::Historical);
        assert_eq!(slot(&local(Anchor::Entries { epoch: None, after: 9 }), Some(&l), 6), Slot::Historical);
        assert_eq!(slot(&local(Anchor::Entries { epoch: Some(3), after: 4 }), Some(&l), 6), Slot::Historical);
        assert_eq!(slot(&local(Anchor::TurnEnd { epoch: 3 }), Some(&l), 6), Slot::Historical);
        assert_eq!(slot(&local(Anchor::Start), None, 6), Slot::Top);
        assert_eq!(slot(&local(Anchor::Entries { epoch: None, after: 3 }), None, 6), Slot::Historical);
    }

    #[test]
    fn a_compacted_or_rewound_record_is_historical() {
        let record = |relation| {
            Placement::Record(RecordPlacement {
                item_id: "x".into(),
                position_seq: Some(1),
                revision: 1,
                relation,
                anchor: Anchor::Entries { epoch: None, after: 4 },
                order: Order::default(),
            })
        };
        let l = layout();
        assert_eq!(slot(&record(PresentationRelation::Active), Some(&l), 6), Slot::At(2));
        for relation in [
            PresentationRelation::Compacted,
            PresentationRelation::Rewound,
            PresentationRelation::Unknown,
        ] {
            assert_eq!(slot(&record(relation), Some(&l), 6), Slot::Historical);
        }
    }

    #[test]
    fn local_blocks_order_after_the_records_they_had_seen() {
        let record = |position| {
            Placement::Record(RecordPlacement {
                item_id: format!("r{position}"),
                position_seq: Some(position),
                revision: 1,
                relation: PresentationRelation::Active,
                anchor: Anchor::Start,
                order: Order::default(),
            })
        };
        let before = Placement::Local { anchor: Anchor::Start, order: Order { seq: 1, seen: 4 } };
        assert!(record(4).order_key() < before.order_key());
        assert!(before.order_key() < record(5).order_key());
    }
}
