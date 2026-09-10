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
use tokio::sync::{mpsc, oneshot, watch};
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
    /// Builds a responder over a raw outgoing frame channel.
    ///
    /// Public because a responder is only meaningful against the channel that
    /// carries its reply, and a client that wants to exercise its own
    /// request-handling path — the TUI's pending-request registry does
    /// exactly this — must be able to build one whose answer it can observe.
    /// The alternative is a client-side mock, which would prove that the mock
    /// behaves, not that the real fail-closed `Drop` does.
    pub fn new(id: RequestId, outgoing: mpsc::UnboundedSender<Vec<u8>>) -> Self {
        Self { id, outgoing, answered: false }
    }

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

    /// Abandons the request **without** answering it, and without the
    /// fail-closed cancellation `Drop` would send.
    ///
    /// For exactly one situation: the engine that raised this request is
    /// gone, so there is nothing to answer. A request id is only meaningful
    /// to the process that minted it — over a connection that outlives that
    /// process (a proxy that kept the socket while the engine behind it was
    /// replaced) a late cancellation would be delivered to the *new* engine
    /// and would resolve whichever of its requests happens to hold that id.
    ///
    /// Everywhere else, dropping is still the right thing and still declines:
    /// this is deliberately an explicit call, so "nobody is waiting for this"
    /// has to be stated rather than achieved by letting a value fall out of
    /// scope.
    pub fn discard(mut self) {
        self.answered = true;
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

type Waiter = oneshot::Sender<Result<Value, ResponseError>>;

/// The waiter map and the one flag that decides whether it may still be added
/// to. They live under one lock on purpose: "register a waiter" and "fail
/// every waiter" must not interleave, or a request can be registered into a
/// map that has already been drained and then wait for ever.
#[derive(Debug, Default)]
struct Pending {
    waiters: HashMap<RequestId, Waiter>,
    closed: bool,
}

/// State shared by a [`Connection`], its clones and the two transport tasks.
#[derive(Debug)]
struct Shared {
    pending: Mutex<Pending>,
    /// Wakes the writer when the connection is closed while it is idle.
    ///
    /// A watch rather than a `Notify` because the wakeup must not be lost: a
    /// close that lands between the writer's own check and its await would
    /// leave the task parked for ever on a connection nobody can use.
    closed: watch::Sender<bool>,
}

impl Shared {
    fn new() -> Arc<Self> {
        Arc::new(Self {
            pending: Mutex::new(Pending::default()),
            closed: watch::channel(false).0,
        })
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Pending> {
        self.pending.lock().expect("pending map poisoned")
    }

    fn is_closed(&self) -> bool {
        self.lock().closed
    }

    /// Registers a waiter for `id`, or reports that the connection is closed.
    ///
    /// Returning `None` is what stops a request from being registered into a
    /// map that will never be drained again.
    fn register(&self, id: RequestId) -> Option<oneshot::Receiver<Result<Value, ResponseError>>> {
        let mut pending = self.lock();
        if pending.closed {
            return None;
        }
        let (tx, rx) = oneshot::channel();
        pending.waiters.insert(id, tx);
        Some(rx)
    }

    fn take(&self, id: &RequestId) -> Option<Waiter> {
        self.lock().waiters.remove(id)
    }

    /// Marks the connection closed and fails every waiter, exactly once.
    ///
    /// Idempotent: the flag and the drain happen under one lock, so a second
    /// close finds an empty map and a caller can only ever be answered once —
    /// by a response, or by this.
    fn close(&self) {
        let waiters = {
            let mut pending = self.lock();
            pending.closed = true;
            std::mem::take(&mut pending.waiters)
        };
        for waiter in waiters.into_values() {
            let _ = waiter.send(Err(ResponseError {
                code: error_codes::INTERNAL_ERROR,
                message: "the engine connection closed before responding".to_string(),
                data: None,
            }));
        }
        let _ = self.closed.send(true);
    }
}

/// Handle used to talk to the engine. Cheap to clone and `Send`.
#[derive(Debug, Clone)]
pub struct Connection {
    outgoing: mpsc::UnboundedSender<Vec<u8>>,
    shared: Arc<Shared>,
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
    ///
    /// Every failure here is reported before the caller has anything to await:
    /// a closed connection refuses without touching the wire, and a frame the
    /// writer can no longer accept closes the connection rather than leaving
    /// its waiter in a map nothing will drain.
    pub fn send_request(
        &self,
        method: impl Into<String>,
        params: Option<Value>,
    ) -> Result<oneshot::Receiver<Result<Value, ResponseError>>, ClientError> {
        let id = RequestId::Number(self.next_id.fetch_add(1, Ordering::Relaxed));
        let request = Request::new(id.clone(), method, params);
        let bytes = serde_json::to_vec(&request)?;

        let Some(rx) = self.shared.register(id) else {
            return Err(ClientError::ConnectionClosed);
        };

        if self.outgoing.send(encode_frame(&bytes)).is_err() {
            // The writer is gone, so this frame will never be written and
            // neither will any other. Closing here is what fails this
            // waiter — and every other one still in the map — rather than
            // returning an error to this caller and abandoning the rest.
            self.shared.close();
            return Err(ClientError::ConnectionClosed);
        }
        Ok(rx)
    }

    /// Sends a one-way notification.
    pub fn notify(
        &self,
        method: impl Into<String>,
        params: Option<Value>,
    ) -> Result<(), ClientError> {
        if self.shared.is_closed() {
            return Err(ClientError::ConnectionClosed);
        }
        let bytes = serde_json::to_vec(&Notification::new(method, params))?;
        self.outgoing.send(encode_frame(&bytes)).map_err(|_| {
            self.shared.close();
            ClientError::ConnectionClosed
        })
    }

    /// Whether this connection is known to be unusable.
    ///
    /// True once anything has ended it — the engine's output reaching EOF, a
    /// failed write, an explicit [`close`](Self::close) — and not merely when
    /// the writer's channel happens to be gone. Callers use it to refuse work
    /// that could not be answered rather than to describe the channel.
    pub fn is_closed(&self) -> bool {
        self.shared.is_closed() || self.outgoing.is_closed()
    }

    /// Ends this connection for every clone of it.
    ///
    /// Fails every in-flight request at once, refuses new ones, and stops the
    /// writer once it has drained what was already queued — so a reply to a
    /// reverse request that was answered a moment ago still reaches the
    /// engine, and nothing new is written after it.
    ///
    /// The alternative — dropping a handle — only ends the connection when
    /// the *last* clone goes, which is exactly the case that hangs: an engine
    /// stops while the application still holds a clone, and every request made
    /// afterwards waits on a process that is gone.
    pub fn close(&self) {
        self.shared.close();
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
    let shared = Shared::new();

    let connection = Connection {
        outgoing: outgoing_tx.clone(),
        shared: Arc::clone(&shared),
        next_id: Arc::new(AtomicI64::new(1)),
    };

    let writer_task = tokio::spawn(write_loop(writer, outgoing_rx, Arc::clone(&shared)));
    let reader_task = tokio::spawn(read_loop(reader, inbound_tx, outgoing_tx, shared));

    (
        connection,
        inbound_rx,
        ConnectionTasks {
            reader: reader_task,
            writer: writer_task,
        },
    )
}

async fn write_loop<W>(
    mut writer: W,
    mut outgoing: mpsc::UnboundedReceiver<Vec<u8>>,
    shared: Arc<Shared>,
) where
    W: AsyncWrite + Unpin + Send + 'static,
{
    let mut closed = shared.closed.subscribe();
    loop {
        let frame = if shared.is_closed() {
            // Closed: nothing new can be queued, so whatever is already in the
            // queue is drained — an answer to a reverse request the engine is
            // blocked on was said before the close and is still worth
            // writing — and then the writer stops.
            match outgoing.try_recv() {
                Ok(frame) => frame,
                Err(_) => break,
            }
        } else {
            tokio::select! {
                biased;
                _ = closed.changed() => continue,
                frame = outgoing.recv() => match frame {
                    Some(frame) => frame,
                    None => break,
                },
            }
        };
        if let Err(error) = writer.write_all(&frame).await {
            tracing::debug!(%error, "engine stdin closed; stopping writer");
            break;
        }
        if let Err(error) = writer.flush().await {
            tracing::debug!(%error, "failed to flush engine stdin");
            break;
        }
    }
    // Whatever ended this loop, nothing outbound can be written again — and
    // the frame that failed was already taken off the queue, so its waiter is
    // only reachable from here. Closing fails it with all the others instead
    // of leaving one caller waiting for a response that was never sent.
    shared.close();
    let _ = writer.shutdown().await;
}

async fn read_loop<R>(
    mut reader: R,
    inbound: mpsc::UnboundedSender<Inbound>,
    outgoing: mpsc::UnboundedSender<Vec<u8>>,
    shared: Arc<Shared>,
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
                Ok(Some(frame)) => dispatch(&frame, &inbound, &outgoing, &shared),
                Ok(None) => break,
                Err(error) => return finish(&shared, Err(ClientError::Framing(error))),
            }
        }
    };

    finish(&shared, outcome)
}

