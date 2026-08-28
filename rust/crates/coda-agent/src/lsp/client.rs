//! LSP client: transport, initialize handshake, notification/request dispatch.
//!
//! # Transport
//!
//! LSP uses the same Content-Length-framed JSON-RPC as `coda serve`, so this
//! module reuses `coda_client::transport::connect` directly rather than
//! building a new framing layer. Both sides speak JSON-RPC 2.0; `coda-client`'s
//! `Connection` handles outgoing requests and its `Inbound` stream delivers
//! incoming notifications (including `textDocument/publishDiagnostics`) and
//! server-initiated requests (`workspace/configuration`).
//!
//! # Bounding
//!
//! The `initialize` request is wrapped in `startup_timeout`. The `shutdown`
//! request is wrapped in a 2-second deadline. A server that hangs in either
//! step does not block the agent.

use std::collections::HashMap;
use std::process::Stdio;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use serde_json::Value;
use tokio::process::Command;
use tokio::sync::mpsc;
use tokio::task::JoinHandle;

use coda_client::transport::{connect, Connection, Inbound};

use crate::lsp::config::LspServerConfig;

/// Default startup timeout: 15 seconds (matching C#).
pub const DEFAULT_STARTUP_TIMEOUT_MS: u64 = 15_000;

/// How long we wait for `shutdown` before giving up.
const SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(2);

/// Notification handler: `fn(params: Option<Value>)`.
type NotifHandler = Arc<dyn Fn(Option<Value>) + Send + Sync + 'static>;
/// Request handler: `fn(params: Option<Value>) -> Option<Value>`.
type ReqHandler = Arc<dyn Fn(Option<Value>) -> Option<Value> + Send + Sync + 'static>;

/// A live connection to one LSP server.
///
/// Methods may be called concurrently; the underlying `Connection` and handler
/// maps are `Send + Sync`.
pub struct LspClient {
    connection: Connection,
    _dispatch_task: JoinHandle<()>,
    notification_handlers: Arc<Mutex<HashMap<String, NotifHandler>>>,
    request_handlers: Arc<Mutex<HashMap<String, ReqHandler>>>,
    pub server_name: String,
    pub capabilities: Value,
}

