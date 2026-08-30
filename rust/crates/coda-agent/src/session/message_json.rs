//! JSON (de)serialization for `coda_llm::Message` / `Content`.
//!
//! Mirrors C# `ChatMessageJson` and `AuditJson` so the Rust engine can read
//! transcripts and bundles written by the C# engine and vice-versa.
//!
//! ## On-disk message format
//! ```json
//! { "role": "user" | "assistant", "blocks": [...] }
//! ```
//!
//! ## Block shapes
//! | C# / common type     | JSON `"type"` | key fields |
//! |----------------------|---------------|------------|
//! | TextBlock            | `"text"`      | `"text"` |
//! | ToolUseBlock         | `"tool_use"`  | `"id"`, `"name"`, `"input"`, optional correlation |
//! | ToolResultBlock      | `"tool_result"` | `"toolUseId"`, `"content"`, `"isError"`, optional fields |
//! | Image (Rust only)    | `"image"`     | `"mediaType"`, `"base64"` |
//! | Thinking (Rust only) | `"thinking"`  | `"text"`, optional `"signature"` |
//! | RedactedThinking     | `"redacted_thinking"` | `"data"` |
//!
//! Unknown block types are silently dropped on deserialization, matching C#.
//!
//! ## Correlation (optional fields on tool_use and tool_result)
//! `"rootTurnId"`, `"activityId"`, `"sourceId"` — absent when not set.
//!
//! ## Tool-result status
//! `"toolStatus"` — absent when not set (matches C# `ToolResultBlock.ToolStatus`).

use serde_json::{Map, Value, json};

use coda_llm::{Content, Correlation, Message, Role, ToolDefinition};

// ─────────────────────────────────────────────────────────────────────────────
// Messages
// ─────────────────────────────────────────────────────────────────────────────

/// Serialise a slice of messages to a JSON array matching the C# transcript format.
pub fn serialize_messages(messages: &[Message]) -> Value {
    let arr: Vec<Value> = messages.iter().map(serialize_message).collect();
    Value::Array(arr)
}

fn serialize_message(msg: &Message) -> Value {
    json!({
        "role": role_str(msg.role),
        "blocks": serialize_blocks(&msg.content),
    })
}

/// Deserialise messages from a JSON array. Skips malformed entries, never panics.
pub fn deserialize_messages(arr: &Value) -> Vec<Message> {
    let Some(arr) = arr.as_array() else { return Vec::new() };
    arr.iter().filter_map(deserialize_message).collect()
}

fn deserialize_message(v: &Value) -> Option<Message> {
    let obj = v.as_object()?;
    let role = match obj.get("role")?.as_str()? {
        r if r.eq_ignore_ascii_case("assistant") => Role::Assistant,
        _ => Role::User,
    };
    let blocks_raw = obj.get("blocks").cloned().unwrap_or(Value::Array(vec![]));
    let content = deserialize_blocks(&blocks_raw);
    Some(Message::new(role, content))
}

// ─────────────────────────────────────────────────────────────────────────────
// Blocks
// ─────────────────────────────────────────────────────────────────────────────

/// Serialise a slice of `Content` blocks to a JSON array.
pub fn serialize_blocks(blocks: &[Content]) -> Value {
    let arr: Vec<Value> = blocks.iter().map(serialize_block).collect();
    Value::Array(arr)
}

fn serialize_block(block: &Content) -> Value {
    match block {
        Content::Text(t) => json!({ "type": "text", "text": t }),
        Content::ToolUse { id, name, input_json, correlation } => {
            let mut obj = Map::new();
            obj.insert("type".into(), json!("tool_use"));
            obj.insert("id".into(), json!(id));
            obj.insert("name".into(), json!(name));
            obj.insert("input".into(), json!(input_json));
            set_correlation(&mut obj, correlation);
            Value::Object(obj)
        }
        Content::ToolResult { tool_use_id, content, is_error, correlation, status } => {
            let mut obj = Map::new();
            obj.insert("type".into(), json!("tool_result"));
            obj.insert("toolUseId".into(), json!(tool_use_id));
            obj.insert("content".into(), json!(content));
            obj.insert("isError".into(), json!(is_error));
            set_correlation(&mut obj, correlation);
            if let Some(s) = status {
                obj.insert("toolStatus".into(), json!(s));
            }
            Value::Object(obj)
        }
        Content::Image { media_type, base64 } => {
            json!({ "type": "image", "mediaType": media_type, "base64": base64 })
        }
        Content::Thinking { text, signature } => {
            let mut obj = Map::new();
            obj.insert("type".into(), json!("thinking"));
            obj.insert("text".into(), json!(text));
            if let Some(sig) = signature {
                obj.insert("signature".into(), json!(sig));
            }
            Value::Object(obj)
        }
        Content::RedactedThinking { data } => {
            json!({ "type": "redacted_thinking", "data": data })
        }
    }
}

