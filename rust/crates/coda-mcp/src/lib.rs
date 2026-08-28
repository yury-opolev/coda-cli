//! MCP (Model Context Protocol) client for the Coda agent.
//!
//! This crate connects to MCP servers over stdio (newline-delimited JSON-RPC 2.0),
//! performs the initialize handshake, enumerates their tools, and bridges those
//! tools into the agent's `Tool` registry.
//!
//! # Non-negotiables
//!
//! - Every wait (handshake, `tools/call`, shutdown) is bounded; a server that
//!   never responds is killed after a configurable deadline.
//! - One server failing to start or dying leaves the others and the session
//!   unaffected.
//! - Output from `tools/call` is capped at [`MAX_TOOL_OUTPUT_CHARS`] characters,
//!   matching the built-in tools' contract.

pub mod config;
pub mod error;
pub mod manager;
pub mod tool;

pub(crate) mod client;
pub(crate) mod process;
pub(crate) mod transport;

pub use error::{McpConnectError, McpError};
pub use manager::McpClientManager;
pub use tool::McpTool;

/// Hard cap on tool result text forwarded to the model, matching the built-in
/// tool limit and preventing a misbehaving MCP server from flooding the context.
pub const MAX_TOOL_OUTPUT_CHARS: usize = 100_000;
