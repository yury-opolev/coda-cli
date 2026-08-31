//! Provider clients and the neutral chat model used by the Coda engine.
//!
//! The engine works in provider-neutral types; each client translates them to
//! and from its wire format. Protocol decoding is kept pure and separate from
//! transport so it can be tested without a network.

pub mod anthropic;
pub mod client;
pub mod copilot;
pub mod credential_source;
pub mod error;
pub mod message;
pub mod reasoning;
pub mod retry;
pub mod sse;
pub(crate) mod pump;

pub use client::{CompletedResponse, LlmClient, ResponseStream};
pub use copilot::{CopilotClient, CopilotConfig};
pub use credential_source::CredentialSource;
pub use error::{FailureKind, LlmError};
pub use reasoning::{ReasoningCapability, resolve as resolve_reasoning, resolve_applied_level};
pub use retry::RetryPolicy;
pub use message::{
    ChatRequest, Content, Correlation, Effort, Message, ModelInfo, Role, ToolDefinition, Usage,
};
pub use sse::{SseDecoder, SseEvent};
