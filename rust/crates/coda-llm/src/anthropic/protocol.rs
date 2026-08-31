//! Anthropic Messages API streaming.
//!
//! The wire protocol is a state machine: `content_block_start` opens a block,
//! deltas accumulate into it, `content_block_stop` closes it. Tool arguments in
//! particular arrive as `input_json_delta` fragments that are only valid JSON
//! once concatenated, so blocks must be assembled before being emitted.
//!
//! [`AnthropicDecoder`] is pure: it turns SSE events into stream events with no
//! I/O, which is what makes the protocol testable without a network.

use serde_json::Value;

use crate::error::LlmError;
use crate::message::{Content, Correlation, Usage};

/// What the caller observes while a response streams.
#[derive(Debug, Clone, PartialEq)]
pub enum StreamEvent {
    /// A fragment of assistant text.
    TextDelta(String),
    /// A fragment of reasoning.
    ThinkingDelta(String),
    /// A completed reasoning block, with the signature that must be replayed.
    ThinkingDone(Content),
    /// A completed tool call.
    ToolUse(Content),
    /// The response finished.
    Done {
        stop_reason: Option<String>,
        usage: Usage,
    },
}

/// A content block being assembled.
#[derive(Debug, Clone)]
enum Partial {
    Text(String),
    Thinking {
        text: String,
        signature: Option<String>,
    },
    RedactedThinking(String),
    ToolUse {
        id: String,
        name: String,
        /// Concatenated `input_json_delta` fragments.
        input: String,
    },
}

/// Decodes the Anthropic streaming protocol.
#[derive(Debug, Default)]
pub struct AnthropicDecoder {
    /// Blocks currently open, keyed by their wire index.
    blocks: std::collections::BTreeMap<u64, Partial>,
    usage: Usage,
    stop_reason: Option<String>,
    finished: bool,
}

impl AnthropicDecoder {
    pub fn new() -> Self {
        Self::default()
    }

    /// Whether a terminal event has been seen.
    ///
    /// A stream that ends without one was truncated, and the caller must treat
    /// that as a failure rather than a short answer.
    pub fn finished(&self) -> bool {
        self.finished
    }

    pub fn usage(&self) -> Usage {
        self.usage
    }

    /// Decodes one SSE event.
    pub fn decode(&mut self, event_name: &str, data: &str) -> Result<Vec<StreamEvent>, LlmError> {
        // Anthropic sends a `ping` with no useful payload.
        if event_name == "ping" || data.trim().is_empty() {
            return Ok(Vec::new());
        }

        let value: Value = serde_json::from_str(data).map_err(|error| {
            // Include what actually arrived. A bare parse error names a column
            // in a payload the reader cannot see, which is useless in the field
            // — the first real Copilot `/v1/messages` turn failed with exactly
            // that and gave no clue what the provider had sent.
            let preview: String = data.chars().take(200).collect();
            LlmError::Protocol(format!(
                "invalid event JSON on '{event_name}': {error}; payload starts: {preview:?}"
            ))
        })?;

        // The `type` field is authoritative; the SSE event name mirrors it.
        let kind = value
            .get("type")
            .and_then(Value::as_str)
            .unwrap_or(event_name);

        match kind {
            "message_start" => {
                if let Some(usage) = value.get("message").and_then(|m| m.get("usage")) {
                    self.usage = merge_usage(self.usage, usage);
                }
                Ok(Vec::new())
            }
            "content_block_start" => {
                self.start_block(&value);
                Ok(Vec::new())
            }
            "content_block_delta" => Ok(self.apply_delta(&value)),
            "content_block_stop" => Ok(self.stop_block(&value)),
            "message_delta" => {
                if let Some(reason) = value
                    .get("delta")
                    .and_then(|d| d.get("stop_reason"))
                    .and_then(Value::as_str)
                {
                    self.stop_reason = Some(reason.to_string());
                }
                if let Some(usage) = value.get("usage") {
                    self.usage = merge_usage(self.usage, usage);
                }
                Ok(Vec::new())
            }
            "message_stop" => {
                self.finished = true;
                Ok(vec![StreamEvent::Done {
                    stop_reason: self.stop_reason.clone(),
                    usage: self.usage,
                }])
            }
            "error" => {
                let message = value
                    .get("error")
                    .and_then(|e| e.get("message"))
                    .and_then(Value::as_str)
                    .unwrap_or("the provider reported an error");
                Err(LlmError::Api {
                    status: 0,
                    message: message.to_string(),
                    kind: crate::error::FailureKind::Transient,
                    retry_after: None,
                    body: None,
                })
            }
            // Unknown events are ignored so a newer API cannot break the client.
            _ => Ok(Vec::new()),
        }
    }

