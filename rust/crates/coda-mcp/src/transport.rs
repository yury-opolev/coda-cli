//! Newline-delimited JSON-RPC 2.0 transport for the MCP stdio protocol.
//!
//! MCP's stdio transport sends one JSON object per line, unlike the
//! Content-Length-framed protocol that `coda-client`'s transport uses.
//! The framing difference is the only reason this module exists; the
//! structural pattern (Connection, PendingMap, write_loop, read_loop, finish)
//! follows `coda-client/transport.rs` closely.
//!
//! # Why we don't reuse coda-client's transport
//!
//! 1. **Different framing** – newlines vs `Content-Length` headers.
//! 2. **MCP is client-only** – the server sends responses and server-sent
//!    notifications but does not send server-initiated requests that need a
//!    `Responder`. Removing the `Responder` pattern simplifies the code.
//! 3. **Different crate** – `coda-mcp` must not depend on `coda-client` to
//!    avoid a layering violation (coda-client knows about `coda serve`).

use std::collections::HashMap;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex};

use coda_proto::error_codes;
use serde_json::Value;
use tokio::io::{AsyncBufReadExt, AsyncRead, AsyncWrite, AsyncWriteExt, BufReader};
use tokio::sync::{mpsc, oneshot};
use tokio::task::JoinHandle;

use crate::error::McpTransportError;

// Re-export so callers in the crate don't have to import coda_proto directly.
pub(crate) use coda_proto::{RequestId, ResponseError};

type PendingMap = Arc<Mutex<HashMap<RequestId, oneshot::Sender<Result<Value, ResponseError>>>>>;

/// A notification pushed by the server (not a response to one of our requests).
#[derive(Debug)]
pub(crate) struct ServerNotification {
    #[allow(dead_code)]
    pub method: String,
    #[allow(dead_code)]
    pub params: Option<Value>,
}

/// Handle for sending requests and notifications to the MCP server.
///
/// Cheap to clone; all clones share the same underlying queue. Matches the
/// shape of `coda-client`'s `Connection` so readers of both can apply the
/// same patterns.
#[derive(Debug, Clone)]
pub(crate) struct Connection {
    outgoing: mpsc::UnboundedSender<String>,
    pending: PendingMap,
    next_id: Arc<AtomicI64>,
}

impl Connection {
    /// Sends a request and awaits its response.
    pub async fn request(
        &self,
        method: impl Into<String>,
        params: Option<Value>,
    ) -> Result<Value, McpTransportError> {
        let rx = self.send_request(method, params)?;
        rx.await
            .map_err(|_| McpTransportError::ConnectionClosed)?
            .map_err(McpTransportError::Rpc)
    }

    /// Sends a notification (fire-and-forget at the protocol level).
    pub fn notify(
        &self,
        method: impl Into<String>,
        params: Option<Value>,
    ) -> Result<(), McpTransportError> {
        let mut msg = serde_json::json!({ "jsonrpc": "2.0", "method": method.into() });
        if let Some(p) = params {
            msg["params"] = p;
        }
        let line = serde_json::to_string(&msg).map_err(McpTransportError::Json)?;
        self.outgoing
            .send(line)
            .map_err(|_| McpTransportError::ConnectionClosed)
    }

    #[allow(dead_code)]
    pub fn is_closed(&self) -> bool {
        self.outgoing.is_closed()
    }

    fn send_request(
        &self,
        method: impl Into<String>,
        params: Option<Value>,
    ) -> Result<oneshot::Receiver<Result<Value, ResponseError>>, McpTransportError> {
        let id = RequestId::Number(self.next_id.fetch_add(1, Ordering::Relaxed));
        let mut msg = serde_json::json!({
            "jsonrpc": "2.0",
            "id": id,
            "method": method.into(),
        });
        if let Some(p) = params {
            msg["params"] = p;
        }
        let line = serde_json::to_string(&msg).map_err(McpTransportError::Json)?;

        let (tx, rx) = oneshot::channel();
        self.pending
            .lock()
            .expect("pending map poisoned")
            .insert(id.clone(), tx);

        if let Err(_) = self.outgoing.send(line) {
            // Remove the registration we just made; otherwise finish() would
            // send a spurious error to a receiver nobody holds.
            self.pending
                .lock()
                .expect("pending map poisoned")
                .remove(&id);
            return Err(McpTransportError::ConnectionClosed);
        }

        Ok(rx)
    }
}

