//! MCP Streamable-HTTP transport.
//!
//! Each JSON-RPC request is a `POST` whose response is either a single JSON
//! object or an SSE stream. The `Mcp-Session-Id` returned by `initialize` is
//! echoed on subsequent requests, and an optional `McpAuthProvider` supplies
//! the bearer token and handles 401s.
//!
//! # Security
//!
//! - The server URL is validated on construction (`validate_mcp_url`): only
//!   `https` is allowed except for loopback, and embedded credentials are
//!   rejected.
//! - SSRF is checked before the first connection: the host is resolved once,
//!   all addresses are checked against blocked ranges, and the reqwest client
//!   is pinned to the vetted address to prevent DNS-rebinding attacks.
//! - An MCP server must NOT be able to waive its own approval. `is_read_only`
//!   on every tool returned through this transport always returns `false`.
//! - Redirects are disabled: `reqwest::redirect::Policy::none()`. A redirect
//!   to a private range would bypass the SSRF check; we never follow them.

use std::collections::HashMap;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures::TryStreamExt;
use tokio::io::{AsyncBufReadExt, BufReader};
use tokio_util::io::StreamReader;
use tokio_util::sync::CancellationToken;

use crate::auth::provider::McpAuthProvider;
use crate::client::{
    McpPromptInfo, McpResourceInfo, McpServerInfo, McpToolInfo,
    format_call_result, parse_one_tool, parse_prompt_list, parse_resource_list,
};
use crate::error::McpError;
use crate::ssrf::{check_ssrf, validate_mcp_url};

/// MCP protocol version this client advertises.
const PROTOCOL_VERSION: &str = "2025-06-18";

/// Default timeout for the full HTTP handshake (`initialize` + tool list).
pub(crate) const DEFAULT_HTTP_CONNECT_TIMEOUT: Duration = Duration::from_secs(30);

/// Maximum bytes of a non-SSE error body to include in the error message.
const ERROR_BODY_PREVIEW: usize = 500;

// ── McpHttpClient ─────────────────────────────────────────────────────────────

/// An active HTTP MCP connection.
///
/// Constructed via [`McpHttpClient::connect`], which validates the URL,
/// runs the SSRF check, builds a pinned `reqwest::Client`, performs the
/// initialize handshake, and returns a ready-to-use client.
pub(crate) struct McpHttpClient {
    http: reqwest::Client,
    url: reqwest::Url,
    static_headers: HashMap<String, String>,
    auth: Option<Arc<dyn McpAuthProvider>>,
    /// `Mcp-Session-Id` captured from the initialize response and sent on every
    /// subsequent request.
    session_id: Arc<Mutex<Option<String>>>,
    last_id: Arc<AtomicI64>,
    /// Cancelled when this client is dropped/shut down so in-flight calls abort.
    lifetime: CancellationToken,
    pub server_info: McpServerInfo,
    pub tools: Vec<McpToolInfo>,
    pub tool_timeout: Duration,
}

impl McpHttpClient {
    /// Validate the URL, perform the SSRF check, connect, and initialize.
    pub async fn connect(
        server_name: &str,
        url: &str,
        headers: &HashMap<String, String>,
        auth: Option<Arc<dyn McpAuthProvider>>,
        connect_timeout: Duration,
        tool_timeout: Duration,
    ) -> Result<Self, McpError> {
        // 1. Validate URL scheme and credentials.
        validate_mcp_url(url).map_err(McpError::Ssrf)?;

        // 2. Resolve host, check for SSRF-blocked ranges.
        let lifetime = CancellationToken::new();
        let vetted = check_ssrf(url, lifetime.clone())
            .await
            .map_err(McpError::Ssrf)?;

        // 3. Build a reqwest client pinned to the vetted IP so a second DNS
        //    resolution at connect time cannot slip a private address through.
        let parsed_url: reqwest::Url =
            url.parse().map_err(|_| McpError::Ssrf(format!("invalid URL: {url}")))?;
        let mut builder = reqwest::Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .timeout(tool_timeout);

        if let (Some(host), Some(ip)) = (
            parsed_url.host_str().map(str::to_owned),
            vetted.first().copied(),
        ) {
            let port = parsed_url.port_or_known_default().unwrap_or(443);
            builder = builder.resolve(&host, std::net::SocketAddr::new(ip, port));
        }

        let http = builder.build().map_err(|e| McpError::Ssrf(e.to_string()))?;

        let mut client = McpHttpClient {
            http,
            url: parsed_url,
            static_headers: headers.clone(),
            auth,
            session_id: Arc::new(Mutex::new(None)),
            last_id: Arc::new(AtomicI64::new(1)),
            lifetime: lifetime.clone(),
            server_info: McpServerInfo { name: server_name.to_owned(), version: "?".to_owned() },
            tools: Vec::new(),
            tool_timeout,
        };

        // 4. Handshake (bounded by connect_timeout).
        match tokio::time::timeout(connect_timeout, client.handshake(server_name)).await {
            Ok(Ok(tools)) => {
                client.tools = tools;
                Ok(client)
            }
            Ok(Err(e)) => Err(e),
            Err(_) => Err(McpError::ConnectTimeout),
        }
    }

