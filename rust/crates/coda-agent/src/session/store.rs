//! Persists conversation transcripts to `<working_dir>/.coda/sessions/<id>.json`.
//!
//! Mirrors C# `Coda.Sdk.SessionTranscriptStore` exactly in:
//! - on-disk location
//! - JSON format (`{id, createdUtc, messages, systemPromptOverride?}`)
//! - atomic write (temp-then-rename; stray `.tmp` removed on failure)
//! - corrupt-tolerant load (returns `None`, never panics)
//! - `createdUtc` is preserved across incremental saves

use std::path::{Path, PathBuf};

use chrono::{DateTime, Utc};
use coda_llm::Message;
use serde_json::{Value, json};

use super::ids;
use super::message_json;

// ─────────────────────────────────────────────────────────────────────────────
// Public types
// ─────────────────────────────────────────────────────────────────────────────

/// A loaded session: its messages plus any optional metadata.
#[derive(Debug)]
pub struct StoredSession {
    pub messages: Vec<Message>,
    /// When present, overrides the default system prompt for the session.
    pub system_prompt_override: Option<String>,
}

/// Lightweight description of one persisted session, returned by
/// [`SessionTranscriptStore::list`].
#[derive(Debug, Clone)]
pub struct SessionSummary {
    pub id: String,
    pub created_utc: DateTime<Utc>,
    pub message_count: usize,
    pub preview: String,
}

// ─────────────────────────────────────────────────────────────────────────────
// Store
// ─────────────────────────────────────────────────────────────────────────────

/// Persists and loads conversation transcripts.
pub struct SessionTranscriptStore {
    working_dir: PathBuf,
}

impl SessionTranscriptStore {
    pub fn new(working_dir: impl Into<PathBuf>) -> Self {
        Self { working_dir: working_dir.into() }
    }

    fn sessions_dir(&self) -> PathBuf {
        self.working_dir.join(".coda").join("sessions")
    }

