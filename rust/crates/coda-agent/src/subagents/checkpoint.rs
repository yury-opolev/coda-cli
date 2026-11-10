//! Serializable state of a suspended subagent (planned restart).
//!
//! A [`SubagentCheckpoint`] carries everything needed to re-create a
//! subagent that paused at a safe point: how it was spawned
//! ([`SubagentSpec`]), its conversation, its loop position and its
//! undelivered inbox. It is written to `.coda/sessions/<id>.tasks/<taskId>.json`
//! by the restart writer and handed back to `SubagentHost::restore`.

use std::path::{Path, PathBuf};

use chrono::{DateTime, Utc};
use coda_llm::{Content, Message};
use serde::{Deserialize, Serialize};

use crate::session::message_json::{deserialize_messages, serialize_messages};
use crate::session::private_file::write_private_atomic;
use crate::steering::InboxItemKind;
use crate::tasks::{TaskKind, TaskManager, TaskRunStatus};
use crate::tool::ScheduleOrigin;

/// Format version of a checkpoint file; newer files are refused.
pub const SUBAGENT_CHECKPOINT_VERSION: u32 = 1;

/// Largest checkpoint file written; older tool output is truncated to fit.
pub const CHECKPOINT_SIZE_CAP: usize = 8 * 1024 * 1024;

/// How a subagent was spawned, recorded once its run is fully configured.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SubagentSpec {
    pub agent_type: String,
    pub prompt: String,
    /// The final system prompt (after any SubagentStart hook additions).
    pub system_prompt: String,
    pub model: String,
    pub provider: Option<String>,
    pub effort: Option<String>,
    pub depth: u32,
    pub max_steps: usize,
    pub max_tokens: u32,
    pub foreground: bool,
    /// `{"definitionId", "definitionName"}` of a scheduled run, if any.
    pub schedule_origin: Option<serde_json::Value>,
}

impl SubagentSpec {
    pub(crate) fn origin_to_json(origin: &ScheduleOrigin) -> serde_json::Value {
        serde_json::json!({
            "definitionId": origin.definition_id,
            "definitionName": origin.definition_name,
        })
    }

    /// The scheduled provenance stored in this spec.
    pub fn schedule_origin(&self) -> Option<ScheduleOrigin> {
        let value = self.schedule_origin.as_ref()?;
        let id = value.get("definitionId")?.as_str()?;
        let name = value.get("definitionName").and_then(|n| n.as_str()).map(str::to_owned);
        Some(ScheduleOrigin::new(id, name))
    }
}

/// One undelivered inbox item (`kind` is an [`InboxItemKind`] name).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CheckpointInboxItem {
    pub kind: String,
    pub text: String,
}

impl CheckpointInboxItem {
    /// The inbox kind, defaulting to a plain message for unknown names.
    pub fn inbox_kind(&self) -> InboxItemKind {
        InboxItemKind::parse(&self.kind).unwrap_or_default()
    }
}

/// A suspended subagent, ready to be written to disk and restored.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SubagentCheckpoint {
    pub version: u32,
    pub task_id: String,
    pub owner_task_id: Option<String>,
    /// `"main"` or the owner's task id.
    pub reports_to: String,
    pub kind: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
    pub description: String,
    pub mode: String,
    pub spec: SubagentSpec,
    /// The conversation in the session transcript's message format.
    pub conversation: serde_json::Value,
    pub steps_used: usize,
    pub parked: bool,
    pub steering: Vec<CheckpointInboxItem>,
    pub explicitly_suspended: bool,
    pub started_at: DateTime<Utc>,
    pub suspended_at: DateTime<Utc>,
    pub truncated: bool,
}

