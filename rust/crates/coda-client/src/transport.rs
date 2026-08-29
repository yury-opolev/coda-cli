//! Duplex JSON-RPC transport over any async byte stream.
//!
//! The connection is symmetric: we send requests and notifications to the
//! engine, and the engine sends us notifications (`event/*`) and server-initiated
//! requests (`request/permission` and friends) that we must answer.
//!
//! Reader and writer are driven by independent tasks so a slow consumer of
//! inbound events can never deadlock an outbound write, and vice versa.

use std::collections::HashMap;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex};

use coda_proto::{
    encode_frame, error_codes, FrameDecoder, Message, Notification, Request, RequestId, Response,
    ResponseError,
};
use serde_json::Value;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::sync::{mpsc, oneshot};
use tokio::task::JoinHandle;

use crate::error::ClientError;

/// A message the engine sent to us.
#[derive(Debug)]
pub enum Inbound {
    /// A one-way `event/*` notification.
    Notification {
        method: String,
        params: Option<Value>,
    },
    /// A server-initiated request that must be answered via [`Responder`].
    Request {
        method: String,
        params: Option<Value>,
        responder: Responder,
    },
}

/// Answers a server-initiated request exactly once.
///
/// Dropping a responder without answering replies with a "cancelled" error so
/// the engine is never left waiting on a turn-blocking request.
#[derive(Debug)]
pub struct Responder {
    id: RequestId,
    outgoing: mpsc::UnboundedSender<Vec<u8>>,
    answered: bool,
}

impl Responder {
    pub fn id(&self) -> &RequestId {
        &self.id
    }

    /// Replies with a successful result.
    pub fn respond(mut self, result: Value) {
        self.answered = true;
        self.send(Response::success(self.id.clone(), result));
    }

    /// Replies with a JSON-RPC error.
    pub fn fail(mut self, code: i64, message: impl Into<String>) {
        self.answered = true;
        self.send(Response::failure(self.id.clone(), code, message));
    }

    fn send(&self, response: Response) {
        match serde_json::to_vec(&response) {
            Ok(bytes) => {
                let _ = self.outgoing.send(encode_frame(&bytes));
            }
            Err(error) => tracing::error!(%error, "failed to serialise a response"),
        }
    }
}

impl Drop for Responder {
    fn drop(&mut self) {
        if !self.answered {
            tracing::warn!(id = %self.id, "server request dropped without an answer");
            self.send(Response::failure(
                self.id.clone(),
                error_codes::REQUEST_CANCELLED,
                "the client dropped this request without answering",
            ));
        }
    }
}

type PendingMap = Arc<Mutex<HashMap<RequestId, oneshot::Sender<Result<Value, ResponseError>>>>>;

/// Handle used to talk to the engine. Cheap to clone and `Send`.
#[derive(Debug, Clone)]
pub struct Connection {
    outgoing: mpsc::UnboundedSender<Vec<u8>>,
    pending: PendingMap,
    next_id: Arc<AtomicI64>,
}

impl Connection {
    /// Sends a request and waits for its response.
    pub async fn request(
        &self,
        method: impl Into<String>,
        params: Option<Value>,
    ) -> Result<Value, ClientError> {
        let rx = self.send_request(method, params)?;
        rx.await.map_err(|_| ClientError::ConnectionClosed)?.map_err(ClientError::Rpc)
    }

    /// Sends a request and returns a future for its response without awaiting,
    /// so callers can hold several in flight.
    pub fn send_request(
        &self,
        method: impl Into<String>,
        params: Option<Value>,
    ) -> Result<oneshot::Receiver<Result<Value, ResponseError>>, ClientError> {
        let id = RequestId::Number(self.next_id.fetch_add(1, Ordering::Relaxed));
        let request = Request::new(id.clone(), method, params);
        let bytes = serde_json::to_vec(&request)?;

        let (tx, rx) = oneshot::channel();
        self.pending
            .lock()
            .expect("pending map poisoned")
            .insert(id, tx);

        self.outgoing
            .send(encode_frame(&bytes))
            .map_err(|_| ClientError::ConnectionClosed)?;
        Ok(rx)
    }