/// Ends the connection so no caller waits forever on a dead engine, and so
/// none is registered after it.
fn finish(shared: &Shared, outcome: Result<(), ClientError>) -> Result<(), ClientError> {
    shared.close();
    outcome
}

fn dispatch(
    frame: &[u8],
    inbound: &mpsc::UnboundedSender<Inbound>,
    outgoing: &mpsc::UnboundedSender<Vec<u8>>,
    shared: &Shared,
) {
    let message: Message = match serde_json::from_slice(frame) {
        Ok(message) => message,
        Err(error) => {
            // A malformed frame is the peer's problem, not a reason to tear the
            // session down; log it and keep reading. Scrubbed: no `payload`
            // field — the raw bytes came straight off the engine's protocol
            // stream and can contain a prompt or tool result; only their
            // length is safe to note.
            tracing::warn!(%error, frame_bytes = frame.len(), "unparsable frame");
            return;
        }
    };

    match message {
        Message::Response(response) => match shared.take(&response.id) {
            Some(tx) => {
                let _ = tx.send(response.into_result());
            }
            None => tracing::warn!(id = %response.id, "response for an unknown request id"),
        },
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
    async fn a_discarded_responder_sends_nothing_at_all() {
        // A request raised by an engine process that has since been replaced
        // addresses nothing. Its numeric id is only meaningful to the process
        // that minted it, so over a connection that outlived that process — a
        // proxy holding the socket open — a cancellation would answer
        // whatever now happens to hold that id. Discarding is how a client
        // says "this is not mine to answer" without inventing an answer.
        let (_connection, mut inbound, mut engine_out, mut engine_in) = harness();

        write_frame(
            &mut engine_out,
            json!({ "jsonrpc": "2.0", "id": 7, "method": "request/question" }),
        )
        .await;
        let Inbound::Request { responder, .. } = inbound.recv().await.expect("inbound") else {
            panic!("expected a server request");
        };
        responder.discard();

        // A later, genuine reply is the only thing on the wire: if the
        // discard had emitted anything, this read would return it instead.
        write_frame(
            &mut engine_out,
            json!({ "jsonrpc": "2.0", "id": 8, "method": "request/question" }),
        )
        .await;
        let Inbound::Request { responder, .. } = inbound.recv().await.expect("inbound") else {
            panic!("expected a server request");
        };
        responder.fail(error_codes::REQUEST_CANCELLED, "declined");

        let reply = engine_in.read_frame().await;
        assert_eq!(reply["id"], 8, "the discarded request must never have been answered");
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

    // ── The connection's own lifecycle ───────────────────────────────────────
    //
    // Every test below is bounded by an explicit timeout rather than by the
    // harness's patience: the failure they cover is a caller that waits for a
    // response that can never arrive, and a test that reproduces it by hanging
    // is indistinguishable from a test that is merely slow.

    /// The bound every lifecycle test awaits under. Long enough that a busy
    /// machine cannot fail it, short enough that a genuine hang is a failure
    /// rather than a stalled run.
    const NO_HANG: std::time::Duration = std::time::Duration::from_secs(5);

    async fn within<F: std::future::Future>(what: &str, future: F) -> F::Output {
        match tokio::time::timeout(NO_HANG, future).await {
            Ok(value) => value,
            Err(_) => panic!("{what} never completed: the caller is waiting forever"),
        }
    }

    #[tokio::test]
    async fn a_request_the_writer_could_not_write_fails_instead_of_waiting() {
        // The deadlock this closes. The writer takes a frame off the queue,
        // the write fails on a dead stdin and the writer stops — but the
        // waiter it took the frame *for* stayed in the pending map, so the
        // caller awaited a response nobody could ever send. Failing the send
        // is not enough on its own: the frame was already consumed.
        let (engine_out, client_in) = duplex(64 * 1024);
        let (client_out, engine_in) = duplex(64 * 1024);
        let (connection, _inbound, _tasks) = connect(client_in, client_out);
        // The engine is gone: its end of both pipes is closed, so the very
        // first write fails.
        drop(engine_in);
        drop(engine_out);

        let error = within(
            "a request written to a dead engine",
            connection.request("session/fork", None),
        )
        .await
        .expect_err("a request that cannot be written must fail");
        assert!(
            matches!(error, ClientError::Rpc(_) | ClientError::ConnectionClosed),
            "got {error:?}"
        );
    }

    #[tokio::test]
    async fn closing_fails_every_in_flight_request_exactly_once() {
        let (connection, _inbound, _engine_out, mut engine_in) = harness();

        let first = connection.send_request("a", None).expect("send a");
        let second = connection.send_request("b", None).expect("send b");
        // Both frames reached the engine, so neither can be failed by a write
        // that never happened: only the close may fail them.
        let _ = engine_in.read_frame().await;
        let _ = engine_in.read_frame().await;

        connection.close();

        for (name, waiter) in [("a", first), ("b", second)] {
            let outcome = within("a request failed by a close", waiter)
                .await
                .expect("a closed connection must fail its waiters, not drop them");
            assert!(outcome.is_err(), "{name} was resolved rather than failed");
        }

        // Closing again is not a second answer: the waiters are gone, and a
        // repeat must neither panic nor resurrect anything.
        connection.close();
        assert!(connection.is_closed());
    }

    #[tokio::test]
    async fn a_request_made_after_a_close_fails_at_once_without_touching_the_wire() {
        let (connection, _inbound, _engine_out, mut engine_in) = harness();
        connection.close();

        let error = within("a request made after a close", connection.request("a", None))
            .await
            .expect_err("a closed connection must refuse");
        assert!(matches!(error, ClientError::ConnectionClosed), "got {error:?}");
        assert!(matches!(
            connection.notify("b", None).expect_err("a closed connection must refuse"),
            ClientError::ConnectionClosed
        ));

        // Nothing was written: a closed connection is not a queue.
        let wrote = tokio::time::timeout(
            std::time::Duration::from_millis(200),
            engine_in.stream.read(&mut [0u8; 64]),
        )
        .await;
        match wrote {
            Err(_) => {}
            Ok(Ok(0)) => {}
            Ok(other) => panic!("a closed connection still wrote to the engine: {other:?}"),
        }
    }

    #[tokio::test]
    async fn a_close_racing_a_request_never_leaves_a_waiter_behind() {
        // The interleaving that matters: the waiter is registered and the
        // frame queued at the same moment the connection is closed. Whichever
        // order they land in, the caller must be answered — with a response
        // or with an error — and never left waiting.
        for _ in 0..50 {
            let (connection, _inbound, _engine_out, _engine_in) = harness();
            let caller = connection.clone();
            let call = tokio::spawn(async move { caller.request("a", None).await });
            connection.close();
            let outcome = within("a request racing a close", call).await.expect("join");
            assert!(outcome.is_err(), "a closed connection answered a request");
        }
    }

    #[tokio::test]
    async fn a_reply_queued_before_a_close_still_reaches_the_engine() {
        // Closing is not a cancellation of what was already said. A reverse
        // request answered a moment before the connection is closed is a
        // decision the engine is blocked on: the writer drains it, and only
        // then stops.
        let (connection, mut inbound, mut engine_out, mut engine_in) = harness();

        write_frame(
            &mut engine_out,
            json!({ "jsonrpc": "2.0", "id": 11, "method": "request/permission" }),
        )
        .await;
        let Inbound::Request { responder, .. } = inbound.recv().await.expect("inbound") else {
            panic!("expected a server request");
        };
        responder.respond(json!({ "decision": "allow" }));
        connection.close();

        let reply = within("the queued reply", engine_in.read_frame()).await;
        assert_eq!(reply["id"], 11);
        assert_eq!(reply["result"], json!({ "decision": "allow" }));
    }

    #[tokio::test]
    async fn a_connection_whose_reader_ended_refuses_new_requests() {
        // EOF on the engine's output is the end of the connection, not just
        // of the inbound stream: a request sent after it would be written into
        // a pipe nobody is reading and awaited for ever.
        let (connection, _inbound, engine_out, _engine_in) = harness();
        drop(engine_out);

        let error = within("a request sent after EOF", async {
            // The reader has to observe the EOF first; retry until the
            // connection reports itself closed, bounded by the outer timeout.
            loop {
                if connection.is_closed() {
                    break connection.request("a", None).await;
                }
                tokio::time::sleep(std::time::Duration::from_millis(10)).await;
            }
        })
        .await
        .expect_err("a connection whose engine went away must refuse");
        assert!(matches!(error, ClientError::ConnectionClosed), "got {error:?}");
    }
}

