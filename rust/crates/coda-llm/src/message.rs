//! The chat model shared by every provider.
//!
//! Providers differ wildly in their wire formats, so the engine works in these
//! neutral types and each client translates. Anything provider-specific that
//! must survive a round trip (a thinking block's signature, a tool-use id) is
//! carried here rather than reconstructed.

use serde::{Deserialize, Serialize};

/// Who produced a message.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Role {
    User,
    Assistant,
}

impl Role {
    pub fn as_str(self) -> &'static str {
        match self {
            Role::User => "user",
            Role::Assistant => "assistant",
        }
    }
}

/// Ids tying a tool call to the turn and batch that produced it.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Correlation {
    pub root_turn_id: Option<String>,
    pub activity_id: Option<String>,
    pub source_id: Option<String>,
}

impl Correlation {
    pub fn is_empty(&self) -> bool {
        self.root_turn_id.is_none() && self.activity_id.is_none() && self.source_id.is_none()
    }
}

/// One piece of a message.
#[derive(Debug, Clone, PartialEq)]
pub enum Content {
    Text(String),
    /// The model asked to run a tool.
    ToolUse {
        id: String,
        name: String,
        /// Arguments as a JSON string, kept verbatim so re-serialising cannot
        /// change what the model actually asked for.
        input_json: String,
        correlation: Correlation,
    },
    /// The result of running a tool, sent back to the model.
    ToolResult {
        tool_use_id: String,
        content: String,
        is_error: bool,
        correlation: Correlation,
        /// Terminal status, when the runtime tracked one.
        status: Option<String>,
    },
    Image {
        media_type: String,
        base64: String,
    },
    /// Extended reasoning. The signature must be preserved and replayed, or
    /// the provider rejects the follow-up request.
    Thinking {
        text: String,
        signature: Option<String>,
    },
    /// Reasoning the provider redacted; opaque, but replayed verbatim.
    RedactedThinking { data: String },
}

impl Content {
    pub fn text(text: impl Into<String>) -> Self {
        Content::Text(text.into())
    }

    /// The readable text of this block, if it has any.
    pub fn as_text(&self) -> Option<&str> {
        match self {
            Content::Text(text) => Some(text),
            Content::Thinking { text, .. } => Some(text),
            _ => None,
        }
    }
}

/// One turn of the conversation.
#[derive(Debug, Clone, PartialEq)]
pub struct Message {
    pub role: Role,
    pub content: Vec<Content>,
}

impl Message {
    pub fn user(text: impl Into<String>) -> Self {
        Self {
            role: Role::User,
            content: vec![Content::text(text)],
        }
    }

    pub fn assistant(text: impl Into<String>) -> Self {
        Self {
            role: Role::Assistant,
            content: vec![Content::text(text)],
        }
    }

    pub fn new(role: Role, content: Vec<Content>) -> Self {
        Self { role, content }
    }

    /// All text blocks joined, which is what a transcript shows.
    pub fn text(&self) -> String {
        self.content
            .iter()
            .filter_map(|block| match block {
                Content::Text(text) => Some(text.as_str()),
                _ => None,
            })
            .collect::<Vec<_>>()
            .join("")
    }

    pub fn tool_uses(&self) -> impl Iterator<Item = &Content> {
        self.content
            .iter()
            .filter(|block| matches!(block, Content::ToolUse { .. }))
    }
}

/// A tool offered to the model.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolDefinition {
    pub name: String,
    pub description: String,
    /// JSON Schema for the tool's arguments, as a JSON string.
    pub input_schema_json: String,
}

impl ToolDefinition {
    pub fn new(
        name: impl Into<String>,
        description: impl Into<String>,
        input_schema_json: impl Into<String>,
    ) -> Self {
        Self {
            name: name.into(),
            description: description.into(),
            input_schema_json: input_schema_json.into(),
        }
    }
}

/// How much reasoning effort to request, where the provider supports it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Effort {
    Low,
    Medium,
    High,
    Max,
}

impl Effort {
    pub fn parse(raw: &str) -> Option<Effort> {
        match raw.trim().to_ascii_lowercase().as_str() {
            "low" => Some(Effort::Low),
            "medium" => Some(Effort::Medium),
            "high" => Some(Effort::High),
            "max" => Some(Effort::Max),
            _ => None,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Effort::Low => "low",
            Effort::Medium => "medium",
            Effort::High => "high",
            Effort::Max => "max",
        }
    }
}