    async fn handshake(&mut self, server_name: &str) -> Result<Vec<McpToolInfo>, McpError> {
        // 1. initialize
        let init_params = serde_json::json!({
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": { "name": "coda", "version": "0.1" },
        });
        let init_result = self
            .send_request("initialize", Some(&init_params))
            .await
            .map_err(|e| McpError::Protocol {
                server: server_name.to_owned(),
                phase: "initialize",
                message: e.to_string(),
            })?;

        self.server_info = parse_server_info(&init_result);

        // 2. notifications/initialized (fire-and-forget; ignore errors)
        let _ = self.send_notification("notifications/initialized").await;

        // 3. tools/list
        let tools_result = self
            .send_request("tools/list", None)
            .await
            .map_err(|e| McpError::Protocol {
                server: server_name.to_owned(),
                phase: "tools/list",
                message: e.to_string(),
            })?;

        Ok(parse_tools_list(&tools_result))
    }

    /// Call a remote tool. Bounded by `tool_timeout`.
    ///
    /// Returns `Ok((text, is_error))` on success, or an error string on failure.
    pub async fn call_tool(
        &self,
        name: &str,
        arguments: &serde_json::Value,
    ) -> Result<(String, bool), String> {
        let params = serde_json::json!({ "name": name, "arguments": arguments });
        match tokio::time::timeout(
            self.tool_timeout,
            self.send_request("tools/call", Some(&params)),
        )
        .await
        {
            Ok(Ok(result)) => Ok(format_call_result(&result)),
            Ok(Err(HttpCallError::Stopped)) => Err(format!(
                "MCP tool '{name}' call aborted: server was stopped"
            )),
            Ok(Err(e)) => Err(format!("MCP tool '{name}' failed: {e}")),
            Err(_) => Err(format!("MCP tool '{name}' timed out")),
        }
    }

    /// List server resources. Returns `[]` on MCP protocol errors.
    #[allow(dead_code)] // exposed for future resources tool
    pub async fn list_resources(&self) -> Vec<McpResourceInfo> {
        match tokio::time::timeout(
            Duration::from_secs(10),
            self.send_request("resources/list", None),
        )
        .await
        {
            Ok(Ok(result)) => parse_resource_list(&result),
            _ => Vec::new(),
        }
    }

    /// List server prompts. Returns `[]` on MCP protocol errors.
    #[allow(dead_code)] // exposed for future prompts tool
    pub async fn list_prompts(&self) -> Vec<McpPromptInfo> {
        match tokio::time::timeout(
            Duration::from_secs(10),
            self.send_request("prompts/list", None),
        )
        .await
        {
            Ok(Ok(result)) => parse_prompt_list(&result),
            _ => Vec::new(),
        }
    }

    /// Fetches a prompt's rendered text (`prompts/get`).
    ///
    /// Reports failure rather than degrading to an empty result: the caller
    /// asked for one specific prompt, so returning nothing silently would look
    /// like an empty prompt rather than an error.
    pub async fn get_prompt(&self, name: &str) -> Result<String, String> {
        let params = serde_json::json!({ "name": name });
        match tokio::time::timeout(
            Duration::from_secs(30),
            self.send_request("prompts/get", Some(&params)),
        )
        .await
        {
            Ok(Ok(result)) => Ok(crate::client::render_prompt_messages(&result)),
            Ok(Err(e)) => Err(e.to_string()),
            Err(_) => Err(format!("timed out fetching prompt '{name}'")),
        }
    }

