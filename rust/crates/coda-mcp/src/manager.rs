//! `McpClientManager` — connects all configured MCP servers and aggregates
//! their tools into the agent registry.
//!
//! # Design
//!
//! Each server gets its own `McpClient` (stdio) or `McpHttpClient` (HTTP).
//! The manager runs all connects concurrently; a server that fails to start
//! is logged and skipped without blocking the others or crashing the session.
//!
//! Tools are namespaced `mcp__{server}__{tool}` to guarantee no collision
//! with built-in names, and are returned as `Arc<dyn Tool>` ready for
//! insertion into any `ToolRegistry`.
//!
//! # Thread safety
//!
//! The manager is `Send + Sync`. Client handles are stored behind an
//! `Arc<RwLock<…>>` so `call_tool` can be called concurrently from the
//! agent loop's parallel tool execution paths.

use std::collections::HashMap;
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use tokio::sync::RwLock;

use coda_tool::Tool;

use crate::client::{McpClient, McpServerInfo, McpToolCallError, McpToolInfo, DEFAULT_CONNECT_TIMEOUT};
use crate::config::{self, McpConnectable, McpHttpConnectable};
use crate::error::{McpConnectError, McpError};
use crate::http_client::{McpHttpClient, DEFAULT_HTTP_CONNECT_TIMEOUT};
use crate::process::McpProcess;
use crate::tool::McpTool;

/// Default shutdown grace period when the manager is dropped.
const SHUTDOWN_GRACE: Duration = Duration::from_secs(5);

/// Unified client handle: either a stdio or HTTP MCP client.
enum AnyClient {
    Stdio(McpClient),
    Http(McpHttpClient),
}

impl AnyClient {
    fn server_info(&self) -> &McpServerInfo {
        match self {
            Self::Stdio(c) => &c.server_info,
            Self::Http(c) => &c.server_info,
        }
    }

    fn tools(&self) -> &[McpToolInfo] {
        match self {
            Self::Stdio(c) => &c.tools,
            Self::Http(c) => &c.tools,
        }
    }

    async fn call_tool(
        &self,
        name: &str,
        arguments: &serde_json::Value,
    ) -> Result<(String, bool), McpToolCallError> {
        match self {
            Self::Stdio(c) => c.call_tool(name, arguments).await,
            Self::Http(c) => c.call_tool(name, arguments).await.map_err(|e| {
                // Map the HTTP-specific error string into a Transport variant so
                // the manager's unified error path produces a good message.
                McpToolCallError::Transport(crate::error::McpTransportError::Other(e))
            }),
        }
    }

    async fn shutdown(self) {
        match self {
            Self::Stdio(c) => c.shutdown().await,
            Self::Http(c) => c.shutdown().await,
        }
    }
}

/// Per-server state held by the manager.
struct ServerEntry {
    client: AnyClient,
    /// Child process kept alive as long as the entry exists; `None` for HTTP.
    _process: Option<McpProcess>,
}

/// Connects configured MCP servers and exposes their tools.
pub struct McpClientManager {
    /// Per-server client, protected so `call_tool` is lock-free while the
    /// manager is stable and only serialised during connect/disconnect.
    servers: RwLock<HashMap<String, ServerEntry>>,
    tool_timeout: Duration,
}

impl McpClientManager {
    /// Create an empty manager.
    pub fn new() -> Self {
        Self { servers: RwLock::new(HashMap::new()), tool_timeout: McpClient::resolve_tool_timeout() }
    }

    /// Connect all enabled stdio and HTTP servers from the given `.mcp.json` paths.
    ///
    /// Servers are connected concurrently. Failures are collected and returned
    /// alongside the successfully connected servers; each failure is already
    /// logged at warn level so the caller does not have to re-log.
    ///
    /// This method is designed to be called once at startup; calling it again
    /// adds more servers without removing existing ones.
    pub async fn connect_all(
        &self,
        user_mcp: &Path,
        project_mcp: &Path,
    ) -> Vec<McpConnectError> {
        let connectable = config::load_connectable(user_mcp, project_mcp);
        let http_connectable = config::load_http_connectable(user_mcp, project_mcp);
        let mut errors = self.connect_many(connectable, None).await;
        errors.extend(self.connect_http_many(http_connectable, None).await);
        errors
    }