/// A single request to a provider.
#[derive(Debug, Clone, PartialEq)]
pub struct ChatRequest {
    pub model: String,
    pub max_tokens: u32,
    pub system: Option<String>,
    pub messages: Vec<Message>,
    pub tools: Vec<ToolDefinition>,
    pub effort: Option<Effort>,
    /// The tool list changes between turns, so it must not be cached.
    pub tools_volatile: bool,
    pub tool_choice: Option<String>,
    /// Request the one-hour cache TTL rather than the default five minutes.
    pub use_one_hour_ttl: bool,
}

impl ChatRequest {
    pub fn new(model: impl Into<String>, messages: Vec<Message>) -> Self {
        Self {
            model: model.into(),
            max_tokens: 4096,
            system: None,
            messages,
            tools: Vec::new(),
            effort: None,
            tools_volatile: false,
            tool_choice: None,
            use_one_hour_ttl: false,
        }
    }

    pub fn with_system(mut self, system: impl Into<String>) -> Self {
        self.system = Some(system.into());
        self
    }

    pub fn with_tools(mut self, tools: Vec<ToolDefinition>) -> Self {
        self.tools = tools;
        self
    }

    pub fn with_max_tokens(mut self, max_tokens: u32) -> Self {
        self.max_tokens = max_tokens;
        self
    }

    pub fn with_effort(mut self, effort: Option<Effort>) -> Self {
        self.effort = effort;
        self
    }
}

/// Token counts for one request.
///
/// Cache writes are split by TTL because they are billed differently.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Usage {
    pub input_tokens: u32,
    pub output_tokens: u32,
    pub cache_read_tokens: u32,
    pub cache_write_5m_tokens: u32,
    pub cache_write_1h_tokens: u32,
}

impl Usage {
    pub const ZERO: Usage = Usage {
        input_tokens: 0,
        output_tokens: 0,
        cache_read_tokens: 0,
        cache_write_5m_tokens: 0,
        cache_write_1h_tokens: 0,
    };

    pub fn cache_write_tokens(&self) -> u32 {
        self.cache_write_5m_tokens + self.cache_write_1h_tokens
    }

    /// Every token that counted as input, including cache traffic.
    pub fn total_input_tokens(&self) -> u32 {
        self.input_tokens + self.cache_read_tokens + self.cache_write_tokens()
    }

    pub fn total(&self) -> u32 {
        self.total_input_tokens() + self.output_tokens
    }

    pub fn has_cache_activity(&self) -> bool {
        self.cache_read_tokens > 0 || self.cache_write_tokens() > 0
    }

    /// Accumulates another request's usage.
    pub fn add(self, other: Usage) -> Usage {
        Usage {
            input_tokens: self.input_tokens + other.input_tokens,
            output_tokens: self.output_tokens + other.output_tokens,
            cache_read_tokens: self.cache_read_tokens + other.cache_read_tokens,
            cache_write_5m_tokens: self.cache_write_5m_tokens + other.cache_write_5m_tokens,
            cache_write_1h_tokens: self.cache_write_1h_tokens + other.cache_write_1h_tokens,
        }
    }
}

/// A model the provider offers.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelInfo {
    pub id: String,
    pub display_name: Option<String>,
    pub context_limit: Option<u32>,
    /// Endpoints the model supports, when the provider advertises them.
    pub supported_endpoints: Vec<String>,
}

