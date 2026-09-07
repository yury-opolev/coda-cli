//! OpenAI Responses API protocol for Copilot's `/responses` endpoint.
//!
//! Request building and streaming decoder. The decoder is pure so it can be
//! tested without a network. Tool calls accumulate across two event types:
//! `response.output_item.added` brings the id and name;
//! `response.function_call_arguments.delta` appends argument fragments. Both
//! flush together at `response.completed` or `response.incomplete`.

use std::collections::BTreeMap;

use serde_json::{json, Value};

use crate::anthropic::StreamEvent;
use crate::error::{FailureKind, LlmError};
use crate::message::{ChatRequest, Content, Correlation, Effort, Role, Usage};

/// Builds an OpenAI Responses API request body.
pub fn build(request: &ChatRequest) -> Value {
    let input = build_input(&request.messages);
    let mut body = json!({
        "model": request.model,
        "stream": true,
        "input": input,
    });

    if let Some(system) = &request.system {
        body["instructions"] = json!(system);
    }

    if let Some(effort) = request.effort {
        body["reasoning"] = json!({ "effort": map_effort(effort), "summary": "auto" });
    }

    if !request.tools.is_empty() {
        let tools: Vec<Value> = request
            .tools
            .iter()
            .map(|tool| {
                let params: Value = serde_json::from_str(&tool.input_schema_json)
                    .unwrap_or_else(|_| json!({"type": "object"}));
                json!({
                    "type": "function",
                    "name": tool.name,
                    "description": tool.description,
                    "parameters": params,
                })
            })
            .collect();
        body["tools"] = Value::Array(tools);
    }

    body
}

fn build_input(messages: &[crate::message::Message]) -> Value {
    let mut input = Vec::new();
    for message in messages {
        if message.role == Role::User {
            append_user_input(&mut input, message);
        } else {
            append_assistant_input(&mut input, message);
        }
    }
    Value::Array(input)
}

fn append_user_input(input: &mut Vec<Value>, message: &crate::message::Message) {
    // Tool results come first as `function_call_output` items.
    for block in &message.content {
        if let Content::ToolResult {
            tool_use_id,
            content,
            ..
        } = block
        {
            input.push(json!({
                "type": "function_call_output",
                "call_id": tool_use_id,
                "output": content,
            }));
        }
    }

    let mut parts = Vec::new();
    for block in &message.content {
        match block {
            Content::Text(text) => parts.push(json!({ "type": "input_text", "text": text })),
            Content::Image { media_type, base64 } => parts.push(json!({
                "type": "input_image",
                "image_url": format!("data:{media_type};base64,{base64}"),
            })),
            _ => {}
        }
    }
    if !parts.is_empty() {
        input.push(json!({ "role": "user", "content": parts }));
    }
}

fn append_assistant_input(input: &mut Vec<Value>, message: &crate::message::Message) {
    // Thinking blocks with a signature must be replayed before text items so
    // the model retains its reasoning state across stateless turns. The
    // signature stores a JSON object with `id` and `encrypted_content`.
    for block in &message.content {
        if let Content::Thinking {
            signature: Some(sig),
            ..
        } = block
        {
            if let Ok(doc) = serde_json::from_str::<Value>(sig) {
                if let (Some(id), Some(enc)) = (
                    doc.get("id").and_then(Value::as_str),
                    doc.get("encrypted_content").and_then(Value::as_str),
                ) {
                    input.push(json!({
                        "type": "reasoning",
                        "id": id,
                        "encrypted_content": enc,
                    }));
                }
            }
        }
    }

    let text_parts: Vec<Value> = message
        .content
        .iter()
        .filter_map(|b| {
            if let Content::Text(text) = b {
                Some(json!({ "type": "output_text", "text": text }))
            } else {
                None
            }
        })
        .collect();
    if !text_parts.is_empty() {
        input.push(json!({ "role": "assistant", "content": text_parts }));
    }

    for block in &message.content {
        if let Content::ToolUse {
            id, name, input_json, ..
        } = block
        {
            input.push(json!({
                "type": "function_call",
                "call_id": id,
                "name": name,
                "arguments": input_json,
            }));
        }
    }
}