    /// Connect all servers from a supplied list, optionally overriding the
    /// per-server connect timeout (used in tests).
    pub async fn connect_many(
        &self,
        servers: Vec<McpConnectable>,
        connect_timeout_override: Option<Duration>,
    ) -> Vec<McpConnectError> {
        let connect_timeout = connect_timeout_override.unwrap_or(DEFAULT_CONNECT_TIMEOUT);
        let tool_timeout = self.tool_timeout;

        let futures: Vec<_> = servers
            .into_iter()
            .map(|cfg| {
                let name = cfg.name.clone();
                async move {
                    let result = connect_one_stdio(&cfg, connect_timeout, tool_timeout).await;
                    (name, result)
                }
            })
            .collect();

        let results = futures::future::join_all(futures).await;
        let mut errors = Vec::new();
        let mut guard = self.servers.write().await;

        for (name, result) in results {
            match result {
                Ok(entry) => {
                    tracing::info!(
                        server = %name,
                        server_info = %entry.client.server_info().name,
                        tools = entry.client.tools().len(),
                        "MCP stdio server connected"
                    );
                    guard.insert(name, entry);
                }
                Err(error) => {
                    tracing::warn!(server = %name, %error, "MCP stdio server failed to start");
                    errors.push(McpConnectError { server_name: name, error });
                }
            }
        }
        errors
    }

    /// Connect HTTP servers from a supplied list.
    pub async fn connect_http_many(
        &self,
        servers: Vec<McpHttpConnectable>,
        connect_timeout_override: Option<Duration>,
    ) -> Vec<McpConnectError> {
        let connect_timeout = connect_timeout_override.unwrap_or(DEFAULT_HTTP_CONNECT_TIMEOUT);
        let tool_timeout = self.tool_timeout;

        let futures: Vec<_> = servers
            .into_iter()
            .map(|cfg| {
                let name = cfg.name.clone();
                async move {
                    let result = connect_one_http(&cfg, connect_timeout, tool_timeout).await;
                    (name, result)
                }
            })
            .collect();

        let results = futures::future::join_all(futures).await;
        let mut errors = Vec::new();
        let mut guard = self.servers.write().await;

        for (name, result) in results {
            match result {
                Ok(entry) => {
                    tracing::info!(
                        server = %name,
                        server_info = %entry.client.server_info().name,
                        tools = entry.client.tools().len(),
                        "MCP HTTP server connected"
                    );
                    guard.insert(name, entry);
                }
                Err(error) => {
                    tracing::warn!(server = %name, %error, "MCP HTTP server failed to start");
                    errors.push(McpConnectError { server_name: name, error });
                }
            }
        }
        errors
    }

    /// Return all tools across all connected servers as `Arc<dyn Tool>`.
    ///
    /// Called after `connect_all`; the returned vector is fed into
    /// `ToolRegistry::new` alongside the built-in tools.
    pub async fn tools(self: &Arc<Self>) -> Vec<Arc<dyn Tool>> {
        let guard = self.servers.read().await;
        guard
            .iter()
            .flat_map(|(server_name, entry)| {
                entry.client.tools().iter().map(|info| {
                    let tool: Arc<dyn Tool> = Arc::new(McpTool::new(
                        server_name.clone(),
                        info.clone(),
                        Arc::clone(self),
                    ));
                    tool
                })
            })
            .collect()
    }