    fn start_block(&mut self, value: &Value) {
        let Some(index) = value.get("index").and_then(Value::as_u64) else {
            return;
        };
        let Some(block) = value.get("content_block") else {
            return;
        };
        let block_type = block.get("type").and_then(Value::as_str).unwrap_or("");
        let string =
            |key: &str| block.get(key).and_then(Value::as_str).unwrap_or("").to_string();

        let partial = match block_type {
            "text" => Partial::Text(string("text")),
            "thinking" => Partial::Thinking {
                text: string("thinking"),
                signature: block
                    .get("signature")
                    .and_then(Value::as_str)
                    .map(str::to_string),
            },
            "redacted_thinking" => Partial::RedactedThinking(string("data")),
            "tool_use" => Partial::ToolUse {
                id: string("id"),
                name: string("name"),
                input: String::new(),
            },
            _ => return,
        };

        self.blocks.insert(index, partial);
    }

    fn apply_delta(&mut self, value: &Value) -> Vec<StreamEvent> {
        let Some(index) = value.get("index").and_then(Value::as_u64) else {
            return Vec::new();
        };
        let Some(delta) = value.get("delta") else {
            return Vec::new();
        };
        let delta_type = delta.get("type").and_then(Value::as_str).unwrap_or("");
        let Some(block) = self.blocks.get_mut(&index) else {
            return Vec::new();
        };

        match (delta_type, block) {
            ("text_delta", Partial::Text(text)) => {
                let fragment = delta.get("text").and_then(Value::as_str).unwrap_or("");
                text.push_str(fragment);
                if fragment.is_empty() {
                    Vec::new()
                } else {
                    vec![StreamEvent::TextDelta(fragment.to_string())]
                }
            }
            ("thinking_delta", Partial::Thinking { text, .. }) => {
                let fragment = delta.get("thinking").and_then(Value::as_str).unwrap_or("");
                text.push_str(fragment);
                if fragment.is_empty() {
                    Vec::new()
                } else {
                    vec![StreamEvent::ThinkingDelta(fragment.to_string())]
                }
            }
            ("signature_delta", Partial::Thinking { signature, .. }) => {
                let fragment = delta.get("signature").and_then(Value::as_str).unwrap_or("");
                signature.get_or_insert_with(String::new).push_str(fragment);
                Vec::new()
            }
            ("input_json_delta", Partial::ToolUse { input, .. }) => {
                // Fragments are only valid JSON once concatenated, so nothing
                // is emitted until the block closes.
                let fragment = delta
                    .get("partial_json")
                    .and_then(Value::as_str)
                    .unwrap_or("");
                input.push_str(fragment);
                Vec::new()
            }
            _ => Vec::new(),
        }
    }

    fn stop_block(&mut self, value: &Value) -> Vec<StreamEvent> {
        let Some(index) = value.get("index").and_then(Value::as_u64) else {
            return Vec::new();
        };
        let Some(block) = self.blocks.remove(&index) else {
            return Vec::new();
        };

        match block {
            // Text was already emitted delta by delta.
            Partial::Text(_) => Vec::new(),
            Partial::Thinking { text, signature } => {
                vec![StreamEvent::ThinkingDone(Content::Thinking { text, signature })]
            }
            Partial::RedactedThinking(data) => {
                vec![StreamEvent::ThinkingDone(Content::RedactedThinking { data })]
            }
            Partial::ToolUse { id, name, input } => {
                // An empty argument list arrives as no deltas at all.
                let input_json = if input.trim().is_empty() {
                    "{}".to_string()
                } else {
                    input
                };
                vec![StreamEvent::ToolUse(Content::ToolUse {
                    id,
                    name,
                    input_json,
                    correlation: Correlation::default(),
                })]
            }
        }
    }
}