    fn file_path(&self, session_id: &str) -> PathBuf {
        self.sessions_dir().join(format!("{session_id}.json"))
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// Persist `messages` for `session_id`.
    ///
    /// Skips writing when the message list is empty or the id is invalid.
    /// Preserves the original `createdUtc` so incremental saves do not reset
    /// the session's timestamp.  Uses an atomic temp-then-rename write so a
    /// crash mid-write cannot corrupt an existing transcript.
    pub async fn save(
        &self,
        session_id: &str,
        messages: &[Message],
        system_prompt_override: Option<&str>,
    ) -> std::io::Result<()> {
        if messages.is_empty() || !ids::is_valid(session_id) {
            return Ok(());
        }

        let path = self.file_path(session_id);
        let created_utc = resolve_created_utc(&path).await;

        let mut root = json!({
            "id": session_id,
            "createdUtc": created_utc.to_rfc3339(),
            "messages": message_json::serialize_messages(messages),
        });

        if let Some(override_prompt) = system_prompt_override {
            root["systemPromptOverride"] = json!(override_prompt);
        }

        let text = serde_json::to_string(&root)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;

        write_atomic(&path, &text).await
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// Load the messages for `session_id`, or `None` if not found / corrupt.
    pub async fn load(&self, session_id: &str) -> Option<Vec<Message>> {
        self.load_session(session_id).await.map(|s| s.messages)
    }

    /// Load the full stored session (messages + metadata), or `None` if not
    /// found, invalid id, or corrupt file.  Never panics.
    pub async fn load_session(&self, session_id: &str) -> Option<StoredSession> {
        if !ids::is_valid(session_id) {
            return None;
        }
        let path = self.file_path(session_id);
        if !path.exists() {
            return None;
        }
        load_session_from_path(&path).await
    }

    // ── List ──────────────────────────────────────────────────────────────────

    /// Return summaries of all persisted sessions, newest first.
    /// Corrupt or unreadable files are silently skipped.
    pub fn list(&self) -> Vec<SessionSummary> {
        let dir = self.sessions_dir();
        if !dir.is_dir() {
            return Vec::new();
        }

        let mut summaries = Vec::new();
        let entries = match std::fs::read_dir(&dir) {
            Ok(e) => e,
            Err(_) => return Vec::new(),
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("json") {
                continue;
            }
            if let Some(s) = read_summary_from_path(&path) {
                summaries.push(s);
            }
        }
        summaries.sort_by(|a, b| b.created_utc.cmp(&a.created_utc));
        summaries
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Private helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Resolve the `createdUtc` to use for a save: the value already on disk (so
/// it persists across incremental saves) or `Utc::now()` for a brand-new
/// session.  Corrupt existing files fall back to now.
async fn resolve_created_utc(path: &Path) -> DateTime<Utc> {
    if path.exists() {
        if let Ok(text) = tokio::fs::read_to_string(path).await {
            if let Ok(v) = serde_json::from_str::<Value>(&text) {
                if let Some(raw) = v.get("createdUtc").and_then(|v| v.as_str()) {
                    if let Ok(dt) = DateTime::parse_from_rfc3339(raw) {
                        return dt.with_timezone(&Utc);
                    }
                }
            }
        }
    }
    Utc::now()
}

/// Load a stored session from an arbitrary path (used by load_session and bundle import).
pub(super) async fn load_session_from_path(path: &Path) -> Option<StoredSession> {
    let text = tokio::fs::read_to_string(path).await.ok()?;
    let root: Value = serde_json::from_str(&text).ok()?;
    let messages_raw = root.get("messages")?;
    let messages = message_json::deserialize_messages(messages_raw);
    let system_prompt_override = root
        .get("systemPromptOverride")
        .and_then(|v| v.as_str())
        .map(|s| s.to_owned());
    Some(StoredSession { messages, system_prompt_override })
}

fn read_summary_from_path(path: &Path) -> Option<SessionSummary> {
    let text = std::fs::read_to_string(path).ok()?;
    let root: Value = serde_json::from_str(&text).ok()?;

    let id = root
        .get("id")
        .and_then(|v| v.as_str())
        .map(|s| s.to_owned())
        .or_else(|| {
            path.file_stem()
                .and_then(|s| s.to_str())
                .map(|s| s.to_owned())
        })?;

    let created_utc = root
        .get("createdUtc")
        .and_then(|v| v.as_str())
        .and_then(|s| DateTime::parse_from_rfc3339(s).ok())
        .map(|dt| dt.with_timezone(&Utc))
        .unwrap_or_else(|| {
            path.metadata()
                .and_then(|m| m.modified())
                .map(|t| DateTime::<Utc>::from(t))
                .unwrap_or_else(|_| Utc::now())
        });

    let messages_arr = root.get("messages").and_then(|v| v.as_array());
    let message_count = messages_arr.map(|a| a.len()).unwrap_or(0);
    let preview = extract_preview(messages_arr);

    Some(SessionSummary { id, created_utc, message_count, preview })
}

fn extract_preview(messages_arr: Option<&Vec<Value>>) -> String {
    let arr = match messages_arr {
        Some(a) => a,
        None => return String::new(),
    };
    for msg in arr {
        let obj = match msg.as_object() {
            Some(o) => o,
            None => continue,
        };
        let role = obj.get("role").and_then(|v| v.as_str()).unwrap_or("");
        if !role.eq_ignore_ascii_case("user") {
            continue;
        }
        let blocks = match obj.get("blocks").and_then(|v| v.as_array()) {
            Some(b) => b,
            None => continue,
        };
        for block in blocks {
            let bobj = match block.as_object() {
                Some(o) => o,
                None => continue,
            };
            if bobj.get("type").and_then(|v| v.as_str()) == Some("text") {
                let text = bobj.get("text").and_then(|v| v.as_str()).unwrap_or("");
                return if text.len() <= 80 { text.to_owned() } else { text[..80].to_owned() };
            }
        }
    }
    String::new()
}

/// Atomic write: serialize to a temp file then rename over the target.
/// Removes the temp file if the rename fails so no stray `.tmp` accumulates.
pub(super) async fn write_atomic(path: &Path, text: &str) -> std::io::Result<()> {
    let parent = path.parent().unwrap_or(Path::new("."));
    tokio::fs::create_dir_all(parent).await?;

    let stem = path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("session");
    let temp_name = format!(".{stem}.{}.tmp", uuid::Uuid::new_v4().simple());
    let temp = parent.join(&temp_name);

    let result: std::io::Result<()> = async {
        tokio::fs::write(&temp, text.as_bytes()).await?;
        tokio::fs::rename(&temp, path).await
    }
    .await;

    if result.is_err() {
        let _ = tokio::fs::remove_file(&temp).await;
    }
    result
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use coda_llm::{Content, Correlation, Message, Role};

    fn make_store(dir: &tempfile::TempDir) -> SessionTranscriptStore {
        SessionTranscriptStore::new(dir.path())
    }

    #[tokio::test]
    async fn save_and_load_round_trip() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        let msgs = vec![Message::user("hello"), Message::assistant("world")];
        store.save("abc123456789", &msgs, None).await.unwrap();
        let loaded = store.load("abc123456789").await.unwrap();
        assert_eq!(loaded.len(), 2);
        assert_eq!(loaded[0].text(), "hello");
        assert_eq!(loaded[1].text(), "world");
    }

    #[tokio::test]
    async fn empty_messages_do_not_create_file() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        store.save("abc123456789", &[], None).await.unwrap();
        assert!(store.load("abc123456789").await.is_none());
    }

    #[tokio::test]
    async fn invalid_id_does_not_create_file() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        let msgs = vec![Message::user("hi")];
        store.save("../evil", &msgs, None).await.unwrap();
        // nothing was written
        assert!(!dir.path().join(".coda").join("sessions").join("..").join("evil.json").exists());
    }

    #[tokio::test]
    async fn load_nonexistent_returns_none() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        assert!(store.load("nothere123456").await.is_none());
    }

    #[tokio::test]
    async fn corrupt_file_returns_none() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        let sessions_dir = dir.path().join(".coda").join("sessions");
        tokio::fs::create_dir_all(&sessions_dir).await.unwrap();
        tokio::fs::write(sessions_dir.join("abc123456789.json"), b"not json").await.unwrap();
        assert!(store.load("abc123456789").await.is_none());
    }