    /// Sends a one-way notification.
    pub fn notify(
        &self,
        method: impl Into<String>,
        params: Option<Value>,
    ) -> Result<(), ClientError> {
        let bytes = serde_json::to_vec(&Notification::new(method, params))?;
        self.outgoing
            .send(encode_frame(&bytes))
            .map_err(|_| ClientError::ConnectionClosed)
    }

    /// Whether the writer task is still accepting messages.
    pub fn is_closed(&self) -> bool {
        self.outgoing.is_closed()
    }
}

/// The background tasks servicing a [`Connection`].
#[derive(Debug)]
pub struct ConnectionTasks {
    pub reader: JoinHandle<Result<(), ClientError>>,
    pub writer: JoinHandle<()>,
}

/// Wires a reader/writer pair into a [`Connection`] plus an inbound stream.
pub fn connect<R, W>(
    reader: R,
    writer: W,
) -> (Connection, mpsc::UnboundedReceiver<Inbound>, ConnectionTasks)
where
    R: AsyncRead + Unpin + Send + 'static,
    W: AsyncWrite + Unpin + Send + 'static,
{
    let (outgoing_tx, outgoing_rx) = mpsc::unbounded_channel::<Vec<u8>>();
    let (inbound_tx, inbound_rx) = mpsc::unbounded_channel::<Inbound>();
    let pending: PendingMap = Arc::new(Mutex::new(HashMap::new()));

    let connection = Connection {
        outgoing: outgoing_tx.clone(),
        pending: Arc::clone(&pending),
        next_id: Arc::new(AtomicI64::new(1)),
    };

    let writer_task = tokio::spawn(write_loop(writer, outgoing_rx));
    let reader_task = tokio::spawn(read_loop(reader, inbound_tx, outgoing_tx, pending));

    (
        connection,
        inbound_rx,
        ConnectionTasks {
            reader: reader_task,
            writer: writer_task,
        },
    )
}

async fn write_loop<W>(mut writer: W, mut outgoing: mpsc::UnboundedReceiver<Vec<u8>>)
where
    W: AsyncWrite + Unpin + Send + 'static,
{
    while let Some(frame) = outgoing.recv().await {
        if let Err(error) = writer.write_all(&frame).await {
            tracing::debug!(%error, "engine stdin closed; stopping writer");
            break;
        }
        if let Err(error) = writer.flush().await {
            tracing::debug!(%error, "failed to flush engine stdin");
            break;
        }
    }
    let _ = writer.shutdown().await;
}

async fn read_loop<R>(
    mut reader: R,
    inbound: mpsc::UnboundedSender<Inbound>,
    outgoing: mpsc::UnboundedSender<Vec<u8>>,
    pending: PendingMap,
) -> Result<(), ClientError>
where
    R: AsyncRead + Unpin + Send + 'static,
{
    let mut decoder = FrameDecoder::new();
    let mut chunk = vec![0u8; 16 * 1024];

    let outcome = loop {
        let read = match reader.read(&mut chunk).await {
            Ok(0) => break Ok(()),
            Ok(n) => n,
            Err(error) => break Err(ClientError::Io(error)),
        };
        decoder.feed(&chunk[..read]);

        loop {
            match decoder.next_frame() {
                Ok(Some(frame)) => dispatch(&frame, &inbound, &outgoing, &pending),
                Ok(None) => break,
                Err(error) => return finish(pending, Err(ClientError::Framing(error))),
            }
        }
    };

    finish(pending, outcome)
}

/// Fails every in-flight request so no caller waits forever on a dead engine.
fn finish(pending: PendingMap, outcome: Result<(), ClientError>) -> Result<(), ClientError> {
    let waiters: Vec<_> = pending
        .lock()
        .expect("pending map poisoned")
        .drain()
        .map(|(_, tx)| tx)
        .collect();
    for waiter in waiters {
        let _ = waiter.send(Err(ResponseError {
            code: error_codes::INTERNAL_ERROR,
            message: "the engine connection closed before responding".to_string(),
            data: None,
        }));
    }
    outcome
}