    /// Reads a resource's contents (`resources/read`).
    pub async fn read_resource(&self, uri: &str) -> Result<String, String> {
        let params = serde_json::json!({ "uri": uri });
        match tokio::time::timeout(
            Duration::from_secs(30),
            self.send_request("resources/read", Some(&params)),
        )
        .await
        {
            Ok(Ok(result)) => Ok(crate::client::render_resource_contents(&result)),
            Ok(Err(e)) => Err(e.to_string()),
            Err(_) => Err(format!("timed out reading resource '{uri}'")),
        }
    }

    /// Cancel all in-flight requests and drop the connection.
    pub async fn shutdown(self) {
        self.lifetime.cancel();
        // Dropping self frees the reqwest client and its connection pool.
    }

    // ── internal ──────────────────────────────────────────────────────────────

    async fn send_request(
        &self,
        method: &str,
        params: Option<&serde_json::Value>,
    ) -> Result<serde_json::Value, HttpCallError> {
        if self.lifetime.is_cancelled() {
            return Err(HttpCallError::Stopped);
        }

        let id = self.last_id.fetch_add(1, Ordering::Relaxed);
        let mut message = serde_json::json!({
            "jsonrpc": "2.0",
            "id": id,
            "method": method,
        });
        if let Some(p) = params {
            message["params"] = p.clone();
        }

        let (status, content_type, body) = tokio::select! {
            r = self.post(&message) => r?,
            _ = self.lifetime.cancelled() => return Err(HttpCallError::Stopped),
        };

        if !status.is_success() {
            let preview = truncate(&body, ERROR_BODY_PREVIEW);
            return Err(HttpCallError::Http(status.as_u16(), preview.to_owned()));
        }

        let message = if is_sse(content_type.as_deref()) {
            read_sse_message_from_string(&body, id)?
        } else {
            parse_json_message(&body)
                .ok_or_else(|| HttpCallError::Protocol("empty or invalid response".into()))?
        };

        extract_result(message)
    }

    async fn send_notification(&self, method: &str) -> Result<(), HttpCallError> {
        if self.lifetime.is_cancelled() {
            return Err(HttpCallError::Stopped);
        }
        let message = serde_json::json!({ "jsonrpc": "2.0", "method": method });
        let _ = tokio::select! {
            r = self.post(&message) => r?,
            _ = self.lifetime.cancelled() => return Err(HttpCallError::Stopped),
        };
        Ok(())
    }

    /// POST a JSON-RPC message. Returns `(status, content-type, body)`.
    ///
    /// For SSE responses the entire stream is buffered into the body string so
    /// the SSE parser can work on it synchronously. MCP SSE streams contain
    /// one event per request, so buffering is acceptable.
    async fn post(
        &self,
        message: &serde_json::Value,
    ) -> Result<(reqwest::StatusCode, Option<String>, String), HttpCallError> {
        let mut response = self.send_once(message).await?;

        // Handle 401 with auth retry.
        if response.status() == reqwest::StatusCode::UNAUTHORIZED {
            if let Some(auth) = &self.auth {
                let www_auth = response
                    .headers()
                    .get("WWW-Authenticate")
                    .and_then(|v| v.to_str().ok())
                    .map(str::to_owned);
                let recovered = auth.handle_unauthorized(www_auth.as_deref()).await;
                if recovered {
                    response = self.send_once(message).await?;
                }
            }
        }

        self.capture_session(&response);

        let status = response.status();
        let ct = response
            .headers()
            .get("Content-Type")
            .and_then(|v| v.to_str().ok())
            .map(|s| s.split(';').next().unwrap_or(s).trim().to_ascii_lowercase());

        let body = if is_sse(ct.as_deref()) {
            // Stream into a string through StreamReader for incremental reading.
            let byte_stream = response
                .bytes_stream()
                .map_err(|e| std::io::Error::new(std::io::ErrorKind::Other, e));
            let reader = BufReader::new(StreamReader::new(byte_stream));
            let mut lines = reader.lines();
            let mut buf = String::new();
            loop {
                match tokio::select! {
                    r = lines.next_line() => r,
                    _ = self.lifetime.cancelled() => return Err(HttpCallError::Stopped),
                } {
                    Ok(Some(line)) => {
                        buf.push_str(&line);
                        buf.push('\n');
                    }
                    Ok(None) => break,
                    Err(e) => {
                        tracing::debug!(error = %e, "SSE stream read error");
                        break;
                    }
                }
            }
            buf
        } else {
            response
                .text()
                .await
                .map_err(|e| HttpCallError::Transport(e.to_string()))?
        };

        Ok((status, ct, body))
    }

