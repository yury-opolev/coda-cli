//! OpenAI chat-completions protocol for Copilot's `/chat/completions` endpoint.
//!
//! Request building and streaming decoder. The decoder is pure so it can be
//! tested without a network. Tool call arguments arrive as string fragments
//! (`function.arguments`) that are only valid JSON when concatenated, so tool
//! calls are held back until `[DONE]` closes the stream rather than emitted
//! per-delta.

use std::collections::BTreeMap;

use serde_json::{json, Value};

use crate::anthropic::StreamEvent;
use crate::error::LlmError;
use crate::message::{ChatRequest, Content, Correlation, Role, Usage};

/// Builds an OpenAI chat-completions request body.
///
/// `max_tokens` is intentionally omitted: Copilot applies its own default and
/// an explicit cap caused premature `stop=max_tokens` truncations because it
/// also bounds reasoning tokens, leaving no room for the actual output.
pub fn build(request: &ChatRequest) -> Value {
    let mut messages = Vec::new();

    if let Some(system) = &request.system {
        messages.push(json!({ "role": "system", "content": system }));
    }

    for message in &request.messages {
        append_message(&mut messages, message);
    }

    let mut body = json!({
        "model": request.model,
        "stream": true,
        "messages": messages,
    });

    if !request.tools.is_empty() {
        let tools: Vec<Value> = request
            .tools
            .iter()
            .map(|tool| {
                let params: Value = serde_json::from_str(&tool.input_schema_json)
                    .unwrap_or_else(|_| json!({"type": "object"}));
                json!({
                    "type": "function",
                    "function": {
                        "name": tool.name,
                        "description": tool.description,
                        "parameters": params,
                    }
                })
            })
            .collect();
        body["tools"] = Value::Array(tools);
    }

    body
}

fn append_message(out: &mut Vec<Value>, message: &crate::message::Message) {
    if message.role == Role::User {
        // Tool results become separate `role:"tool"` messages so the model can
        // correlate them with the `tool_calls` on the preceding assistant turn.
        for block in &message.content {
            if let Content::ToolResult {
                tool_use_id,
                content,
                ..
            } = block
            {
                out.push(json!({
                    "role": "tool",
                    "tool_call_id": tool_use_id,
                    "content": content,
                }));
            }
        }
        let text = concat_text(&message.content);
        if !text.is_empty() {
            out.push(json!({ "role": "user", "content": text }));
        }
        return;
    }

    // Assistant: text content (null when empty) + optional tool_calls array.
    let text = concat_text(&message.content);
    let content_value: Value = if text.is_empty() {
        Value::Null
    } else {
        Value::String(text)
    };

    let tool_calls: Vec<Value> = message
        .content
        .iter()
        .filter_map(|b| {
            if let Content::ToolUse {
                id, name, input_json, ..
            } = b
            {
                // OpenAI expects the arguments as a JSON STRING, not an object.
                Some(json!({
                    "id": id,
                    "type": "function",
                    "function": { "name": name, "arguments": input_json }
                }))
            } else {
                None
            }
        })
        .collect();

    let mut msg = json!({ "role": "assistant", "content": content_value });
    if !tool_calls.is_empty() {
        msg["tool_calls"] = Value::Array(tool_calls);
    }
    out.push(msg);
}

/// Concatenates text blocks; images become a placeholder so the model knows
/// one was present rather than silently losing it.
///
/// Copilot's OpenAI-shaped endpoint does not support multimodal images, so a
/// text description is the best we can do on this path.
fn concat_text(content: &[Content]) -> String {
    let mut result = String::new();
    for block in content {
        match block {
            Content::Text(text) => result.push_str(text),
            Content::Image { media_type, .. } => {
                if !result.is_empty() {
                    result.push(' ');
                }
                result.push_str(&format!("[image attached: {media_type}]"));
            }
            _ => {}
        }
    }
    result
}

