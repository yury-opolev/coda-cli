//! Building Anthropic Messages requests.
//!
//! The interesting part is prompt caching. Anthropic caches by prefix, so a
//! `cache_control` marker means "everything up to here is reusable". Markers
//! are limited and misplacing one silently costs money on every turn: too far
//! forward and the cache misses whenever the tail changes, too far back and
//! most of the prompt is re-billed.
//!
//! The rules here mirror the C# `PromptCachePlanner`: cache the system prompt
//! and the tool list when they are stable, and cache the conversation prefix up
//! to the last stable message.

use serde_json::{json, Map, Value};

use crate::message::{ChatRequest, Content, Effort, Message};

/// Anthropic allows at most four cache breakpoints per request.
pub const MAX_BREAKPOINTS: usize = 4;

/// Below this the cache costs more than it saves, so no marker is placed.
const MIN_CACHEABLE_CHARS: usize = 2048;

/// Serialises a request into the Anthropic Messages wire format.
pub fn build(request: &ChatRequest) -> Value {
    let mut body = Map::new();
    body.insert("model".into(), json!(request.model));
    body.insert("max_tokens".into(), json!(request.max_tokens));
    body.insert("stream".into(), json!(true));

    if let Some(system) = &request.system {
        body.insert("system".into(), build_system(system, request));
    }

    if !request.tools.is_empty() {
        body.insert("tools".into(), build_tools(request));
    }

    if let Some(choice) = &request.tool_choice {
        body.insert("tool_choice".into(), json!({ "type": choice }));
    }

    if let Some(effort) = request.effort {
        // Extended thinking is expressed as a token budget, not a level.
        body.insert(
            "thinking".into(),
            json!({
                "type": "enabled",
                "budget_tokens": thinking_budget(effort, request.max_tokens),
            }),
        );
    }

    body.insert("messages".into(), build_messages(request));
    Value::Object(body)
}

/// The system prompt, with a cache marker when it is worth one.
fn build_system(system: &str, request: &ChatRequest) -> Value {
    let mut block = json!({ "type": "text", "text": system });

    // A volatile tool list invalidates any prefix that precedes it, so caching
    // the system prompt would never hit.
    if system.len() >= MIN_CACHEABLE_CHARS && !request.tools_volatile {
        block["cache_control"] = cache_control(request.use_one_hour_ttl);
    }
    Value::Array(vec![block])
}

/// The tool list, with a trailing cache marker when stable.
fn build_tools(request: &ChatRequest) -> Value {
    let last = request.tools.len().saturating_sub(1);

    let tools: Vec<Value> = request
        .tools
        .iter()
        .enumerate()
        .map(|(index, tool)| {
            let schema: Value = serde_json::from_str(&tool.input_schema_json)
                .unwrap_or_else(|_| json!({ "type": "object" }));

            let mut value = json!({
                "name": tool.name,
                "description": tool.description,
                "input_schema": schema,
            });

            // One marker after the final tool caches the whole list.
            if index == last && !request.tools_volatile {
                value["cache_control"] = cache_control(request.use_one_hour_ttl);
            }
            value
        })
        .collect();

    Value::Array(tools)
}

fn cache_control(one_hour: bool) -> Value {
    if one_hour {
        json!({ "type": "ephemeral", "ttl": "1h" })
    } else {
        json!({ "type": "ephemeral" })
    }
}

/// Reasoning budget for an effort level, bounded by the response limit.
///
/// The budget must leave room for the answer itself, so it is capped below
/// `max_tokens` rather than allowed to consume all of it.
fn thinking_budget(effort: Effort, max_tokens: u32) -> u32 {
    let requested = match effort {
        Effort::Low => 2_048,
        Effort::Medium => 8_192,
        Effort::High => 16_384,
        Effort::Max => 32_768,
    };
    // Anthropic requires a minimum of 1024, and the budget must be strictly
    // less than max_tokens.
    requested.min(max_tokens.saturating_sub(1024).max(1024))
}

/// Serialises the conversation, placing the final cache marker.
fn build_messages(request: &ChatRequest) -> Value {
    // The last message is always changing, so the cacheable prefix ends before
    // it. Marking the last *stable* message keeps the growing conversation
    // mostly cached across turns.
    let breakpoint = cache_breakpoint(&request.messages);

    let messages: Vec<Value> = request
        .messages
        .iter()
        .enumerate()
        .map(|(index, message)| {
            let mut blocks = build_content(&message.content);
            if Some(index) == breakpoint {
                mark_last_block(&mut blocks, request.use_one_hour_ttl);
            }
            json!({ "role": message.role.as_str(), "content": blocks })
        })
        .collect();

    Value::Array(messages)
}

