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

use coda_llm::LlmClient;
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
    let working_dir = std::env::current_dir()
        .map(|p| p.to_string_lossy().into_owned())
        .unwrap_or_else(|_| ".".into());
    // Full credential probe at startup (env + keyring).
    let client = crate::host::try_build_client(None).await;
    serve_inner(tokio::io::stdin(), tokio::io::stdout(), client, working_dir).await
}

/// Runs the engine on the given reader/writer pair.
#[cfg(test)]
pub(crate) async fn serve<R, W>(reader: R, writer: W) -> anyhow::Result<()>
where
    R: AsyncRead + Unpin + Send + 'static,
    W: AsyncWrite + Unpin + Send + 'static,
{
    let working_dir = std::env::current_dir()
        .map(|p| p.to_string_lossy().into_owned())
        .unwrap_or_else(|_| ".".into());
    serve_inner(reader, writer, None, working_dir).await
}

/// Runs the engine with a pre-built client (used by integration tests so
/// the LLM call never touches the network).
#[cfg(test)]
pub(crate) async fn serve_with_client<R, W>(
    reader: R,
    writer: W,
    client: Arc<dyn LlmClient>,
    working_dir: &str,
) -> anyhow::Result<()>
where
    R: AsyncRead + Unpin + Send + 'static,
    W: AsyncWrite + Unpin + Send + 'static,
{
    serve_inner(reader, writer, Some(client), working_dir.to_string()).await
}

