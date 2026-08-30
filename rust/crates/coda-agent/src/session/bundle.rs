//! Portable session bundles: export to `*.coda-session.json` and import back.
//!
//! Mirrors C# `Coda.Sdk.SessionBundle` + `Coda.Sdk.SessionBundleService`.
//!
//! ## Schema
//! ```json
//! {
//!   "schema": "coda.session/1",
//!   "codaVersion": "...",
//!   "exportedUtc": "2024-01-01T00:00:00Z",
//!   "id": "abc123456789",
//!   "createdUtc": "...",
//!   "provider": "...",
//!   "model": "...",
//!   "auditAvailable": true,
//!   "systemPrompt": "...",
//!   "systemPromptOverride": "...",
//!   "toolDefs": [...],
//!   "turns": [{"role":"user","blocks":[...]},...],
//!   "auditTurns": [...]
//! }
//! ```

use std::path::{Path, PathBuf};

use chrono::{DateTime, Utc};
use coda_llm::{Message, Role, ToolDefinition};
use serde_json::{Value, json};

use super::audit::{AuditToolCall, AuditTurn, SessionAuditStore};
use super::ids;
use super::message_json::{
    deserialize_blocks, deserialize_tool_defs, serialize_blocks,
    serialize_tool_defs,
};
use super::store::SessionTranscriptStore;

// ─────────────────────────────────────────────────────────────────────────────
// Schema constants
// ─────────────────────────────────────────────────────────────────────────────

const SCHEMA_PREFIX: &str = "coda.session/";
const SUPPORTED_SCHEMA_MAJOR: u32 = 1;

// ─────────────────────────────────────────────────────────────────────────────
// Types
// ─────────────────────────────────────────────────────────────────────────────

/// An in-memory portable bundle.
#[derive(Debug)]
pub struct SessionBundle {
    pub schema: String,
    pub coda_version: String,
    pub exported_utc: DateTime<Utc>,
    pub id: String,
    pub created_utc: DateTime<Utc>,
    pub provider: Option<String>,
    pub model: Option<String>,
    pub audit_available: bool,
    pub system_prompt: Option<String>,
    pub system_prompt_override: Option<String>,
    pub tool_defs: Vec<ToolDefinition>,
    /// Transcript turns (role + blocks).
    pub turns: Vec<BundleTurn>,
    /// Audit-sidecar turns, verbatim.
    pub audit_turns: Vec<AuditTurn>,
}

#[derive(Debug)]
pub struct BundleTurn {
    pub role: String,
    pub blocks: Vec<coda_llm::Content>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Service
// ─────────────────────────────────────────────────────────────────────────────

/// Exports and imports session bundles.
pub struct SessionBundleService {
    working_dir: PathBuf,
    coda_version: String,
}

impl SessionBundleService {
    pub fn new(working_dir: impl Into<PathBuf>, coda_version: impl Into<String>) -> Self {
        Self { working_dir: working_dir.into(), coda_version: coda_version.into() }
    }

    /// Build a bundle for `session_id`.
    /// Returns `None` if no transcript is found.  Never panics.
    pub async fn export(
        &self,
        session_id: &str,
        exported_utc: DateTime<Utc>,
    ) -> Option<SessionBundle> {
        let transcript_store = SessionTranscriptStore::new(&self.working_dir);
        let stored = transcript_store.load_session(session_id).await?;

        // Resolve createdUtc from the listing (so we get the real creation time).
        let created_utc = transcript_store
            .list()
            .into_iter()
            .find(|s| s.id == session_id)
            .map(|s| s.created_utc)
            .unwrap_or(exported_utc);

        let audit_store = SessionAuditStore::new(&self.working_dir);
        let audit_available = audit_store.exists(session_id);
        let audit_turns = if audit_available {
            audit_store.load(session_id).await
        } else {
            Vec::new()
        };

        // Extract effective last system_prompt / tool_defs / provider / model.
        let (system_prompt, tool_defs, provider, model) = if let Some(last) = audit_turns.last() {
            (
                last.system_prompt.clone(),
                last.tool_defs.clone(),
                Some(last.provider.clone()),
                Some(last.model.clone()),
            )
        } else {
            (None, Vec::new(), None, None)
        };

        let turns: Vec<BundleTurn> = stored
            .messages
            .iter()
            .map(|m| BundleTurn {
                role: match m.role {
                    Role::User => "user".into(),
                    Role::Assistant => "assistant".into(),
                },
                blocks: m.content.clone(),
            })
            .collect();

        Some(SessionBundle {
            schema: format!("{SCHEMA_PREFIX}{SUPPORTED_SCHEMA_MAJOR}"),
            coda_version: self.coda_version.clone(),
            exported_utc,
            id: session_id.to_owned(),
            created_utc,
            provider,
            model,
            audit_available,
            system_prompt,
            system_prompt_override: stored.system_prompt_override,
            tool_defs,
            turns,
            audit_turns,
        })
    }