/// Index of the message that should carry the conversation cache marker.
///
/// Returns `None` when the conversation is too short for caching to pay off.
fn cache_breakpoint(messages: &[Message]) -> Option<usize> {
    if messages.len() < 3 {
        return None;
    }

    // Everything except the final exchange is stable.
    let candidate = messages.len() - 2;

    let prefix_size: usize = messages[..=candidate]
        .iter()
        .flat_map(|message| &message.content)
        .map(content_size)
        .sum();

    (prefix_size >= MIN_CACHEABLE_CHARS).then_some(candidate)
}

fn content_size(block: &Content) -> usize {
    match block {
        Content::Text(text) => text.len(),
        Content::ToolUse {
            name, input_json, ..
        } => name.len() + input_json.len(),
        Content::ToolResult { content, .. } => content.len(),
        Content::Thinking { text, .. } => text.len(),
        Content::RedactedThinking { data } => data.len(),
        // Images are large but billed differently; they do not drive the
        // decision to place a text cache marker.
        Content::Image { .. } => 0,
    }
}

fn mark_last_block(blocks: &mut Value, one_hour: bool) {
    if let Some(last) = blocks.as_array_mut().and_then(|array| array.last_mut()) {
        if let Some(object) = last.as_object_mut() {
            object.insert("cache_control".into(), cache_control(one_hour));
        }
    }
}

/// Serialises one message's content blocks.
fn build_content(content: &[Content]) -> Value {
    let blocks: Vec<Value> = content
        .iter()
        .map(|block| match block {
            Content::Text(text) => json!({ "type": "text", "text": text }),
            Content::ToolUse {
                id,
                name,
                input_json,
                ..
            } => {
                // Arguments are stored as a string but must go out as an
                // object; a malformed one becomes empty rather than failing
                // the whole request.
                let input: Value =
                    serde_json::from_str(input_json).unwrap_or_else(|_| json!({}));
                json!({ "type": "tool_use", "id": id, "name": name, "input": input })
            }
            Content::ToolResult {
                tool_use_id,
                content,
                is_error,
                ..
            } => json!({
                "type": "tool_result",
                "tool_use_id": tool_use_id,
                "content": content,
                "is_error": is_error,
            }),
            Content::Image { media_type, base64 } => json!({
                "type": "image",
                "source": { "type": "base64", "media_type": media_type, "data": base64 },
            }),
            Content::Thinking { text, signature } => {
                let mut value = json!({ "type": "thinking", "thinking": text });
                // Replaying a thinking block without its signature is rejected.
                if let Some(signature) = signature {
                    value["signature"] = json!(signature);
                }
                value
            }
            Content::RedactedThinking { data } => {
                json!({ "type": "redacted_thinking", "data": data })
            }
        })
        .collect();

    Value::Array(blocks)
}