/// A tool call being assembled from streaming argument fragments.
#[derive(Debug, Default)]
struct PartialToolCall {
    id: String,
    name: String,
    arguments: String,
}

/// Decodes the OpenAI chat-completions streaming protocol.
///
/// Text deltas are emitted on arrival. Tool calls are accumulated by index and
/// only emitted when `flush()` is called on `[DONE]` because argument fragments
/// form valid JSON only when concatenated.
#[derive(Debug, Default)]
pub struct ChatDecoder {
    tool_calls: BTreeMap<usize, PartialToolCall>,
    stop_reason: Option<String>,
    usage: Usage,
    finished: bool,
}

impl ChatDecoder {
    pub fn new() -> Self {
        Self::default()
    }

    /// Whether `flush()` has been called (i.e., `[DONE]` was received).
    pub fn finished(&self) -> bool {
        self.finished
    }

    /// Decodes one SSE data payload (the string after `data:`, never `[DONE]`).
    pub fn decode(&mut self, data: &str) -> Result<Vec<StreamEvent>, LlmError> {
        let value: Value = serde_json::from_str(data)
            .map_err(|e| LlmError::Protocol(format!("invalid JSON in chat chunk: {e}")))?;

        // A usage-only chunk may arrive with no `choices`.
        if let Some(usage) = value.get("usage") {
            self.read_usage(usage);
        }

        let Some(choices) = value.get("choices").and_then(Value::as_array) else {
            return Ok(Vec::new());
        };
        let Some(choice) = choices.first() else {
            return Ok(Vec::new());
        };

        if let Some(reason) = choice
            .get("finish_reason")
            .and_then(Value::as_str)
            .filter(|s| !s.is_empty())
        {
            self.stop_reason = Some(map_finish_reason(reason));
        }

        let Some(delta) = choice.get("delta") else {
            return Ok(Vec::new());
        };

        let mut events = Vec::new();

        if let Some(text) = delta
            .get("content")
            .and_then(Value::as_str)
            .filter(|s| !s.is_empty())
        {
            events.push(StreamEvent::TextDelta(text.to_string()));
        }

        if let Some(tcs) = delta.get("tool_calls").and_then(Value::as_array) {
            for tc in tcs {
                self.accumulate(tc);
            }
        }

        Ok(events)
    }

    /// Called when `[DONE]` is received — flushes accumulated tool calls and
    /// emits `Done`.
    pub fn flush(&mut self) -> Vec<StreamEvent> {
        self.finished = true;

        let mut events: Vec<StreamEvent> = self
            .tool_calls
            .values()
            .map(|tc| {
                StreamEvent::ToolUse(Content::ToolUse {
                    id: tc.id.clone(),
                    name: tc.name.clone(),
                    input_json: if tc.arguments.trim().is_empty() {
                        "{}".to_string()
                    } else {
                        tc.arguments.clone()
                    },
                    correlation: Correlation::default(),
                })
            })
            .collect();

        events.push(StreamEvent::Done {
            stop_reason: self.stop_reason.clone(),
            usage: self.usage,
        });

        events
    }

    fn accumulate(&mut self, tc: &Value) {
        let index = tc.get("index").and_then(Value::as_u64).unwrap_or(0) as usize;
        let entry = self.tool_calls.entry(index).or_default();

        if let Some(id) = tc.get("id").and_then(Value::as_str) {
            entry.id = id.to_string();
        }
        if let Some(func) = tc.get("function") {
            if let Some(name) = func.get("name").and_then(Value::as_str) {
                entry.name = name.to_string();
            }
            if let Some(args) = func.get("arguments").and_then(Value::as_str) {
                entry.arguments.push_str(args);
            }
        }
    }

