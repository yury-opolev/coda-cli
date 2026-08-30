//! MCP (Model Context Protocol) client for the Coda agent.
//!
//! This crate connects to MCP servers over stdio (newline-delimited JSON-RPC
//! 2.0) or HTTP (Streamable HTTP transport), performs the initialize
//! handshake, enumerates their tools, and bridges those tools into the
//! agent's `Tool` registry.
//!
//! # Non-negotiables
//!
//! - Every wait (handshake, `tools/call`, shutdown) is bounded; a server
//!   that never responds is killed after a configurable deadline.
//! - One server failing to start or dying leaves the others and the session
//!   unaffected.
//! - Output from `tools/call` is capped at [`MAX_TOOL_OUTPUT_CHARS`]
//!   characters, matching the built-in tools' contract.
//! - HTTP connections are SSRF-guarded: only `https` is allowed except for
//!   loopback, embedded credentials are rejected, the host is resolved once,
//!   all resolved IPs are checked against blocked ranges, and the reqwest
//!   client is pinned to the vetted address.
//! - An MCP server can NEVER waive its own approval: `McpTool::is_read_only`
//!   unconditionally returns `false`.

pub mod auth;
pub mod config;
pub mod error;
pub mod manager;
pub mod management_tools;
pub mod secret;
pub mod tool;

pub(crate) mod client;
pub(crate) mod http_client;
pub(crate) mod process;
pub(crate) mod ssrf;
pub(crate) mod transport;

pub use error::{McpConnectError, McpError};
pub use manager::McpClientManager;
pub use tool::McpTool;

// Auth re-exports for consumers that need to configure HTTP servers.
pub use auth::{
    McpAuthConfig, McpAuthMode, McpAuthProvider, McpOAuthProvider,
    McpOAuthReauthenticator, McpAuthResult, StaticBearerAuthProvider,
};

/// Hard cap on tool result text forwarded to the model, matching the built-in
/// tool limit and preventing a misbehaving MCP server from flooding the context.
pub const MAX_TOOL_OUTPUT_CHARS: usize = 100_000;
