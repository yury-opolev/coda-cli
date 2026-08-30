//! Append-only per-session audit trail under
//! `<working_dir>/.coda/sessions/<id>.audit.jsonl`.
//!
//! Mirrors C# `Coda.Sdk.SessionAuditStore`:
//! - one JSON object per line (JSONL)
//! - `systemPrompt` and `toolDefs` are written change-only (carried forward)
//! - corrupt or torn lines are skipped on load
//! - a missing or corrupt file returns an empty list
//! - all write failures are swallowed (best-effort, never panic)

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use chrono::{DateTime, Utc};
use coda_llm::{ToolDefinition};
use serde_json::{Value, json};

use super::ids;
use super::message_json::{deserialize_tool_defs, serialize_tool_defs};

// ─────────────────────────────────────────────────────────────────────────────
// Types
// ─────────────────────────────────────────────────────────────────────────────

/// One entry in the audit trail: everything sent to and received from the model
/// during one user turn.
#[derive(Debug, Clone)]
pub struct AuditTurn {
    pub turn_index: u32,
    pub ts_utc: DateTime<Utc>,
    pub provider: String,
    pub model: String,
    pub input_tokens: u32,
    pub output_tokens: u32,
    pub stop_reason: Option<String>,
    pub tool_calls: Vec<AuditToolCall>,
    /// Effective system prompt for this turn (carried forward if unchanged).
    pub system_prompt: Option<String>,
    /// Effective tool definitions for this turn (carried forward if unchanged).
    pub tool_defs: Vec<ToolDefinition>,
}

#[derive(Debug, Clone)]
pub struct AuditToolCall {
    pub name: String,
    pub input: String,
    pub result: Option<String>,
    pub is_error: bool,
    pub call_id: Option<String>,
    pub status: Option<String>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Store
// ─────────────────────────────────────────────────────────────────────────────

/// Per-session change-only emission state.
struct EmittedState {
    system_prompt: Option<String>,
    tool_defs_json: Option<String>,
}

impl EmittedState {
    fn empty() -> Self {
        Self { system_prompt: None, tool_defs_json: None }
    }
}

/// Append-only audit store.
pub struct SessionAuditStore {
    working_dir: PathBuf,
    /// Tracks the last-emitted systemPrompt/toolDefs per session so subsequent
    /// appends can skip unchanged values (change-only emission).
    emitted: Mutex<HashMap<String, EmittedState>>,
}

impl SessionAuditStore {
    pub fn new(working_dir: impl Into<PathBuf>) -> Self {
        Self { working_dir: working_dir.into(), emitted: Mutex::new(HashMap::new()) }
    }

    fn sessions_dir(&self) -> PathBuf {
        self.working_dir.join(".coda").join("sessions")
    }

    fn file_path(&self, id: &str) -> PathBuf {
        self.sessions_dir().join(format!("{id}.audit.jsonl"))
    }

    /// Returns `true` when an audit file exists for `id`.
    pub fn exists(&self, id: &str) -> bool {
        ids::is_valid(id) && self.file_path(id).exists()
    }

    /// Append one turn.  Invalid id, I/O errors, and serialisation failures
    /// are all swallowed — this is called from a best-effort seam.
    pub async fn append_turn(&self, id: &str, turn: &AuditTurn) {
        if !ids::is_valid(id) {
            return;
        }
        let Ok(()) = self.try_append(id, turn).await else { return };
    }

    async fn try_append(&self, id: &str, turn: &AuditTurn) -> std::io::Result<()> {
        let path = self.file_path(id);

        // Recover emitted state on first append in this process (resume case).
        let (emit_system_prompt, emit_tool_defs, tool_defs_json) = {
            let mut map = self.emitted.lock().expect("audit emitted poisoned");
            let state = map.entry(id.to_owned()).or_insert_with(|| recover_state(&path));

            let tool_defs_json = serialize_tool_defs(&turn.tool_defs).to_string();

            let emit_system_prompt = state.system_prompt.as_deref() != turn.system_prompt.as_deref();
            let emit_tool_defs = state.tool_defs_json.as_deref() != Some(tool_defs_json.as_str());

            if emit_system_prompt {
                state.system_prompt = turn.system_prompt.clone();
            }
            if emit_tool_defs {
                state.tool_defs_json = Some(tool_defs_json.clone());
            }
            (emit_system_prompt, emit_tool_defs, tool_defs_json)
        };

        let tool_calls_v: Vec<Value> = turn
            .tool_calls
            .iter()
            .map(|c| {
                let mut o = serde_json::Map::new();
                o.insert("name".into(), json!(c.name));
                o.insert("input".into(), json!(c.input));
                if let Some(ref r) = c.result {
                    o.insert("result".into(), json!(r));
                }
                o.insert("isError".into(), json!(c.is_error));
                if let Some(ref id) = c.call_id {
                    o.insert("callId".into(), json!(id));
                }
                if let Some(ref s) = c.status {
                    o.insert("status".into(), json!(s));
                }
                Value::Object(o)
            })
            .collect();

        let mut line_obj = serde_json::Map::new();
        line_obj.insert("turnIndex".into(), json!(turn.turn_index));
        line_obj.insert("tsUtc".into(), json!(turn.ts_utc.to_rfc3339()));
        line_obj.insert("provider".into(), json!(turn.provider));
        line_obj.insert("model".into(), json!(turn.model));
        line_obj.insert(
            "usage".into(),
            json!({ "in": turn.input_tokens, "out": turn.output_tokens }),
        );
        line_obj.insert("stopReason".into(), json!(turn.stop_reason));
        line_obj.insert("toolCalls".into(), Value::Array(tool_calls_v));

        if emit_system_prompt {
            line_obj.insert("systemPrompt".into(), json!(turn.system_prompt));
        }
        if emit_tool_defs {
            let td: Value = serde_json::from_str(&tool_defs_json).unwrap_or(Value::Array(vec![]));
            line_obj.insert("toolDefs".into(), td);
        }

        let line = serde_json::to_string(&Value::Object(line_obj))
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?
            + "\n";

        std::fs::create_dir_all(self.sessions_dir())?;
        use std::io::Write;
        let mut f = std::fs::OpenOptions::new().create(true).append(true).open(&path)?;
        f.write_all(line.as_bytes())
    }

