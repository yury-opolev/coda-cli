//! MCP protocol client: initialize handshake, tool listing and calling,
//! resource/prompt listing, and graceful shutdown.
//!
//! `McpClient` is constructed from any `AsyncRead + AsyncWrite` pair so tests
//! can drive it through in-memory streams (`tokio::io::duplex`) without
//! spawning a process. Production code passes a child process's stdout/stdin.
//!
//! # Bounding
//!
//! Every wait is bounded:
//! - The entire handshake (`initialize` + `notifications/initialized` +
//!   `tools/list`) is wrapped in `connect_timeout`.
//! - Each `tools/call` is wrapped in its own timeout.
//! - Shutdown sends `shutdown` then drops the connection; the process kill
//!   is handled by the caller (McpClientManager / McpProcess).
//!
//! A server that hangs, sends garbage, dies, or floods output is handled
//! gracefully: the timeout fires, the `Connection` is dropped (which closes
//! the write channel and ends the writer task), and the caller receives an
//! error result instead of waiting forever.

use std::time::Duration;

use serde_json::Value;
use tokio::io::{AsyncRead, AsyncWrite};

use crate::error::{McpError, McpTransportError};
use crate::transport::{self, Connection};

/// MCP protocol version this client advertises.
const PROTOCOL_VERSION: &str = "2025-06-18";

/// Default timeout for the initialize + tools/list handshake.
pub(crate) const DEFAULT_CONNECT_TIMEOUT: Duration = Duration::from_secs(30);

/// Default per-call timeout for `tools/call` (10 minutes, matching C#).
pub(crate) const DEFAULT_TOOL_TIMEOUT: Duration = Duration::from_secs(600);

/// Environment variable that overrides the tool-call timeout (whole seconds;
/// ≤ 0 disables the timeout). Matches `McpTool.TimeoutEnv` from C#.
pub const TOOL_TIMEOUT_ENV: &str = "CODA_MCP_TOOL_TIMEOUT";

/// Information about a connected MCP server.
#[allow(dead_code)] // name used for logging; version exposed for future /mcp info display
#[derive(Debug, Clone)]
pub struct McpServerInfo {
    pub name: String,
    pub version: String,
}

/// A tool advertised by the server, parsed from the `tools/list` response.
#[derive(Debug, Clone)]
pub struct McpToolInfo {
    pub name: String,
    pub description: String,
    /// A JSON Schema object, guaranteed to carry `"type": "object"`. If the
    /// server advertised a broken schema it is repaired here (see
    /// `normalize_schema`).
    pub input_schema_json: String,
    /// Set when the schema was repaired (`schema_coerced = true` in C#).
    pub schema_coerced: bool,
    /// From `annotations.readOnlyHint`.
    pub read_only: bool,
}

/// An active connection to one MCP server after a successful handshake.
///
/// Holds a `Connection` (to send requests) and the tool list discovered during
/// `initialize`. Methods that call the server are bounded by `tool_timeout`.
pub(crate) struct McpClient {
    connection: Connection,
    pub server_info: McpServerInfo,
    pub tools: Vec<McpToolInfo>,
    tool_timeout: Duration,
    _tasks: transport::ConnectionTasks,
}

impl McpClient {
    /// Connect to an MCP server via an already-open byte stream and perform
    /// the initialize handshake.
    ///
    /// The entire handshake must complete within `connect_timeout`. On
    /// timeout, a `McpError::ConnectTimeout` is returned; the caller is
    /// responsible for killing any underlying process.
    pub async fn connect<R, W>(
        reader: R,
        writer: W,
        connect_timeout: Duration,
        tool_timeout: Duration,
    ) -> Result<Self, McpError>
    where
        R: AsyncRead + Unpin + Send + 'static,
        W: AsyncWrite + Unpin + Send + 'static,
    {
        let (connection, _notif_rx, tasks) = transport::connect(reader, writer);

        match tokio::time::timeout(connect_timeout, handshake(&connection)).await {
            Ok(Ok((server_info, tools))) => Ok(Self {
                connection,
                server_info,
                tools,
                tool_timeout,
                _tasks: tasks,
            }),
            Ok(Err(e)) => Err(e),
            Err(_) => Err(McpError::ConnectTimeout),
        }
    }

