//! Error types for the MCP client.

use coda_proto::ResponseError;

/// Transport-level error (not an MCP protocol error).
#[derive(Debug, thiserror::Error)]
pub enum McpTransportError {
    #[error("connection closed")]
    ConnectionClosed,
    #[error("JSON serialisation failed: {0}")]
    Json(#[from] serde_json::Error),
    #[error("RPC error {}: {}", .0.code, .0.message)]
    Rpc(ResponseError),
}

/// Errors that can occur when connecting to or calling an MCP server.
#[derive(Debug, thiserror::Error)]
pub enum McpError {
    #[error("spawning MCP server '{program}' failed: {source}")]
    Spawn {
        program: String,
        #[source]
        source: std::io::Error,
    },
    #[error("MCP server handshake timed out")]
    ConnectTimeout,
    #[error("MCP server '{server}' returned an error during '{phase}': {message}")]
    Protocol {
        server: String,
        phase: &'static str,
        message: String,
    },
    #[error("MCP transport error: {0}")]
    Transport(#[from] McpTransportError),
    #[error("MCP server stdout closed unexpectedly")]
    StdoutClosed,
}

/// A connect attempt that failed with a diagnostic context.
///
/// Returned from `McpClientManager::connect_all` for each server that could
/// not be started. The manager logs these and continues with the servers that
/// did start.
#[derive(Debug)]
pub struct McpConnectError {
    pub server_name: String,
    pub error: McpError,
}

impl std::fmt::Display for McpConnectError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "server '{}': {}", self.server_name, self.error)
    }
}