impl SubagentCheckpoint {
    /// Captures a subagent that is suspended at a safe point. `None` unless
    /// the task is a suspended subagent with a checkpoint and a spawn spec.
    pub fn from_task(mgr: &TaskManager, id: &str) -> Option<Self> {
        let task = mgr.find_task(id)?;
        if task.kind != TaskKind::Subagent || task.status() != TaskRunStatus::Suspended {
            return None;
        }
        let checkpoint = task.checkpoint()?;
        let spec: SubagentSpec = serde_json::from_value(task.spec()?.clone()).ok()?;
        let steering = task
            .steering()
            .map(|inbox| inbox.snapshot_items())
            .unwrap_or_default()
            .into_iter()
            .map(|(kind, text)| CheckpointInboxItem { kind: kind.as_str().to_owned(), text })
            .collect();
        Some(Self {
            version: SUBAGENT_CHECKPOINT_VERSION,
            task_id: task.id.clone(),
            owner_task_id: task.parent_id.clone(),
            reports_to: task.parent_id.clone().unwrap_or_else(|| "main".to_owned()),
            kind: task.kind.as_str().to_owned(),
            title: task.title.clone(),
            description: task.description.clone(),
            mode: task.mode().as_str().to_owned(),
            spec,
            conversation: serialize_messages(&checkpoint.history),
            steps_used: checkpoint.steps_used,
            parked: checkpoint.parked,
            steering,
            explicitly_suspended: task.suspend_explicit(),
            started_at: DateTime::<Utc>::from(task.started_at),
            suspended_at: DateTime::<Utc>::from(checkpoint.suspended_at),
            truncated: false,
        })
    }

    /// The stored conversation as messages.
    pub fn messages(&self) -> Vec<Message> {
        deserialize_messages(&self.conversation)
    }

    /// JSON bytes no larger than `cap` where possible: while too large, the
    /// oldest tool results are replaced by a truncation marker (and
    /// `truncated` is set). Returns the bytes and whether anything was cut.
    pub fn to_json_capped(&self, cap: usize) -> (Vec<u8>, bool) {
        let bytes = serde_json::to_vec(self).expect("checkpoint serializes");
        if bytes.len() <= cap {
            return (bytes, self.truncated);
        }
        let mut shrunk = self.clone();
        shrunk.truncated = true;
        let mut messages = self.messages();
        let mut bytes = serde_json::to_vec(&shrunk).expect("checkpoint serializes");
        'outer: for mi in 0..messages.len() {
            for bi in 0..messages[mi].content.len() {
                if bytes.len() <= cap {
                    break 'outer;
                }
                let Content::ToolResult { content, .. } = &mut messages[mi].content[bi] else {
                    continue;
                };
                let omitted = content.chars().count();
                let marker =
                    format!("[truncated for a planned restart: {omitted} characters omitted]");
                if content.starts_with("[truncated for a planned restart:")
                    || marker.len() >= content.len()
                {
                    continue;
                }
                *content = marker;
                shrunk.conversation = serialize_messages(&messages);
                bytes = serde_json::to_vec(&shrunk).expect("checkpoint serializes");
            }
        }
        (bytes, true)
    }

    /// Writes `dir/<taskId>.json` (owner-only, atomic), capped at
    /// [`CHECKPOINT_SIZE_CAP`]. Returns the path and whether output was truncated.
    pub async fn write(&self, dir: &Path) -> std::io::Result<(PathBuf, bool)> {
        if !is_canonical_task_id(&self.task_id) {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                format!("invalid task id '{}'", self.task_id),
            ));
        }
        let path = dir.join(format!("{}.json", self.task_id));
        let (bytes, truncated) = self.to_json_capped(CHECKPOINT_SIZE_CAP);
        write_private_atomic(&path, &bytes).await?;
        Ok((path, truncated))
    }

    /// Reads a checkpoint file, refusing a newer format version.
    pub fn read(path: &Path) -> Result<Self, String> {
        let text = std::fs::read_to_string(path)
            .map_err(|e| format!("cannot read {}: {e}", path.display()))?;
        let value: serde_json::Value = serde_json::from_str(&text)
            .map_err(|e| format!("cannot parse {}: {e}", path.display()))?;
        let version = value.get("version").and_then(serde_json::Value::as_u64).unwrap_or(0);
        if version > u64::from(SUBAGENT_CHECKPOINT_VERSION) {
            return Err(format!(
                "{} has checkpoint version {version}; this Coda reads up to {}",
                path.display(),
                SUBAGENT_CHECKPOINT_VERSION
            ));
        }
        serde_json::from_value(value).map_err(|e| format!("cannot parse {}: {e}", path.display()))
    }
}

fn is_canonical_task_id(id: &str) -> bool {
    id.strip_prefix("task-")
        .is_some_and(|digits| !digits.is_empty() && digits.bytes().all(|b| b.is_ascii_digit()))
}