    /// Call a remote tool and return its text result and error flag.
    ///
    /// The call is bounded by this client's `tool_timeout` AND the supplied
    /// cancellation signal (whichever fires first). A timeout from this
    /// client's own deadline is returned as an `Err`; a cancellation from
    /// the agent cancel token propagates as `Err` as well (the agent loop
    /// handles the distinction).
    pub async fn call_tool(
        &self,
        name: &str,
        arguments: &Value,
    ) -> Result<(String, bool), McpToolCallError> {
        let params = serde_json::json!({
            "name": name,
            "arguments": arguments,
        });

        let result =
            tokio::time::timeout(self.tool_timeout, self.connection.request("tools/call", Some(params)))
                .await
                .map_err(|_| McpToolCallError::Timeout)?
                .map_err(McpToolCallError::Transport)?;

        Ok(format_call_result(&result))
    }

    /// List resources advertised by the server. Returns an empty list if the
    /// server does not support resources (MCP error response).
    #[allow(dead_code)] // exposed for future tool/UI use
    pub async fn list_resources(&self) -> Vec<McpResourceInfo> {
        match tokio::time::timeout(
            Duration::from_secs(10),
            self.connection.request("resources/list", None),
        )
        .await
        {
            Ok(Ok(result)) => parse_resource_list(&result),
            _ => Vec::new(),
        }
    }

    /// List prompts advertised by the server. Returns an empty list if the
    /// server does not support prompts or does not respond within 10 s.
    #[allow(dead_code)] // exposed for future tool/UI use
    pub async fn list_prompts(&self) -> Vec<McpPromptInfo> {
        match tokio::time::timeout(
            Duration::from_secs(10),
            self.connection.request("prompts/list", None),
        )
        .await
        {
            Ok(Ok(result)) => parse_prompt_list(&result),
            _ => Vec::new(),
        }
    }

    /// Sends the MCP `shutdown` notification and drops the connection.
    ///
    /// MCP's stdio shutdown is simpler than LSP's: closing stdin tells the
    /// server we are done. We send a courtesy `shutdown` request first (best-
    /// effort, bounded at 2 s) then let the connection drop, which closes the
    /// write channel and signals the server via stdin EOF.
    pub async fn shutdown(self) {
        // Best-effort shutdown request; ignore errors and timeout.
        let _ = tokio::time::timeout(
            Duration::from_secs(2),
            self.connection.request("shutdown", None),
        )
        .await;
        // Dropping `self.connection` closes the outgoing channel → write_loop
        // ends → stdin EOF reaches the server.
    }

    /// Resolve the tool-call timeout from the environment variable.
    pub fn resolve_tool_timeout() -> Duration {
        if let Ok(raw) = std::env::var(TOOL_TIMEOUT_ENV) {
            if let Ok(secs) = raw.trim().parse::<i64>() {
                return if secs <= 0 {
                    Duration::from_secs(u64::MAX) // effectively infinite
                } else {
                    Duration::from_secs(secs as u64)
                };
            }
        }
        DEFAULT_TOOL_TIMEOUT
    }
}

/// Error returned by `call_tool` (separate from connection-level `McpError`
/// so the tool wrapper can produce the right user-facing message).
#[derive(Debug)]
pub(crate) enum McpToolCallError {
    /// The call timed out (not a caller-cancel; the tool's own deadline fired).
    Timeout,
    /// The transport reported an error (server disconnected, JSON-RPC error, etc.).
    Transport(McpTransportError),
}

// ── Parsed result types ───────────────────────────────────────────────────────

/// A resource advertised by the server (from `resources/list`).
#[allow(dead_code)] // exposed for future resources tool
#[derive(Debug, Clone)]
pub struct McpResourceInfo {
    pub uri: String,
    pub name: String,
    pub description: String,
}