    #[tokio::test]
    async fn created_utc_preserved_across_saves() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        let id = "abc123456789";
        let msgs = vec![Message::user("first")];
        store.save(id, &msgs, None).await.unwrap();

        let path = dir.path().join(".coda").join("sessions").join(format!("{id}.json"));
        let v: Value = serde_json::from_str(&tokio::fs::read_to_string(&path).await.unwrap()).unwrap();
        let ts1 = v["createdUtc"].as_str().unwrap().to_owned();

        // Second save should preserve createdUtc.
        let msgs2 = vec![Message::user("first"), Message::assistant("second")];
        store.save(id, &msgs2, None).await.unwrap();

        let v2: Value = serde_json::from_str(&tokio::fs::read_to_string(&path).await.unwrap()).unwrap();
        let ts2 = v2["createdUtc"].as_str().unwrap().to_owned();
        assert_eq!(ts1, ts2, "createdUtc must be preserved across incremental saves");
    }

    #[tokio::test]
    async fn system_prompt_override_round_trips() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        let msgs = vec![Message::user("hello")];
        store.save("abc123456789", &msgs, Some("custom prompt")).await.unwrap();
        let stored = store.load_session("abc123456789").await.unwrap();
        assert_eq!(stored.system_prompt_override.as_deref(), Some("custom prompt"));
    }

    #[tokio::test]
    async fn list_returns_sessions_newest_first() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        store.save("aaa111111111", &[Message::user("first")], None).await.unwrap();
        tokio::time::sleep(std::time::Duration::from_millis(20)).await;
        store.save("bbb222222222", &[Message::user("second")], None).await.unwrap();

        let list = store.list();
        assert_eq!(list.len(), 2);
        // Newer session must appear first.
        assert_eq!(list[0].id, "bbb222222222");
        assert_eq!(list[1].id, "aaa111111111");
    }

    #[tokio::test]
    async fn list_empty_when_no_sessions() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        assert!(store.list().is_empty());
    }

    #[tokio::test]
    async fn list_skips_corrupt_files() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        let sdir = dir.path().join(".coda").join("sessions");
        tokio::fs::create_dir_all(&sdir).await.unwrap();
        tokio::fs::write(sdir.join("corrupt123456.json"), b"{bad}").await.unwrap();
        store.save("good1234567a", &[Message::user("hi")], None).await.unwrap();
        let list = store.list();
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].id, "good1234567a");
    }

    #[tokio::test]
    async fn preview_extracted_from_first_user_text_block() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        let msgs = vec![
            Message::new(
                Role::User,
                vec![Content::ToolResult {
                    tool_use_id: "t".into(),
                    content: "not preview".into(),
                    is_error: false,
                    correlation: Correlation::default(),
                    status: None,
                }],
            ),
            Message::user("actual preview text"),
        ];
        store.save("abc123456789", &msgs, None).await.unwrap();
        let list = store.list();
        assert_eq!(list[0].preview, "actual preview text");
    }

    #[tokio::test]
    async fn invalid_id_load_returns_none() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        // path traversal id
        assert!(store.load("../evil").await.is_none());
        assert!(store.load("../../etc/passwd").await.is_none());
    }

    #[tokio::test]
    async fn tool_use_and_result_round_trip_in_transcript() {
        let dir = tempfile::tempdir().unwrap();
        let store = make_store(&dir);
        let id = "abc123456789";
        let msgs = vec![
            Message::user("do thing"),
            Message::new(
                Role::Assistant,
                vec![Content::ToolUse {
                    id: "tu1".into(),
                    name: "shell".into(),
                    input_json: r#"{"cmd":"ls"}"#.into(),
                    correlation: Correlation::default(),
                }],
            ),
            Message::new(
                Role::User,
                vec![Content::ToolResult {
                    tool_use_id: "tu1".into(),
                    content: "file1\nfile2".into(),
                    is_error: false,
                    correlation: Correlation::default(),
                    status: None,
                }],
            ),
        ];
        store.save(id, &msgs, None).await.unwrap();
        let loaded = store.load(id).await.unwrap();
        assert_eq!(loaded.len(), 3);
        assert!(matches!(&loaded[1].content[0], Content::ToolUse { name, .. } if name == "shell"));
        assert!(matches!(&loaded[2].content[0], Content::ToolResult { tool_use_id, .. } if tool_use_id == "tu1"));
    }
}