/// Maps a validated effort level to the string the Responses API carries.
///
/// This is a faithful pass-through: the level name is sent verbatim. The host
/// is the single place that clamps a requested level against the model's
/// advertised capability (`resolve_applied_level`), so by the time an effort
/// reaches here it is already a level the model declared it supports. Collapsing
/// `xhigh`/`max` to `high` here would be dishonest — it would tell the user an
/// effective level the wire never actually requested. A model that tops out at
/// `high` never sees `xhigh`/`max`, because the host drops or clamps them first.
fn map_effort(effort: Effort) -> &'static str {
    effort.as_str()
}

/// A function call being accumulated across streaming events.
#[derive(Debug, Default)]
struct PartialToolCall {
    id: String,
    name: String,
    arguments: String,
}

#[derive(Debug, Default)]
struct PartialReasoning {
    text: String,
    id: Option<String>,
    encrypted_content: Option<String>,
    started: bool,
    complete: bool,
}

impl PartialReasoning {
    fn start(&mut self) -> Vec<StreamEvent> {
        if self.started {
            return Vec::new();
        }
        self.started = true;
        vec![StreamEvent::ThinkingStarted]
    }

    fn finish(&mut self) -> Option<StreamEvent> {
        if !self.started || self.complete {
            return None;
        }
        self.complete = true;
        let signature = self.encrypted_content.take().map(|encrypted| {
            json!({
                "id": self.id.take().unwrap_or_default(),
                "encrypted_content": encrypted,
            }).to_string()
        });
        Some(StreamEvent::ThinkingDone(Content::Thinking {
            text: std::mem::take(&mut self.text),
            signature,
        }))
    }
}

/// Decodes the OpenAI Responses API streaming protocol.
///
/// Tool calls accumulate across `response.output_item.added` (id/name) and
/// `response.function_call_arguments.delta` (argument fragments), then flush
/// all at once on `response.completed` / `response.incomplete`.
///
/// Reasoning starts with its output item, before summary text, and finishes
/// with that item. Terminal events flush any reasoning not explicitly closed.
#[derive(Debug, Default)]
pub struct ResponsesDecoder {
    tool_calls: BTreeMap<usize, PartialToolCall>,
    reasoning: BTreeMap<usize, PartialReasoning>,
    stop_reason: Option<String>,
    usage: Usage,
    finished: bool,
}

impl ResponsesDecoder {
    pub fn new() -> Self {
        Self::default()
    }

    /// Whether a terminal event (`response.completed` / `response.incomplete` /
    /// `response.failed`) has been seen.
    pub fn finished(&self) -> bool {
        self.finished
    }