/// Counts the cache breakpoints in a built request, for tests and diagnostics.
pub fn count_breakpoints(body: &Value) -> usize {
    fn walk(value: &Value, count: &mut usize) {
        match value {
            Value::Object(map) => {
                if map.contains_key("cache_control") {
                    *count += 1;
                }
                for nested in map.values() {
                    walk(nested, count);
                }
            }
            Value::Array(items) => {
                for item in items {
                    walk(item, count);
                }
            }
            _ => {}
        }
    }

    let mut count = 0;
    walk(body, &mut count);
    count
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::message::{Correlation, Role, ToolDefinition};

    fn long_text(chars: usize) -> String {
        "x".repeat(chars)
    }

    fn request_with(messages: Vec<Message>) -> ChatRequest {
        ChatRequest::new("claude-opus-5", messages)
    }

    #[test]
    fn builds_a_minimal_streaming_request() {
        let body = build(&request_with(vec![Message::user("hi")]));

        assert_eq!(body["model"], "claude-opus-5");
        assert_eq!(body["stream"], true);
        assert_eq!(body["max_tokens"], 4096);
        assert_eq!(body["messages"][0]["role"], "user");
        assert_eq!(body["messages"][0]["content"][0]["text"], "hi");
    }

    #[test]
    fn omits_tools_and_system_when_unset() {
        let body = build(&request_with(vec![Message::user("hi")]));
        assert!(body.get("tools").is_none());
        assert!(body.get("system").is_none());
        assert!(body.get("thinking").is_none());
    }

    #[test]
    fn serialises_tools_with_their_schema() {
        let request = request_with(vec![Message::user("hi")]).with_tools(vec![
            ToolDefinition::new(
                "read_file",
                "Reads a file",
                r#"{"type":"object","properties":{"path":{"type":"string"}}}"#,
            ),
        ]);
        let body = build(&request);

        assert_eq!(body["tools"][0]["name"], "read_file");
        assert_eq!(body["tools"][0]["description"], "Reads a file");
        assert_eq!(body["tools"][0]["input_schema"]["type"], "object");
    }

    #[test]
    fn a_malformed_tool_schema_degrades_to_an_empty_object() {
        let request = request_with(vec![Message::user("hi")])
            .with_tools(vec![ToolDefinition::new("t", "d", "not json")]);
        let body = build(&request);

        // The request must still be sendable.
        assert_eq!(body["tools"][0]["input_schema"]["type"], "object");
    }

    #[test]
    fn serialises_a_tool_use_input_as_an_object_not_a_string() {
        let message = Message::new(
            Role::Assistant,
            vec![Content::ToolUse {
                id: "t1".into(),
                name: "read_file".into(),
                input_json: r#"{"path":"a.rs"}"#.into(),
                correlation: Correlation::default(),
            }],
        );
        let body = build(&request_with(vec![message]));

        assert_eq!(body["messages"][0]["content"][0]["input"]["path"], "a.rs");
        assert!(
            body["messages"][0]["content"][0]["input"].is_object(),
            "input must be an object, not a JSON string"
        );
    }

    #[test]
    fn a_malformed_tool_input_degrades_to_an_empty_object() {
        let message = Message::new(
            Role::Assistant,
            vec![Content::ToolUse {
                id: "t1".into(),
                name: "n".into(),
                input_json: "{broken".into(),
                correlation: Correlation::default(),
            }],
        );
        let body = build(&request_with(vec![message]));
        assert!(body["messages"][0]["content"][0]["input"].is_object());
    }

    #[test]
    fn serialises_a_tool_result() {
        let message = Message::new(
            Role::User,
            vec![Content::ToolResult {
                tool_use_id: "t1".into(),
                content: "file body".into(),
                is_error: true,
                correlation: Correlation::default(),
                status: None,
            }],
        );
        let body = build(&request_with(vec![message]));

        let block = &body["messages"][0]["content"][0];
        assert_eq!(block["type"], "tool_result");
        assert_eq!(block["tool_use_id"], "t1");
        assert_eq!(block["is_error"], true);
    }

    #[test]
    fn replays_a_thinking_block_with_its_signature() {
        let message = Message::new(
            Role::Assistant,
            vec![Content::Thinking {
                text: "reasoning".into(),
                signature: Some("sig".into()),
            }],
        );
        let body = build(&request_with(vec![message]));

        let block = &body["messages"][0]["content"][0];
        assert_eq!(block["type"], "thinking");
        assert_eq!(
            block["signature"], "sig",
            "the provider rejects a replay without the signature"
        );
    }

    #[test]
    fn omits_the_signature_when_there_was_none() {
        let message = Message::new(
            Role::Assistant,
            vec![Content::Thinking {
                text: "reasoning".into(),
                signature: None,
            }],
        );
        let body = build(&request_with(vec![message]));
        assert!(body["messages"][0]["content"][0].get("signature").is_none());
    }

    #[test]
    fn serialises_an_image_as_a_base64_source() {
        let message = Message::new(
            Role::User,
            vec![Content::Image {
                media_type: "image/png".into(),
                base64: "AAAA".into(),
            }],
        );
        let body = build(&request_with(vec![message]));

        let source = &body["messages"][0]["content"][0]["source"];
        assert_eq!(source["type"], "base64");
        assert_eq!(source["media_type"], "image/png");
        assert_eq!(source["data"], "AAAA");
    }

    #[test]
    fn caches_a_large_system_prompt() {
        let request = request_with(vec![Message::user("hi")]).with_system(long_text(4000));
        let body = build(&request);

        assert_eq!(body["system"][0]["cache_control"]["type"], "ephemeral");
    }

    #[test]
    fn does_not_cache_a_small_system_prompt() {
        let request = request_with(vec![Message::user("hi")]).with_system("be brief");
        let body = build(&request);

        assert!(
            body["system"][0].get("cache_control").is_none(),
            "caching a tiny prefix costs more than it saves"
        );
    }

    #[test]
    fn does_not_cache_when_the_tool_list_is_volatile() {
        let mut request = request_with(vec![Message::user("hi")]).with_system(long_text(4000));
        request.tools_volatile = true;
        let body = build(&request);

        assert!(
            body["system"][0].get("cache_control").is_none(),
            "a volatile tool list invalidates the prefix, so the marker would never hit"
        );
    }

    #[test]
    fn marks_only_the_final_tool() {
        let request = request_with(vec![Message::user("hi")]).with_tools(vec![
            ToolDefinition::new("a", "d", "{}"),
            ToolDefinition::new("b", "d", "{}"),
            ToolDefinition::new("c", "d", "{}"),
        ]);
        let body = build(&request);

        assert!(body["tools"][0].get("cache_control").is_none());
        assert!(body["tools"][1].get("cache_control").is_none());
        assert!(body["tools"][2].get("cache_control").is_some());
    }

    #[test]
    fn caches_the_conversation_prefix_but_not_the_last_message() {
        let messages = vec![
            Message::user(long_text(1500)),
            Message::assistant(long_text(1500)),
            Message::user("what next?"),
        ];
        let body = build(&request_with(messages));

        assert!(
            body["messages"][1]["content"][0]
                .get("cache_control")
                .is_some(),
            "the last stable message should carry the marker"
        );
        assert!(
            body["messages"][2]["content"][0]
                .get("cache_control")
                .is_none(),
            "the newest message changes every turn and must not be marked"
        );
    }

    #[test]
    fn does_not_cache_a_short_conversation() {
        let body = build(&request_with(vec![
            Message::user("hi"),
            Message::assistant("hello"),
            Message::user("again"),
        ]));
        assert_eq!(count_breakpoints(&body), 0);
    }

    #[test]
    fn does_not_cache_a_conversation_with_too_few_messages() {
        let body = build(&request_with(vec![
            Message::user(long_text(5000)),
            Message::assistant(long_text(5000)),
        ]));
        assert_eq!(
            count_breakpoints(&body),
            0,
            "there is no stable prefix before the final exchange"
        );
    }

    #[test]
    fn never_exceeds_the_provider_breakpoint_limit() {
        let request = ChatRequest {
            system: Some(long_text(4000)),
            tools: vec![
                ToolDefinition::new("a", "d", "{}"),
                ToolDefinition::new("b", "d", "{}"),
            ],
            ..request_with(vec![
                Message::user(long_text(3000)),
                Message::assistant(long_text(3000)),
                Message::user(long_text(3000)),
                Message::assistant(long_text(3000)),
                Message::user("next"),
            ])
        };
        let body = build(&request);

        let count = count_breakpoints(&body);
        assert!(
            count <= MAX_BREAKPOINTS,
            "{count} breakpoints exceeds the provider limit of {MAX_BREAKPOINTS}"
        );
        assert!(count > 0, "a large request should use caching");
    }

    #[test]
    fn requests_the_one_hour_ttl_when_asked() {
        let mut request = request_with(vec![Message::user("hi")]).with_system(long_text(4000));
        request.use_one_hour_ttl = true;
        let body = build(&request);

        assert_eq!(body["system"][0]["cache_control"]["ttl"], "1h");
    }

    #[test]
    fn effort_becomes_a_thinking_budget() {
        for (effort, expected) in [
            (Effort::Low, 2048),
            (Effort::Medium, 8192),
            (Effort::High, 16384),
            (Effort::Max, 32768),
        ] {
            let request = request_with(vec![Message::user("hi")])
                .with_max_tokens(64_000)
                .with_effort(Some(effort));
            let body = build(&request);

            assert_eq!(body["thinking"]["type"], "enabled");
            assert_eq!(body["thinking"]["budget_tokens"], expected, "{effort:?}");
        }
    }

    #[test]
    fn the_thinking_budget_leaves_room_for_the_answer() {
        let request = request_with(vec![Message::user("hi")])
            .with_max_tokens(4096)
            .with_effort(Some(Effort::Max));
        let body = build(&request);

        let budget = body["thinking"]["budget_tokens"].as_u64().unwrap();
        assert!(
            budget < 4096,
            "a budget of {budget} would leave no room for the response"
        );
        assert!(budget >= 1024, "the provider requires at least 1024");
    }

    #[test]
    fn serialises_a_tool_choice() {
        let mut request = request_with(vec![Message::user("hi")]);
        request.tool_choice = Some("any".into());
        let body = build(&request);
        assert_eq!(body["tool_choice"]["type"], "any");
    }

    #[test]
    fn the_built_request_is_always_serialisable() {
        let request = ChatRequest {
            system: Some(long_text(3000)),
            tools: vec![ToolDefinition::new("t", "d", "{}")],
            effort: Some(Effort::High),
            ..request_with(vec![
                Message::user("one"),
                Message::assistant("two"),
                Message::user("three"),
            ])
        };
        let body = build(&request);
        serde_json::to_string(&body).expect("the request must serialise");
    }
}