fn dispatch(
    frame: &[u8],
    inbound: &mpsc::UnboundedSender<Inbound>,
    outgoing: &mpsc::UnboundedSender<Vec<u8>>,
    pending: &PendingMap,
) {
    let message: Message = match serde_json::from_slice(frame) {
        Ok(message) => message,
        Err(error) => {
            // A malformed frame is the peer's problem, not a reason to tear the
            // session down; log it and keep reading.
            tracing::warn!(%error, payload = %String::from_utf8_lossy(frame), "unparsable frame");
            return;
        }
    };

    match message {
        Message::Response(response) => {
            let waiter = pending
                .lock()
                .expect("pending map poisoned")
                .remove(&response.id);
            match waiter {
                Some(tx) => {
                    let _ = tx.send(response.into_result());
                }
                None => tracing::warn!(id = %response.id, "response for an unknown request id"),
            }
        }
        Message::Request(request) => {
            let responder = Responder {
                id: request.id,
                outgoing: outgoing.clone(),
                answered: false,
            };
            let _ = inbound.send(Inbound::Request {
                method: request.method,
                params: request.params,
                responder,
            });
        }
        Message::Notification(notification) => {
            let _ = inbound.send(Inbound::Notification {
                method: notification.method,
                params: notification.params,
            });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_proto::encode_frame;
    use serde_json::json;
    use tokio::io::duplex;

    /// Reads framed messages from the peer end of the connection.
    ///
    /// The decoder is retained across calls: two frames often arrive in a single
    /// read, and a fresh decoder per call would silently discard the second.
    struct Peer {
        stream: tokio::io::DuplexStream,
        decoder: FrameDecoder,
    }

    impl Peer {
        fn new(stream: tokio::io::DuplexStream) -> Self {
            Self {
                stream,
                decoder: FrameDecoder::new(),
            }
        }

        async fn read_frame(&mut self) -> Value {
            let mut chunk = vec![0u8; 4096];
            loop {
                if let Some(frame) = self.decoder.next_frame().expect("decode") {
                    return serde_json::from_slice(&frame).expect("json");
                }
                let n = self.stream.read(&mut chunk).await.expect("read");
                assert_ne!(n, 0, "stream closed before a full frame arrived");
                self.decoder.feed(&chunk[..n]);
            }
        }
    }

    /// Builds a connection wired to an in-memory peer, returning the peer's
    /// own reader/writer so a test can play the engine.
    fn harness() -> (
        Connection,
        mpsc::UnboundedReceiver<Inbound>,
        tokio::io::DuplexStream,
        Peer,
    ) {
        let (engine_out, client_in) = duplex(64 * 1024);
        let (client_out, engine_in) = duplex(64 * 1024);
        let (connection, inbound, _tasks) = connect(client_in, client_out);
        (connection, inbound, engine_out, Peer::new(engine_in))
    }

    async fn write_frame(stream: &mut tokio::io::DuplexStream, value: Value) {
        let bytes = serde_json::to_vec(&value).expect("serialise");
        stream.write_all(&encode_frame(&bytes)).await.expect("write");
        stream.flush().await.expect("flush");
    }

    #[tokio::test]
    async fn sends_a_request_and_resolves_its_response() {
        let (connection, _inbound, mut engine_out, mut engine_in) = harness();

        let call = tokio::spawn({
            let connection = connection.clone();
            async move { connection.request("initialize", Some(json!({ "v": 1 }))).await }
        });

        let sent = engine_in.read_frame().await;
        assert_eq!(sent["method"], "initialize");
        assert_eq!(sent["jsonrpc"], "2.0");
        assert_eq!(sent["params"], json!({ "v": 1 }));

        write_frame(
            &mut engine_out,
            json!({ "jsonrpc": "2.0", "id": sent["id"], "result": { "ok": true } }),
        )
        .await;

        let result = call.await.expect("join").expect("request");
        assert_eq!(result, json!({ "ok": true }));
    }

    #[tokio::test]
    async fn surfaces_an_error_response_to_the_caller() {
        let (connection, _inbound, mut engine_out, mut engine_in) = harness();

        let call = tokio::spawn({
            let connection = connection.clone();
            async move { connection.request("nope", None).await }
        });

        let sent = engine_in.read_frame().await;
        write_frame(
            &mut engine_out,
            json!({
                "jsonrpc": "2.0",
                "id": sent["id"],
                "error": { "code": -32601, "message": "Method not found" }
            }),
        )
        .await;

        let error = call.await.expect("join").expect_err("expected failure");
        assert!(matches!(error, ClientError::Rpc(e) if e.code == -32601));
    }

    #[tokio::test]
    async fn correlates_concurrent_requests_out_of_order() {
        let (connection, _inbound, mut engine_out, mut engine_in) = harness();

        let first = connection.send_request("a", None).expect("send a");
        let second = connection.send_request("b", None).expect("send b");

        let sent_a = engine_in.read_frame().await;
        let sent_b = engine_in.read_frame().await;
        assert_eq!(sent_a["method"], "a");
        assert_eq!(sent_b["method"], "b");
        assert_ne!(sent_a["id"], sent_b["id"]);

        // Answer in reverse order.
        write_frame(
            &mut engine_out,
            json!({ "jsonrpc": "2.0", "id": sent_b["id"], "result": "B" }),
        )
        .await;
        write_frame(
            &mut engine_out,
            json!({ "jsonrpc": "2.0", "id": sent_a["id"], "result": "A" }),
        )
        .await;

        assert_eq!(second.await.expect("b").expect("ok"), json!("B"));
        assert_eq!(first.await.expect("a").expect("ok"), json!("A"));
    }

    #[tokio::test]
    async fn delivers_notifications_to_the_inbound_stream() {
        let (_connection, mut inbound, mut engine_out, _engine_in) = harness();

        write_frame(
            &mut engine_out,
            json!({
                "jsonrpc": "2.0",
                "method": "event/assistantText",
                "params": { "text": "hello" }
            }),
        )
        .await;

        let message = inbound.recv().await.expect("inbound");
        match message {
            Inbound::Notification { method, params } => {
                assert_eq!(method, "event/assistantText");
                assert_eq!(params.unwrap()["text"], "hello");
            }
            other => panic!("expected a notification, got {other:?}"),
        }
    }

    #[tokio::test]
    async fn answers_a_server_initiated_request() {
        let (_connection, mut inbound, mut engine_out, mut engine_in) = harness();

        write_frame(
            &mut engine_out,
            json!({
                "jsonrpc": "2.0",
                "id": 99,
                "method": "request/permission",
                "params": { "tool": "run_command" }
            }),
        )
        .await;

        let message = inbound.recv().await.expect("inbound");
        let Inbound::Request {
            method, responder, ..
        } = message
        else {
            panic!("expected a server request");
        };
        assert_eq!(method, "request/permission");
        responder.respond(json!({ "decision": "allow" }));

        let reply = engine_in.read_frame().await;
        assert_eq!(reply["id"], 99);
        assert_eq!(reply["result"], json!({ "decision": "allow" }));
    }

    #[tokio::test]
    async fn dropping_a_responder_sends_a_cancellation() {
        let (_connection, mut inbound, mut engine_out, mut engine_in) = harness();

        write_frame(
            &mut engine_out,
            json!({ "jsonrpc": "2.0", "id": 5, "method": "request/question" }),
        )
        .await;

        let Inbound::Request { responder, .. } = inbound.recv().await.expect("inbound") else {
            panic!("expected a server request");
        };
        drop(responder);

        let reply = engine_in.read_frame().await;
        assert_eq!(reply["id"], 5);
        assert_eq!(reply["error"]["code"], error_codes::REQUEST_CANCELLED);
    }

    #[tokio::test]
    async fn fails_pending_requests_when_the_engine_disconnects() {
        let (connection, _inbound, engine_out, mut engine_in) = harness();

        let call = tokio::spawn({
            let connection = connection.clone();
            async move { connection.request("session/prompt", None).await }
        });
        let _ = engine_in.read_frame().await;

        drop(engine_out);

        let error = call.await.expect("join").expect_err("expected failure");
        assert!(matches!(error, ClientError::Rpc(_) | ClientError::ConnectionClosed));
    }

    #[tokio::test]
    async fn ignores_an_unparsable_frame_and_keeps_reading() {
        let (_connection, mut inbound, mut engine_out, _engine_in) = harness();

        engine_out
            .write_all(&encode_frame(b"this is not json"))
            .await
            .expect("write");
        write_frame(
            &mut engine_out,
            json!({ "jsonrpc": "2.0", "method": "event/turnComplete" }),
        )
        .await;

        let message = inbound.recv().await.expect("inbound");
        assert!(matches!(
            message,
            Inbound::Notification { ref method, .. } if method == "event/turnComplete"
        ));
    }

    #[tokio::test]
    async fn sends_notifications_without_an_id() {
        let (connection, _inbound, _engine_out, mut engine_in) = harness();

        connection
            .notify("session/interrupt", Some(json!({ "reason": "user" })))
            .expect("notify");

        let sent = engine_in.read_frame().await;
        assert_eq!(sent["method"], "session/interrupt");
        assert!(sent.get("id").is_none());
    }

    // ── additional behaviours from the C# JsonRpcConnectionTests spec ─────────

    /// A response whose id does not match any in-flight request must be silently
    /// discarded.  The connection must keep serving subsequent messages normally —
    /// one stray frame must not poison the entire session.
    #[tokio::test]
    async fn response_for_unknown_id_is_ignored_and_connection_survives() {
        let (_connection, mut inbound, mut engine_out, _engine_in) = harness();

        // Send a response for an id that was never requested.
        write_frame(
            &mut engine_out,
            json!({ "jsonrpc": "2.0", "id": 9999, "result": "stray" }),
        )
        .await;

        // Then send a real notification; it must still arrive.
        write_frame(
            &mut engine_out,
            json!({ "jsonrpc": "2.0", "method": "event/turnComplete" }),
        )
        .await;

        let message = inbound.recv().await.expect("inbound notification");
        assert!(matches!(
            message,
            Inbound::Notification { ref method, .. } if method == "event/turnComplete"
        ));
    }

    /// When the engine sends a server-initiated request whose id is a string
    /// rather than a number, the responder must echo the same string id back —
    /// the protocol is symmetric and the server may choose any id type.
    #[tokio::test]
    async fn server_request_with_string_id_echoes_the_same_string_id() {
        let (_connection, mut inbound, mut engine_out, mut engine_in) = harness();

        write_frame(
            &mut engine_out,
            json!({
                "jsonrpc": "2.0",
                "id": "str-id-42",
                "method": "request/permission",
                "params": { "tool": "run_command" }
            }),
        )
        .await;

        let Inbound::Request { responder, .. } = inbound.recv().await.expect("inbound") else {
            panic!("expected a server request");
        };
        assert_eq!(responder.id(), &RequestId::String("str-id-42".into()));
        responder.respond(json!({ "decision": "allow" }));

        let reply = engine_in.read_frame().await;
        assert_eq!(reply["id"], "str-id-42");
        assert_eq!(reply["result"], json!({ "decision": "allow" }));
    }

    /// After the outgoing channel is closed (engine side disconnected), any
    /// attempt to send a request must return an error rather than silently
    /// dropping the message.
    #[tokio::test]
    async fn sending_request_after_engine_disconnects_returns_connection_closed() {
        // Close the engine's read side so the very next write will fail, causing
        // the write loop to exit and close the outgoing channel.
        let (connection, _inbound, _engine_out, engine_in) = harness();
        drop(engine_in); // client's writes now go nowhere

        // Queue a notification: the write loop will attempt to flush it and fail.
        let _ = connection.notify("ping", None);

        // Let the write loop observe the broken pipe and exit.
        tokio::time::sleep(std::time::Duration::from_millis(200)).await;

        // With the write loop gone, the outgoing receiver is dropped; the sender
        // detects this as "closed".
        assert!(
            connection.is_closed(),
            "connection must report closed after the write loop exits"
        );
        let err = connection.notify("gone", None).expect_err("must fail");
        assert!(
            matches!(err, ClientError::ConnectionClosed),
            "expected ConnectionClosed, got {err:?}"
        );
    }
}