    /// Decodes one SSE event by name and data payload.
    ///
    /// The JSON `type` field is authoritative; the SSE event name is only used
    /// as a fallback so that future API changes that rename events still work.
    pub fn decode(&mut self, event_type: &str, data: &str) -> Result<Vec<StreamEvent>, LlmError> {
        if self.finished {
            return Ok(Vec::new());
        }
        // Some streams close with `data: [DONE]` after the terminal event.
        if data.trim() == "[DONE]" {
            return Ok(Vec::new());
        }

        let value: Value = serde_json::from_str(data)
            .map_err(|e| LlmError::Protocol(format!("invalid JSON in responses event: {e}")))?;

        let kind = value
            .get("type")
            .and_then(Value::as_str)
            .unwrap_or(event_type);

        match kind {
            "response.output_text.delta" => {
                let text = value.get("delta").and_then(Value::as_str).unwrap_or("");
                if text.is_empty() {
                    Ok(Vec::new())
                } else {
                    Ok(vec![StreamEvent::TextDelta(text.to_string())])
                }
            }

            "response.reasoning_summary_text.delta" => {
                let chunk = value.get("delta").and_then(Value::as_str).unwrap_or("");
                if chunk.is_empty() {
                    return Ok(Vec::new());
                }
                let entry = self.reasoning.entry(output_index(&value)).or_default();
                if entry.complete {
                    return Ok(Vec::new());
                }
                let mut events = entry.start();
                entry.text.push_str(chunk);
                events.push(StreamEvent::ThinkingDelta(chunk.to_string()));
                Ok(events)
            }

            "response.output_item.added" | "response.output_item.done" => {
                let Some(item) = value.get("item") else {
                    return Ok(Vec::new());
                };
                let item_type = item.get("type").and_then(Value::as_str).unwrap_or("");

                if item_type == "function_call" {
                    let index = output_index(&value);
                    let entry = self.tool_calls.entry(index).or_default();
                    read_tool_call(item, entry);
                } else if item_type == "reasoning" {
                    let entry = self.reasoning.entry(output_index(&value)).or_default();
                    if entry.complete {
                        return Ok(Vec::new());
                    }
                    let mut events = entry.start();
                    if let Some(id) = item.get("id").and_then(Value::as_str) {
                        entry.id = Some(id.to_string());
                    }
                    if let Some(enc) = item.get("encrypted_content").and_then(Value::as_str) {
                        entry.encrypted_content = Some(enc.to_string());
                    }
                    // Some providers include the summary only on the completed
                    // item. Do not replay it when deltas already supplied it.
                    if entry.text.is_empty() {
                        if let Some(summary) = item.get("summary").and_then(Value::as_array) {
                            entry.text = summary.iter()
                                .filter_map(|part| part.get("text").and_then(Value::as_str))
                                .collect::<Vec<_>>().join("\n\n");
                            if !entry.text.is_empty() {
                                events.push(StreamEvent::ThinkingDelta(entry.text.clone()));
                            }
                        }
                    }
                    if kind == "response.output_item.done" {
                        events.extend(entry.finish());
                    }
                    return Ok(events);
                }
                Ok(Vec::new())
            }

            "response.function_call_arguments.delta" => {
                let index = output_index(&value);
                let entry = self.tool_calls.entry(index).or_default();
                if let Some(fragment) = value.get("delta").and_then(Value::as_str) {
                    entry.arguments.push_str(fragment);
                }
                Ok(Vec::new())
            }

            "response.completed" | "response.incomplete" => {
                let response_obj = value.get("response").cloned().unwrap_or(Value::Null);
                self.read_usage(&response_obj);

                let is_incomplete = kind == "response.incomplete";
                self.stop_reason = Some(if is_incomplete {
                    map_incomplete_reason(&response_obj)
                } else if self.tool_calls.is_empty() {
                    "end_turn".to_string()
                } else {
                    "tool_use".to_string()
                });

                self.finished = true;
                Ok(self.flush_events())
            }

            "response.failed" | "error" => {
                let message = read_error_message(&value)
                    .unwrap_or_else(|| "the Responses API stream failed".to_string());
                Err(LlmError::Api {
                    status: 0,
                    message,
                    kind: FailureKind::Transient,
                    retry_after: None,
                    body: None,
                })
            }

            // Unknown events are ignored so future API additions cannot break the client.
            _ => Ok(Vec::new()),
        }
    }

    fn flush_events(&mut self) -> Vec<StreamEvent> {
        let mut events: Vec<_> = self.reasoning.values_mut().filter_map(PartialReasoning::finish).collect();

        for tc in self.tool_calls.values() {
            events.push(StreamEvent::ToolUse(Content::ToolUse {
                id: tc.id.clone(),
                name: tc.name.clone(),
                input_json: if tc.arguments.trim().is_empty() {
                    "{}".to_string()
                } else {
                    tc.arguments.clone()
                },
                correlation: Correlation::default(),
            }));
        }

        events.push(StreamEvent::Done {
            stop_reason: self.stop_reason.clone(),
            usage: self.usage,
        });

        events
    }