impl LspClient {
    /// Spawn the LSP server process, perform the `initialize` handshake, and
    /// return a live client.
    pub async fn start(
        name: &str,
        config: &LspServerConfig,
        workspace_root: Option<&str>,
    ) -> Result<Self, LspError> {
        let startup_ms = config.startup_timeout_ms.unwrap_or(DEFAULT_STARTUP_TIMEOUT_MS);

        let mut builder = Command::new(&config.command);
        builder
            .args(&config.args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .kill_on_drop(true);
        for (k, v) in &config.env {
            builder.env(k, v);
        }

        let mut child = builder.spawn().map_err(|e| LspError::Spawn {
            server: name.to_string(),
            source: e,
        })?;

        let stdin = child.stdin.take().expect("stdin piped");
        let stdout = child.stdout.take().expect("stdout piped");

        let notif_handlers: Arc<Mutex<HashMap<String, NotifHandler>>> =
            Arc::new(Mutex::new(HashMap::new()));
        let req_handlers: Arc<Mutex<HashMap<String, ReqHandler>>> =
            Arc::new(Mutex::new(HashMap::new()));

        let (connection, inbound_rx, _transport_tasks) = connect(stdout, stdin);

        let dispatch_task = tokio::spawn(dispatch_loop(
            inbound_rx,
            connection.clone(),
            Arc::clone(&notif_handlers),
            Arc::clone(&req_handlers),
        ));

        let client = Self {
            connection: connection.clone(),
            _dispatch_task: dispatch_task,
            notification_handlers: notif_handlers,
            request_handlers: req_handlers,
            server_name: name.to_string(),
            capabilities: Value::Null,
        };

        // Perform the initialize handshake.
        let root_uri = workspace_root.map(|r| {
            let path = std::path::Path::new(r);
            std::fs::canonicalize(path)
                .unwrap_or_else(|_| path.to_path_buf())
                .to_string_lossy()
                .replace('\\', "/")
        }).map(|s| {
            // Build a file:// URI without pulling in a url crate.
            if s.len() >= 2 && s.as_bytes()[1] == b':' {
                format!("file:///{s}") // Windows drive path: C:/...
            } else {
                format!("file://{s}")
            }
        });

        let init_params = serde_json::json!({
            "processId": null,
            "rootUri": root_uri,
            "capabilities": {},
            "initializationOptions": config.initialization_options
        });

        let caps = tokio::time::timeout(
            Duration::from_millis(startup_ms),
            connection.request("initialize", Some(init_params)),
        )
        .await
        .map_err(|_| LspError::StartupTimeout { server: name.to_string() })?
        .map_err(|e| LspError::Protocol { server: name.to_string(), message: e.to_string() })?;

        // Send initialized notification.
        let _ = connection.notify(
            "initialized",
            Some(serde_json::json!({})),
        );

        Ok(Self {
            capabilities: caps,
            _dispatch_task: client._dispatch_task,
            ..client
        })
    }

    /// Build a client directly from a Connection (used in tests with in-memory
    /// streams, bypassing process spawn).
    pub fn from_connection(
        server_name: impl Into<String>,
        connection: Connection,
        inbound_rx: mpsc::UnboundedReceiver<Inbound>,
        capabilities: Value,
    ) -> Self {
        let notif_handlers: Arc<Mutex<HashMap<String, NotifHandler>>> =
            Arc::new(Mutex::new(HashMap::new()));
        let req_handlers: Arc<Mutex<HashMap<String, ReqHandler>>> =
            Arc::new(Mutex::new(HashMap::new()));

        let dispatch_task = tokio::spawn(dispatch_loop(
            inbound_rx,
            connection.clone(),
            Arc::clone(&notif_handlers),
            Arc::clone(&req_handlers),
        ));

        Self {
            connection,
            _dispatch_task: dispatch_task,
            notification_handlers: notif_handlers,
            request_handlers: req_handlers,
            server_name: server_name.into(),
            capabilities,
        }
    }

    /// Send a request and wait for its response.
    pub async fn request(
        &self,
        method: &str,
        params: Option<Value>,
    ) -> Result<Value, LspError> {
        self.connection
            .request(method, params)
            .await
            .map_err(|e| LspError::Protocol {
                server: self.server_name.clone(),
                message: e.to_string(),
            })
    }

    /// Send a notification (fire-and-forget at the protocol level).
    pub fn notify(&self, method: &str, params: Option<Value>) {
        let _ = self.connection.notify(method, params);
    }

    /// Register a handler for server-pushed notifications. If a handler for
    /// this method already exists, it is replaced.
    pub fn on_notification<F>(&self, method: impl Into<String>, handler: F)
    where
        F: Fn(Option<Value>) + Send + Sync + 'static,
    {
        self.notification_handlers
            .lock()
            .expect("handlers poisoned")
            .insert(method.into(), Arc::new(handler));
    }

    /// Register a handler for server-initiated requests. The handler must
    /// return the response value (`None` is serialised as `null`).
    pub fn on_request<F>(&self, method: impl Into<String>, handler: F)
    where
        F: Fn(Option<Value>) -> Option<Value> + Send + Sync + 'static,
    {
        self.request_handlers
            .lock()
            .expect("handlers poisoned")
            .insert(method.into(), Arc::new(handler));
    }

    /// Send `shutdown` (best-effort, bounded at 2 s) then `exit`.
    pub async fn stop(&self) {
        let _ = tokio::time::timeout(
            SHUTDOWN_TIMEOUT,
            self.connection.request("shutdown", Some(serde_json::json!({}))),
        )
        .await;
        let _ = self.connection.notify("exit", None);
    }
}

/// Errors from the LSP client.
#[derive(Debug, thiserror::Error)]
pub enum LspError {
    #[error("failed to spawn LSP server '{server}': {source}")]
    Spawn {
        server: String,
        #[source]
        source: std::io::Error,
    },
    #[error("LSP server '{server}' did not respond within the startup timeout")]
    StartupTimeout { server: String },
    #[error("LSP server '{server}' protocol error: {message}")]
    Protocol { server: String, message: String },
}

// ── Dispatch task ─────────────────────────────────────────────────────────────

async fn dispatch_loop(
    mut inbound: mpsc::UnboundedReceiver<Inbound>,
    // Kept alive so the outgoing channel stays open for the connection's lifetime.
    _connection: Connection,
    notif_handlers: Arc<Mutex<HashMap<String, NotifHandler>>>,
    req_handlers: Arc<Mutex<HashMap<String, ReqHandler>>>,
) {
    while let Some(msg) = inbound.recv().await {
        match msg {
            Inbound::Notification { method, params } => {
                let handler = notif_handlers
                    .lock()
                    .expect("poisoned")
                    .get(&method)
                    .cloned();
                if let Some(h) = handler {
                    h(params);
                }
            }
            Inbound::Request { method, params, responder } => {
                let handler = req_handlers
                    .lock()
                    .expect("poisoned")
                    .get(&method)
                    .cloned();
                match handler {
                    Some(h) => {
                        let result = h(params).unwrap_or(Value::Null);
                        responder.respond(result);
                    }
                    None => {
                        // No handler: reply with method-not-found so the server
                        // is not left waiting.
                        use coda_proto::error_codes;
                        responder.fail(error_codes::METHOD_NOT_FOUND, "method not handled by client");
                    }
                }
            }
        }
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use tokio::io::{duplex, AsyncWriteExt};

    use coda_client::transport::connect as transport_connect;
    use coda_proto::{encode_frame, FrameDecoder};

    /// Builds an `LspClient` driven by in-memory streams, returning the
    /// "server" end of the duplex so tests can write/read protocol messages.
    fn harness() -> (LspClient, tokio::io::DuplexStream, tokio::io::DuplexStream) {
        let (server_out, client_in) = duplex(128 * 1024);
        let (client_out, server_in) = duplex(128 * 1024);
        let (conn, inbound, _tasks) = transport_connect(client_in, client_out);
        let client = LspClient::from_connection("test-lsp", conn, inbound, json!({}));
        (client, server_out, server_in)
    }

    async fn read_frame(stream: &mut tokio::io::DuplexStream) -> Value {
        use tokio::io::AsyncReadExt;
        let mut decoder = FrameDecoder::new();
        let mut buf = vec![0u8; 4096];
        loop {
            if let Some(frame) = decoder.next_frame().unwrap() {
                return serde_json::from_slice(&frame).unwrap();
            }
            let n = stream.read(&mut buf).await.unwrap();
            assert_ne!(n, 0, "stream closed before frame");
            decoder.feed(&buf[..n]);
        }
    }

    async fn write_frame(stream: &mut tokio::io::DuplexStream, value: Value) {
        let bytes = serde_json::to_vec(&value).unwrap();
        stream.write_all(&encode_frame(&bytes)).await.unwrap();
        stream.flush().await.unwrap();
    }

    #[tokio::test]
    async fn request_correlates_response() {
        let (client, mut server_out, mut server_in) = harness();

        let call = tokio::spawn(async move {
            client.request("textDocument/hover", Some(json!({ "pos": 1 }))).await
        });

        let sent = read_frame(&mut server_in).await;
        assert_eq!(sent["method"], "textDocument/hover");

        write_frame(&mut server_out, json!({
            "jsonrpc": "2.0",
            "id": sent["id"],
            "result": { "contents": "hover info" }
        }))
        .await;

        let result = call.await.unwrap().unwrap();
        assert_eq!(result["contents"], "hover info");
    }

    #[tokio::test]
    async fn notification_handler_is_called() {
        let (client, mut server_out, _server_in) = harness();

        let received: Arc<Mutex<Vec<Value>>> = Arc::new(Mutex::new(Vec::new()));
        let received2 = Arc::clone(&received);

        client.on_notification("textDocument/publishDiagnostics", move |params| {
            received2.lock().unwrap().push(params.unwrap_or(Value::Null));
        });

        write_frame(
            &mut server_out,
            json!({
                "jsonrpc": "2.0",
                "method": "textDocument/publishDiagnostics",
                "params": { "uri": "file:///a.rs", "diagnostics": [] }
            }),
        )
        .await;

        // Give the dispatch task time to run.
        tokio::time::sleep(Duration::from_millis(50)).await;

        let got = received.lock().unwrap();
        assert_eq!(got.len(), 1);
        assert_eq!(got[0]["uri"], "file:///a.rs");
    }

    #[tokio::test]
    async fn request_handler_for_workspace_configuration() {
        let (client, mut server_out, mut server_in) = harness();

        client.on_request("workspace/configuration", |_params| {
            Some(json!([null]))
        });

        write_frame(
            &mut server_out,
            json!({
                "jsonrpc": "2.0",
                "id": 99,
                "method": "workspace/configuration",
                "params": { "items": [{ "section": "rust" }] }
            }),
        )
        .await;

        tokio::time::sleep(Duration::from_millis(50)).await;

        let reply = read_frame(&mut server_in).await;
        assert_eq!(reply["id"], 99);
        assert!(reply.get("error").is_none(), "expected a successful result");
    }

    #[tokio::test]
    async fn unhandled_server_request_gets_method_not_found() {
        // A client with no request handlers: the dispatch task should reply
        // with method-not-found to any server-initiated request.
        let (server_out, client_in) = duplex(128 * 1024);
        let (client_out, server_in) = duplex(128 * 1024);
        let (conn, inbound, _tasks) = transport_connect(client_in, client_out);
        let _client = LspClient::from_connection("test", conn, inbound, json!({}));
        let mut server_out = server_out;
        let mut server_in = server_in;

        write_frame(
            &mut server_out,
            json!({
                "jsonrpc": "2.0",
                "id": 7,
                "method": "unknown/method",
            }),
        )
        .await;

        tokio::time::sleep(Duration::from_millis(50)).await;
        let reply = read_frame(&mut server_in).await;
        assert_eq!(reply["id"], 7);
        assert!(reply.get("error").is_some(), "expected method-not-found error");
    }

    #[tokio::test]
    async fn stop_sends_shutdown_and_exit() {
        let (client, mut server_out, mut server_in) = harness();

        let stop = tokio::spawn(async move { client.stop().await });
        let shutdown = read_frame(&mut server_in).await;
        assert_eq!(shutdown["method"], "shutdown");

        write_frame(
            &mut server_out,
            json!({ "jsonrpc": "2.0", "id": shutdown["id"], "result": null }),
        )
        .await;

        let exit = read_frame(&mut server_in).await;
        assert_eq!(exit["method"], "exit");

        stop.await.unwrap();
    }

    #[tokio::test]
    async fn stop_does_not_hang_when_server_ignores_shutdown() {
        // Server never responds to shutdown — stop must complete within the
        // bounded 2-second SHUTDOWN_TIMEOUT.
        let (client, _server_out, _server_in) = harness();
        let start = std::time::Instant::now();
        // Close the server side to simulate a dead server.
        drop(_server_out);
        client.stop().await;
        assert!(start.elapsed() < Duration::from_secs(5), "stop hung");
    }
}