    /// Call a tool on the named server.
    ///
    /// Returns the (text, is_error) pair from the server's `tools/call`
    /// response, or a user-facing error string (wrapped in `Err`) when the
    /// server is not connected or the call fails.
    pub async fn call_tool(
        &self,
        server_name: &str,
        tool_name: &str,
        arguments: &serde_json::Value,
    ) -> Result<(String, bool), String> {
        let guard = self.servers.read().await;
        let entry = match guard.get(server_name) {
            Some(e) => e,
            None => {
                return Err(format!(
                    "MCP server '{server_name}' is not connected. \
                     Check /mcp status and restart it if needed."
                ));
            }
        };

        match entry.client.call_tool(tool_name, arguments).await {
            Ok(pair) => Ok(pair),
            Err(McpToolCallError::Timeout) => Err(format!(
                "MCP tool '{tool_name}' on server '{server_name}' timed out. \
                 The server may be unresponsive."
            )),
            Err(McpToolCallError::Transport(e)) => Err(format!(
                "MCP tool '{tool_name}' on server '{server_name}' failed: {e}. \
                 The server connection was lost."
            )),
        }
    }

    /// Gracefully shut down all servers.
    pub async fn shutdown(&self) {
        let mut guard = self.servers.write().await;
        let entries: Vec<_> = guard.drain().collect();
        drop(guard);

        for (name, entry) in entries {
            tracing::debug!(server = %name, "shutting down MCP server");
            entry.client.shutdown().await;
            if let Some(proc) = entry._process {
                proc.kill(SHUTDOWN_GRACE).await;
            }
        }
    }
}

impl Default for McpClientManager {
    fn default() -> Self {
        Self::new()
    }
}

async fn connect_one_stdio(
    config: &McpConnectable,
    connect_timeout: Duration,
    tool_timeout: Duration,
) -> Result<ServerEntry, McpError> {
    let (proc, stdin, stdout) = McpProcess::spawn(config)?;
    let client = McpClient::connect(stdout, stdin, connect_timeout, tool_timeout).await?;
    Ok(ServerEntry { client: AnyClient::Stdio(client), _process: Some(proc) })
}

