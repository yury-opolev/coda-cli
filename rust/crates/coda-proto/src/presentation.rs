//! The presentation timeline: user-visible records that are not part of the
//! model conversation (task completions, passive notifications), with stable
//! identities and positions relative to the committed conversation.
//!
//! This is a read model. Displaying, restoring or recovering a record never
//! executes a task, recreates a schedule, publishes a message or delivers
//! anything to the model.
//!
//! Additive: older clients ignore `GetHistoryResult.presentation` and
//! `event/presentationRecord`; engines that do not advertise the
//! `presentationTimeline` capability never send them.

use serde::{Deserialize, Serialize};

/// The version of the record shape below. Advertised as the
/// `presentationTimeline` capability version.
pub const PRESENTATION_SCHEMA_VERSION: u32 = 1;

/// The capability name in `initialize.capabilities`.
pub const PRESENTATION_CAPABILITY: &str = "presentationTimeline";

/// Where a record sits relative to the committed conversation.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq, Hash)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PresentationAnchor {
    /// The record follows the first `afterEntries` committed entries of the
    /// session's current conversation (history generation). `0` means before
    /// every entry.
    pub after_entries: i64,
    /// Set while the turn the record occurred in is still running: the
    /// record belongs at that turn's end, which is resolved when the turn
    /// commits. A record never sits inside a streaming reply.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub turn_id: Option<String>,
}

/// How a record's anchor relates to the conversation as it is now.
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq, Hash, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum PresentationRelation {
    /// The anchor names entries of the current conversation.
    #[default]
    Active,
    /// The entries around the anchor were replaced by a compaction summary.
    Compacted,
    /// The entries around the anchor were removed by a rewind.
    Rewound,
    /// The anchor does not fit the conversation the record was restored with.
    Unknown,
}

/// Bounded, UI-safe content of one record.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(tag = "kind", rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum PresentationPayload {
    /// A main-agent background task reached a terminal state.
    #[serde(rename_all = "camelCase")]
    TaskCompletion {
        /// The task id as the engine that ran it named it (process-local).
        task_id: String,
        /// `completed` | `failed` | `stopped`.
        status: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        task_kind: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        title: Option<String>,
        /// The task's prompt, capped.
        instructions: String,
    },
    /// A passive `notify_user` notification.
    #[serde(rename_all = "camelCase")]
    Notification {
        /// The message-bus id of the original publication.
        message_id: String,
        label: String,
        text: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        context: Option<String>,
        /// `scheduledTask` | `subagent` | `main` | `taskCompletion`.
        source: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        task_id: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        schedule_definition_id: Option<String>,
    },
}

impl PresentationPayload {
    pub fn kind(&self) -> &'static str {
        match self {
            PresentationPayload::TaskCompletion { .. } => "taskCompletion",
            PresentationPayload::Notification { .. } => "notification",
        }
    }
}

/// One user-visible occurrence.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PresentationRecord {
    /// Stable identity of the occurrence, the same live, in snapshots and
    /// after a restart. Never derived from a label or from text.
    pub item_id: String,
    /// Immutable acceptance order within the timeline.
    pub position_seq: u64,
    /// Content version; a newer revision replaces an older one in place.
    pub revision: u32,
    /// The timeline change that last touched this record (a separate
    /// namespace from `positionSeq`).
    pub change_seq: u64,
    /// When the engine accepted it (RFC 3339). Explanatory, never a sort key.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub occurred_at: Option<String>,
    pub anchor: PresentationAnchor,
    #[serde(default)]
    pub relation: PresentationRelation,
    pub payload: PresentationPayload,
}

/// The records of one session timeline, as of one read.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PresentationTimeline {
    pub schema_version: u32,
    /// Stable identity of the timeline; a fork starts a new one.
    pub timeline_id: String,
    /// The highest change applied, for ordering live updates after a read.
    pub change_seq: u64,
    /// Retained records in `positionSeq` order.
    pub records: Vec<PresentationRecord>,
    /// Records with a smaller `positionSeq` were evicted by the retention
    /// bound and are not shown.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub evicted_before_seq: Option<u64>,
    /// `durable` (persisted with the session), `memory` (not yet persisted,
    /// or persistence failed) or `corrupt` (the stored timeline could not be
    /// read; what is shown starts after that point).
    pub durability: String,
}

#[cfg(test)]
mod tests {
    use super::*;

    fn record() -> PresentationRecord {
        PresentationRecord {
            item_id: "task:engine-1:t1".into(),
            position_seq: 3,
            revision: 1,
            change_seq: 7,
            occurred_at: Some("2026-11-17T10:00:00Z".into()),
            anchor: PresentationAnchor { after_entries: 4, turn_id: None },
            relation: PresentationRelation::Active,
            payload: PresentationPayload::TaskCompletion {
                task_id: "t1".into(),
                status: "completed".into(),
                task_kind: Some("agent".into()),
                title: Some("background check".into()),
                instructions: "check things".into(),
            },
        }
    }

    #[test]
    fn a_record_round_trips_in_camel_case_with_a_kind_tag() {
        let value = serde_json::to_value(record()).unwrap();
        assert_eq!(value["itemId"], "task:engine-1:t1");
        assert_eq!(value["anchor"]["afterEntries"], 4);
        assert!(value["anchor"].get("turnId").is_none());
        assert_eq!(value["relation"], "active");
        assert_eq!(value["payload"]["kind"], "taskCompletion");
        assert_eq!(value["payload"]["taskId"], "t1");
        let back: PresentationRecord = serde_json::from_value(value).unwrap();
        assert_eq!(back, record());
    }

    #[test]
    fn a_missing_relation_reads_as_active() {
        let mut value = serde_json::to_value(record()).unwrap();
        value.as_object_mut().unwrap().remove("relation");
        let back: PresentationRecord = serde_json::from_value(value).unwrap();
        assert_eq!(back.relation, PresentationRelation::Active);
    }
}
