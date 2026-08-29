//! Stdio read/write loop.
//!
//! The transport:
//! - Reads `Content-Length`-framed JSON-RPC messages from the reader.
//! - Spawns a task per incoming request to dispatch via `dispatch::dispatch`.
//! - Routes incoming `Response` messages to the `PromptChannel` so
//!   server-initiated round-trips resolve.
//! - Writes outbound frames (event notifications, request results, and
//!   server-initiated requests) through a single FIFO writer task.
//!
//! `serve_stdio()` is the public entry point for the binary.

use std::sync::Arc;

use coda_proto::{FrameDecoder, Message, Response, RequestId, encode_frame};
use serde_json::Value;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::sync::mpsc;

use crate::dispatch::{RpcError, dispatch};
use crate::host::ServeHost;
use crate::prompts::PromptChannel;
use crate::sink::ServeSink;

// ─────────────────────────────────────────────────────────────────────────────
// Public entry points
// ─────────────────────────────────────────────────────────────────────────────

/// Runs the engine on process stdin/stdout until the connection is closed.
pub async fn serve_stdio() -> anyhow::Result<()> {
    serve(tokio::io::stdin(), tokio::io::stdout()).await
}

/// Runs the engine on the given reader/writer pair.  Useful for tests that
/// inject an in-memory duplex stream.
pub(crate) async fn serve<R, W>(reader: R, writer: W) -> anyhow::Result<()>
where
    R: AsyncRead + Unpin + Send + 'static,
    W: AsyncWrite + Unpin + Send + 'static,
{
    let (outgoing_tx, outgoing_rx) = mpsc::unbounded_channel::<Vec<u8>>();

    let prompt_channel = Arc::new(PromptChannel::new(outgoing_tx.clone()));
    let sink = Arc::new(ServeSink::new(outgoing_tx.clone()));
    let backend = ServeHost::new(sink);

    let writer_task = tokio::spawn(write_loop(writer, outgoing_rx));

    read_loop(reader, outgoing_tx, prompt_channel, backend).await;

    writer_task.abort();
    Ok(())
}

// ─────────────────────────────────────────────────────────────────────────────
// Write loop
// ─────────────────────────────────────────────────────────────────────────────

async fn write_loop<W>(mut writer: W, mut rx: mpsc::UnboundedReceiver<Vec<u8>>)
where
    W: AsyncWrite + Unpin + Send + 'static,
{
    while let Some(frame) = rx.recv().await {
        if writer.write_all(&frame).await.is_err() {
            break;
        }
        if writer.flush().await.is_err() {
            break;
        }
    }
    let _ = writer.shutdown().await;
}

// ─────────────────────────────────────────────────────────────────────────────
// Read loop
// ─────────────────────────────────────────────────────────────────────────────

async fn read_loop<R>(
    mut reader: R,
    outgoing: mpsc::UnboundedSender<Vec<u8>>,
    prompt_channel: Arc<PromptChannel>,
    backend: Arc<ServeHost>,
) where
    R: AsyncRead + Unpin + Send + 'static,
{
    let mut decoder = FrameDecoder::new();
    let mut chunk = vec![0u8; 16 * 1024];

    loop {
        let n = match reader.read(&mut chunk).await {
            Ok(0) | Err(_) => break,
            Ok(n) => n,
        };
        decoder.feed(&chunk[..n]);

        loop {
            match decoder.next_frame() {
                Ok(Some(frame)) => {
                    dispatch_frame(&frame, &outgoing, &prompt_channel, &backend);
                }
                Ok(None) => break,
                Err(e) => {
                    tracing::warn!(%e, "framing error; dropping connection");
                    prompt_channel.fail_all_pending();
                    return;
                }
            }
        }
    }

    // Connection closed: fail all in-flight server-initiated requests.
    prompt_channel.fail_all_pending();
}

/// Route one parsed frame.  Requests are dispatched in separate tasks so
/// `session/prompt` does not block the read loop while the agent runs and
/// `request/*` responses arrive.
fn dispatch_frame(
    frame: &[u8],
    outgoing: &mpsc::UnboundedSender<Vec<u8>>,
    prompt_channel: &Arc<PromptChannel>,
    backend: &Arc<ServeHost>,
) {
    let message: Message = match serde_json::from_slice(frame) {
        Ok(m) => m,
        Err(e) => {
            tracing::warn!(%e, payload = %String::from_utf8_lossy(frame), "unparseable frame");
            return;
        }
    };

    match message {
        Message::Request(req) => {
            let id = req.id.clone();
            let method = req.method.clone();
            let params = req.params.clone();
            let backend = Arc::clone(backend);
            let outgoing = outgoing.clone();

            tokio::spawn(async move {
                let result = dispatch(&method, params, backend.as_ref()).await;
                let response = rpc_result_to_response(id, result);
                match serde_json::to_vec(&response) {
                    Ok(bytes) => {
                        let _ = outgoing.send(encode_frame(&bytes));
                    }
                    Err(e) => tracing::error!(%e, "failed to serialise response"),
                }
            });
        }
        Message::Response(resp) => {
            // This is the client's response to a server-initiated request.
            let id = resp.id.clone();
            prompt_channel.route_response(&id, resp.into_result());
        }
        Message::Notification(_) => {
            // Clients don't send notifications in normal operation; ignore.
        }
    }
}