/// Background tasks servicing a `Connection`.
#[allow(dead_code)] // kept alive via Drop; the fields don't need to be read
pub(crate) struct ConnectionTasks {
    pub reader: JoinHandle<()>,
    pub writer: JoinHandle<()>,
}

/// Wires a reader/writer pair into a `Connection` plus a notification stream.
///
/// The reader uses `BufReader::lines()` to split the MCP stdio stream into
/// newline-terminated JSON objects. Each complete line is dispatched: if it
/// carries an `id` matching a pending request, the waiter is resolved;
/// otherwise it is sent to the notification channel.
pub(crate) fn connect<R, W>(
    reader: R,
    writer: W,
) -> (Connection, mpsc::UnboundedReceiver<ServerNotification>, ConnectionTasks)
where
    R: AsyncRead + Unpin + Send + 'static,
    W: AsyncWrite + Unpin + Send + 'static,
{
    let (outgoing_tx, outgoing_rx) = mpsc::unbounded_channel::<String>();
    let (notif_tx, notif_rx) = mpsc::unbounded_channel::<ServerNotification>();
    let pending: PendingMap = Arc::new(Mutex::new(HashMap::new()));

    let connection = Connection {
        outgoing: outgoing_tx,
        pending: Arc::clone(&pending),
        next_id: Arc::new(AtomicI64::new(1)),
    };

    let writer_task = tokio::spawn(write_loop(writer, outgoing_rx));
    let reader_task = tokio::spawn(read_loop(reader, notif_tx, pending));

    (connection, notif_rx, ConnectionTasks { reader: reader_task, writer: writer_task })
}

async fn write_loop<W>(mut writer: W, mut outgoing: mpsc::UnboundedReceiver<String>)
where
    W: AsyncWrite + Unpin + Send + 'static,
{
    while let Some(line) = outgoing.recv().await {
        let data = format!("{line}\n");
        if let Err(e) = writer.write_all(data.as_bytes()).await {
            tracing::debug!(%e, "MCP stdin closed; stopping writer");
            break;
        }
        if let Err(e) = writer.flush().await {
            tracing::debug!(%e, "failed to flush MCP stdin");
            break;
        }
    }
    let _ = writer.shutdown().await;
}

async fn read_loop<R>(
    reader: R,
    notifications: mpsc::UnboundedSender<ServerNotification>,
    pending: PendingMap,
) where
    R: AsyncRead + Unpin + Send + 'static,
{
    let mut lines = BufReader::new(reader).lines();
    loop {
        match lines.next_line().await {
            Ok(Some(line)) if !line.trim().is_empty() => {
                dispatch(&line, &notifications, &pending);
            }
            Ok(Some(_)) => {} // empty / whitespace-only line: skip
            Ok(None) | Err(_) => break, // EOF or I/O error
        }
    }
    finish(pending);
}

/// Fail every in-flight request so no caller waits forever on a dead server.
fn finish(pending: PendingMap) {
    let waiters: Vec<_> = pending
        .lock()
        .expect("pending map poisoned")
        .drain()
        .map(|(_, tx)| tx)
        .collect();
    for tx in waiters {
        let _ = tx.send(Err(ResponseError {
            code: error_codes::INTERNAL_ERROR,
            message: "MCP server connection closed before responding".to_string(),
            data: None,
        }));
    }
}