    fn read_usage(&mut self, response: &Value) {
        let Some(usage) = response.get("usage") else {
            return;
        };
        // Same inversion as chat: input_tokens is total; cached_tokens is a subset.
        let input = usage
            .get("input_tokens")
            .and_then(Value::as_u64)
            .unwrap_or(0) as u32;
        let output = usage
            .get("output_tokens")
            .and_then(Value::as_u64)
            .unwrap_or(0) as u32;
        let cached = usage
            .get("input_tokens_details")
            .and_then(|d| d.get("cached_tokens"))
            .and_then(Value::as_u64)
            .unwrap_or(0) as u32;
        let cached = cached.min(input);

        if input > 0 || output > 0 {
            self.usage = Usage {
                input_tokens: input - cached,
                output_tokens: output,
                cache_read_tokens: cached,
                ..Usage::ZERO
            };
        }
    }
}

fn output_index(value: &Value) -> usize {
    value
        .get("output_index")
        .and_then(Value::as_u64)
        .unwrap_or(0) as usize
}

fn read_tool_call(item: &Value, entry: &mut PartialToolCall) {
    if let Some(id) = item.get("call_id").and_then(Value::as_str) {
        entry.id = id.to_string();
    }
    if let Some(name) = item.get("name").and_then(Value::as_str) {
        entry.name = name.to_string();
    }
    // `response.output_item.done` may carry the complete arguments string;
    // replace accumulated fragments if it arrives to avoid duplication.
    if let Some(args) = item
        .get("arguments")
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
    {
        entry.arguments.clear();
        entry.arguments.push_str(args);
    }
}

fn map_incomplete_reason(response: &Value) -> String {
    response
        .get("incomplete_details")
        .and_then(|d| d.get("reason"))
        .and_then(Value::as_str)
        .map(|r| match r {
            "max_output_tokens" => "max_tokens".to_string(),
            other => other.to_string(),
        })
        .unwrap_or_else(|| "incomplete".to_string())
}