fn rpc_result_to_response(id: RequestId, result: Result<Value, RpcError>) -> Response {
    match result {
        Ok(value) => Response::success(id, value),
        Err(e) => Response::failure(id, e.code, e.message),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use coda_proto::{FrameDecoder, Notification, Request, RequestId, Version, encode_frame};
    use serde_json::json;
    use tokio::io::{AsyncReadExt, AsyncWriteExt, duplex, split};

    // ── Test helpers ──────────────────────────────────────────────────────────

    /// A persistent reader that keeps the FrameDecoder alive across calls.
    /// Using a fresh decoder per call would silently lose bytes buffered from
    /// the previous `read()` that went beyond the first frame boundary.
    struct TestReader<R> {
        reader: R,
        decoder: FrameDecoder,
        buf: Vec<u8>,
    }

    impl<R: tokio::io::AsyncRead + Unpin> TestReader<R> {
        fn new(reader: R) -> Self {
            Self { reader, decoder: FrameDecoder::new(), buf: vec![0u8; 16 * 1024] }
        }

        async fn next(&mut self) -> serde_json::Value {
            loop {
                if let Ok(Some(frame)) = self.decoder.next_frame() {
                    return serde_json::from_slice(&frame).expect("json");
                }
                let n = self.reader.read(&mut self.buf).await.expect("read");
                assert_ne!(n, 0, "stream closed before a full message arrived");
                self.decoder.feed(&self.buf[..n]);
            }
        }
    }

    fn make_request(id: i64, method: &str, params: Option<serde_json::Value>) -> Vec<u8> {
        let req = Request {
            jsonrpc: Version,
            id: RequestId::Number(id),
            method: method.into(),
            params,
        };
        encode_frame(&serde_json::to_vec(&req).unwrap())
    }

    // ── End-to-end handshake ──────────────────────────────────────────────────

    /// Mirrors `engine_contract.rs::completes_the_handshake_against_a_real_engine`.
    #[tokio::test]
    async fn end_to_end_initialize_handshake() {
        let (server_end, client_end) = duplex(64 * 1024);
        let (server_read, server_write) = split(server_end);
        let (client_read, mut client_write) = split(client_end);
        let mut client = TestReader::new(client_read);

        tokio::spawn(serve(server_read, server_write));

        // Send initialize.
        let req_bytes = make_request(
            1,
            "initialize",
            Some(json!({"protocolVersion":"1","clientInfo":"test"})),
        );
        client_write.write_all(&req_bytes).await.unwrap();

        let msg = client.next().await;
        assert_eq!(msg["id"], 1, "id must echo back verbatim");
        let result = &msg["result"];
        assert_eq!(result["protocolVersion"], "1");
        assert!(result["sessionId"].is_string());
        assert!(!result["sessionId"].as_str().unwrap().is_empty());
        assert_eq!(result["serverInfo"], "coda-serve");
        assert!(result.get("telemetryLogPath").is_none(), "must omit telemetryLogPath");
    }

    /// String ids are echoed back verbatim.
    #[tokio::test]
    async fn string_request_id_is_echoed_verbatim() {
        let (server_end, client_end) = duplex(64 * 1024);
        let (server_read, server_write) = split(server_end);
        let (client_read, mut client_write) = split(client_end);
        let mut client = TestReader::new(client_read);

        tokio::spawn(serve(server_read, server_write));

        let req = Request {
            jsonrpc: Version,
            id: RequestId::String("my-id-42".into()),
            method: "shutdown".into(),
            params: None,
        };
        let frame = encode_frame(&serde_json::to_vec(&req).unwrap());
        client_write.write_all(&frame).await.unwrap();

        let msg = client.next().await;
        assert_eq!(msg["id"], "my-id-42", "string id must echo back verbatim");
        assert_eq!(msg["result"]["ok"], true);
    }

    /// Unknown method returns -32601 as a proper JSON-RPC error response.
    #[tokio::test]
    async fn unknown_method_returns_error_response_with_32601() {
        let (server_end, client_end) = duplex(64 * 1024);
        let (server_read, server_write) = split(server_end);
        let (client_read, mut client_write) = split(client_end);
        let mut client = TestReader::new(client_read);

        tokio::spawn(serve(server_read, server_write));

        let req_bytes = make_request(99, "totally/unknown", None);
        client_write.write_all(&req_bytes).await.unwrap();

        let msg = client.next().await;
        assert_eq!(msg["id"], 99);
        assert_eq!(msg["error"]["code"], -32601);
    }

    /// `skills/trust` always returns -32600 as an error response.
    #[tokio::test]
    async fn skills_trust_returns_32600_error_response() {
        let (server_end, client_end) = duplex(64 * 1024);
        let (server_read, server_write) = split(server_end);
        let (client_read, mut client_write) = split(client_end);
        let mut client = TestReader::new(client_read);

        tokio::spawn(serve(server_read, server_write));

        let req_bytes = make_request(7, "skills/trust", None);
        client_write.write_all(&req_bytes).await.unwrap();

        let msg = client.next().await;
        assert_eq!(msg["id"], 7);
        assert_eq!(msg["error"]["code"], -32600);
    }

    // ── Ordering: event/turnComplete before session/prompt result ─────────────

    /// The `event/turnComplete` notification MUST arrive before the
    /// `session/prompt` result.
    ///
    /// This is guaranteed by design: `ServeHost::session_prompt` emits the
    /// notification via the `ServeSink` (which enqueues it on the FIFO write
    /// channel) and THEN returns its result, which the transport task
    /// subsequently enqueues.  FIFO order on the write channel guarantees
    /// the notification precedes the response.
    #[tokio::test]
    async fn turn_complete_arrives_before_session_prompt_result() {
        let (server_end, client_end) = duplex(64 * 1024);
        let (server_read, server_write) = split(server_end);
        let (client_read, mut client_write) = split(client_end);
        // IMPORTANT: reuse a single decoder across reads so buffered bytes are not lost.
        let mut client = TestReader::new(client_read);

        tokio::spawn(serve(server_read, server_write));

        // First: initialize.
        let init_bytes = make_request(1, "initialize", Some(json!({"protocolVersion":"1"})));
        client_write.write_all(&init_bytes).await.unwrap();
        let _ = client.next().await; // consume initialize response

        // Now send session/prompt.
        let prompt_bytes = make_request(2, "session/prompt", Some(json!({"text":"hello"})));
        client_write.write_all(&prompt_bytes).await.unwrap();

        // Read up to two messages; the FIRST must be the TurnComplete notification.
        let first = client.next().await;
        let second = client.next().await;

        assert_eq!(
            first["method"], "event/turnComplete",
            "event/turnComplete must arrive BEFORE the session/prompt result; got first={first}, second={second}"
        );
        assert_eq!(second["id"], 2, "second message must be the session/prompt response");
        assert_eq!(second["result"]["ok"], true);
    }

    // ── Framing: partial / split reads ───────────────────────────────────────

    /// A frame split across two writes is reassembled correctly.
    #[tokio::test]
    async fn split_frame_is_reassembled() {
        let (server_end, client_end) = duplex(64 * 1024);
        let (server_read, server_write) = split(server_end);
        let (client_read, mut client_write) = split(client_end);
        let mut client = TestReader::new(client_read);

        tokio::spawn(serve(server_read, server_write));

        let req = make_request(5, "shutdown", None);
        // Send in two halves.
        let mid = req.len() / 2;
        client_write.write_all(&req[..mid]).await.unwrap();
        tokio::time::sleep(std::time::Duration::from_millis(5)).await;
        client_write.write_all(&req[mid..]).await.unwrap();

        let msg = client.next().await;
        assert_eq!(msg["id"], 5);
        assert_eq!(msg["result"]["ok"], true);
    }

    /// An unparseable JSON body does not crash the server; the next valid
    /// message is still processed.
    #[tokio::test]
    async fn unparseable_frame_does_not_crash_server() {
        let (server_end, client_end) = duplex(64 * 1024);
        let (server_read, server_write) = split(server_end);
        let (client_read, mut client_write) = split(client_end);
        let mut client = TestReader::new(client_read);

        tokio::spawn(serve(server_read, server_write));

        // Send a garbage frame (valid framing, invalid JSON body).
        let garbage = encode_frame(b"this is not json");
        client_write.write_all(&garbage).await.unwrap();

        // Then a valid request.
        let valid = make_request(10, "shutdown", None);
        client_write.write_all(&valid).await.unwrap();

        let msg = client.next().await;
        assert_eq!(msg["id"], 10, "valid request after garbage must be processed");
        assert_eq!(msg["result"]["ok"], true);
    }

    // ── Notification from client is silently ignored ──────────────────────────

    #[tokio::test]
    async fn client_notification_is_ignored() {
        let (server_end, client_end) = duplex(64 * 1024);
        let (server_read, server_write) = split(server_end);
        let (client_read, mut client_write) = split(client_end);
        let mut client = TestReader::new(client_read);

        tokio::spawn(serve(server_read, server_write));

        // Client sends a notification (no id).
        let notif = Notification::new("client/ready", None);
        let frame = encode_frame(&serde_json::to_vec(&notif).unwrap());
        client_write.write_all(&frame).await.unwrap();

        // Then a real request that we can wait on.
        let req = make_request(3, "shutdown", None);
        client_write.write_all(&req).await.unwrap();

        let msg = client.next().await;
        assert_eq!(msg["id"], 3);
    }
}