/// A prompt advertised by the server (from `prompts/list`).
#[allow(dead_code)] // exposed for future prompts tool
#[derive(Debug, Clone)]
pub struct McpPromptInfo {
    pub name: String,
    pub description: String,
}

// ── Handshake ─────────────────────────────────────────────────────────────────

async fn handshake(conn: &Connection) -> Result<(McpServerInfo, Vec<McpToolInfo>), McpError> {
    // 1. initialize
    let init_params = serde_json::json!({
        "protocolVersion": PROTOCOL_VERSION,
        "capabilities": {},
        "clientInfo": { "name": "coda", "version": "0.1" }
    });
    let init_result = conn
        .request("initialize", Some(init_params))
        .await
        .map_err(|e| McpError::Protocol {
            server: "(connecting)".into(),
            phase: "initialize",
            message: e.to_string(),
        })?;

    let server_info = parse_server_info(&init_result);

    // 2. notifications/initialized  (fire-and-forget; errors are transport-fatal)
    conn.notify("notifications/initialized", None).map_err(|e| McpError::Protocol {
        server: server_info.name.clone(),
        phase: "notifications/initialized",
        message: e.to_string(),
    })?;

    // 3. tools/list
    let tools_result = conn
        .request("tools/list", None)
        .await
        .map_err(|e| McpError::Protocol {
            server: server_info.name.clone(),
            phase: "tools/list",
            message: e.to_string(),
        })?;

    let tools = parse_tools_list(&tools_result);

    Ok((server_info, tools))
}

// ── Parsers ───────────────────────────────────────────────────────────────────

fn parse_server_info(result: &Value) -> McpServerInfo {
    let server = result.get("serverInfo");
    McpServerInfo {
        name: server
            .and_then(|s| s.get("name"))
            .and_then(Value::as_str)
            .unwrap_or("unknown")
            .to_string(),
        version: server
            .and_then(|s| s.get("version"))
            .and_then(Value::as_str)
            .unwrap_or("?")
            .to_string(),
    }
}

fn parse_tools_list(result: &Value) -> Vec<McpToolInfo> {
    let Some(arr) = result.get("tools").and_then(Value::as_array) else {
        return Vec::new();
    };
    arr.iter().filter_map(parse_one_tool).collect()
}

pub(crate) fn parse_one_tool(tool: &Value) -> Option<McpToolInfo> {
    let name = tool.get("name").and_then(Value::as_str)?.to_string();
    if name.is_empty() {
        return None;
    }
    let description = tool
        .get("description")
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_string();

    let (input_schema_json, schema_coerced) = normalize_schema(tool);

    let read_only = tool
        .get("annotations")
        .and_then(|a| a.get("readOnlyHint"))
        .and_then(Value::as_bool)
        .unwrap_or(false);

    Some(McpToolInfo { name, description, input_schema_json, schema_coerced, read_only })
}

/// Returns a schema string that is always a JSON object with `"type":"object"`.
///
/// Some published servers advertise schemas that are valid JSON objects but
/// lack the `"type"` field that model APIs require. Repairing here keeps
/// the damage local to the tool rather than poisoning every request.
///
/// Mirrors `McpToolInfo.NormalizeSchema` from C#.
fn normalize_schema(tool: &Value) -> (String, bool) {
    const EMPTY: &str = r#"{"type":"object","properties":{}}"#;

    let Some(schema) = tool.get("inputSchema") else {
        return (EMPTY.to_string(), false); // absent = no arguments, not a defect
    };
    if schema.is_null() {
        return (EMPTY.to_string(), false);
    }
    let Some(obj) = schema.as_object() else {
        // Present but not an object — every parameter is lost; flag it.
        return (EMPTY.to_string(), true);
    };

    if obj.get("type").and_then(Value::as_str) == Some("object") {
        return (schema.to_string(), false);
    }

    // Has keys but missing "type": add the minimum fields.
    let mut patched = obj.clone();
    patched.insert("type".to_string(), Value::String("object".to_string()));
    patched.entry("properties").or_insert_with(|| Value::Object(Default::default()));
    (Value::Object(patched).to_string(), true)
}