    async fn send_once(
        &self,
        message: &serde_json::Value,
    ) -> Result<reqwest::Response, HttpCallError> {
        let body = serde_json::to_string(message)
            .map_err(|e| HttpCallError::Protocol(e.to_string()))?;

        let mut req = self
            .http
            .post(self.url.clone())
            .header("Content-Type", "application/json")
            .header("Accept", "application/json, text/event-stream")
            .header("MCP-Protocol-Version", PROTOCOL_VERSION)
            .body(body);

        // Mcp-Session-Id from previous initialize response.
        {
            let guard = self.session_id.lock().unwrap_or_else(|e| e.into_inner());
            if let Some(sid) = guard.as_deref() {
                req = req.header("Mcp-Session-Id", sid.to_owned());
            }
        }

        // Static headers from config.
        for (k, v) in &self.static_headers {
            req = req.header(k.as_str(), v.as_str());
        }

        // Auth token — never logged (Secret<T> redacts Debug).
        if let Some(auth) = &self.auth {
            if let Some(token) = auth.get_access_token().await {
                req = req.header("Authorization", format!("Bearer {}", token.expose()));
            }
        }

        req.send()
            .await
            .map_err(|e| HttpCallError::Transport(e.to_string()))
    }

    fn capture_session(&self, response: &reqwest::Response) {
        if let Some(value) = response
            .headers()
            .get("Mcp-Session-Id")
            .and_then(|v| v.to_str().ok())
            .filter(|s| !s.is_empty())
        {
            *self.session_id.lock().unwrap_or_else(|e| e.into_inner()) =
                Some(value.to_owned());
        }
    }
}

impl Drop for McpHttpClient {
    fn drop(&mut self) {
        self.lifetime.cancel();
    }
}

// ── SSE parsing ───────────────────────────────────────────────────────────────

fn is_sse(content_type: Option<&str>) -> bool {
    content_type
        .map(|ct| ct.eq_ignore_ascii_case("text/event-stream"))
        .unwrap_or(false)
}

/// Parse SSE events from a buffered string, returning the first JSON-RPC
/// message whose `id` matches `expected_id`.
fn read_sse_message_from_string(
    data: &str,
    expected_id: i64,
) -> Result<serde_json::Value, HttpCallError> {
    let mut current_data = String::new();

    for line in data.lines() {
        if line.is_empty() {
            // Blank line = end of SSE event.
            if let Some(msg) = parse_json_message(current_data.trim_end()) {
                current_data.clear();
                if matches_id(&msg, expected_id) {
                    return Ok(msg);
                }
            } else {
                current_data.clear();
            }
            continue;
        }

        if let Some(rest) = line.strip_prefix("data:") {
            // Consume one optional leading space per the SSE spec.
            let payload = rest.strip_prefix(' ').unwrap_or(rest);
            current_data.push_str(payload);
        }
        // Other SSE fields (event:, id:, retry:) are intentionally ignored.
    }

    // Handle trailing event without a final blank line (C# does the same).
    let trailing = current_data.trim_end();
    if !trailing.is_empty() {
        if let Some(msg) = parse_json_message(trailing) {
            if matches_id(&msg, expected_id) {
                return Ok(msg);
            }
        }
    }

    Err(HttpCallError::Protocol(
        "SSE stream closed before a matching response arrived".into(),
    ))
}

fn matches_id(msg: &serde_json::Value, id: i64) -> bool {
    msg.get("id")
        .and_then(|v| v.as_i64())
        .map(|n| n == id)
        .unwrap_or(false)
}

// ── JSON-RPC helpers ──────────────────────────────────────────────────────────

fn parse_json_message(text: &str) -> Option<serde_json::Value> {
    let text = text.trim();
    if text.is_empty() {
        return None;
    }
    serde_json::from_str(text).ok()
}

fn extract_result(msg: serde_json::Value) -> Result<serde_json::Value, HttpCallError> {
    if let Some(error) = msg.get("error") {
        let message = error
            .get("message")
            .and_then(|v| v.as_str())
            .unwrap_or("MCP server returned an error")
            .to_owned();
        return Err(HttpCallError::McpError(message));
    }
    Ok(msg.get("result").cloned().unwrap_or(serde_json::Value::Null))
}