    /// Write a bundle to `out_path` (compact or pretty).
    pub async fn write_bundle(
        &self,
        bundle: &SessionBundle,
        out_path: &Path,
        pretty: bool,
    ) -> std::io::Result<()> {
        if let Some(parent) = out_path.parent() {
            if !parent.as_os_str().is_empty() {
                tokio::fs::create_dir_all(parent).await?;
            }
        }
        let root = serialize_bundle(bundle);
        let text = if pretty {
            serde_json::to_string_pretty(&root)
        } else {
            serde_json::to_string(&root)
        }
        .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
        tokio::fs::write(out_path, text.as_bytes()).await
    }

    /// Import a bundle from `bundle_path` into the working directory.
    ///
    /// If the bundle's session id already exists locally a new id is minted.
    /// Returns the (possibly new) id.
    ///
    /// # Errors
    /// Returns an error if the file is unreadable, not a JSON object, lacks a
    /// `"schema"` or `"id"` field, or the schema major version is unsupported.
    pub async fn import_bundle(&self, bundle_path: &Path) -> Result<String, ImportError> {
        let json_text = tokio::fs::read_to_string(bundle_path).await.map_err(|e| {
            ImportError::Io(format!("cannot read '{}': {e}", bundle_path.display()))
        })?;

        let root: Value =
            serde_json::from_str(&json_text).map_err(|_| ImportError::NotABundle {
                path: bundle_path.display().to_string(),
                reason: "not a JSON object".into(),
            })?;

        let schema = root
            .get("schema")
            .and_then(|v| v.as_str())
            .ok_or_else(|| ImportError::NotABundle {
                path: bundle_path.display().to_string(),
                reason: "missing 'schema' field".into(),
            })?;
        validate_schema(schema, bundle_path)?;

        let bundle = deserialize_bundle(&root, schema).map_err(|r| ImportError::NotABundle {
            path: bundle_path.display().to_string(),
            reason: r,
        })?;

        let transcript_store = SessionTranscriptStore::new(&self.working_dir);
        // Mint a new id if the bundle's id already exists locally.
        let target_id = if transcript_store.load(&bundle.id).await.is_some() {
            ids::new_id()
        } else {
            bundle.id.clone()
        };

        let messages: Vec<Message> = bundle
            .turns
            .iter()
            .map(|t| {
                let role = if t.role.eq_ignore_ascii_case("assistant") {
                    Role::Assistant
                } else {
                    Role::User
                };
                Message::new(role, t.blocks.clone())
            })
            .collect();

        transcript_store
            .save(&target_id, &messages, bundle.system_prompt_override.as_deref())
            .await
            .map_err(|e| ImportError::Io(e.to_string()))?;

        // Replay audit turns verbatim.
        if !bundle.audit_turns.is_empty() {
            let audit_store = SessionAuditStore::new(&self.working_dir);
            for turn in &bundle.audit_turns {
                audit_store.append_turn(&target_id, turn).await;
            }
        }

        Ok(target_id)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Error type
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, thiserror::Error)]
pub enum ImportError {
    #[error("I/O error: {0}")]
    Io(String),
    #[error("'{path}' is not a valid session bundle: {reason}")]
    NotABundle { path: String, reason: String },
    #[error("unsupported schema '{schema}': only major version {SUPPORTED_SCHEMA_MAJOR} is supported")]
    UnsupportedSchema { schema: String },
}

// ─────────────────────────────────────────────────────────────────────────────
// Validation
// ─────────────────────────────────────────────────────────────────────────────

fn validate_schema(schema: &str, path: &Path) -> Result<(), ImportError> {
    if !schema.starts_with(SCHEMA_PREFIX) {
        return Err(ImportError::NotABundle {
            path: path.display().to_string(),
            reason: format!("unrecognised schema '{schema}'; expected '{SCHEMA_PREFIX}*'"),
        });
    }
    let version_part = &schema[SCHEMA_PREFIX.len()..];
    let major_digits: String = version_part.chars().take_while(|c| c.is_ascii_digit()).collect();
    let major: u32 = major_digits.parse().unwrap_or(0);
    if major != SUPPORTED_SCHEMA_MAJOR {
        return Err(ImportError::UnsupportedSchema { schema: schema.to_owned() });
    }
    Ok(())
}

// ─────────────────────────────────────────────────────────────────────────────
// Serialization
// ─────────────────────────────────────────────────────────────────────────────

fn serialize_bundle(bundle: &SessionBundle) -> Value {
    let turns_v: Vec<Value> = bundle
        .turns
        .iter()
        .map(|t| {
            json!({
                "role": t.role,
                "blocks": serialize_blocks(&t.blocks),
            })
        })
        .collect();

    let audit_turns_v: Vec<Value> = bundle.audit_turns.iter().map(serialize_audit_turn).collect();

    let mut root = json!({
        "schema": bundle.schema,
        "codaVersion": bundle.coda_version,
        "exportedUtc": bundle.exported_utc.to_rfc3339(),
        "id": bundle.id,
        "createdUtc": bundle.created_utc.to_rfc3339(),
        "provider": bundle.provider,
        "model": bundle.model,
        "auditAvailable": bundle.audit_available,
        "systemPrompt": bundle.system_prompt,
        "toolDefs": serialize_tool_defs(&bundle.tool_defs),
        "turns": turns_v,
        "auditTurns": audit_turns_v,
    });

    if let Some(ref sp) = bundle.system_prompt_override {
        root["systemPromptOverride"] = json!(sp);
    }
    root
}

fn serialize_audit_turn(t: &AuditTurn) -> Value {
    let tool_calls_v: Vec<Value> = t
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

    json!({
        "turnIndex": t.turn_index,
        "tsUtc": t.ts_utc.to_rfc3339(),
        "provider": t.provider,
        "model": t.model,
        "usage": { "in": t.input_tokens, "out": t.output_tokens },
        "stopReason": t.stop_reason,
        "toolCalls": tool_calls_v,
        "systemPrompt": t.system_prompt,
        "toolDefs": serialize_tool_defs(&t.tool_defs),
    })
}

fn deserialize_bundle(root: &Value, schema: &str) -> Result<SessionBundle, String> {
    let obj = root.as_object().ok_or("not a JSON object")?;

    let id = obj
        .get("id")
        .and_then(|v| v.as_str())
        .ok_or("missing 'id'")?
        .to_owned();

    let coda_version = obj
        .get("codaVersion")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_owned();

    let exported_utc = parse_dt(obj.get("exportedUtc")).unwrap_or_else(Utc::now);
    let created_utc = parse_dt(obj.get("createdUtc")).unwrap_or(exported_utc);
    let provider = obj.get("provider").and_then(|v| v.as_str()).map(|s| s.to_owned());
    let model = obj.get("model").and_then(|v| v.as_str()).map(|s| s.to_owned());
    let audit_available = obj.get("auditAvailable").and_then(|v| v.as_bool()).unwrap_or(false);
    let system_prompt = obj.get("systemPrompt").and_then(|v| v.as_str()).map(|s| s.to_owned());
    let system_prompt_override =
        obj.get("systemPromptOverride").and_then(|v| v.as_str()).map(|s| s.to_owned());
    let tool_defs = obj
        .get("toolDefs")
        .map(|v| deserialize_tool_defs(v))
        .unwrap_or_default();

    let turns_raw = obj.get("turns").cloned().unwrap_or(Value::Array(vec![]));
    let turns = deserialize_turns(&turns_raw);

    let audit_turns_raw = obj.get("auditTurns").cloned().unwrap_or(Value::Array(vec![]));
    let audit_turns = deserialize_audit_turns_from_bundle(&audit_turns_raw);

    Ok(SessionBundle {
        schema: schema.to_owned(),
        coda_version,
        exported_utc,
        id,
        created_utc,
        provider,
        model,
        audit_available,
        system_prompt,
        system_prompt_override,
        tool_defs,
        turns,
        audit_turns,
    })
}

fn deserialize_turns(arr: &Value) -> Vec<BundleTurn> {
    let Some(arr) = arr.as_array() else { return Vec::new() };
    arr.iter()
        .filter_map(|v| {
            let obj = v.as_object()?;
            let role = obj.get("role")?.as_str()?.to_owned();
            let blocks_raw = obj.get("blocks").cloned().unwrap_or(Value::Array(vec![]));
            let blocks = deserialize_blocks(&blocks_raw);
            Some(BundleTurn { role, blocks })
        })
        .collect()
}

fn deserialize_audit_turns_from_bundle(arr: &Value) -> Vec<AuditTurn> {
    let Some(arr) = arr.as_array() else { return Vec::new() };
    let mut current_system_prompt: Option<String> = None;
    let mut current_tool_defs: Vec<ToolDefinition> = Vec::new();

    arr.iter()
        .filter_map(|v| {
            let obj = v.as_object()?;
            let turn_index = obj.get("turnIndex")?.as_u64()? as u32;
            let ts_utc =
                parse_dt(obj.get("tsUtc")).unwrap_or(DateTime::<Utc>::from(std::time::UNIX_EPOCH));
            let provider = obj.get("provider")?.as_str()?.to_owned();
            let model = obj.get("model")?.as_str()?.to_owned();
            let usage = obj.get("usage")?.as_object()?;
            let input_tokens = usage.get("in")?.as_u64()? as u32;
            let output_tokens = usage.get("out")?.as_u64()? as u32;
            let stop_reason =
                obj.get("stopReason").and_then(|v| v.as_str()).map(|s| s.to_owned());

            let tool_calls: Vec<AuditToolCall> = obj
                .get("toolCalls")
                .and_then(|v| v.as_array())
                .map(|arr| {
                    arr.iter()
                        .filter_map(|v| {
                            let o = v.as_object()?;
                            Some(AuditToolCall {
                                name: o.get("name")?.as_str()?.to_owned(),
                                input: o
                                    .get("input")
                                    .and_then(|v| v.as_str())
                                    .unwrap_or("")
                                    .to_owned(),
                                result: o
                                    .get("result")
                                    .and_then(|v| v.as_str())
                                    .map(|s| s.to_owned()),
                                is_error: o
                                    .get("isError")
                                    .and_then(|v| v.as_bool())
                                    .unwrap_or(false),
                                call_id: o
                                    .get("callId")
                                    .and_then(|v| v.as_str())
                                    .map(|s| s.to_owned()),
                                status: o
                                    .get("status")
                                    .and_then(|v| v.as_str())
                                    .map(|s| s.to_owned()),
                            })
                        })
                        .collect()
                })
                .unwrap_or_default();

            // Carry forward systemPrompt/toolDefs.
            if let Some(sp) = obj.get("systemPrompt").and_then(|v| v.as_str()) {
                current_system_prompt = Some(sp.to_owned());
            }
            if let Some(td) = obj.get("toolDefs") {
                current_tool_defs = deserialize_tool_defs(td);
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
                system_prompt: current_system_prompt.clone(),
                tool_defs: current_tool_defs.clone(),
            })
        })
        .collect()
}