/// Parses the `content` array of a `tools/call` result into plain text and
/// error flag. Mirrors `McpToolInfo.FormatCallResult` from C#.
pub(crate) fn format_call_result(result: &Value) -> (String, bool) {
    let is_error = result
        .get("isError")
        .and_then(Value::as_bool)
        .unwrap_or(false);

    let mut text = String::new();
    if let Some(content) = result.get("content").and_then(Value::as_array) {
        for part in content {
            let type_ = part.get("type").and_then(Value::as_str);
            match type_ {
                Some("text") => {
                    if let Some(t) = part.get("text").and_then(Value::as_str) {
                        text.push_str(t);
                        text.push('\n');
                    }
                }
                Some(other) => {
                    text.push('[');
                    text.push_str(other);
                    text.push_str(" content]\n");
                }
                None => {}
            }
        }
    }

    let trimmed = text.trim_end_matches('\n');
    (
        if trimmed.is_empty() { "(no content)".to_string() } else { trimmed.to_string() },
        is_error,
    )
}

pub(crate) fn parse_resource_list(result: &Value) -> Vec<McpResourceInfo> {
    result
        .get("resources")
        .and_then(Value::as_array)
        .map(|arr| {
            arr.iter()
                .filter_map(|r| {
                    let uri = r.get("uri").and_then(Value::as_str)?.to_string();
                    Some(McpResourceInfo {
                        uri,
                        name: r.get("name").and_then(Value::as_str).unwrap_or("").to_string(),
                        description: r
                            .get("description")
                            .and_then(Value::as_str)
                            .unwrap_or("")
                            .to_string(),
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

pub(crate) fn parse_prompt_list(result: &Value) -> Vec<McpPromptInfo> {
    result
        .get("prompts")
        .and_then(Value::as_array)
        .map(|arr| {
            arr.iter()
                .filter_map(|p| {
                    let name = p.get("name").and_then(Value::as_str)?.to_string();
                    Some(McpPromptInfo {
                        name,
                        description: p
                            .get("description")
                            .and_then(Value::as_str)
                            .unwrap_or("")
                            .to_string(),
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    // Serializes the three env-var tests so they don't race each other.
    // Concurrent env::set_var / remove_var calls on different tests cause flaky failures.
    static ENV_LOCK: std::sync::LazyLock<std::sync::Mutex<()>> =
        std::sync::LazyLock::new(|| std::sync::Mutex::new(()));
    use tokio::io::{duplex, AsyncWriteExt};
    /// A minimal MCP server simulated with an in-memory duplex stream.
    struct FakeServer {
        /// The side the server writes to (client reads from this).
        client_reads: tokio::io::DuplexStream,
        /// The side the server reads from (client writes to this).
        client_writes: tokio::io::DuplexStream,
    }

    impl FakeServer {
        fn new() -> (Self, tokio::io::DuplexStream, tokio::io::DuplexStream) {
            let (server_out, client_in) = duplex(64 * 1024);
            let (client_out, server_in) = duplex(64 * 1024);
            (Self { client_reads: server_out, client_writes: server_in }, client_in, client_out)
        }

        async fn write(&mut self, value: Value) {
            let line = serde_json::to_string(&value).unwrap();
            self.client_reads
                .write_all(format!("{line}\n").as_bytes())
                .await
                .unwrap();
            self.client_reads.flush().await.unwrap();
        }

        async fn read(&mut self) -> Value {
            use tokio::io::AsyncReadExt;
            let mut line = Vec::new();
            let mut byte = [0u8; 1];
            loop {
                self.client_writes.read_exact(&mut byte).await.unwrap();
                if byte[0] == b'\n' {
                    break;
                }
                line.push(byte[0]);
            }
            serde_json::from_slice(line.trim_ascii()).unwrap()
        }

        /// Respond to the next `initialize` + `tools/list` handshake with the
        /// supplied tool list.
        async fn run_handshake(&mut self, tools: Value) {
            // Read and respond to initialize.
            let init = self.read().await;
            assert_eq!(init["method"], "initialize");
            self.write(json!({
                "jsonrpc": "2.0",
                "id": init["id"],
                "result": {
                    "protocolVersion": "2025-06-18",
                    "capabilities": {},
                    "serverInfo": { "name": "test-server", "version": "1.0" }
                }
            }))
            .await;

            // Read notifications/initialized (no response needed).
            let _notif = self.read().await;

            // Read and respond to tools/list.
            let tl = self.read().await;
            assert_eq!(tl["method"], "tools/list");
            self.write(json!({
                "jsonrpc": "2.0",
                "id": tl["id"],
                "result": { "tools": tools }
            }))
            .await;
        }
    }

    fn one_timeout() -> Duration {
        Duration::from_secs(5)
    }

    #[tokio::test]
    async fn successful_handshake_populates_server_info_and_tools() {
        let (mut server, client_in, client_out) = FakeServer::new();

        let connect = tokio::spawn(async move {
            McpClient::connect(client_in, client_out, one_timeout(), one_timeout()).await
        });

        server
            .run_handshake(json!([{
                "name": "my_tool",
                "description": "does stuff",
                "inputSchema": { "type": "object", "properties": {} }
            }]))
            .await;

        let client = connect.await.unwrap().unwrap();
        assert_eq!(client.server_info.name, "test-server");
        assert_eq!(client.tools.len(), 1);
        assert_eq!(client.tools[0].name, "my_tool");
    }

    #[tokio::test]
    async fn handshake_timeout_returns_connect_timeout_error() {
        let (_server_out, client_in) = duplex(64 * 1024);
        let (client_out, _server_in) = duplex(64 * 1024);

        // No server responding → timeout.
        let result = McpClient::connect(
            client_in,
            client_out,
            Duration::from_millis(50), // very short timeout
            one_timeout(),
        )
        .await;

        assert!(matches!(result, Err(McpError::ConnectTimeout)));
    }

    #[tokio::test]
    async fn tools_list_with_missing_type_is_coerced() {
        let (mut server, client_in, client_out) = FakeServer::new();

        let connect = tokio::spawn(async move {
            McpClient::connect(client_in, client_out, one_timeout(), one_timeout()).await
        });

        server
            .run_handshake(json!([{
                "name": "broken_schema",
                "inputSchema": { "$schema": "http://json-schema.org/draft-07/schema#" }
            }]))
            .await;

        let client = connect.await.unwrap().unwrap();
        assert!(client.tools[0].schema_coerced);
        assert!(client.tools[0].input_schema_json.contains("\"type\":\"object\""));
    }

    #[tokio::test]
    async fn call_tool_returns_text_content() {
        let (mut server, client_in, client_out) = FakeServer::new();

        let connect = tokio::spawn(async move {
            McpClient::connect(client_in, client_out, one_timeout(), one_timeout()).await
        });

        server.run_handshake(json!([])).await;
        let client = connect.await.unwrap().unwrap();

        let call =
            tokio::spawn(async move { client.call_tool("echo", &json!({"msg": "hi"})).await });

        // Read the tools/call request.
        let req = server.read().await;
        assert_eq!(req["method"], "tools/call");
        assert_eq!(req["params"]["name"], "echo");

        server
            .write(json!({
                "jsonrpc": "2.0",
                "id": req["id"],
                "result": {
                    "content": [{ "type": "text", "text": "hello" }],
                    "isError": false
                }
            }))
            .await;

        let (text, is_error) = call.await.unwrap().unwrap();
        assert_eq!(text, "hello");
        assert!(!is_error);
    }

    #[tokio::test]
    async fn call_tool_timeout_returns_timeout_error() {
        let (mut server, client_in, client_out) = FakeServer::new();

        let connect = tokio::spawn(async move {
            McpClient::connect(
                client_in,
                client_out,
                one_timeout(),
                Duration::from_millis(50), // very short tool timeout
            )
            .await
        });

        server.run_handshake(json!([])).await;
        let client = connect.await.unwrap().unwrap();

        // Server never responds to tools/call.
        let call = tokio::spawn(async move { client.call_tool("slow_tool", &json!({})).await });
        let _req = server.read().await; // consume the request

        let err = call.await.unwrap().unwrap_err();
        assert!(matches!(err, McpToolCallError::Timeout));
    }

    #[tokio::test]
    async fn server_dies_mid_call_returns_transport_error() {
        let (mut server, client_in, client_out) = FakeServer::new();

        let connect = tokio::spawn(async move {
            McpClient::connect(client_in, client_out, one_timeout(), Duration::from_secs(5)).await
        });

        server.run_handshake(json!([])).await;
        let client = connect.await.unwrap().unwrap();

        let call = tokio::spawn(async move { client.call_tool("boom", &json!({})).await });
        let _req = server.read().await;

        // Kill the server (drop its write side → EOF for client).
        drop(server);

        let err = call.await.unwrap().unwrap_err();
        assert!(matches!(err, McpToolCallError::Transport(_)));
    }

    #[tokio::test]
    async fn normalize_schema_absent_returns_empty_object_schema() {
        let tool = json!({ "name": "t" });
        let (schema, coerced) = normalize_schema(&tool);
        assert!(!coerced);
        assert!(schema.contains("\"type\":\"object\""));
    }

    #[tokio::test]
    async fn normalize_schema_non_object_is_coerced() {
        let tool = json!({ "name": "t", "inputSchema": "a string" });
        let (schema, coerced) = normalize_schema(&tool);
        assert!(coerced);
        assert!(schema.contains("\"type\":\"object\""));
    }

    #[tokio::test]
    async fn normalize_schema_missing_type_is_coerced() {
        let tool = json!({ "name": "t", "inputSchema": { "properties": {} } });
        let (schema, coerced) = normalize_schema(&tool);
        assert!(coerced);
        assert!(schema.contains("\"type\":\"object\""));
    }

    #[tokio::test]
    async fn normalize_schema_valid_schema_unchanged() {
        let tool = json!({ "name": "t", "inputSchema": { "type": "object", "properties": {} } });
        let (schema, coerced) = normalize_schema(&tool);
        assert!(!coerced);
        assert_eq!(schema, r#"{"type":"object","properties":{}}"#);
    }

    #[tokio::test]
    async fn format_call_result_concatenates_text_parts() {
        let result = json!({
            "content": [
                { "type": "text", "text": "line1" },
                { "type": "text", "text": "line2" }
            ],
            "isError": false
        });
        let (text, is_error) = format_call_result(&result);
        assert_eq!(text, "line1\nline2");
        assert!(!is_error);
    }

    #[tokio::test]
    async fn format_call_result_image_content_becomes_placeholder() {
        let result = json!({
            "content": [{ "type": "image", "data": "base64..." }],
            "isError": false
        });
        let (text, _) = format_call_result(&result);
        assert!(text.contains("[image content]"));
    }

    #[tokio::test]
    async fn format_call_result_empty_content_returns_no_content() {
        let result = json!({ "content": [], "isError": false });
        let (text, _) = format_call_result(&result);
        assert_eq!(text, "(no content)");
    }

    #[tokio::test]
    async fn resolve_tool_timeout_uses_default_when_env_unset() {
        let _guard = ENV_LOCK.lock().unwrap();
        std::env::remove_var(TOOL_TIMEOUT_ENV);
        assert_eq!(McpClient::resolve_tool_timeout(), DEFAULT_TOOL_TIMEOUT);
    }

    #[tokio::test]
    async fn resolve_tool_timeout_zero_means_infinite() {
        let _guard = ENV_LOCK.lock().unwrap();
        std::env::set_var(TOOL_TIMEOUT_ENV, "0");
        let t = McpClient::resolve_tool_timeout();
        assert_eq!(t, Duration::from_secs(u64::MAX));
        std::env::remove_var(TOOL_TIMEOUT_ENV);
    }

    #[tokio::test]
    async fn resolve_tool_timeout_custom_seconds() {
        let _guard = ENV_LOCK.lock().unwrap();
        std::env::set_var(TOOL_TIMEOUT_ENV, "42");
        let t = McpClient::resolve_tool_timeout();
        assert_eq!(t, Duration::from_secs(42));
        std::env::remove_var(TOOL_TIMEOUT_ENV);
    }
}

// ── Real-child-process tests ──────────────────────────────────────────────────
// These tests spin up real OS processes to verify the bounding guarantees
// against actual misbehaviour, not just simulated streams.

#[cfg(test)]
#[cfg(target_os = "windows")]
mod real_process_tests {
    use super::*;
    use crate::process::McpProcess;

    const HANG_TIMEOUT: Duration = Duration::from_millis(500);

    // Multi-thread flavor so background I/O tasks can run concurrently with
    // the test coroutine; with the default single-thread executor a flooding
    // child can starve the timeout future.
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn hanging_server_connect_times_out() {
        // A server that reads stdin but never writes to stdout.
        let Ok((proc, stdin, stdout)) = McpProcess::spawn_raw(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-Command", "while ($true) { Start-Sleep 60 }"],
        ) else {
            return; // PowerShell not available; skip
        };

        let result = McpClient::connect(stdout, stdin, HANG_TIMEOUT, Duration::from_secs(5)).await;
        drop(proc); // kill_on_drop

        assert!(
            matches!(result, Err(McpError::ConnectTimeout)),
            "expected ConnectTimeout"
        );
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn server_that_exits_immediately_returns_error() {
        let Ok((proc, stdin, stdout)) = McpProcess::spawn_raw(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-Command", "exit 1"],
        ) else {
            return;
        };

        let result =
            McpClient::connect(stdout, stdin, Duration::from_secs(2), Duration::from_secs(5)).await;
        drop(proc);

        assert!(
            matches!(result, Err(McpError::Protocol { .. } | McpError::ConnectTimeout)),
            "expected a connect error, got Ok"
        );
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn server_that_sends_garbage_times_out() {
        let script = r#"
$i = 0
while ($i -lt 1000) {
    Write-Output "garbage-line-$i"
    $i++
}
Start-Sleep 300
"#;
        let Ok((proc, stdin, stdout)) = McpProcess::spawn_raw(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-Command", script],
        ) else {
            return;
        };

        let result = McpClient::connect(stdout, stdin, HANG_TIMEOUT, Duration::from_secs(5)).await;
        drop(proc);

        // Either a timeout or a protocol error is acceptable.
        assert!(
            matches!(result, Err(McpError::ConnectTimeout | McpError::Protocol { .. })),
            "expected an error for garbage output, got Ok"
        );
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn server_flooding_stdout_does_not_oom() {
        // A server that floods stdout with non-JSON lines. The transport must
        // discard them without buffering, and the test must finish promptly.
        // We use 500 lines (not 50000) to keep the test fast; the architecture
        // (discard-on-dispatch) is the OOM protection, not the line count.
        let script = r#"
$i = 0
while ($i -lt 500) {
    Write-Output "flood-line-$i"
    $i++
}
Start-Sleep 300
"#;
        let Ok((proc, stdin, stdout)) = McpProcess::spawn_raw(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-Command", script],
        ) else {
            return;
        };

        // Connect will time out (server never sends a valid JSON-RPC response),
        // but crucially it must finish within the timeout and not OOM.
        let result = McpClient::connect(stdout, stdin, Duration::from_secs(3), Duration::from_secs(5)).await;
        drop(proc);

        assert!(matches!(result, Err(_)), "expected an error, got Ok");
    }
}