fn parse_server_info(result: &serde_json::Value) -> McpServerInfo {
    let server = result.get("serverInfo");
    McpServerInfo {
        name: server
            .and_then(|s| s.get("name"))
            .and_then(|v| v.as_str())
            .unwrap_or("unknown")
            .to_owned(),
        version: server
            .and_then(|s| s.get("version"))
            .and_then(|v| v.as_str())
            .unwrap_or("?")
            .to_owned(),
    }
}

fn parse_tools_list(result: &serde_json::Value) -> Vec<McpToolInfo> {
    let Some(arr) = result.get("tools").and_then(|v| v.as_array()) else {
        return Vec::new();
    };
    arr.iter().filter_map(parse_one_tool).collect()
}

fn truncate(s: &str, max: usize) -> &str {
    if s.len() <= max { s } else { &s[..max] }
}

// ── Error type ────────────────────────────────────────────────────────────────

#[derive(Debug)]
pub(crate) enum HttpCallError {
    Transport(String),
    Http(u16, String),
    McpError(String),
    Protocol(String),
    Stopped,
}

impl std::fmt::Display for HttpCallError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Transport(e) => write!(f, "transport error: {e}"),
            Self::Http(s, b) => write!(f, "HTTP {s}: {b}"),
            Self::McpError(m) => write!(f, "MCP error: {m}"),
            Self::Protocol(m) => write!(f, "protocol error: {m}"),
            Self::Stopped => write!(f, "MCP server was stopped"),
        }
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    // ── SSE parsing ──────────────────────────────────────────────────────────

    #[test]
    fn sse_parses_single_event() {
        let sse = "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"ok\":true}}\n\n";
        let msg = read_sse_message_from_string(sse, 1).unwrap();
        assert_eq!(msg["result"]["ok"], true);
    }

    #[test]
    fn sse_finds_matching_id_among_multiple_events() {
        let sse = concat!(
            "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"first\"}\n\n",
            "data: {\"jsonrpc\":\"2.0\",\"id\":2,\"result\":\"second\"}\n\n",
        );
        let msg = read_sse_message_from_string(sse, 2).unwrap();
        assert_eq!(msg["result"], "second");
    }

    #[test]
    fn sse_returns_error_when_no_match() {
        let sse = "data: {\"jsonrpc\":\"2.0\",\"id\":5,\"result\":{}}\n\n";
        assert!(read_sse_message_from_string(sse, 99).is_err());
    }

    #[test]
    fn sse_handles_trailing_event_without_blank_line() {
        let sse = "data: {\"jsonrpc\":\"2.0\",\"id\":3,\"result\":\"trailing\"}";
        let msg = read_sse_message_from_string(sse, 3).unwrap();
        assert_eq!(msg["result"], "trailing");
    }

    #[test]
    fn sse_ignores_non_data_fields() {
        let sse = concat!(
            "event: message\n",
            "id: 1\n",
            "data: {\"jsonrpc\":\"2.0\",\"id\":42,\"result\":\"ok\"}\n\n",
        );
        let msg = read_sse_message_from_string(sse, 42).unwrap();
        assert_eq!(msg["result"], "ok");
    }

    // ── extract_result ───────────────────────────────────────────────────────

    #[test]
    fn extract_result_returns_result_field() {
        let msg = serde_json::json!({"jsonrpc":"2.0","id":1,"result":{"x":1}});
        let r = extract_result(msg).unwrap();
        assert_eq!(r["x"], 1);
    }

    #[test]
    fn extract_result_errors_on_mcp_error() {
        let msg = serde_json::json!({"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Not found"}});
        assert!(matches!(extract_result(msg), Err(HttpCallError::McpError(_))));
    }

    // ── is_sse ───────────────────────────────────────────────────────────────

    #[test]
    fn is_sse_matches_exact() {
        assert!(is_sse(Some("text/event-stream")));
    }

    #[test]
    fn is_sse_is_case_insensitive() {
        assert!(is_sse(Some("TEXT/EVENT-STREAM")));
    }

    #[test]
    fn is_sse_rejects_json() {
        assert!(!is_sse(Some("application/json")));
    }

    // ── truncate ─────────────────────────────────────────────────────────────

    #[test]
    fn truncate_cuts_long_strings() {
        let s = "a".repeat(600);
        assert_eq!(truncate(&s, 500).len(), 500);
    }

    #[test]
    fn truncate_leaves_short_strings_intact() {
        assert_eq!(truncate("hello", 500), "hello");
    }
}