async fn connect_one_http(
    config: &McpHttpConnectable,
    connect_timeout: Duration,
    tool_timeout: Duration,
) -> Result<ServerEntry, McpError> {
    let client = McpHttpClient::connect(
        &config.name,
        &config.url,
        &config.headers,
        None, // auth provider would be injected by the caller in a future PR
        connect_timeout,
        tool_timeout,
    )
    .await?;
    Ok(ServerEntry { client: AnyClient::Http(client), _process: None })
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::time::Duration;
    use tokio::io::{duplex, AsyncWriteExt};

    use crate::client::McpClient;
    use crate::config::McpConnectable;

    /// Injects a pre-connected `McpClient` (built from in-memory streams)
    /// directly into the manager, bypassing the process layer. Used in unit
    /// tests that do not want to spawn real processes.
    async fn inject_client(
        manager: &McpClientManager,
        server_name: &str,
        client: McpClient,
    ) {
        let mut guard = manager.servers.write().await;
        guard.insert(
            server_name.to_string(),
            ServerEntry { client: AnyClient::Stdio(client), _process: None },
        );
    }

    /// Builds a minimal McpClient from in-memory streams with the given tools.
    async fn make_client(tools: serde_json::Value) -> McpClient {
        let (server_out, client_in) = duplex(64 * 1024);
        let (client_out, server_in) = duplex(64 * 1024);

        let connect = tokio::spawn(async move {
            McpClient::connect(client_in, client_out, Duration::from_secs(5), Duration::from_secs(5)).await
        });

        // Run handshake from the server side.
        let mut srv_out = server_out;
        let mut srv_in = server_in;

        macro_rules! read_line {
            ($stream:ident) => {{
                use tokio::io::AsyncReadExt;
                let mut line = Vec::new();
                let mut byte = [0u8; 1];
                loop {
                    $stream.read_exact(&mut byte).await.unwrap();
                    if byte[0] == b'\n' { break; }
                    line.push(byte[0]);
                }
                serde_json::from_slice::<serde_json::Value>(line.trim_ascii()).unwrap()
            }};
        }

        macro_rules! write_line {
            ($stream:ident, $val:expr) => {{
                let s = serde_json::to_string(&$val).unwrap();
                $stream.write_all(format!("{s}\n").as_bytes()).await.unwrap();
                $stream.flush().await.unwrap();
            }};
        }

        let init = read_line!(srv_in);
        write_line!(
            srv_out,
            json!({
                "jsonrpc": "2.0",
                "id": init["id"],
                "result": {
                    "protocolVersion": "2025-06-18",
                    "serverInfo": { "name": "test", "version": "1" },
                    "capabilities": {}
                }
            })
        );

        let _notif = read_line!(srv_in); // notifications/initialized

        let tl = read_line!(srv_in);
        write_line!(srv_out, json!({ "jsonrpc": "2.0", "id": tl["id"], "result": { "tools": tools } }));

        connect.await.unwrap().unwrap()
    }

    #[tokio::test]
    async fn tools_returns_namespaced_arc_tools() {
        let manager = Arc::new(McpClientManager::new());
        let client = make_client(json!([{
            "name": "my_tool",
            "description": "desc",
            "inputSchema": { "type": "object", "properties": {} }
        }]))
        .await;

        inject_client(&manager, "my-server", client).await;

        let tools = manager.tools().await;
        assert_eq!(tools.len(), 1);
        assert!(tools[0].name().starts_with("mcp__"), "tool name must be namespaced");
    }

    #[tokio::test]
    async fn call_tool_on_missing_server_returns_error() {
        let manager = McpClientManager::new();
        let result = manager.call_tool("no-such-server", "tool", &json!({})).await;
        assert!(result.is_err());
        let msg = result.unwrap_err();
        assert!(msg.contains("no-such-server"));
    }

    #[tokio::test]
    async fn one_server_failing_does_not_block_others() {
        // connect_many with two servers: one valid McpConnectable (bad command)
        // and we inject a good one directly. The bad one fails; the good one stays.
        let manager = Arc::new(McpClientManager::new());

        let bad = McpConnectable {
            name: "bad-server".into(),
            command: "definitely-not-a-real-mcp-executable-xyz".into(),
            args: vec![],
            env: Default::default(),
        };

        // inject a good client first
        let client = make_client(json!([{ "name": "ok_tool", "description": "", "inputSchema": null }])).await;
        inject_client(&manager, "good-server", client).await;

        // Now try to connect the bad server; it should fail.
        let errors = manager.connect_many(vec![bad], Some(Duration::from_millis(500))).await;
        assert_eq!(errors.len(), 1, "bad server must report an error");
        assert_eq!(errors[0].server_name, "bad-server");

        // The good server is still there.
        let tools = manager.tools().await;
        assert_eq!(tools.len(), 1);
        assert!(tools[0].name().contains("good-server") || tools[0].name().contains("ok_tool"));
    }

    #[tokio::test]
    async fn namespacing_makes_names_collision_free() {
        let manager = Arc::new(McpClientManager::new());

        // Two servers with the same tool name.
        let client_a = make_client(json!([{
            "name": "shared_tool",
            "description": "from a",
            "inputSchema": null
        }]))
        .await;
        let client_b = make_client(json!([{
            "name": "shared_tool",
            "description": "from b",
            "inputSchema": null
        }]))
        .await;

        inject_client(&manager, "server-a", client_a).await;
        inject_client(&manager, "server-b", client_b).await;

        let tools = manager.tools().await;
        assert_eq!(tools.len(), 2, "both tools must be present");

        let names: Vec<&str> = tools.iter().map(|t| t.name()).collect();
        // Names must be distinct despite sharing the underlying tool name.
        assert_ne!(names[0], names[1]);
        // Both must be namespaced.
        assert!(names.iter().all(|n| n.starts_with("mcp__")));
    }

    #[tokio::test]
    async fn empty_config_yields_no_errors_and_no_tools() {
        let manager = Arc::new(McpClientManager::new());
        let errors = manager.connect_many(vec![], None).await;
        assert!(errors.is_empty());
        assert!(manager.tools().await.is_empty());
    }
}