async fn serve_inner<R, W>(
    reader: R,
    writer: W,
    client: Option<Arc<dyn LlmClient>>,
    working_dir: String,
) -> anyhow::Result<()>
where
    R: AsyncRead + Unpin + Send + 'static,
    W: AsyncWrite + Unpin + Send + 'static,
{
    let (outgoing_tx, outgoing_rx) = mpsc::unbounded_channel::<Vec<u8>>();
    let prompt_channel = Arc::new(PromptChannel::new(outgoing_tx.clone()));
    let sink = Arc::new(ServeSink::new(outgoing_tx.clone()));

    let backend = match client {
        Some(c) => ServeHost::new_with_client(c, sink, Arc::clone(&prompt_channel), working_dir),
        None => ServeHost::new(sink, Arc::clone(&prompt_channel), working_dir),
    };

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
        // Must be "coda", matching the C# engine verbatim — clients key off
        // this string, so the crate name here would be a silent parity break.
        assert_eq!(result["serverInfo"], "coda");
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
    /// Uses `serve_with_client` with a minimal fake so the test is independent
    /// of credentials.  FIFO order on the write channel guarantees the
    /// notification precedes the response.
    #[tokio::test]
    async fn turn_complete_arrives_before_session_prompt_result() {
        // A fake client that produces a single end_turn response.
        let llm: Arc<dyn LlmClient> = Arc::new(FakeLlmClient::new(vec![vec![
            StreamEvent::TextDelta("ok".into()),
            StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO },
        ]]));

        let (server_end, client_end) = duplex(64 * 1024);
        let (server_read, server_write) = split(server_end);
        let (client_read, mut client_write) = split(client_end);
        let mut reader = TestReader::new(client_read);

        tokio::spawn(async move {
            let _ = serve_with_client(server_read, server_write, llm, ".").await;
        });

        // First: initialize.
        let init_bytes = make_request(1, "initialize", Some(json!({"protocolVersion":"1"})));
        client_write.write_all(&init_bytes).await.unwrap();
        let _ = reader.next().await; // consume initialize response

        // Now send session/prompt.
        let prompt_bytes = make_request(2, "session/prompt", Some(json!({"text":"hello"})));
        client_write.write_all(&prompt_bytes).await.unwrap();

        // Read messages until we see id=2; verify TurnComplete comes first.
        let mut found_turn_complete = false;
        let prompt_result = tokio::time::timeout(
            std::time::Duration::from_secs(30),
            async {
                loop {
                    let msg = reader.next().await;
                    if msg["method"] == "event/turnComplete" {
                        found_turn_complete = true;
                    }
                    if msg.get("id").map(|id| id == 2).unwrap_or(false) {
                        break msg;
                    }
                }
            },
        )
        .await
        .expect("session/prompt timed out");

        assert!(
            found_turn_complete,
            "event/turnComplete must have arrived before the session/prompt result"
        );
        assert_eq!(prompt_result["result"]["ok"], true);
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

    // ── End-to-end tests with a fake LLM client ───────────────────────────────
    //
    // These tests inject a fake `LlmClient` so the agent runs a real turn
    // (with built-in tools, real history accumulation, real event emission)
    // but never touches the network.

    use coda_agent as _;
    use coda_llm::{
        ChatRequest, Content, Correlation, LlmClient,
        ResponseStream, Usage,
    };
    use coda_llm::anthropic::StreamEvent;
    use coda_llm::error::LlmError;
    use std::collections::VecDeque;

    /// A fake LLM client that replays preset streams, one per `stream()` call.
    struct FakeLlmClient {
        turns: std::sync::Mutex<VecDeque<Vec<StreamEvent>>>,
    }

    impl FakeLlmClient {
        fn new(turns: Vec<Vec<StreamEvent>>) -> Self {
            Self { turns: std::sync::Mutex::new(turns.into()) }
        }
    }

    #[async_trait::async_trait]
    impl LlmClient for FakeLlmClient {
        fn provider_id(&self) -> &str { "fake" }

        async fn stream(&self, _req: ChatRequest) -> Result<ResponseStream, LlmError> {
            let events = self
                .turns
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or_else(|| vec![
                    StreamEvent::TextDelta("(no more turns)".into()),
                    StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO },
                ]);
            let (tx, rx) = tokio::sync::mpsc::channel(32);
            tokio::spawn(async move {
                for event in events {
                    let _ = tx.send(Ok(event)).await;
                }
            });
            Ok(ResponseStream::new(rx))
        }
    }

    /// A fake client that always returns an auth error (non-retried).
    struct ErrorLlmClient;

    #[async_trait::async_trait]
    impl LlmClient for ErrorLlmClient {
        fn provider_id(&self) -> &str { "error-fake" }
        async fn stream(&self, _req: ChatRequest) -> Result<ResponseStream, LlmError> {
            Err(LlmError::Unauthorized("simulated auth failure".into()))
        }
    }

    /// A fake client whose stream never produces a `Done` event (blocks until dropped).
    struct BlockingLlmClient;

    #[async_trait::async_trait]
    impl LlmClient for BlockingLlmClient {
        fn provider_id(&self) -> &str { "blocking-fake" }
        async fn stream(&self, _req: ChatRequest) -> Result<ResponseStream, LlmError> {
            // The sender is held by the spawned task which sleeps forever.
            // When the receiver is dropped (agent cancelled), the task is aborted.
            let (tx, rx) = tokio::sync::mpsc::channel::<Result<StreamEvent, LlmError>>(1);
            tokio::spawn(async move {
                tokio::time::sleep(std::time::Duration::from_secs(60)).await;
                drop(tx);
            });
            Ok(ResponseStream::new(rx))
        }
    }

    // Helpers shared by integration tests
    fn make_fake_harness(client: Arc<dyn LlmClient>, working_dir: &str) -> (
        tokio::io::WriteHalf<tokio::io::DuplexStream>,
        TestReader<tokio::io::ReadHalf<tokio::io::DuplexStream>>,
    ) {
        let (server_end, client_end) = duplex(256 * 1024);
        let (server_read, server_write) = split(server_end);
        let (client_read, client_write) = split(client_end);
        let wd = working_dir.to_string();
        tokio::spawn(async move {
            let _ = serve_with_client(server_read, server_write, client, &wd).await;
        });
        (client_write, TestReader::new(client_read))
    }

    async fn do_initialize(
        client_write: &mut tokio::io::WriteHalf<tokio::io::DuplexStream>,
        reader: &mut TestReader<tokio::io::ReadHalf<tokio::io::DuplexStream>>,
    ) {
        // NOTE: serve_with_client pre-wires the client, so initialize here
        // just completes the handshake without any credential lookup.
        client_write
            .write_all(&make_request(1, "initialize", Some(json!({"protocolVersion":"1"}))))
            .await
            .unwrap();
        let init = reader.next().await;
        assert!(
            init["result"]["sessionId"].is_string(),
            "initialize must succeed: {init}"
        );
    }

    // ── Test 1: Real turn with tool call + tool execution + text reply ─────────

    /// A `session/prompt` that triggers a `read_file` tool call, asserts:
    /// - event stream contains `event/toolCall`, `event/toolResult`,
    ///   assistant text, `event/turnComplete` (in order, TurnComplete last)
    /// - the tool actually executed: the file contents appear in `event/toolResult`
    /// - history contains user message + assistant tool call + tool result + reply
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn full_turn_runs_tool_call_then_text_and_updates_history() {
        // Write a temp file with known contents.
        let dir = std::path::PathBuf::from(std::env::temp_dir()).join("coda-serve-e2e");
        std::fs::create_dir_all(&dir).unwrap();
        let file = dir.join("hello.txt");
        std::fs::write(&file, "hello from the test file").unwrap();
        let file_path = file.to_str().unwrap().replace('\\', "/");

        let client: Arc<dyn LlmClient> = Arc::new(FakeLlmClient::new(vec![
            // Turn 1: model asks to read_file
            vec![
                StreamEvent::ToolUse(Content::ToolUse {
                    id: "call_1".into(),
                    name: "read_file".into(),
                    input_json: format!(r#"{{"path":"{file_path}"}}"#),
                    correlation: Correlation::default(),
                }),
                StreamEvent::Done {
                    stop_reason: Some("tool_use".into()),
                    usage: Usage::ZERO,
                },
            ],
            // Turn 2: model produces a text reply
            vec![
                StreamEvent::TextDelta("I read the file.".into()),
                StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO },
            ],
        ]));

        let (mut cw, mut cr) = make_fake_harness(Arc::clone(&client), dir.to_str().unwrap());
        do_initialize(&mut cw, &mut cr).await;

        // Send session/prompt
        cw.write_all(&make_request(2, "session/prompt", Some(json!({"text":"read the file"}))))
            .await
            .unwrap();

        // Collect all messages until the session/prompt result (id=2)
        let mut notifications: Vec<serde_json::Value> = Vec::new();
        let prompt_result = tokio::time::timeout(
            std::time::Duration::from_secs(30),
            async {
                loop {
                    let msg = cr.next().await;
                    if msg.get("id").map(|id| id == 2).unwrap_or(false) {
                        break msg;
                    }
                    notifications.push(msg);
                }
            },
        )
        .await
        .expect("session/prompt timed out after 30s");

        let methods: Vec<&str> =
            notifications.iter().map(|m| m["method"].as_str().unwrap_or("")).collect();

        // Verify event order
        assert!(methods.contains(&"event/toolCall"), "missing event/toolCall; got: {methods:?}");
        assert!(
            methods.contains(&"event/toolResult"),
            "missing event/toolResult; got: {methods:?}"
        );
        assert_eq!(
            *methods.last().unwrap(),
            "event/turnComplete",
            "event/turnComplete must be the last notification before the result"
        );

        // Verify tool actually ran: file contents in toolResult
        let tool_result_evt = notifications
            .iter()
            .find(|m| m["method"] == "event/toolResult")
            .expect("no event/toolResult found");
        let result_content = tool_result_evt["params"]["content"].as_str().unwrap_or("");
        assert!(
            result_content.contains("hello from the test file"),
            "tool must have read the real file; got content: {result_content}"
        );

        // Verify prompt response
        assert_eq!(prompt_result["result"]["ok"], true);
        assert_eq!(prompt_result["result"]["interrupted"], false);

        // Verify history
        cw.write_all(&make_request(3, "session/history", None)).await.unwrap();
        let hist = cr.next().await;
        let msgs = hist["result"]["messages"].as_array().expect("messages array");
        let user: Vec<_> = msgs.iter().filter(|m| m["role"] == "user").collect();
        let asst: Vec<_> = msgs.iter().filter(|m| m["role"] == "assistant").collect();
        assert!(!user.is_empty(), "must have user messages");
        let asst_with_text: Vec<_> =
            asst.iter().filter(|m| !m["content"].as_str().unwrap_or("").is_empty()).collect();
        assert!(!asst_with_text.is_empty(), "must have assistant messages with text content");

        let _ = std::fs::remove_dir_all(&dir);
    }

    // ── Test 2: session/interrupt yields interrupted:true ─────────────────────

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn session_interrupt_yields_interrupted_true() {
        let client: Arc<dyn LlmClient> = Arc::new(BlockingLlmClient);
        let (mut cw, mut cr) = make_fake_harness(Arc::clone(&client), ".");
        do_initialize(&mut cw, &mut cr).await;

        // Send session/prompt (will block indefinitely without interrupt)
        cw.write_all(&make_request(2, "session/prompt", Some(json!({"text":"do something"}))))
            .await
            .unwrap();

        // Give the agent loop a moment to start.
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;

        // Send interrupt
        cw.write_all(&make_request(3, "session/interrupt", None)).await.unwrap();

        // Drain messages until we see the session/prompt result.
        let mut found_turn_complete = false;
        let prompt_result = tokio::time::timeout(
            std::time::Duration::from_secs(10),
            async {
                loop {
                    let msg = cr.next().await;
                    if msg["method"] == "event/turnComplete" {
                        found_turn_complete = true;
                    }
                    if msg.get("id").map(|id| id == 2).unwrap_or(false) {
                        break msg;
                    }
                }
            },
        )
        .await
        .expect("interrupt did not complete in 10s");

        assert!(found_turn_complete, "event/turnComplete must be emitted on interrupt");
        assert_eq!(
            prompt_result["result"]["interrupted"], true,
            "session/prompt must report interrupted:true after cancel"
        );
        assert_eq!(prompt_result["result"]["ok"], true);
    }

    // ── Test 3: Failing model returns ok:false with error ─────────────────────

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn failing_model_returns_ok_false_with_error_not_a_panic() {
        let client: Arc<dyn LlmClient> = Arc::new(ErrorLlmClient);
        let (mut cw, mut cr) = make_fake_harness(Arc::clone(&client), ".");
        do_initialize(&mut cw, &mut cr).await;

        cw.write_all(&make_request(2, "session/prompt", Some(json!({"text":"do it"}))))
            .await
            .unwrap();

        // Drain until we see id=2
        let prompt_result = tokio::time::timeout(
            std::time::Duration::from_secs(10),
            async {
                loop {
                    let msg = cr.next().await;
                    if msg.get("id").map(|id| id == 2).unwrap_or(false) {
                        break msg;
                    }
                }
            },
        )
        .await
        .expect("did not get session/prompt response in 10s");

        assert_eq!(
            prompt_result["result"]["ok"], false,
            "a failing model must produce ok:false"
        );
        assert!(
            prompt_result["result"].get("error").is_some()
                && !prompt_result["result"]["error"].as_str().unwrap_or("").is_empty(),
            "a failing model must include a non-empty error field"
        );
        assert_eq!(
            prompt_result["result"]["interrupted"], false,
            "a model error is not an interrupt"
        );
    }
}