fn read_error_message(value: &Value) -> Option<String> {
    value
        .get("message")
        .and_then(Value::as_str)
        .map(str::to_string)
        .or_else(|| {
            value
                .get("error")
                .and_then(|e| e.get("message"))
                .and_then(Value::as_str)
                .map(str::to_string)
        })
        .or_else(|| {
            value
                .get("response")
                .and_then(|r| r.get("error"))
                .and_then(|e| e.get("message"))
                .and_then(Value::as_str)
                .map(str::to_string)
        })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::message::{Correlation, Message, ToolDefinition};
    use serde_json::json;

    fn run(events: &[(&str, Value)]) -> (Vec<StreamEvent>, ResponsesDecoder) {
        let mut decoder = ResponsesDecoder::new();
        let mut out = Vec::new();
        for (name, payload) in events {
            out.extend(
                decoder
                    .decode(name, &payload.to_string())
                    .expect("decode should not fail"),
            );
        }
        (out, decoder)
    }

    #[test]
    fn builds_a_minimal_request() {
        let body = build(&ChatRequest::new("gpt-4o", vec![Message::user("hi")]));
        assert_eq!(body["model"], "gpt-4o");
        assert_eq!(body["stream"], true);
        assert_eq!(body["input"][0]["role"], "user");
        assert_eq!(body["input"][0]["content"][0]["type"], "input_text");
        assert_eq!(body["input"][0]["content"][0]["text"], "hi");
    }

    #[test]
    fn includes_instructions_for_system_prompt() {
        let body = build(
            &ChatRequest::new("gpt-4o", vec![Message::user("hi")]).with_system("be brief"),
        );
        assert_eq!(body["instructions"], "be brief");
    }

    #[test]
    fn includes_reasoning_effort() {
        let body = build(
            &ChatRequest::new("gpt-4o", vec![Message::user("hi")])
                .with_effort(Some(Effort::Medium)),
        );
        assert_eq!(body["reasoning"]["effort"], "medium");
    }

    #[test]
    fn effort_level_is_sent_to_the_wire_verbatim() {
        // The wire is honest: whatever level reaches `build` (already clamped by
        // the host against the model's advertised capability) is sent as-is. It
        // must never silently downgrade the level the user was told is in force.
        for effort in [Effort::Low, Effort::Medium, Effort::High, Effort::Xhigh, Effort::Max] {
            let body = build(
                &ChatRequest::new("gpt-4o", vec![Message::user("hi")])
                    .with_effort(Some(effort)),
            );
            assert_eq!(
                body["reasoning"]["effort"], effort.as_str(),
                "{} must be sent verbatim, not collapsed",
                effort.as_str()
            );
        }
    }

    #[test]
    fn xhigh_is_not_dishonestly_reported_as_high() {
        // Regression: xhigh used to collapse to "high" on the wire while the
        // capability layer reported "xhigh" as effective — an outbound lie.
        let body = build(
            &ChatRequest::new("gpt-4o", vec![Message::user("hi")])
                .with_effort(Some(Effort::Xhigh)),
        );
        assert_eq!(body["reasoning"]["effort"], "xhigh");
        assert_ne!(body["reasoning"]["effort"], "high");
    }

    #[test]
    fn includes_tool_definitions() {
        let request = ChatRequest::new("gpt-4o", vec![Message::user("hi")]).with_tools(vec![
            ToolDefinition::new("read_file", "Read a file", r#"{"type":"object"}"#),
        ]);
        let body = build(&request);
        assert_eq!(body["tools"][0]["type"], "function");
        assert_eq!(body["tools"][0]["name"], "read_file");
        assert_eq!(body["tools"][0]["parameters"]["type"], "object");
    }

    #[test]
    fn tool_results_become_function_call_output() {
        let message = crate::message::Message::new(
            Role::User,
            vec![Content::ToolResult {
                tool_use_id: "call_1".into(),
                content: "result".into(),
                is_error: false,
                correlation: Correlation::default(),
                status: None,
            }],
        );
        let body = build(&ChatRequest::new("gpt-4o", vec![message]));
        assert_eq!(body["input"][0]["type"], "function_call_output");
        assert_eq!(body["input"][0]["call_id"], "call_1");
        assert_eq!(body["input"][0]["output"], "result");
    }

    #[test]
    fn images_become_input_image_with_data_url() {
        let message = crate::message::Message::new(
            Role::User,
            vec![Content::Image {
                media_type: "image/png".into(),
                base64: "abc".into(),
            }],
        );
        let body = build(&ChatRequest::new("gpt-4o", vec![message]));
        let part = &body["input"][0]["content"][0];
        assert_eq!(part["type"], "input_image");
        assert_eq!(part["image_url"], "data:image/png;base64,abc");
    }

    #[test]
    fn thinking_blocks_with_signature_replay_as_reasoning_items() {
        let sig = json!({ "id": "r1", "encrypted_content": "enc_abc" }).to_string();
        let message = crate::message::Message::new(
            Role::Assistant,
            vec![Content::Thinking {
                text: "some reasoning".into(),
                signature: Some(sig),
            }],
        );
        let body = build(&ChatRequest::new("gpt-4o", vec![message]));
        assert_eq!(body["input"][0]["type"], "reasoning");
        assert_eq!(body["input"][0]["id"], "r1");
        assert_eq!(body["input"][0]["encrypted_content"], "enc_abc");
    }

    #[test]
    fn thinking_blocks_without_signature_are_skipped() {
        let message = crate::message::Message::new(
            Role::Assistant,
            vec![
                Content::Thinking {
                    text: "unsigned".into(),
                    signature: None,
                },
                Content::Text("answer".into()),
            ],
        );
        let body = build(&ChatRequest::new("gpt-4o", vec![message]));
        // Should only emit the assistant text item, not a reasoning item.
        assert_eq!(body["input"][0]["role"], "assistant");
        assert_eq!(body["input"][0]["content"][0]["text"], "answer");
    }

    #[test]
    fn decodes_text_delta() {
        let (events, _) = run(&[
            ("response.output_text.delta", json!({ "type": "response.output_text.delta", "delta": "Hello" })),
            ("response.output_text.delta", json!({ "type": "response.output_text.delta", "delta": ", world" })),
        ]);
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
    fn reasoning_start_is_visible_before_any_summary_text_arrives() {
        let mut decoder = ResponsesDecoder::new();
        let events = decoder.decode("response.output_item.added", &json!({
            "output_index": 0,
            "item": {"type": "reasoning", "id": "r1", "summary": []}
        }).to_string()).unwrap();
        assert!(!events.is_empty(), "the provider has started reasoning, but the UI receives nothing");
        assert!(!decoder.finished());
    }

    #[test]
    fn reasoning_item_done_preserves_encrypted_only_thinking_before_answer() {
        let (events, _) = run(&[
            ("response.output_item.added", json!({"output_index":0,"item":{"type":"reasoning","id":"r1","summary":[]}})),
            ("response.output_item.done", json!({"output_index":0,"item":{"type":"reasoning","id":"r1","summary":[],"encrypted_content":"opaque"}})),
            ("response.output_text.delta", json!({"delta":"answer"})),
            ("response.completed", json!({"response":{}})),
        ]);
        let thinking: Vec<_> = events.iter().enumerate()
            .filter(|(_, event)| matches!(event, StreamEvent::ThinkingDone(_))).collect();
        assert_eq!(thinking.len(), 1, "encrypted reasoning must not be dropped or completed twice");
        let answer = events.iter().position(|event| matches!(event, StreamEvent::TextDelta(_))).unwrap();
        assert!(thinking[0].0 < answer, "thinking must finish before the answer starts");
        let StreamEvent::ThinkingDone(Content::Thinking { text, signature }) = thinking[0].1 else {
            panic!("expected thinking");
        };
        assert!(text.is_empty());
        let signature: Value = serde_json::from_str(signature.as_deref().unwrap()).unwrap();
        assert_eq!(signature["encrypted_content"], "opaque");
    }

    #[test]
    fn reasoning_items_complete_independently_and_do_not_repeat_summary() {
        let (events, _) = run(&[
            ("response.output_item.added", json!({"output_index":0,"item":{"type":"reasoning","id":"r1"}})),
            ("response.reasoning_summary_text.delta", json!({"output_index":0,"delta":"first"})),
            ("response.output_item.done", json!({"output_index":0,"item":{"type":"reasoning","id":"r1","summary":[{"type":"summary_text","text":"first"}],"encrypted_content":"one"}})),
            ("response.output_item.added", json!({"output_index":2,"item":{"type":"reasoning","id":"r2"}})),
            ("response.output_item.done", json!({"output_index":2,"item":{"type":"reasoning","id":"r2","summary":[{"type":"summary_text","text":"second"}],"encrypted_content":"two"}})),
            ("response.completed", json!({"response":{}})),
        ]);
        let started = events.iter().filter(|e| matches!(e, StreamEvent::ThinkingStarted)).count();
        assert_eq!(started, 2);
        let deltas: Vec<_> = events.iter().filter_map(|e| match e {
            StreamEvent::ThinkingDelta(text) => Some(text.as_str()),
            _ => None,
        }).collect();
        assert_eq!(deltas, ["first", "second"]);
        let blocks: Vec<_> = events.iter().filter_map(|e| match e {
            StreamEvent::ThinkingDone(Content::Thinking { text, signature }) =>
                Some((text.as_str(), serde_json::from_str::<Value>(signature.as_deref().unwrap()).unwrap())),
            _ => None,
        }).collect();
        assert_eq!(blocks.len(), 2);
        assert_eq!(blocks[0].0, "first");
        assert_eq!(blocks[0].1["id"], "r1");
        assert_eq!(blocks[1].0, "second");
        assert_eq!(blocks[1].1["id"], "r2");
    }

    #[test]
    fn reasoning_effort_opts_in_to_visible_summary() {
        let request = ChatRequest::new("gpt-6-astra", vec![Message::user("hello")])
            .with_effort(Some(Effort::Xhigh));
        let body = build(&request);
        assert_eq!(body["reasoning"], json!({"effort":"xhigh","summary":"auto"}));
        assert!(build(&ChatRequest::new("gpt-4o", vec![])).get("reasoning").is_none());
    }

    #[test]
    fn decodes_reasoning_summary_delta_as_thinking() {
        let (events, _) = run(&[
            ("response.reasoning_summary_text.delta", json!({ "type": "response.reasoning_summary_text.delta", "delta": "let me " })),
            ("response.reasoning_summary_text.delta", json!({ "type": "response.reasoning_summary_text.delta", "delta": "consider" })),
        ]);
        let thinking: String = events
            .iter()
            .filter_map(|e| match e {
                StreamEvent::ThinkingDelta(t) => Some(t.as_str()),
                _ => None,
            })
            .collect();
        assert_eq!(thinking, "let me consider");
    }

    #[test]
    fn completed_closes_thinking_before_tools_and_done() {
        let (events, decoder) = run(&[
            ("response.output_item.added", json!({ "type": "response.output_item.added", "output_index": 0, "item": { "type": "function_call", "call_id": "c1", "name": "read_file" } })),
            ("response.function_call_arguments.delta", json!({ "type": "response.function_call_arguments.delta", "output_index": 0, "delta": "{}" })),
            ("response.reasoning_summary_text.delta", json!({ "type": "response.reasoning_summary_text.delta", "delta": "reasoning" })),
            ("response.completed", json!({ "type": "response.completed", "response": { "usage": { "input_tokens": 10, "output_tokens": 5 } } })),
        ]);

        assert!(decoder.finished());
        let tool = events.iter().position(|e| matches!(e, StreamEvent::ToolUse(_))).unwrap();
        let thinking = events.iter().position(|e| matches!(e, StreamEvent::ThinkingDone(_))).unwrap();
        let done = events.iter().position(|e| matches!(e, StreamEvent::Done { .. })).unwrap();
        assert!(thinking < tool, "thinking closes before tool execution");
        assert!(thinking < done, "thinking done before final Done");

        let Some(StreamEvent::Done { usage, stop_reason }) = events.last() else { panic!() };
        assert_eq!(stop_reason.as_deref(), Some("tool_use"));
        assert_eq!(usage.input_tokens, 10);
        assert_eq!(usage.output_tokens, 5);
    }

    #[test]
    fn completed_without_tools_uses_end_turn_reason() {
        let (events, _) = run(&[(
            "response.completed",
            json!({ "type": "response.completed", "response": {} }),
        )]);
        let Some(StreamEvent::Done { stop_reason, .. }) = events.last() else {
            panic!("expected Done")
        };
        assert_eq!(stop_reason.as_deref(), Some("end_turn"));
    }

    #[test]
    fn incomplete_maps_reason_from_incomplete_details() {
        let (events, _) = run(&[(
            "response.incomplete",
            json!({
                "type": "response.incomplete",
                "response": {
                    "incomplete_details": { "reason": "max_output_tokens" },
                    "usage": {}
                }
            }),
        )]);
        let Some(StreamEvent::Done { stop_reason, .. }) = events.last() else {
            panic!("expected Done")
        };
        assert_eq!(stop_reason.as_deref(), Some("max_tokens"));
    }

    #[test]
    fn function_call_arguments_are_accumulated_per_index() {
        let (events, _) = run(&[
            ("response.output_item.added", json!({ "type": "response.output_item.added", "output_index": 0, "item": { "type": "function_call", "call_id": "c1", "name": "f" } })),
            ("response.function_call_arguments.delta", json!({ "type": "response.function_call_arguments.delta", "output_index": 0, "delta": "{\"pa" })),
            ("response.function_call_arguments.delta", json!({ "type": "response.function_call_arguments.delta", "output_index": 0, "delta": "th\":\"x\"}" })),
            ("response.completed", json!({ "type": "response.completed", "response": {} })),
        ]);
        let Some(StreamEvent::ToolUse(Content::ToolUse { id, name, input_json, .. })) =
            events.iter().find(|e| matches!(e, StreamEvent::ToolUse(_)))
        else { panic!("expected tool use") };
        assert_eq!(id, "c1");
        assert_eq!(name, "f");
        assert_eq!(input_json, r#"{"path":"x"}"#);
    }

    #[test]
    fn thinking_done_carries_signature_when_encrypted_content_is_present() {
        let (events, _) = run(&[
            ("response.output_item.added", json!({ "type": "response.output_item.added", "output_index": 0, "item": { "type": "reasoning", "id": "r1", "encrypted_content": "enc_xyz" } })),
            ("response.reasoning_summary_text.delta", json!({ "type": "response.reasoning_summary_text.delta", "delta": "thoughts" })),
            ("response.completed", json!({ "type": "response.completed", "response": {} })),
        ]);
        let Some(StreamEvent::ThinkingDone(Content::Thinking { text, signature })) =
            events.iter().find(|e| matches!(e, StreamEvent::ThinkingDone(_)))
        else { panic!("expected ThinkingDone") };
        assert_eq!(text, "thoughts");
        let sig = signature.as_deref().expect("signature should be present");
        let parsed: Value = serde_json::from_str(sig).expect("signature should be JSON");
        assert_eq!(parsed["id"], "r1");
        assert_eq!(parsed["encrypted_content"], "enc_xyz");
    }

    #[test]
    fn thinking_done_has_no_signature_without_encrypted_content() {
        let (events, _) = run(&[
            ("response.reasoning_summary_text.delta", json!({ "type": "response.reasoning_summary_text.delta", "delta": "thoughts" })),
            ("response.completed", json!({ "type": "response.completed", "response": {} })),
        ]);
        let Some(StreamEvent::ThinkingDone(Content::Thinking { signature, .. })) =
            events.iter().find(|e| matches!(e, StreamEvent::ThinkingDone(_)))
        else { panic!("expected ThinkingDone") };
        assert!(signature.is_none());
    }

    #[test]
    fn error_event_fails_the_stream() {
        let mut decoder = ResponsesDecoder::new();
        let result = decoder.decode(
            "error",
            &json!({ "type": "error", "message": "Overloaded" }).to_string(),
        );
        let err = result.expect_err("error event should fail");
        assert!(err.to_string().contains("Overloaded"));
        assert!(err.is_retryable());
    }

    #[test]
    fn response_failed_reads_nested_message() {
        let mut decoder = ResponsesDecoder::new();
        let result = decoder.decode(
            "response.failed",
            &json!({ "type": "response.failed", "response": { "error": { "message": "quota exceeded" } } }).to_string(),
        );
        let err = result.expect_err("should fail");
        assert!(err.to_string().contains("quota exceeded"));
    }

    #[test]
    fn unknown_events_are_silently_ignored() {
        let mut decoder = ResponsesDecoder::new();
        let events = decoder
            .decode("response.future_event", &json!({ "type": "response.future_event" }).to_string())
            .expect("unknown events must not fail");
        assert!(events.is_empty());
    }

    #[test]
    fn a_stream_without_a_terminal_event_is_not_finished() {
        let (_, decoder) = run(&[(
            "response.output_text.delta",
            json!({ "type": "response.output_text.delta", "delta": "truncated" }),
        )]);
        assert!(!decoder.finished());
    }

    #[test]
    fn done_sentinel_is_silently_skipped() {
        let mut decoder = ResponsesDecoder::new();
        let events = decoder.decode("", "[DONE]").expect("should not fail");
        assert!(events.is_empty());
        assert!(!decoder.finished());
    }

    #[test]
    fn malformed_json_is_a_protocol_error() {
        let mut decoder = ResponsesDecoder::new();
        assert!(matches!(
            decoder.decode("response.output_text.delta", "{bad"),
            Err(LlmError::Protocol(_))
        ));
    }
}