fn parse_dt(v: Option<&Value>) -> Option<DateTime<Utc>> {
    let s = v?.as_str()?;
    DateTime::parse_from_rfc3339(s).ok().map(|dt| dt.with_timezone(&Utc))
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    async fn make_session(dir: &tempfile::TempDir, id: &str, msgs: &[Message]) {
        let store = SessionTranscriptStore::new(dir.path());
        store.save(id, msgs, None).await.unwrap();
    }

    #[tokio::test]
    async fn export_returns_none_for_missing_session() {
        let dir = tempfile::tempdir().unwrap();
        let svc = SessionBundleService::new(dir.path(), "0.1.0");
        assert!(svc.export("notexist123456", Utc::now()).await.is_none());
    }

    #[tokio::test]
    async fn export_and_import_round_trip() {
        // Export from one directory, import into a fresh directory — no collision,
        // so the bundle's id is preserved.
        let export_dir = tempfile::tempdir().unwrap();
        let import_dir = tempfile::tempdir().unwrap();

        let export_svc = SessionBundleService::new(export_dir.path(), "0.1.0");
        let import_svc = SessionBundleService::new(import_dir.path(), "0.1.0");

        let id = "abc123456789";
        let msgs = vec![Message::user("hello"), Message::assistant("world")];
        make_session(&export_dir, id, &msgs).await;

        let bundle = export_svc.export(id, Utc::now()).await.unwrap();
        assert_eq!(bundle.turns.len(), 2);

        let bundle_path = export_dir.path().join("export.coda-session.json");
        export_svc.write_bundle(&bundle, &bundle_path, false).await.unwrap();

        // Import into a fresh directory — no collision, id is preserved.
        let imported_id = import_svc.import_bundle(&bundle_path).await.unwrap();
        assert_eq!(imported_id, id, "no collision: id must be preserved");

        let store = SessionTranscriptStore::new(import_dir.path());
        let loaded = store.load(id).await.unwrap();
        assert_eq!(loaded.len(), 2);
        assert_eq!(loaded[0].text(), "hello");
    }

    #[tokio::test]
    async fn import_mints_new_id_on_collision() {
        let dir = tempfile::tempdir().unwrap();
        let svc = SessionBundleService::new(dir.path(), "0.1.0");
        let id = "abc123456789";
        let msgs = vec![Message::user("hi")];
        make_session(&dir, id, &msgs).await;

        // Export, then import — collision, so new id.
        let bundle = svc.export(id, Utc::now()).await.unwrap();
        let bundle_path = dir.path().join("exp.coda-session.json");
        svc.write_bundle(&bundle, &bundle_path, false).await.unwrap();

        let new_id = svc.import_bundle(&bundle_path).await.unwrap();
        assert_ne!(new_id, id, "collision must mint a new id");
    }

    #[tokio::test]
    async fn import_rejects_missing_schema() {
        let dir = tempfile::tempdir().unwrap();
        let svc = SessionBundleService::new(dir.path(), "0.1.0");
        let bad_path = dir.path().join("bad.json");
        tokio::fs::write(&bad_path, br#"{"id":"x"}"#).await.unwrap();
        let err = svc.import_bundle(&bad_path).await.unwrap_err();
        assert!(matches!(err, ImportError::NotABundle { .. }));
    }

    #[tokio::test]
    async fn import_rejects_unsupported_schema_version() {
        let dir = tempfile::tempdir().unwrap();
        let svc = SessionBundleService::new(dir.path(), "0.1.0");
        let bad_path = dir.path().join("bad.json");
        tokio::fs::write(&bad_path, br#"{"schema":"coda.session/99","id":"x","turns":[]}"#)
            .await
            .unwrap();
        let err = svc.import_bundle(&bad_path).await.unwrap_err();
        assert!(matches!(err, ImportError::UnsupportedSchema { .. }));
    }

    #[tokio::test]
    async fn import_rejects_not_json_object() {
        let dir = tempfile::tempdir().unwrap();
        let svc = SessionBundleService::new(dir.path(), "0.1.0");
        let bad_path = dir.path().join("bad.json");
        tokio::fs::write(&bad_path, b"not json at all").await.unwrap();
        let err = svc.import_bundle(&bad_path).await.unwrap_err();
        assert!(matches!(err, ImportError::NotABundle { .. }));
    }
}