impl ModelInfo {
    pub fn new(id: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            display_name: None,
            context_limit: None,
            supported_endpoints: Vec::new(),
        }
    }

    pub fn label(&self) -> &str {
        self.display_name.as_deref().unwrap_or(&self.id)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_user_message_holds_one_text_block() {
        let message = Message::user("hello");
        assert_eq!(message.role, Role::User);
        assert_eq!(message.text(), "hello");
    }

    #[test]
    fn message_text_joins_every_text_block() {
        let message = Message::new(
            Role::Assistant,
            vec![Content::text("one "), Content::text("two")],
        );
        assert_eq!(message.text(), "one two");
    }

    #[test]
    fn message_text_ignores_non_text_blocks() {
        let message = Message::new(
            Role::Assistant,
            vec![
                Content::text("visible"),
                Content::ToolUse {
                    id: "t1".into(),
                    name: "read_file".into(),
                    input_json: "{}".into(),
                    correlation: Correlation::default(),
                },
                Content::Thinking {
                    text: "hidden".into(),
                    signature: None,
                },
            ],
        );
        assert_eq!(message.text(), "visible");
    }

    #[test]
    fn tool_uses_are_enumerable() {
        let message = Message::new(
            Role::Assistant,
            vec![
                Content::text("x"),
                Content::ToolUse {
                    id: "t1".into(),
                    name: "a".into(),
                    input_json: "{}".into(),
                    correlation: Correlation::default(),
                },
                Content::ToolUse {
                    id: "t2".into(),
                    name: "b".into(),
                    input_json: "{}".into(),
                    correlation: Correlation::default(),
                },
            ],
        );
        assert_eq!(message.tool_uses().count(), 2);
    }

    #[test]
    fn thinking_text_is_readable_but_not_transcript_text() {
        let block = Content::Thinking {
            text: "reasoning".into(),
            signature: Some("sig".into()),
        };
        assert_eq!(block.as_text(), Some("reasoning"));
    }

    #[test]
    fn a_tool_use_block_has_no_readable_text() {
        let block = Content::ToolUse {
            id: "t".into(),
            name: "n".into(),
            input_json: "{}".into(),
            correlation: Correlation::default(),
        };
        assert_eq!(block.as_text(), None);
    }

    #[test]
    fn usage_sums_every_component() {
        let a = Usage {
            input_tokens: 10,
            output_tokens: 5,
            cache_read_tokens: 3,
            cache_write_5m_tokens: 2,
            cache_write_1h_tokens: 1,
        };
        let total = a.add(a);
        assert_eq!(total.input_tokens, 20);
        assert_eq!(total.output_tokens, 10);
        assert_eq!(total.cache_read_tokens, 6);
        assert_eq!(total.cache_write_tokens(), 6);
    }

    #[test]
    fn total_input_counts_cache_traffic() {
        let usage = Usage {
            input_tokens: 100,
            output_tokens: 50,
            cache_read_tokens: 900,
            cache_write_5m_tokens: 10,
            cache_write_1h_tokens: 5,
        };
        assert_eq!(usage.total_input_tokens(), 1015);
        assert_eq!(usage.total(), 1065);
        assert!(usage.has_cache_activity());
    }

    #[test]
    fn zero_usage_reports_no_cache_activity() {
        assert!(!Usage::ZERO.has_cache_activity());
        assert_eq!(Usage::ZERO.total(), 0);
    }

    #[test]
    fn parses_effort_levels_case_insensitively() {
        assert_eq!(Effort::parse("HIGH"), Some(Effort::High));
        assert_eq!(Effort::parse(" max "), Some(Effort::Max));
        assert_eq!(Effort::parse("auto"), None);
        assert_eq!(Effort::parse(""), None);
    }

    #[test]
    fn a_request_defaults_to_no_tools_and_no_effort() {
        let request = ChatRequest::new("m", vec![Message::user("hi")]);
        assert!(request.tools.is_empty());
        assert!(request.effort.is_none());
        assert_eq!(request.max_tokens, 4096);
    }

    #[test]
    fn request_builders_compose() {
        let request = ChatRequest::new("m", vec![Message::user("hi")])
            .with_system("be brief")
            .with_max_tokens(100)
            .with_effort(Some(Effort::High))
            .with_tools(vec![ToolDefinition::new("t", "d", "{}")]);

        assert_eq!(request.system.as_deref(), Some("be brief"));
        assert_eq!(request.max_tokens, 100);
        assert_eq!(request.effort, Some(Effort::High));
        assert_eq!(request.tools.len(), 1);
    }

    #[test]
    fn a_model_falls_back_to_its_id_for_display() {
        assert_eq!(ModelInfo::new("gpt-5").label(), "gpt-5");
        let named = ModelInfo {
            display_name: Some("GPT-5".into()),
            ..ModelInfo::new("gpt-5")
        };
        assert_eq!(named.label(), "GPT-5");
    }

    #[test]
    fn an_empty_correlation_is_reported_as_empty() {
        assert!(Correlation::default().is_empty());
        assert!(!Correlation {
            root_turn_id: Some("t".into()),
            ..Default::default()
        }
        .is_empty());
    }
}