/// Deserialise blocks from a JSON array. Unknown types are silently dropped.
pub fn deserialize_blocks(arr: &Value) -> Vec<Content> {
    let Some(arr) = arr.as_array() else { return Vec::new() };
    arr.iter().filter_map(deserialize_block).collect()
}

fn deserialize_block(v: &Value) -> Option<Content> {
    let obj = v.as_object()?;
    let block_type = obj.get("type")?.as_str()?;
    match block_type {
        "text" => {
            let text = obj.get("text")?.as_str()?.to_owned();
            Some(Content::Text(text))
        }
        "tool_use" => {
            let id = str_field(obj, "id");
            let name = str_field(obj, "name");
            let input_json = str_field(obj, "input");
            let correlation = read_correlation(obj);
            Some(Content::ToolUse { id, name, input_json, correlation })
        }
        "tool_result" => {
            let tool_use_id = str_field(obj, "toolUseId");
            let content = str_field(obj, "content");
            let is_error = obj.get("isError").and_then(|v| v.as_bool()).unwrap_or(false);
            let correlation = read_correlation(obj);
            let status = opt_str(obj, "toolStatus");
            Some(Content::ToolResult { tool_use_id, content, is_error, correlation, status })
        }
        "image" => {
            let media_type = str_field(obj, "mediaType");
            let base64 = str_field(obj, "base64");
            Some(Content::Image { media_type, base64 })
        }
        "thinking" => {
            let text = str_field(obj, "text");
            let signature = opt_str(obj, "signature");
            Some(Content::Thinking { text, signature })
        }
        "redacted_thinking" => {
            let data = str_field(obj, "data");
            Some(Content::RedactedThinking { data })
        }
        _ => None,
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tool definitions (AuditJson.SerializeToolDefs / DeserializeToolDefs)
// ─────────────────────────────────────────────────────────────────────────────

pub fn serialize_tool_defs(defs: &[ToolDefinition]) -> Value {
    let arr: Vec<Value> = defs
        .iter()
        .map(|d| {
            json!({
                "name": d.name,
                "description": d.description,
                "inputSchema": d.input_schema_json,
            })
        })
        .collect();
    Value::Array(arr)
}

pub fn deserialize_tool_defs(arr: &Value) -> Vec<ToolDefinition> {
    let Some(arr) = arr.as_array() else { return Vec::new() };
    arr.iter()
        .filter_map(|v| {
            let obj = v.as_object()?;
            Some(ToolDefinition::new(
                str_field(obj, "name"),
                str_field(obj, "description"),
                str_field(obj, "inputSchema"),
            ))
        })
        .collect()
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

fn role_str(role: Role) -> &'static str {
    match role {
        Role::User => "user",
        Role::Assistant => "assistant",
    }
}

fn str_field(obj: &Map<String, Value>, key: &str) -> String {
    obj.get(key).and_then(|v| v.as_str()).unwrap_or("").to_owned()
}

fn opt_str(obj: &Map<String, Value>, key: &str) -> Option<String> {
    obj.get(key)?.as_str().map(|s| s.to_owned())
}

fn set_correlation(obj: &mut Map<String, Value>, c: &Correlation) {
    if let Some(ref v) = c.root_turn_id {
        obj.insert("rootTurnId".into(), json!(v));
    }
    if let Some(ref v) = c.activity_id {
        obj.insert("activityId".into(), json!(v));
    }
    if let Some(ref v) = c.source_id {
        obj.insert("sourceId".into(), json!(v));
    }
}

fn read_correlation(obj: &Map<String, Value>) -> Correlation {
    Correlation {
        root_turn_id: opt_str(obj, "rootTurnId"),
        activity_id: opt_str(obj, "activityId"),
        source_id: opt_str(obj, "sourceId"),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use coda_llm::{Correlation, Message, Role};

    fn round_trip(messages: &[Message]) -> Vec<Message> {
        let v = serialize_messages(messages);
        deserialize_messages(&v)
    }

    #[test]
    fn text_message_round_trips() {
        let msgs = vec![Message::user("hello"), Message::assistant("world")];
        let rt = round_trip(&msgs);
        assert_eq!(rt.len(), 2);
        assert_eq!(rt[0].role, Role::User);
        assert_eq!(rt[0].text(), "hello");
        assert_eq!(rt[1].role, Role::Assistant);
        assert_eq!(rt[1].text(), "world");
    }

    #[test]
    fn tool_use_round_trips() {
        let msgs = vec![Message::new(
            Role::Assistant,
            vec![Content::ToolUse {
                id: "t1".into(),
                name: "read_file".into(),
                input_json: r#"{"path":"a.txt"}"#.into(),
                correlation: Correlation {
                    root_turn_id: Some("r1".into()),
                    activity_id: None,
                    source_id: None,
                },
            }],
        )];
        let rt = round_trip(&msgs);
        match &rt[0].content[0] {
            Content::ToolUse { id, name, input_json, correlation } => {
                assert_eq!(id, "t1");
                assert_eq!(name, "read_file");
                assert_eq!(input_json, r#"{"path":"a.txt"}"#);
                assert_eq!(correlation.root_turn_id.as_deref(), Some("r1"));
            }
            other => panic!("expected ToolUse, got {other:?}"),
        }
    }

    #[test]
    fn tool_result_round_trips() {
        let msgs = vec![Message::new(
            Role::User,
            vec![Content::ToolResult {
                tool_use_id: "t1".into(),
                content: "ok".into(),
                is_error: false,
                correlation: Correlation::default(),
                status: Some("success".into()),
            }],
        )];
        let rt = round_trip(&msgs);
        match &rt[0].content[0] {
            Content::ToolResult { tool_use_id, content, is_error, status, .. } => {
                assert_eq!(tool_use_id, "t1");
                assert_eq!(content, "ok");
                assert!(!is_error);
                assert_eq!(status.as_deref(), Some("success"));
            }
            other => panic!("expected ToolResult, got {other:?}"),
        }
    }

    #[test]
    fn unknown_block_type_is_silently_dropped() {
        let raw = serde_json::json!([
            {"type": "text", "text": "keep"},
            {"type": "exotic_future_block", "data": "drop"},
            {"type": "text", "text": "also keep"},
        ]);
        let blocks = deserialize_blocks(&raw);
        assert_eq!(blocks.len(), 2);
        assert!(matches!(&blocks[0], Content::Text(t) if t == "keep"));
        assert!(matches!(&blocks[1], Content::Text(t) if t == "also keep"));
    }

    #[test]
    fn thinking_block_round_trips() {
        let msgs = vec![Message::new(
            Role::Assistant,
            vec![Content::Thinking {
                text: "reasoning".into(),
                signature: Some("sig123".into()),
            }],
        )];
        let rt = round_trip(&msgs);
        match &rt[0].content[0] {
            Content::Thinking { text, signature } => {
                assert_eq!(text, "reasoning");
                assert_eq!(signature.as_deref(), Some("sig123"));
            }
            other => panic!("expected Thinking, got {other:?}"),
        }
    }

    #[test]
    fn image_block_round_trips() {
        let msgs = vec![Message::new(
            Role::User,
            vec![Content::Image {
                media_type: "image/png".into(),
                base64: "abc==".into(),
            }],
        )];
        let rt = round_trip(&msgs);
        match &rt[0].content[0] {
            Content::Image { media_type, base64 } => {
                assert_eq!(media_type, "image/png");
                assert_eq!(base64, "abc==");
            }
            other => panic!("expected Image, got {other:?}"),
        }
    }

    #[test]
    fn empty_messages_serializes_to_empty_array() {
        let v = serialize_messages(&[]);
        assert_eq!(v, Value::Array(vec![]));
        let rt = deserialize_messages(&v);
        assert!(rt.is_empty());
    }

    #[test]
    fn deserialize_messages_on_non_array_returns_empty() {
        assert!(deserialize_messages(&json!(null)).is_empty());
        assert!(deserialize_messages(&json!({})).is_empty());
    }

    #[test]
    fn tool_defs_round_trip() {
        let defs = vec![ToolDefinition::new("write_file", "writes a file", r#"{"type":"object"}"#)];
        let v = serialize_tool_defs(&defs);
        let rt = deserialize_tool_defs(&v);
        assert_eq!(rt.len(), 1);
        assert_eq!(rt[0].name, "write_file");
        assert_eq!(rt[0].description, "writes a file");
        assert_eq!(rt[0].input_schema_json, r#"{"type":"object"}"#);
    }

    #[test]
    fn cs_compat_tool_use_json_is_readable() {
        // JSON that would be written by the C# engine.
        let raw = serde_json::json!([{
            "role": "assistant",
            "blocks": [{
                "type": "tool_use",
                "id": "toolu_01",
                "name": "read_file",
                "input": "{\"path\":\"x.txt\"}"
            }]
        }]);
        let msgs = deserialize_messages(&raw);
        assert_eq!(msgs.len(), 1);
        match &msgs[0].content[0] {
            Content::ToolUse { id, name, input_json, .. } => {
                assert_eq!(id, "toolu_01");
                assert_eq!(name, "read_file");
                assert_eq!(input_json, r#"{"path":"x.txt"}"#);
            }
            other => panic!("{other:?}"),
        }
    }
}