/// Folds a wire `usage` object into the running totals.
///
/// `message_start` reports input and cache counts; `message_delta` reports the
/// final output count. Later non-zero values win so the totals end correct
/// whichever order they arrive in.
fn merge_usage(current: Usage, wire: &Value) -> Usage {
    let field = |key: &str| wire.get(key).and_then(Value::as_u64).unwrap_or(0) as u32;

    let cache_creation = wire.get("cache_creation");
    let cache_field = |key: &str| {
        cache_creation
            .and_then(|c| c.get(key))
            .and_then(Value::as_u64)
            .unwrap_or(0) as u32
    };

    let five_minute = cache_field("ephemeral_5m_input_tokens");
    let one_hour = cache_field("ephemeral_1h_input_tokens");
    let total_writes = field("cache_creation_input_tokens");

    Usage {
        input_tokens: pick(current.input_tokens, field("input_tokens")),
        output_tokens: pick(current.output_tokens, field("output_tokens")),
        cache_read_tokens: pick(current.cache_read_tokens, field("cache_read_input_tokens")),
        // Prefer the itemised breakdown; fall back to the total, attributing it
        // to the five-minute bucket, which is the default TTL.
        cache_write_5m_tokens: pick(
            current.cache_write_5m_tokens,
            if five_minute > 0 || one_hour > 0 {
                five_minute
            } else {
                total_writes
            },
        ),
        cache_write_1h_tokens: pick(current.cache_write_1h_tokens, one_hour),
    }
}

