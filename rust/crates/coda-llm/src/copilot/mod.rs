//! GitHub Copilot provider.
//!
//! Proxies the OpenAI chat-completions, OpenAI Responses, and Anthropic Messages
//! APIs under a single `LlmClient` implementation. The right protocol is chosen
//! per model from the `/models` endpoint; the choice is cached for 15 minutes.

pub mod chat;
pub mod client;
pub mod models;
pub mod responses;

pub use client::{CopilotClient, CopilotConfig};
pub use models::CopilotEndpoint;