/// Parse one incoming line and dispatch it.
///
/// Lines that are not valid JSON, or whose `id` is not in the pending map, are
/// silently discarded. This keeps the connection alive even if a misbehaving
/// server sends noise.
fn dispatch(
    line: &str,
    notifications: &mpsc::UnboundedSender<ServerNotification>,
    pending: &PendingMap,
) {
    let msg: Value = match serde_json::from_str(line) {
        Ok(v) => v,
        Err(e) => {
            // Non-JSON output from the server (e.g. a startup banner) must not
            // kill the connection; log and keep reading.
            tracing::warn!(%e, line, "MCP server sent non-JSON line; skipping");
            return;
        }
    };

    // A message with an id that matches a pending request is a response.
    let request_id = match msg.get("id") {
        Some(Value::Number(n)) => n.as_i64().map(RequestId::Number),
        Some(Value::String(s)) => Some(RequestId::String(s.clone())),
        _ => None,
    };

    if let Some(rid) = request_id {
        let waiter = pending.lock().expect("pending map poisoned").remove(&rid);
        if let Some(tx) = waiter {
            let result = if let Some(error) = msg.get("error") {
                let message = error
                    .get("message")
                    .and_then(Value::as_str)
                    .unwrap_or("MCP server returned an error")
                    .to_string();
                let code = error
                    .get("code")
                    .and_then(Value::as_i64)
                    .unwrap_or(error_codes::INTERNAL_ERROR);
                Err(ResponseError { code, message, data: None })
            } else {
                Ok(msg.get("result").cloned().unwrap_or(Value::Null))
            };
            let _ = tx.send(result);
            return;
        }
    }

    // Server-sent notification (or a response for an unknown id).
    if let Some(method) = msg.get("method").and_then(Value::as_str) {
        let _ = notifications.send(ServerNotification {
            method: method.to_string(),
            params: msg.get("params").cloned(),
        });
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use tokio::io::{duplex, AsyncWriteExt};

    /// Write a newline-terminated JSON line to the peer end of a duplex stream.
    async fn write_line(stream: &mut tokio::io::DuplexStream, value: Value) {
        let line = serde_json::to_string(&value).unwrap();
        stream.write_all(format!("{line}\n").as_bytes()).await.unwrap();
        stream.flush().await.unwrap();
    }

    /// Read one newline-terminated JSON line from the peer end.
    ///
    /// We read byte-by-byte to avoid a BufReader dropping its internal
    /// buffer between calls and losing the start of the next message.
    async fn read_line(stream: &mut tokio::io::DuplexStream) -> Value {
        use tokio::io::AsyncReadExt;
        let mut line = Vec::new();
        let mut byte = [0u8; 1];
        loop {
            stream.read_exact(&mut byte).await.unwrap();
            if byte[0] == b'\n' {
                break;
            }
            line.push(byte[0]);
        }
        serde_json::from_slice(line.trim_ascii()).unwrap()
    }

    /// Full harness: server gets both its read (server_in) and write (server_out) sides.
    fn full_harness() -> (
        Connection,
        mpsc::UnboundedReceiver<ServerNotification>,
        tokio::io::DuplexStream,  // server's write side (client reads from this)
        tokio::io::DuplexStream,  // server's read side (client writes to this)
    ) {
        let (server_out, client_in) = duplex(64 * 1024);
        let (client_out, server_in) = duplex(64 * 1024);
        let (conn, notifs, _tasks) = connect(client_in, client_out);
        (conn, notifs, server_out, server_in)
    }

    #[tokio::test]
    async fn sends_request_with_jsonrpc_version() {
        let (conn, _notifs, _server_out, mut server_in) = full_harness();

        let _rx = conn.send_request("initialize", Some(json!({ "v": 1 }))).unwrap();

        let sent = read_line(&mut server_in).await;
        assert_eq!(sent["jsonrpc"], "2.0");
        assert_eq!(sent["method"], "initialize");
        assert_eq!(sent["params"], json!({ "v": 1 }));
        assert!(sent.get("id").is_some());
    }

    #[tokio::test]
    async fn resolves_response_to_matching_request() {
        let (conn, _notifs, mut server_out, mut server_in) = full_harness();

        let call = tokio::spawn({
            let conn = conn.clone();
            async move { conn.request("initialize", None).await }
        });

        let sent = read_line(&mut server_in).await;
        write_line(
            &mut server_out,
            json!({ "jsonrpc": "2.0", "id": sent["id"], "result": { "ok": true } }),
        )
        .await;

        let result = call.await.unwrap().unwrap();
        assert_eq!(result, json!({ "ok": true }));
    }

    #[tokio::test]
    async fn surfaces_error_response_to_caller() {
        let (conn, _notifs, mut server_out, mut server_in) = full_harness();

        let call =
            tokio::spawn({ let conn = conn.clone(); async move { conn.request("x", None).await } });

        let sent = read_line(&mut server_in).await;
        write_line(
            &mut server_out,
            json!({
                "jsonrpc": "2.0",
                "id": sent["id"],
                "error": { "code": -32601, "message": "Method not found" }
            }),
        )
        .await;

        let err = call.await.unwrap().unwrap_err();
        assert!(matches!(err, McpTransportError::Rpc(e) if e.code == -32601));
    }

    #[tokio::test]
    async fn correlates_concurrent_requests_out_of_order() {
        let (conn, _notifs, mut server_out, mut server_in) = full_harness();

        let rx_a = conn.send_request("a", None).unwrap();
        let rx_b = conn.send_request("b", None).unwrap();

        let sent_a = read_line(&mut server_in).await;
        let sent_b = read_line(&mut server_in).await;
        assert_ne!(sent_a["id"], sent_b["id"]);

        // Answer in reverse order.
        write_line(&mut server_out, json!({ "jsonrpc": "2.0", "id": sent_b["id"], "result": "B" })).await;
        write_line(&mut server_out, json!({ "jsonrpc": "2.0", "id": sent_a["id"], "result": "A" })).await;

        assert_eq!(rx_b.await.unwrap().unwrap(), json!("B"));
        assert_eq!(rx_a.await.unwrap().unwrap(), json!("A"));
    }

    #[tokio::test]
    async fn garbage_line_does_not_kill_connection() {
        let (_conn, mut notifs, mut server_out, mut _server_in) = full_harness();

        // Server sends garbage then a valid notification.
        server_out.write_all(b"this is not json at all\n").await.unwrap();
        write_line(&mut server_out, json!({ "jsonrpc": "2.0", "method": "ping" })).await;

        // The notification must arrive despite the garbage line.
        let notif = tokio::time::timeout(
            std::time::Duration::from_secs(2),
            notifs.recv(),
        )
        .await
        .expect("timeout")
        .expect("channel closed");
        assert_eq!(notif.method, "ping");
    }

    #[tokio::test]
    async fn server_notification_delivered_to_channel() {
        let (_conn, mut notifs, mut server_out, _server_in) = full_harness();

        write_line(
            &mut server_out,
            json!({ "jsonrpc": "2.0", "method": "tools/listChanged", "params": {} }),
        )
        .await;

        let notif = tokio::time::timeout(
            std::time::Duration::from_secs(2),
            notifs.recv(),
        )
        .await
        .expect("timeout")
        .expect("closed");
        assert_eq!(notif.method, "tools/listChanged");
    }

    #[tokio::test]
    async fn fails_pending_requests_when_server_disconnects() {
        let (conn, _notifs, server_out, _server_in) = full_harness();

        // Send the request directly (not via spawn) so the pending entry is
        // inserted before we close the server's write side. Then dropping
        // server_out causes the reader task to see EOF and call finish().
        let rx = conn.send_request("init", None).unwrap();

        // Close the server's write side → EOF on client's read side.
        drop(server_out);

        let err = rx.await.unwrap().unwrap_err();
        assert_eq!(err.code, error_codes::INTERNAL_ERROR);
    }

    #[tokio::test]
    async fn sends_notification_without_id() {
        let (conn, _notifs, _server_out, mut server_in) = full_harness();
        conn.notify("notifications/initialized", None).unwrap();
        let sent = read_line(&mut server_in).await;
        assert_eq!(sent["method"], "notifications/initialized");
        assert!(sent.get("id").is_none());
    }

    #[tokio::test]
    async fn empty_lines_are_ignored() {
        let (conn, _notifs, mut server_out, mut server_in) = full_harness();

        // Interleave empty lines with a real response.
        let call =
            tokio::spawn({ let conn = conn.clone(); async move { conn.request("x", None).await } });
        let sent = read_line(&mut server_in).await;

        server_out.write_all(b"\n\n").await.unwrap();
        write_line(&mut server_out, json!({ "jsonrpc": "2.0", "id": sent["id"], "result": 42 })).await;

        let result = call.await.unwrap().unwrap();
        assert_eq!(result, json!(42));
    }
}