    /// Copy the sidecar from `source_id` to `target_id`.  Best-effort: swallows
    /// all errors.  Does not update the emission-state cache (the new session
    /// will recover it on the first append).
    pub fn copy(&self, source_id: &str, target_id: &str) {
        if !ids::is_valid(source_id) || !ids::is_valid(target_id) {
            return;
        }
        let src = self.file_path(source_id);
        if !src.exists() {
            return;
        }
        let dst = self.file_path(target_id);
        let _ = std::fs::create_dir_all(self.sessions_dir());
        let _ = std::fs::copy(&src, &dst);
    }

    /// Load all turns, carrying forward systemPrompt and toolDefs.
    /// Returns an empty vec if the file is missing, unreadable, or fully corrupt.
    pub async fn load(&self, id: &str) -> Vec<AuditTurn> {
        if !ids::is_valid(id) {
            return Vec::new();
        }
        let path = self.file_path(id);
        if !path.exists() {
            return Vec::new();
        }
        match tokio::fs::read_to_string(&path).await {
            Ok(text) => parse_audit_lines(&text),
            Err(_) => Vec::new(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Parsing helpers
// ─────────────────────────────────────────────────────────────────────────────

fn parse_audit_lines(text: &str) -> Vec<AuditTurn> {
    let mut turns = Vec::new();
    let mut current_system_prompt: Option<String> = None;
    let mut current_tool_defs: Vec<ToolDefinition> = Vec::new();

    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        if let Ok(v) = serde_json::from_str::<Value>(line) {
            if let Some(turn) =
                parse_audit_line(&v, &mut current_system_prompt, &mut current_tool_defs)
            {
                turns.push(turn);
            }
        }
        // Corrupt/torn lines are silently skipped.
    }
    turns
}

fn parse_audit_line(
    v: &Value,
    system_prompt: &mut Option<String>,
    tool_defs: &mut Vec<ToolDefinition>,
) -> Option<AuditTurn> {
    let obj = v.as_object()?;
    let turn_index = obj.get("turnIndex")?.as_u64()? as u32;
    let ts_str = obj.get("tsUtc")?.as_str()?;
    let ts_utc = DateTime::parse_from_rfc3339(ts_str).ok()?.with_timezone(&Utc);
    let provider = obj.get("provider")?.as_str()?.to_owned();
    let model = obj.get("model")?.as_str()?.to_owned();
    let usage = obj.get("usage")?.as_object()?;
    let input_tokens = usage.get("in")?.as_u64()? as u32;
    let output_tokens = usage.get("out")?.as_u64()? as u32;
    let stop_reason = obj.get("stopReason").and_then(|v| v.as_str()).map(|s| s.to_owned());

    let tool_calls = obj
        .get("toolCalls")
        .and_then(|v| v.as_array())
        .map(|arr| arr.iter().filter_map(parse_tool_call).collect())
        .unwrap_or_default();

    if let Some(sp) = obj.get("systemPrompt") {
        *system_prompt = sp.as_str().map(|s| s.to_owned());
    }
    if let Some(td) = obj.get("toolDefs") {
        *tool_defs = deserialize_tool_defs(td);
    }

    Some(AuditTurn {
        turn_index,
        ts_utc,
        provider,
        model,
        input_tokens,
        output_tokens,
        stop_reason,
        tool_calls,
        system_prompt: system_prompt.clone(),
        tool_defs: tool_defs.clone(),
    })
}

fn parse_tool_call(v: &Value) -> Option<AuditToolCall> {
    let obj = v.as_object()?;
    Some(AuditToolCall {
        name: obj.get("name")?.as_str()?.to_owned(),
        input: obj.get("input").and_then(|v| v.as_str()).unwrap_or("").to_owned(),
        result: obj.get("result").and_then(|v| v.as_str()).map(|s| s.to_owned()),
        is_error: obj.get("isError").and_then(|v| v.as_bool()).unwrap_or(false),
        call_id: obj.get("callId").and_then(|v| v.as_str()).map(|s| s.to_owned()),
        status: obj.get("status").and_then(|v| v.as_str()).map(|s| s.to_owned()),
    })
}

/// Recover emission state by scanning an existing sidecar (for resume after process restart).
fn recover_state(path: &Path) -> EmittedState {
    let mut state = EmittedState::empty();
    let text = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(_) => return state,
    };
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        if let Ok(v) = serde_json::from_str::<Value>(line) {
            if let Some(obj) = v.as_object() {
                if let Some(sp) = obj.get("systemPrompt") {
                    state.system_prompt = sp.as_str().map(|s| s.to_owned());
                }
                if let Some(td) = obj.get("toolDefs") {
                    state.tool_defs_json = Some(td.to_string());
                }
            }
        }
    }
    state
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    fn make_turn(index: u32, provider: &str, model: &str) -> AuditTurn {
        AuditTurn {
            turn_index: index,
            ts_utc: Utc::now(),
            provider: provider.into(),
            model: model.into(),
            input_tokens: 10,
            output_tokens: 5,
            stop_reason: Some("end_turn".into()),
            tool_calls: vec![],
            system_prompt: Some("You are helpful.".into()),
            tool_defs: vec![],
        }
    }

    #[tokio::test]
    async fn append_and_load_round_trip() {
        let dir = tempfile::tempdir().unwrap();
        let store = SessionAuditStore::new(dir.path());
        let id = "abc123456789";
        store.append_turn(id, &make_turn(0, "anthropic", "claude-opus-5")).await;
        store.append_turn(id, &make_turn(1, "anthropic", "claude-opus-5")).await;
        let turns = store.load(id).await;
        assert_eq!(turns.len(), 2);
        assert_eq!(turns[0].turn_index, 0);
        assert_eq!(turns[1].turn_index, 1);
    }

    #[tokio::test]
    async fn system_prompt_change_only_emission() {
        let dir = tempfile::tempdir().unwrap();
        let store = SessionAuditStore::new(dir.path());
        let id = "abc123456789";

        let t0 = make_turn(0, "anthropic", "claude");
        store.append_turn(id, &t0).await;

        let t1 = make_turn(1, "anthropic", "claude"); // same system_prompt
        store.append_turn(id, &t1).await;

        let path = dir.path().join(".coda").join("sessions").join("abc123456789.audit.jsonl");
        let text = tokio::fs::read_to_string(&path).await.unwrap();
        let lines: Vec<&str> = text.lines().filter(|l| !l.is_empty()).collect();
        assert_eq!(lines.len(), 2);

        let v0: Value = serde_json::from_str(lines[0]).unwrap();
        let v1: Value = serde_json::from_str(lines[1]).unwrap();
        // First turn must emit systemPrompt; second must not (change-only).
        assert!(v0.get("systemPrompt").is_some(), "first turn must emit systemPrompt");
        assert!(v1.get("systemPrompt").is_none(), "second turn must skip unchanged systemPrompt");
    }

    #[tokio::test]
    async fn load_missing_file_returns_empty() {
        let dir = tempfile::tempdir().unwrap();
        let store = SessionAuditStore::new(dir.path());
        let turns = store.load("abc123456789").await;
        assert!(turns.is_empty());
    }

    #[tokio::test]
    async fn load_skips_corrupt_lines() {
        let dir = tempfile::tempdir().unwrap();
        let sdir = dir.path().join(".coda").join("sessions");
        std::fs::create_dir_all(&sdir).unwrap();
        std::fs::write(sdir.join("abc123456789.audit.jsonl"), b"not json\n").unwrap();
        let store = SessionAuditStore::new(dir.path());
        let turns = store.load("abc123456789").await;
        assert!(turns.is_empty());
    }

    #[tokio::test]
    async fn copy_creates_identical_sidecar() {
        let dir = tempfile::tempdir().unwrap();
        let store = SessionAuditStore::new(dir.path());
        let src = "aaa111111111";
        let dst = "bbb222222222";
        store.append_turn(src, &make_turn(0, "p", "m")).await;
        store.copy(src, dst);
        let from_src = store.load(src).await;
        let from_dst = store.load(dst).await;
        assert_eq!(from_src.len(), from_dst.len());
        assert_eq!(from_src[0].turn_index, from_dst[0].turn_index);
    }

    #[test]
    fn invalid_id_exists_returns_false() {
        let dir = tempfile::tempdir().unwrap();
        let store = SessionAuditStore::new(dir.path());
        assert!(!store.exists("../evil"));
    }
}