fn pick(current: u32, incoming: u32) -> u32 {
    if incoming > 0 {
        incoming
    } else {
        current
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// Feeds a sequence of `(event, payload)` pairs through the decoder.
    fn run(events: &[(&str, Value)]) -> (Vec<StreamEvent>, AnthropicDecoder) {
        let mut decoder = AnthropicDecoder::new();
        let mut out = Vec::new();
        for (name, payload) in events {
            out.extend(
                decoder
                    .decode(name, &payload.to_string())
                    .expect("decode should succeed"),
            );
        }
        (out, decoder)
    }

    fn text_of(events: &[StreamEvent]) -> String {
        events
            .iter()
            .filter_map(|e| match e {
                StreamEvent::TextDelta(text) => Some(text.as_str()),
                _ => None,
            })
            .collect()
    }

    #[test]
    fn decodes_a_plain_text_response() {
        let (events, decoder) = run(&[
            ("message_start", json!({ "type": "message_start", "message": { "usage": { "input_tokens": 10 } } })),
            ("content_block_start", json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "text", "text": "" } })),
            ("content_block_delta", json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "text_delta", "text": "Hello" } })),
            ("content_block_delta", json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "text_delta", "text": ", world" } })),
            ("content_block_stop", json!({ "type": "content_block_stop", "index": 0 })),
            ("message_delta", json!({ "type": "message_delta", "delta": { "stop_reason": "end_turn" }, "usage": { "output_tokens": 5 } })),
            ("message_stop", json!({ "type": "message_stop" })),
        ]);

        assert_eq!(text_of(&events), "Hello, world");
        assert!(decoder.finished());

        let Some(StreamEvent::Done { stop_reason, usage }) = events.last() else {
            panic!("expected a terminal event, got {events:?}");
        };
        assert_eq!(stop_reason.as_deref(), Some("end_turn"));
        assert_eq!(usage.input_tokens, 10);
        assert_eq!(usage.output_tokens, 5);
    }

    #[test]
    fn assembles_tool_arguments_from_fragments() {
        let (events, _) = run(&[
            ("content_block_start", json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "tool_use", "id": "toolu_1", "name": "read_file" } })),
            ("content_block_delta", json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "input_json_delta", "partial_json": "{\"pa" } })),
            ("content_block_delta", json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "input_json_delta", "partial_json": "th\":\"a.rs\"}" } })),
            ("content_block_stop", json!({ "type": "content_block_stop", "index": 0 })),
        ]);

        assert_eq!(
            events.len(),
            1,
            "nothing should be emitted before the block closes"
        );
        let StreamEvent::ToolUse(Content::ToolUse {
            id,
            name,
            input_json,
            ..
        }) = &events[0]
        else {
            panic!("expected a tool use, got {events:?}");
        };
        assert_eq!(id, "toolu_1");
        assert_eq!(name, "read_file");
        assert_eq!(input_json, r#"{"path":"a.rs"}"#);
        serde_json::from_str::<Value>(input_json).expect("assembled JSON should be valid");
    }

    #[test]
    fn a_tool_call_with_no_arguments_yields_an_empty_object() {
        let (events, _) = run(&[
            ("content_block_start", json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "tool_use", "id": "t", "name": "doctor" } })),
            ("content_block_stop", json!({ "type": "content_block_stop", "index": 0 })),
        ]);

        let StreamEvent::ToolUse(Content::ToolUse { input_json, .. }) = &events[0] else {
            panic!("expected a tool use");
        };
        assert_eq!(input_json, "{}");
    }

    #[test]
    fn interleaves_two_concurrent_blocks_by_index() {
        let (events, _) = run(&[
            ("content_block_start", json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "text", "text": "" } })),
            ("content_block_start", json!({ "type": "content_block_start", "index": 1, "content_block": { "type": "tool_use", "id": "t1", "name": "a" } })),
            ("content_block_delta", json!({ "type": "content_block_delta", "index": 1, "delta": { "type": "input_json_delta", "partial_json": "{\"x\":1}" } })),
            ("content_block_delta", json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "text_delta", "text": "hi" } })),
            ("content_block_stop", json!({ "type": "content_block_stop", "index": 1 })),
            ("content_block_stop", json!({ "type": "content_block_stop", "index": 0 })),
        ]);

        assert_eq!(text_of(&events), "hi");
        let tool = events.iter().find(|e| matches!(e, StreamEvent::ToolUse(_)));
        let Some(StreamEvent::ToolUse(Content::ToolUse { input_json, .. })) = tool else {
            panic!("expected a tool use");
        };
        assert_eq!(input_json, r#"{"x":1}"#, "fragments leaked between blocks");
    }

    #[test]
    fn decodes_thinking_with_its_signature() {
        let (events, _) = run(&[
            ("content_block_start", json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "thinking", "thinking": "" } })),
            ("content_block_delta", json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "thinking_delta", "thinking": "let me " } })),
            ("content_block_delta", json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "thinking_delta", "thinking": "consider" } })),
            ("content_block_delta", json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "signature_delta", "signature": "sig123" } })),
            ("content_block_stop", json!({ "type": "content_block_stop", "index": 0 })),
        ]);

        let deltas: String = events
            .iter()
            .filter_map(|e| match e {
                StreamEvent::ThinkingDelta(text) => Some(text.as_str()),
                _ => None,
            })
            .collect();
        assert_eq!(deltas, "let me consider");

        let Some(StreamEvent::ThinkingDone(Content::Thinking { text, signature })) = events
            .iter()
            .find(|e| matches!(e, StreamEvent::ThinkingDone(_)))
        else {
            panic!("expected a completed thinking block, got {events:?}");
        };
        assert_eq!(text, "let me consider");
        assert_eq!(
            signature.as_deref(),
            Some("sig123"),
            "the signature must survive; the provider rejects replays without it"
        );
    }

    #[test]
    fn preserves_redacted_thinking_verbatim() {
        let (events, _) = run(&[
            ("content_block_start", json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "redacted_thinking", "data": "opaque-payload" } })),
            ("content_block_stop", json!({ "type": "content_block_stop", "index": 0 })),
        ]);

        let Some(StreamEvent::ThinkingDone(Content::RedactedThinking { data })) = events.first()
        else {
            panic!("expected redacted thinking, got {events:?}");
        };
        assert_eq!(data, "opaque-payload");
    }

    #[test]
    fn accumulates_cache_usage() {
        let (events, _) = run(&[
            ("message_start", json!({ "type": "message_start", "message": { "usage": {
                "input_tokens": 100,
                "cache_read_input_tokens": 900,
                "cache_creation_input_tokens": 50
            } } })),
            ("message_delta", json!({ "type": "message_delta", "delta": {}, "usage": { "output_tokens": 20 } })),
            ("message_stop", json!({ "type": "message_stop" })),
        ]);

        let Some(StreamEvent::Done { usage, .. }) = events.last() else {
            panic!("expected a terminal event");
        };
        assert_eq!(usage.input_tokens, 100);
        assert_eq!(usage.cache_read_tokens, 900);
        assert_eq!(usage.cache_write_5m_tokens, 50);
        assert_eq!(usage.output_tokens, 20);
        assert_eq!(usage.total(), 1070);
    }

    #[test]
    fn splits_cache_writes_by_ttl_when_itemised() {
        let (events, _) = run(&[
            ("message_start", json!({ "type": "message_start", "message": { "usage": {
                "cache_creation_input_tokens": 70,
                "cache_creation": { "ephemeral_5m_input_tokens": 20, "ephemeral_1h_input_tokens": 50 }
            } } })),
            ("message_stop", json!({ "type": "message_stop" })),
        ]);

        let Some(StreamEvent::Done { usage, .. }) = events.last() else {
            panic!("expected a terminal event");
        };
        assert_eq!(usage.cache_write_5m_tokens, 20);
        assert_eq!(usage.cache_write_1h_tokens, 50);
        assert_eq!(usage.cache_write_tokens(), 70);
    }

    #[test]
    fn a_ping_produces_nothing() {
        let mut decoder = AnthropicDecoder::new();
        assert!(decoder
            .decode("ping", r#"{"type":"ping"}"#)
            .unwrap()
            .is_empty());
        assert!(decoder.decode("ping", "").unwrap().is_empty());
    }

    #[test]
    fn an_unknown_event_is_ignored() {
        let mut decoder = AnthropicDecoder::new();
        let events = decoder
            .decode("something_new", r#"{"type":"something_new"}"#)
            .expect("unknown events must not fail the stream");
        assert!(events.is_empty());
    }

    #[test]
    fn an_error_event_becomes_an_error() {
        let mut decoder = AnthropicDecoder::new();
        let result = decoder.decode(
            "error",
            r#"{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"#,
        );

        let error = result.expect_err("an error event should fail the stream");
        assert!(error.to_string().contains("Overloaded"));
        assert!(error.is_retryable(), "an overload should be retried");
    }

    #[test]
    fn malformed_json_is_reported_as_a_protocol_error() {
        let mut decoder = AnthropicDecoder::new();
        let error = decoder
            .decode("message_start", "{not json")
            .expect_err("should fail");
        assert!(matches!(error, LlmError::Protocol(_)));
    }

    #[test]
    fn a_stream_without_a_terminal_event_is_not_finished() {
        let (_, decoder) = run(&[
            ("content_block_start", json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "text", "text": "" } })),
            ("content_block_delta", json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "text_delta", "text": "truncated" } })),
        ]);
        assert!(
            !decoder.finished(),
            "a truncated stream must not look like a complete one"
        );
    }

    #[test]
    fn deltas_for_an_unopened_block_are_ignored() {
        let mut decoder = AnthropicDecoder::new();
        let events = decoder
            .decode(
                "content_block_delta",
                &json!({ "type": "content_block_delta", "index": 7, "delta": { "type": "text_delta", "text": "orphan" } }).to_string(),
            )
            .expect("should not fail");
        assert!(events.is_empty());
    }

    #[test]
    fn an_empty_text_delta_emits_nothing() {
        let (events, _) = run(&[
            ("content_block_start", json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "text", "text": "" } })),
            ("content_block_delta", json!({ "type": "content_block_delta", "index": 0, "delta": { "type": "text_delta", "text": "" } })),
        ]);
        assert!(events.is_empty());
    }

    #[test]
    fn never_panics_on_malformed_but_parseable_events() {
        let samples = [
            json!({}),
            json!({ "type": "content_block_start" }),
            json!({ "type": "content_block_start", "index": 0 }),
            json!({ "type": "content_block_delta", "index": 0 }),
            json!({ "type": "content_block_stop" }),
            json!({ "type": "message_delta" }),
            json!({ "type": "message_start", "message": {} }),
            json!({ "type": "content_block_start", "index": 0, "content_block": { "type": "unknown_kind" } }),
        ];

        let mut decoder = AnthropicDecoder::new();
        for sample in samples {
            let _ = decoder.decode("", &sample.to_string());
        }
    }
}