    fn read_usage(&mut self, usage: &Value) {
        // OpenAI: prompt_tokens is the TOTAL input; cached_tokens is a subset.
        // Subtract cached so TotalInputTokens = InputTokens + CacheReadTokens.
        let prompt = usage
            .get("prompt_tokens")
            .and_then(Value::as_u64)
            .unwrap_or(0) as u32;
        let completion = usage
            .get("completion_tokens")
            .and_then(Value::as_u64)
            .unwrap_or(0) as u32;
        let cached = usage
            .get("prompt_tokens_details")
            .and_then(|d| d.get("cached_tokens"))
            .and_then(Value::as_u64)
            .unwrap_or(0) as u32;
        let cached = cached.min(prompt);

        if prompt > 0 || completion > 0 {
            self.usage = Usage {
                input_tokens: prompt - cached,
                output_tokens: completion,
                cache_read_tokens: cached,
                ..Usage::ZERO
            };
        }
    }
}

fn map_finish_reason(reason: &str) -> String {
    match reason {
        "stop" => "end_turn",
        "tool_calls" => "tool_use",
        "length" => "max_tokens",
        other => other,
    }
    .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::message::{Correlation, Message, ToolDefinition};
    use serde_json::json;

    fn run(payloads: &[&str]) -> (Vec<StreamEvent>, ChatDecoder) {
        let mut decoder = ChatDecoder::new();
        let mut events = Vec::new();
        for &payload in payloads {
            if payload == "[DONE]" {
                events.extend(decoder.flush());
            } else {
                events.extend(decoder.decode(payload).expect("decode"));
            }
        }
        (events, decoder)
    }

    fn text_chunk(text: &str) -> String {
        json!({ "choices": [{ "delta": { "content": text }, "finish_reason": null }] }).to_string()
    }

    fn stop_chunk(reason: &str) -> String {
        json!({ "choices": [{ "delta": {}, "finish_reason": reason }] }).to_string()
    }

    #[test]
    fn builds_a_minimal_request() {
        let body = build(&ChatRequest::new("gpt-4o", vec![Message::user("hi")]));
        assert_eq!(body["model"], "gpt-4o");
        assert_eq!(body["stream"], true);
        assert_eq!(body["messages"][0]["role"], "user");
        assert_eq!(body["messages"][0]["content"], "hi");
    }

    #[test]
    fn includes_system_as_first_message() {
        let body = build(
            &ChatRequest::new("gpt-4o", vec![Message::user("hi")]).with_system("be brief"),
        );
        assert_eq!(body["messages"][0]["role"], "system");
        assert_eq!(body["messages"][0]["content"], "be brief");
        assert_eq!(body["messages"][1]["role"], "user");
    }

    #[test]
    fn omits_max_tokens_intentionally() {
        let body = build(&ChatRequest::new("gpt-4o", vec![Message::user("hi")]));
        assert!(body.get("max_tokens").is_none());
    }

    #[test]
    fn includes_tool_definitions_as_function_type() {
        let request = ChatRequest::new("gpt-4o", vec![Message::user("hi")]).with_tools(vec![
            ToolDefinition::new("read_file", "Read a file", r#"{"type":"object"}"#),
        ]);
        let body = build(&request);
        assert_eq!(body["tools"][0]["type"], "function");
        assert_eq!(body["tools"][0]["function"]["name"], "read_file");
        assert_eq!(body["tools"][0]["function"]["parameters"]["type"], "object");
    }

    #[test]
    fn a_malformed_tool_schema_degrades_to_an_empty_object() {
        let request = ChatRequest::new("gpt-4o", vec![Message::user("hi")])
            .with_tools(vec![ToolDefinition::new("t", "d", "not json")]);
        let body = build(&request);
        assert!(body["tools"][0]["function"]["parameters"].is_object());
    }

    #[test]
    fn tool_results_become_role_tool_messages() {
        let message = crate::message::Message::new(
            Role::User,
            vec![Content::ToolResult {
                tool_use_id: "call_1".into(),
                content: "ok".into(),
                is_error: false,
                correlation: Correlation::default(),
                status: None,
            }],
        );
        let body = build(&ChatRequest::new("gpt-4o", vec![message]));
        assert_eq!(body["messages"][0]["role"], "tool");
        assert_eq!(body["messages"][0]["tool_call_id"], "call_1");
        assert_eq!(body["messages"][0]["content"], "ok");
    }

    #[test]
    fn assistant_tool_uses_emit_tool_calls_array() {
        let message = crate::message::Message::new(
            Role::Assistant,
            vec![Content::ToolUse {
                id: "call_1".into(),
                name: "read_file".into(),
                input_json: r#"{"path":"a"}"#.into(),
                correlation: Correlation::default(),
            }],
        );
        let body = build(&ChatRequest::new("gpt-4o", vec![message]));
        let calls = &body["messages"][0]["tool_calls"];
        assert_eq!(calls[0]["id"], "call_1");
        assert_eq!(calls[0]["function"]["name"], "read_file");
        // Arguments stay as a string, not an object.
        assert_eq!(calls[0]["function"]["arguments"], r#"{"path":"a"}"#);
    }

    #[test]
    fn assistant_with_no_text_uses_null_content() {
        let message = crate::message::Message::new(
            Role::Assistant,
            vec![Content::ToolUse {
                id: "t".into(),
                name: "f".into(),
                input_json: "{}".into(),
                correlation: Correlation::default(),
            }],
        );
        let body = build(&ChatRequest::new("gpt-4o", vec![message]));
        assert!(body["messages"][0]["content"].is_null());
    }

    #[test]
    fn image_blocks_become_placeholder_text() {
        let message = crate::message::Message::new(
            Role::User,
            vec![
                Content::Text("look at this".into()),
                Content::Image {
                    media_type: "image/png".into(),
                    base64: "abc".into(),
                },
            ],
        );
        let body = build(&ChatRequest::new("gpt-4o", vec![message]));
        let content = body["messages"][0]["content"].as_str().unwrap();
        assert!(content.contains("look at this"));
        assert!(content.contains("[image attached: image/png]"));
    }

    #[test]
    fn decodes_text_deltas() {
        let (events, _) = run(&[&text_chunk("Hello"), &text_chunk(", world"), "[DONE]"]);
        let text: String = events
            .iter()
            .filter_map(|e| match e {
                StreamEvent::TextDelta(t) => Some(t.as_str()),
                _ => None,
            })
            .collect();
        assert_eq!(text, "Hello, world");
    }

    #[test]
    fn flush_emits_done_with_mapped_stop_reason() {
        let (events, decoder) = run(&[&stop_chunk("stop"), "[DONE]"]);
        assert!(decoder.finished());
        let Some(StreamEvent::Done { stop_reason, .. }) =
            events.iter().find(|e| matches!(e, StreamEvent::Done { .. }))
        else {
            panic!("expected Done");
        };
        assert_eq!(stop_reason.as_deref(), Some("end_turn"));
    }

    #[test]
    fn maps_all_standard_finish_reasons() {
        assert_eq!(map_finish_reason("stop"), "end_turn");
        assert_eq!(map_finish_reason("tool_calls"), "tool_use");
        assert_eq!(map_finish_reason("length"), "max_tokens");
        assert_eq!(map_finish_reason("content_filter"), "content_filter");
    }

    #[test]
    fn assembles_tool_call_arguments_from_fragments() {
        let chunks = [
            json!({ "choices": [{ "delta": { "tool_calls": [
                { "index": 0, "id": "call_1", "type": "function", "function": { "name": "read_file", "arguments": "" } }
            ] } }] })
            .to_string(),
            json!({ "choices": [{ "delta": { "tool_calls": [
                { "index": 0, "function": { "arguments": "{\"pa" } }
            ] } }] })
            .to_string(),
            json!({ "choices": [{ "delta": { "tool_calls": [
                { "index": 0, "function": { "arguments": "th\":\"x\"}" } }
            ] } }] })
            .to_string(),
            "[DONE]".to_string(),
        ];
        let (events, _) = run(&chunks.iter().map(|s| s.as_str()).collect::<Vec<_>>());

        let Some(StreamEvent::ToolUse(Content::ToolUse {
            id, name, input_json, ..
        })) = events.iter().find(|e| matches!(e, StreamEvent::ToolUse(_)))
        else {
            panic!("expected a tool use");
        };
        assert_eq!(id, "call_1");
        assert_eq!(name, "read_file");
        assert_eq!(input_json, r#"{"path":"x"}"#);
    }

    #[test]
    fn a_tool_with_no_arguments_yields_an_empty_object() {
        let payload = json!({ "choices": [{ "delta": { "tool_calls": [
            { "index": 0, "id": "t", "function": { "name": "f" } }
        ] } }] })
        .to_string();
        let (events, _) = run(&[&payload, "[DONE]"]);
        let Some(StreamEvent::ToolUse(Content::ToolUse { input_json, .. })) =
            events.iter().find(|e| matches!(e, StreamEvent::ToolUse(_)))
        else {
            panic!("expected tool use");
        };
        assert_eq!(input_json, "{}");
    }

    #[test]
    fn multiple_tool_calls_are_emitted_in_index_order() {
        let chunks = [
            json!({ "choices": [{ "delta": { "tool_calls": [
                { "index": 1, "id": "b", "function": { "name": "second", "arguments": "{}" } }
            ] } }] })
            .to_string(),
            json!({ "choices": [{ "delta": { "tool_calls": [
                { "index": 0, "id": "a", "function": { "name": "first", "arguments": "{}" } }
            ] } }] })
            .to_string(),
            "[DONE]".to_string(),
        ];
        let (events, _) = run(&chunks.iter().map(|s| s.as_str()).collect::<Vec<_>>());
        let tools: Vec<_> = events
            .iter()
            .filter(|e| matches!(e, StreamEvent::ToolUse(_)))
            .collect();
        assert_eq!(tools.len(), 2);
        let StreamEvent::ToolUse(Content::ToolUse { name, .. }) = tools[0] else {
            panic!()
        };
        assert_eq!(name, "first");
    }

    #[test]
    fn reads_usage_from_a_usage_only_chunk() {
        let usage_chunk = json!({
            "choices": [],
            "usage": {
                "prompt_tokens": 50,
                "completion_tokens": 20,
                "prompt_tokens_details": { "cached_tokens": 10 }
            }
        })
        .to_string();
        let (events, _) = run(&[&usage_chunk, "[DONE]"]);
        let Some(StreamEvent::Done { usage, .. }) = events.last() else {
            panic!("expected Done");
        };
        // 50 total − 10 cached = 40 non-cached input tokens.
        assert_eq!(usage.input_tokens, 40);
        assert_eq!(usage.cache_read_tokens, 10);
        assert_eq!(usage.output_tokens, 20);
    }

    #[test]
    fn a_stream_without_done_is_not_finished() {
        let (_, decoder) = run(&[&text_chunk("partial")]);
        assert!(!decoder.finished());
    }

    #[test]
    fn malformed_json_is_reported_as_a_protocol_error() {
        let mut decoder = ChatDecoder::new();
        assert!(matches!(
            decoder.decode("{not json}"),
            Err(LlmError::Protocol(_))
        ));
    }

    #[test]
    fn empty_text_delta_emits_nothing() {
        let payload = json!({ "choices": [{ "delta": { "content": "" } }] }).to_string();
        let (events, _) = run(&[&payload, "[DONE]"]);
        assert!(events
            .iter()
            .all(|e| !matches!(e, StreamEvent::TextDelta(_))));
    }

    #[test]
    fn a_chunk_with_no_choices_is_silently_skipped() {
        let payload = json!({ "id": "cmpl-123" }).to_string();
        let events = ChatDecoder::new().decode(&payload).expect("decode");
        assert!(events.is_empty());
    }
}
