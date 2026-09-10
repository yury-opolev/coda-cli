//! Stream consumption and the three retry arms.
//!
//! `drive_stream` processes one `ResponseStream`, updating the
//! `StreamAccumulator` and emitting events to the sink.
//!
//! The three retry guards (context-overflow, transient-transport, schema
//! eviction) are enforced in `stream_with_retries`.  They share the invariant
//! that a retry is only attempted when the accumulator is still empty: no
//! duplicate text, tool results, or usage can reach the sink.

use std::time::Instant;

use coda_llm::anthropic::StreamEvent;
use coda_llm::{ChatRequest, Content, LlmClient, LlmError, ResponseStream, Usage};
use tokio_util::sync::CancellationToken;

use crate::events::{AgentEvent, AgentSink};
use crate::tool::ToolQuarantine;

/// Everything the loop accumulates from one LLM response stream.
#[derive(Default)]
pub(crate) struct StreamAccumulator {
    pub text: String,
    /// Collected tool-use blocks, **in arrival order**, with empty correlation
    /// (stamped by the loop after the stream completes).
    pub tool_uses: Vec<Content>,
    /// Signed thinking blocks (unsigned are collected here too, but filtered
    /// out at assembly time — see `§3 block order`).
    pub thinking_blocks: Vec<Content>,
    /// Opaque redacted thinking blocks; must be replayed verbatim.
    pub redacted_thinking_blocks: Vec<Content>,
    pub stop_reason: Option<String>,
    pub usage: Option<Usage>,
    /// True while a thinking burst is in progress (from ThinkingStarted/Delta to
    /// the corresponding ThinkingDone).
    thinking_burst_open: bool,
    /// Marks when the current thinking burst opened, for `elapsed_ms`.
    thinking_burst_start: Option<Instant>,
    /// When the current stretch of the stream began — the stream itself, or
    /// the last text or tool that interrupted it.
    ///
    /// The fallback start for a burst that produced no deltas. A provider that
    /// encrypts its reasoning sends none, so the burst clock never started and
    /// the turn reported "Thought" with no duration at all, as though no time
    /// had passed. Measuring from the previous activity is an honest estimate:
    /// nothing else was happening in between.
    segment_start: Option<Instant>,
}

impl StreamAccumulator {
    pub fn is_empty(&self) -> bool {
        self.text.is_empty()
            && self.tool_uses.is_empty()
            && self.stop_reason.is_none()
            && self.thinking_blocks.is_empty()
            && self.redacted_thinking_blocks.is_empty()
            && !self.thinking_burst_open
            && self.usage.is_none()
    }

    pub fn clear(&mut self) {
        *self = Self::default();
    }
}

/// Drive a `ResponseStream` to completion, accumulating events and emitting
/// to `sink`.  Returns `Err` on any stream error; the accumulator may be
/// partially filled on error (retry callers should call `acc.clear()`).
///
/// Cancellation is handled by the caller via a wrapping `tokio::select!`
/// in `stream_with_retries`; dropping this future drops the stream, which
/// signals the transport to abort the in-flight request.
pub(crate) async fn drive_stream(
    mut stream: ResponseStream,
    sink: &dyn AgentSink,
    acc: &mut StreamAccumulator,
) -> Result<(), LlmError> {
    while let Some(event) = stream.next().await {
        // The stream's own start, for a thinking burst that never emits a
        // delta to open one.
        acc.segment_start.get_or_insert_with(Instant::now);
        match event? {
            StreamEvent::TextDelta(text) => {
                acc.text.push_str(&text);
                acc.segment_start = Some(Instant::now());
                sink.emit(AgentEvent::AssistantText { delta: text });
            }

            StreamEvent::ThinkingStarted => {
                if !acc.thinking_burst_open {
                    acc.thinking_burst_start = Some(Instant::now());
                    acc.thinking_burst_open = true;
                    // The existing wire event opens a bodyless UI block; no
                    // placeholder reasoning text or protocol extension is needed.
                    sink.emit(AgentEvent::Thinking { delta: String::new() });
                }
            }

            StreamEvent::ThinkingDelta(text) => {
                if acc.thinking_burst_start.is_none() {
                    acc.thinking_burst_start = Some(Instant::now());
                }
                acc.thinking_burst_open = true;
                sink.emit(AgentEvent::Thinking { delta: text });
            }

            StreamEvent::ThinkingDone(block) => match &block {
                Content::RedactedThinking { .. } => {
                    // Opaque block: preserve for history replay but do NOT close
                    // the burst or emit ThinkingComplete — the user never sees it.
                    acc.redacted_thinking_blocks.push(block);
                }
                Content::Thinking { .. } => {
                    // Signed or unsigned — both close the burst and emit.
                    // Unsigned blocks will be filtered out at history assembly.
                    //
                    // Falls back to the segment start, so reasoning that
                    // arrived encrypted still reports how long it took rather
                    // than claiming no time passed.
                    let elapsed_ms = acc
                        .thinking_burst_start
                        .take()
                        .or(acc.segment_start)
                        .map(|t| t.elapsed().as_millis() as i64)
                        .unwrap_or(0);
                    acc.segment_start = Some(Instant::now());
                    acc.thinking_burst_open = false;
                    // Mismatch: thinking_tokens is always None; token counts
                    // arrive in the Done event, not per-burst.
                    sink.emit(AgentEvent::ThinkingComplete {
                        elapsed_ms,
                        thinking_tokens: None,
                    });
                    acc.thinking_blocks.push(block);
                }
                _ => {
                    // Unexpected content type; ignore to be forward-compatible.
                }
            },

            StreamEvent::ToolUse(block) => {
                acc.segment_start = Some(Instant::now());
                acc.tool_uses.push(block);
            }

            StreamEvent::Done { stop_reason, usage } => {
                acc.stop_reason = stop_reason;
                if usage != Usage::ZERO {
                    sink.emit(AgentEvent::Usage { usage });
                    acc.usage = Some(usage);
                }
            }
        }
    }
    Ok(())
}

/// Configuration controlling the retry arms around the stream call.
pub(crate) struct RetryConfig {
    pub max_transport_retries: u32,
    pub max_schema_evictions: u32,
}

impl Default for RetryConfig {
    fn default() -> Self {
        Self { max_transport_retries: 2, max_schema_evictions: 3 }
    }
}

/// Drive the stream with three retry arms.
///
/// **Invariant**: a retry is only attempted when `acc.is_empty()` — i.e.
/// nothing has been emitted yet.  This makes replay clean and prevents
/// duplicate text, tool calls, or usage.
pub(crate) async fn stream_with_retries(
    client: &dyn LlmClient,
    request: &mut ChatRequest,
    quarantine: &ToolQuarantine,
    sink: &dyn AgentSink,
    cancel: CancellationToken,
    retry_cfg: &RetryConfig,
    // Compaction seam: returns (did_compact, blocked_at) or None when not wired.
    // Phase 3 will provide a real implementation; for now always None.
    compact: Option<&(dyn Fn() -> bool + Send + Sync)>,
    blocked_compaction_at: &mut Option<usize>,
) -> Result<StreamAccumulator, LlmError> {
    let mut acc = StreamAccumulator::default();
    let mut overflow_retried = false;
    let mut transport_retries = 0u32;
    let mut schema_evictions = 0u32;
    // Counts outer stream attempts (connect + consume), purely for the
    // optional `ModelRequestStart`/`ModelRequestEnd` debug-detail pair below
    // — distinct from the HTTP-level `attempt` recorded inside
    // `send_with_retry`.
    let mut outer_attempt: u32 = 0;

    loop {
        // Respect caller cancel before each attempt.
        if cancel.is_cancelled() {
            return Err(LlmError::Cancelled);
        }

        // A fresh internal request id per outer stream attempt, scoping both
        // the connection and its consumption so HTTP-attempt diagnostics
        // (recorded inside `send_with_retry`, in-context) and stream/transport
        // failure diagnostics (recorded here, at the consumer) correlate under
        // the same request id — without a process-global "current request".
        let attempt_ctx = coda_diagnostics::current()
            .map(|ctx| ctx.with_request(uuid::Uuid::new_v4().to_string()));
        outer_attempt += 1;
        // Optional detail only (gated to Debug/Trace by
        // `Event::minimum_verbosity`); essential lifecycle/failure events
        // below are unaffected and always present.
        if let Some(ctx) = &attempt_ctx {
            ctx.record(coda_diagnostics::Event::ModelRequestStart { attempt: outer_attempt });
        }
        let attempt_started = Instant::now();

        // Minted per outer stream attempt (§2.6 of the serve API
        // implementation plan): a retry gets a new id, but it links back to
        // the same turn via the state sink's single active-turn tracking.
        // Emitted BEFORE `client.stream()` is awaited — this is the
        // observable boundary the activity phase machine keys off to move
        // from `preparing`/`runningTools` into `waitingForModel`; silence
        // after this point must never be read as `reasoning`.
        let model_request_id = uuid::Uuid::new_v4().to_string();
        sink.emit(AgentEvent::ModelRequestStarted { request_id: model_request_id.clone() });

        let stream_result = match &attempt_ctx {
            Some(ctx) => coda_diagnostics::scope(ctx.clone(), client.stream(request.clone())).await,
            None => client.stream(request.clone()).await,
        };
        let stream = match stream_result {
            Ok(stream) => stream,
            Err(err) => {
                // Bypasses the retry arms below by design: `client.stream()`
                // already exhausted the HTTP-level retry policy internally
                // (`send_with_retry`) before ever returning an error here.
                record_stream_failure(attempt_ctx.as_ref(), &err);
                record_model_request_end(attempt_ctx.as_ref(), outer_attempt, attempt_started, "failed");
                sink.emit(AgentEvent::ModelRequestEnded {
                    request_id: model_request_id,
                    outcome: "error".into(),
                });
                return Err(err);
            }
        };

        // Race the stream against caller cancel.  Cancellation works by DROPPING
        // the ResponseStream: the transport layer sees the receiver disappear and
        // aborts the in-flight HTTP request.
        let drive_fut = async {
            tokio::select! {
                r = drive_stream(stream, sink, &mut acc) => r,
                _ = cancel.cancelled() => Err(LlmError::Cancelled),
            }
        };
        let drive_result = match &attempt_ctx {
            Some(ctx) => coda_diagnostics::scope(ctx.clone(), drive_fut).await,
            None => drive_fut.await,
        };
        let outer_outcome = match &drive_result {
            Ok(()) => "success",
            Err(LlmError::Cancelled) => "cancelled",
            Err(_) => "error",
        };
        record_model_request_end(attempt_ctx.as_ref(), outer_attempt, attempt_started, outer_outcome);
        sink.emit(AgentEvent::ModelRequestEnded {
            request_id: model_request_id,
            outcome: outer_outcome.into(),
        });

        match drive_result {
            Ok(()) => {
                // Close any burst the provider did not explicitly close.
                if acc.thinking_burst_open {
                    let elapsed_ms = acc
                        .thinking_burst_start
                        .take()
                        .map(|t| t.elapsed().as_millis() as i64)
                        .unwrap_or(0);
                    acc.thinking_burst_open = false;
                    sink.emit(AgentEvent::ThinkingComplete {
                        elapsed_ms,
                        thinking_tokens: None,
                    });
                }
                // Final cancel check: if the caller cancelled during the last
                // few events of a text-only turn, surface Cancelled rather than Ok.
                if cancel.is_cancelled() {
                    return Err(LlmError::Cancelled);
                }
                sink.emit(AgentEvent::AssistantTextComplete);
                return Ok(acc);
            }

            // --- arm 1: context-overflow compaction retry ---
            // §MINOR5: acc.is_empty() guard mirrors arms 2 and 3 — a retry is
            // only safe when nothing has been emitted; without it a partial
            // response would be duplicated after compaction.
            Err(err)
                if !overflow_retried
                    && compact.is_some()
                    && !cancel.is_cancelled()
                    && acc.is_empty()
                    && is_context_overflow_error(&err)
                    && !is_compaction_suppressed(*blocked_compaction_at, request) =>
            {
                acc.clear();
                overflow_retried = true;
                let did_compact = compact.unwrap()();
                if !did_compact {
                    *blocked_compaction_at = Some(estimate_tokens(request));
                }
                // Re-use the same (mutated) request; caller must update messages.
            }

            // --- arm 2: transient transport retry ---
            Err(err)
                if transport_retries < retry_cfg.max_transport_retries
                    && !cancel.is_cancelled()
                    && acc.is_empty()
                    && is_transient_transport_error(&err) =>
            {
                // §5: guard is airtight — nothing emitted yet, so replay is clean.
                transport_retries += 1;
                let backoff = transport_retry_backoff(transport_retries);
                tokio::select! {
                    _ = tokio::time::sleep(backoff) => {}
                    _ = cancel.cancelled() => return Err(LlmError::Cancelled),
                }
                acc.clear(); // Defensive clear (should already be empty).
            }

            // --- arm 3: tool-schema eviction ---
            Err(LlmError::Api { status: 400, ref body, .. })
                if schema_evictions < retry_cfg.max_schema_evictions
                    && !cancel.is_cancelled()
                    && acc.is_empty()
                    && body.is_some() =>
            {
                let body_text = body.as_deref().unwrap_or("");
                let tool_names: Vec<&str> =
                    request.tools.iter().map(|t| t.name.as_str()).collect();
                if let Some(offending) =
                    try_identify_schema_rejection(body_text, &tool_names)
                {
                    schema_evictions += 1;
                    quarantine.evict(&offending);
                    sink.emit(AgentEvent::Error {
                        message: format!(
                            "The model provider rejected the definition of tool '{offending}'; \
                             it has been disabled for the rest of this session."
                        ),
                    });
                    let filtered = quarantine.filter(
                        request.tools.iter().cloned().collect::<Vec<_>>(),
                    );
                    // If eviction changed nothing (name not in this request), surface.
                    if filtered.len() == request.tools.len() {
                        return Err(LlmError::Api {
                            status: 400,
                            message: "schema eviction did not remove any tool".into(),
                            kind: coda_llm::FailureKind::Permanent,
                            retry_after: None,
                            body: body.clone(),
                        });
                    }
                    request.tools = filtered;
                    acc.clear();
                } else {
                    // Can't identify the offending tool → surface.
                    return Err(LlmError::Api {
                        status: 400,
                        message: "tool schema rejected".into(),
                        kind: coda_llm::FailureKind::Permanent,
                        retry_after: None,
                        body: body.clone(),
                    });
                }
            }

            Err(err) => {
                record_stream_failure(attempt_ctx.as_ref(), &err);
                return Err(err);
            }
        }
    }
}

/// Records the optional (Debug/Trace-only) end of one outer per-model-request
/// attempt. `outcome` is a small closed classification, never the
/// underlying error's `Display`/`Debug`. A no-op when there is no ambient
/// context — this is purely additive detail, never essential.
fn record_model_request_end(
    ctx: Option<&coda_diagnostics::DiagnosticContext>,
    attempt: u32,
    started: Instant,
    outcome: &'static str,
) {
    let Some(ctx) = ctx else { return };
    ctx.record(coda_diagnostics::Event::ModelRequestEnd {
        attempt,
        duration_ms: started.elapsed().as_millis() as u64,
        outcome,
    });
}

/// Records a stream/transport/protocol failure under `ctx`'s identity, using
/// only closed classification and bounded, allowlisted detail — never the
/// error's `Display`/`Debug`, which can carry provider text or a
/// credential-bearing URL. A no-op when there is no ambient context or the
/// "failure" is an ordinary user-initiated cancellation.
fn record_stream_failure(ctx: Option<&coda_diagnostics::DiagnosticContext>, err: &LlmError) {
    let Some(ctx) = ctx else { return };
    let category = coda_llm::diagnostics::category(err);
    let event = match err {
        LlmError::Cancelled => return,
        LlmError::Transport(_) => coda_diagnostics::Event::TransportFailure { category },
        LlmError::Protocol(_) => coda_diagnostics::Event::ProtocolFailure { category },
        _ => coda_diagnostics::Event::StreamFailure {
            category,
            status: coda_llm::diagnostics::status(err),
            // Unavailable here: the HTTP-attempt request id (when the
            // provider sent one) was already recorded in-context by
            // `send_with_retry`; never invented at this layer.
            provider_request_id: None,
            parameter: coda_llm::diagnostics::parameter(err),
        },
    };
    ctx.record(event);
}

/// Returns `true` when the error suggests the context window was exceeded.
pub(crate) fn is_context_overflow_error(err: &LlmError) -> bool {
    match err {
        LlmError::Api { status, message, .. } => {
            *status == 400
                && (message.contains("context length")
                    || message.contains("context window")
                    || message.contains("too large")
                    || message.contains("maximum token")
                    || message.contains("too long"))
        }
        _ => false,
    }
}

/// Returns `true` for transport-level failures that are safe to retry before
/// anything has been emitted.
pub(crate) fn is_transient_transport_error(err: &LlmError) -> bool {
    matches!(err, LlmError::Transport(_) | LlmError::IncompleteStream)
}

/// Backoff durations for transport retries (0.5s, 2s, …).
fn transport_retry_backoff(attempt: u32) -> std::time::Duration {
    match attempt {
        1 => std::time::Duration::from_millis(500),
        _ => std::time::Duration::from_secs(2),
    }
}

/// Try to identify the tool whose schema was rejected in a 400 error body.
/// The provider typically names the tool in single or double quotes.
pub(crate) fn try_identify_schema_rejection(
    error_body: &str,
    known_tool_names: &[&str],
) -> Option<String> {
    for name in known_tool_names {
        if error_body.contains(&format!("'{name}'"))
            || error_body.contains(&format!("\"{name}\""))
        {
            return Some((*name).to_owned());
        }
    }
    None
}

fn is_compaction_suppressed(
    blocked_at: Option<usize>,
    request: &ChatRequest,
) -> bool {
    match blocked_at {
        Some(at) => estimate_tokens(request) <= at + at, // simple heuristic
        None => false,
    }
}

fn estimate_tokens(request: &ChatRequest) -> usize {
    // Very rough estimate: 1 token ≈ 4 chars.
    request
        .messages
        .iter()
        .map(|m| {
            m.content.iter().map(|c| c.as_text().map_or(0, |t| t.len())).sum::<usize>()
        })
        .sum::<usize>()
        / 4
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_llm::anthropic::StreamEvent;
    use coda_llm::{Content, Usage};
    use tokio::sync::mpsc;

    fn make_stream(events: Vec<Result<StreamEvent, LlmError>>) -> ResponseStream {
        let (tx, rx) = mpsc::channel(64);
        tokio::spawn(async move {
            for ev in events {
                let _ = tx.send(ev).await;
            }
        });
        ResponseStream::new(rx)
    }

    fn done_event() -> StreamEvent {
        StreamEvent::Done {
            stop_reason: Some("end_turn".into()),
            usage: Usage { input_tokens: 10, output_tokens: 5, ..Usage::ZERO },
        }
    }

    // §8 item 3: assistant block order helpers — verify thinking blocks accumulate.
    #[tokio::test]
    async fn reasoning_start_reaches_ui_while_provider_is_still_silent() {
        use coda_llm::copilot::responses::ResponsesDecoder;
        struct ChannelSink(mpsc::UnboundedSender<AgentEvent>);
        impl AgentSink for ChannelSink {
            fn emit(&self, event: AgentEvent) {
                self.0.send(event).unwrap();
            }
        }
        let mut decoder = ResponsesDecoder::new();
        let events = decoder.decode("response.output_item.added",
            r#"{"output_index":0,"item":{"type":"reasoning","id":"r1","summary":[]}}"#,
        ).unwrap();
        let (provider_tx, provider_rx) = mpsc::channel(16);
        for event in events {
            provider_tx.send(Ok(event)).await.unwrap();
        }
        let (ui_tx, mut ui_rx) = mpsc::unbounded_channel();
        let sink = ChannelSink(ui_tx);
        let mut acc = StreamAccumulator::default();
        let event = tokio::time::timeout(std::time::Duration::from_secs(1), async {
            tokio::select! {
                event = ui_rx.recv() => event.expect("UI event"),
                result = drive_stream(ResponseStream::new(provider_rx), &sink, &mut acc) =>
                    panic!("provider is still open: {result:?}"),
            }
        }).await.expect("UI must receive thinking before any summary or answer");
        assert!(matches!(event, AgentEvent::Thinking { delta } if delta.is_empty()));
        assert!(acc.thinking_burst_open);
        drop(provider_tx);
    }

    #[tokio::test]
    async fn drive_stream_accumulates_signed_thinking() {
        use crate::events::NullSink;
        let block = Content::Thinking { text: "reasoning".into(), signature: Some("sig".into()) };
        let stream = make_stream(vec![
            Ok(StreamEvent::ThinkingDelta("reasoning".into())),
            Ok(StreamEvent::ThinkingDone(block.clone())),
            Ok(done_event()),
        ]);
        let mut acc = StreamAccumulator::default();
        drive_stream(stream, &NullSink, &mut acc).await.unwrap();
        assert_eq!(acc.thinking_blocks.len(), 1);
        assert!(matches!(&acc.thinking_blocks[0], Content::Thinking { signature: Some(_), .. }));
    }

    #[tokio::test]
    async fn drive_stream_redacted_thinking_no_burst() {
        use crate::events::CollectingSink;
        let redacted = Content::RedactedThinking { data: "opaque".into() };
        let stream = make_stream(vec![
            Ok(StreamEvent::ThinkingDone(redacted)),
            Ok(done_event()),
        ]);
        let mut acc = StreamAccumulator::default();
        let sink = CollectingSink::new();
        drive_stream(stream, &sink, &mut acc).await.unwrap();

        assert_eq!(acc.redacted_thinking_blocks.len(), 1);
        // No ThinkingComplete emitted for redacted blocks.
        let events = sink.take();
        assert!(
            !events.iter().any(|e| matches!(e, AgentEvent::ThinkingComplete { .. })),
            "ThinkingComplete must not be emitted for redacted thinking"
        );
    }

    #[tokio::test]
    async fn partial_text_breaks_empty_guard() {
        let mut acc = StreamAccumulator::default();
        acc.text.push_str("hello");
        assert!(!acc.is_empty());
    }

    // ── Finding 6: is_empty component tests ─────────────────────────────────
    // Each test verifies that a specific accumulator component makes is_empty()
    // return false.  Removing that component from is_empty() would break the test.

    #[tokio::test]
    async fn thinking_burst_open_breaks_empty_guard() {
        use crate::events::NullSink;
        // ThinkingDelta sets thinking_burst_open = true.  If is_empty() omitted
        // !thinking_burst_open, this would still return true (false signal for retry safety).
        let stream = make_stream(vec![
            Ok(StreamEvent::ThinkingDelta("partial reasoning".into())),
            // No ThinkingDone — burst stays open; transport error interrupts.
            Err(coda_llm::LlmError::Transport("reset".into())),
        ]);
        let mut acc = StreamAccumulator::default();
        let _ = drive_stream(stream, &NullSink, &mut acc).await; // error expected
        assert!(
            !acc.is_empty(),
            "acc with an open thinking burst must not be considered empty"
        );
    }

    #[tokio::test]
    async fn usage_breaks_empty_guard() {
        use crate::events::NullSink;
        // Done event with non-zero usage sets acc.usage.
        let stream = make_stream(vec![Ok(StreamEvent::Done {
            stop_reason: None,
            usage: coda_llm::Usage { input_tokens: 5, ..coda_llm::Usage::ZERO },
        })]);
        let mut acc = StreamAccumulator::default();
        drive_stream(stream, &NullSink, &mut acc).await.unwrap();
        assert!(!acc.is_empty(), "acc with usage set must not be considered empty");
    }

    #[tokio::test]
    async fn stop_reason_breaks_empty_guard() {
        use crate::events::NullSink;
        // Done with zero usage leaves only stop_reason set.
        let stream =
            make_stream(vec![Ok(StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO })]);
        let mut acc = StreamAccumulator::default();
        drive_stream(stream, &NullSink, &mut acc).await.unwrap();
        assert!(!acc.is_empty(), "acc with stop_reason set must not be considered empty");
    }

    // ── MINOR 5: overflow arm must not replay after partial emission ─────────
    //
    // Arm 1 (context-overflow) lacked the acc.is_empty() guard present in arms
    // 2 and 3.  Without the guard, a context-overflow error that arrives after
    // the model already emitted partial text would clear the accumulator and
    // retry, duplicating the emitted text.  With the guard the error surfaces.

    #[tokio::test]
    async fn overflow_arm_does_not_replay_after_partial_emission() {
        use std::sync::Mutex;
        use async_trait::async_trait;
        use crate::events::NullSink;
        use crate::tool::ToolQuarantine;

        struct TwoCallClient {
            calls: Mutex<usize>,
        }
        #[async_trait]
        impl coda_llm::LlmClient for TwoCallClient {
            fn provider_id(&self) -> &str { "mock" }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                let n = {
                    let mut g = self.calls.lock().unwrap();
                    *g += 1;
                    *g
                };
                let events: Vec<Result<StreamEvent, coda_llm::LlmError>> = match n {
                    1 => vec![
                        // Partial text emitted before the overflow error.
                        Ok(StreamEvent::TextDelta("partial".into())),
                        Err(coda_llm::LlmError::Api {
                            status: 400,
                            message: "context length exceeded".into(),
                            kind: coda_llm::FailureKind::Permanent,
                            retry_after: None,
                            body: None,
                        }),
                    ],
                    // Without the guard arm 1 fires and a retry is attempted.
                    // With the guard the error propagates and this call never happens.
                    _ => vec![
                        Ok(StreamEvent::TextDelta("duplicate".into())),
                        Ok(done_event()),
                    ],
                };
                let (tx, rx) = tokio::sync::mpsc::channel(64);
                tokio::spawn(async move {
                    for ev in events { let _ = tx.send(ev).await; }
                });
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }

        let client = TwoCallClient { calls: Mutex::new(0) };
        let quarantine = ToolQuarantine::new();
        let mut request = coda_llm::ChatRequest::new("model".to_owned(), vec![]);
        let retry_cfg = RetryConfig::default();
        let cancel = tokio_util::sync::CancellationToken::new();
        let mut blocked = None;

        let result = stream_with_retries(
            &client,
            &mut request,
            &quarantine,
            &NullSink,
            cancel,
            &retry_cfg,
            Some(&|| false), // compact enabled so arm 1 can be reached
            &mut blocked,
        )
        .await;

        // Without the acc.is_empty() guard on arm 1: retry fires, second call
        // succeeds → Ok.  Test fails.
        // With the guard: error propagates → Err.  Test passes.
        assert!(
            result.is_err(),
            "context-overflow after partial emission must surface as an error, not trigger a replay"
        );
        assert!(
            matches!(result, Err(coda_llm::LlmError::Api { status: 400, .. })),
            "error must be the original context-overflow Api error"
        );
    }

    // §8 item 7: schema eviction identifies tool by name.
    #[test]
    fn identify_schema_rejection_finds_quoted_name() {
        let body = "Invalid schema for tool 'bad_tool': something wrong";
        let names = &["good_tool", "bad_tool", "other"];
        assert_eq!(try_identify_schema_rejection(body, names), Some("bad_tool".to_owned()));
    }

    #[test]
    fn identify_schema_rejection_double_quotes() {
        let body = r#"Tool "bad_tool" has an invalid schema"#;
        let names = &["bad_tool"];
        assert_eq!(try_identify_schema_rejection(body, names), Some("bad_tool".to_owned()));
    }

    #[test]
    fn identify_schema_rejection_no_match_returns_none() {
        let body = "Something went wrong with no tool name";
        assert!(try_identify_schema_rejection(body, &["tool_a", "tool_b"]).is_none());
    }

    // ── Already-closed burst does not produce a second ThinkingComplete ──────
    //
    // When the provider sends ThinkingDone (closing the burst) and then the
    // stream completes normally, stream_with_retries must NOT emit a second
    // ThinkingComplete in the `if acc.thinking_burst_open` check.
    //
    // Mutation-verified: remove the `if acc.thinking_burst_open` guard and
    // restore it to an unconditional emit — this test fails with 2 events.
    #[tokio::test]
    async fn open_burst_at_stream_end_emits_exactly_one_thinking_complete() {
        use crate::events::CollectingSink;
        use crate::tool::ToolQuarantine;

        // A client whose only stream is: ThinkingDelta → ThinkingDone(signed) → Done.
        // ThinkingDone closes the burst; the `if acc.thinking_burst_open` guard in
        // stream_with_retries must be false at that point.
        struct OneShotClient;
        #[async_trait::async_trait]
        impl coda_llm::LlmClient for OneShotClient {
            fn provider_id(&self) -> &str { "mock" }
            async fn stream(&self, _: coda_llm::ChatRequest) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                let block = Content::Thinking { text: "thinking".into(), signature: Some("sig".into()) };
                let events = vec![
                    Ok(StreamEvent::ThinkingDelta("thinking".into())),
                    Ok(StreamEvent::ThinkingDone(block)),
                    Ok(StreamEvent::Done {
                        stop_reason: Some("end_turn".into()),
                        usage: coda_llm::Usage::ZERO,
                    }),
                ];
                let (tx, rx) = tokio::sync::mpsc::channel(8);
                tokio::spawn(async move { for e in events { let _ = tx.send(e).await; } });
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }

        let quarantine = ToolQuarantine::new();
        let sink = CollectingSink::new();
        let mut request = coda_llm::ChatRequest::new("model".to_owned(), vec![]);
        let retry_cfg = RetryConfig::default();
        let cancel = tokio_util::sync::CancellationToken::new();
        let mut blocked = None;

        stream_with_retries(
            &OneShotClient,
            &mut request,
            &quarantine,
            &sink,
            cancel,
            &retry_cfg,
            None,
            &mut blocked,
        )
        .await
        .unwrap();

        let events = sink.take();
        let complete_count = events
            .iter()
            .filter(|e| matches!(e, AgentEvent::ThinkingComplete { .. }))
            .count();
        assert_eq!(
            complete_count,
            1,
            "exactly one ThinkingComplete must be emitted when the provider closes the burst with ThinkingDone"
        );
    }

    // ── ModelRequestStarted/Ended (Slice 0 / Stage C activity phase) ─────────
    //
    // `event/activity` (coda-serve) keys off these to move the phase from
    // `preparing`/`runningTools` to `waitingForModel` BEFORE `client.stream()`
    // is awaited, and back out again once the stream finishes however it
    // finished. Both must fire exactly once per outer attempt, in order, with
    // the same request_id, and ModelRequestStarted must precede any stream
    // content.
    #[tokio::test]
    async fn model_request_started_precedes_stream_content_and_ended_follows_with_matching_id() {
        use crate::events::CollectingSink;
        use crate::tool::ToolQuarantine;

        struct OneShotClient;
        #[async_trait::async_trait]
        impl coda_llm::LlmClient for OneShotClient {
            fn provider_id(&self) -> &str { "mock" }
            async fn stream(&self, _: coda_llm::ChatRequest) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                let events = vec![
                    Ok(StreamEvent::TextDelta("hi".into())),
                    Ok(StreamEvent::Done {
                        stop_reason: Some("end_turn".into()),
                        usage: coda_llm::Usage::ZERO,
                    }),
                ];
                let (tx, rx) = tokio::sync::mpsc::channel(8);
                tokio::spawn(async move { for e in events { let _ = tx.send(e).await; } });
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }

        let quarantine = ToolQuarantine::new();
        let sink = CollectingSink::new();
        let mut request = coda_llm::ChatRequest::new("model".to_owned(), vec![]);
        let retry_cfg = RetryConfig::default();
        let cancel = tokio_util::sync::CancellationToken::new();
        let mut blocked = None;

        stream_with_retries(
            &OneShotClient,
            &mut request,
            &quarantine,
            &sink,
            cancel,
            &retry_cfg,
            None,
            &mut blocked,
        )
        .await
        .unwrap();

        let events = sink.take();
        let started_idx = events
            .iter()
            .position(|e| matches!(e, AgentEvent::ModelRequestStarted { .. }))
            .expect("ModelRequestStarted must be emitted");
        let text_idx = events
            .iter()
            .position(|e| matches!(e, AgentEvent::AssistantText { .. }))
            .expect("AssistantText must be emitted");
        let ended_idx = events
            .iter()
            .position(|e| matches!(e, AgentEvent::ModelRequestEnded { .. }))
            .expect("ModelRequestEnded must be emitted");
        assert!(started_idx < text_idx, "ModelRequestStarted must precede stream content");
        assert!(text_idx < ended_idx, "ModelRequestEnded must follow stream content");

        let AgentEvent::ModelRequestStarted { request_id: started_id } = &events[started_idx] else {
            unreachable!()
        };
        let AgentEvent::ModelRequestEnded { request_id: ended_id, outcome } = &events[ended_idx] else {
            unreachable!()
        };
        assert_eq!(started_id, ended_id, "started/ended request_id must match for one attempt");
        assert_eq!(outcome, "success");
    }

    #[tokio::test]
    async fn model_request_ended_reports_error_when_stream_call_fails() {
        use crate::events::CollectingSink;
        use crate::tool::ToolQuarantine;

        struct AlwaysErrorsClient;
        #[async_trait::async_trait]
        impl coda_llm::LlmClient for AlwaysErrorsClient {
            fn provider_id(&self) -> &str { "mock" }
            async fn stream(&self, _: coda_llm::ChatRequest) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                Err(coda_llm::LlmError::Transport("boom".into()))
            }
        }

        let quarantine = ToolQuarantine::new();
        let sink = CollectingSink::new();
        let mut request = coda_llm::ChatRequest::new("model".to_owned(), vec![]);
        let retry_cfg = RetryConfig::default();
        let cancel = tokio_util::sync::CancellationToken::new();
        let mut blocked = None;

        let result = stream_with_retries(
            &AlwaysErrorsClient,
            &mut request,
            &quarantine,
            &sink,
            cancel,
            &retry_cfg,
            None,
            &mut blocked,
        )
        .await;
        assert!(result.is_err());

        let events = sink.take();
        assert!(events.iter().any(|e| matches!(e, AgentEvent::ModelRequestStarted { .. })));
        let ended = events.iter().find_map(|e| match e {
            AgentEvent::ModelRequestEnded { outcome, .. } => Some(outcome.clone()),
            _ => None,
        });
        assert_eq!(ended.as_deref(), Some("error"));
    }

    // ── Burst open at stream end still emits ThinkingComplete ────────────────
    //
    // If the provider sends ThinkingDelta but never sends ThinkingDone (unusual
    // but permitted), `stream_with_retries` must close the burst and emit
    // ThinkingComplete so callers see a balanced open/close pair.
    //
    // Mutation-verified: remove the `if acc.thinking_burst_open` block in
    // stream_with_retries and this test fails with 0 ThinkingComplete events.
    #[tokio::test]
    async fn unclosed_burst_at_stream_end_still_emits_thinking_complete() {
        use crate::events::CollectingSink;
        use crate::tool::ToolQuarantine;

        struct OpenBurstClient;
        #[async_trait::async_trait]
        impl coda_llm::LlmClient for OpenBurstClient {
            fn provider_id(&self) -> &str { "mock" }
            async fn stream(&self, _: coda_llm::ChatRequest) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                // ThinkingDelta without ThinkingDone — burst stays open when
                // the stream completes (Done event closes the stream, not the burst).
                let events = vec![
                    Ok(StreamEvent::ThinkingDelta("unfinished reasoning".into())),
                    Ok(StreamEvent::Done {
                        stop_reason: Some("end_turn".into()),
                        usage: coda_llm::Usage::ZERO,
                    }),
                ];
                let (tx, rx) = tokio::sync::mpsc::channel(8);
                tokio::spawn(async move { for e in events { let _ = tx.send(e).await; } });
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }

        let quarantine = ToolQuarantine::new();
        let sink = CollectingSink::new();
        let mut request = coda_llm::ChatRequest::new("model".to_owned(), vec![]);
        let retry_cfg = RetryConfig::default();
        let cancel = tokio_util::sync::CancellationToken::new();
        let mut blocked = None;

        stream_with_retries(
            &OpenBurstClient,
            &mut request,
            &quarantine,
            &sink,
            cancel,
            &retry_cfg,
            None,
            &mut blocked,
        )
        .await
        .unwrap();

        let events = sink.take();
        let complete_count = events
            .iter()
            .filter(|e| matches!(e, AgentEvent::ThinkingComplete { .. }))
            .count();
        assert_eq!(
            complete_count,
            1,
            "ThinkingComplete must be emitted for a burst left open at stream end"
        );
    }

    #[tokio::test]
    async fn encrypted_reasoning_still_reports_how_long_it_took() {
        // A provider that encrypts its reasoning sends no ThinkingDelta, so
        // the burst clock never started and the turn reported "Thought" with
        // no duration -- as though no time had passed at all. Measuring from
        // the start of the stream is honest: nothing else was happening.
        use crate::events::CollectingSink;

        let signed = Content::Thinking { text: String::new(), signature: Some("sig".into()) };
        let stream = make_stream(vec![
            Ok(StreamEvent::ThinkingDone(signed)),
            Ok(done_event()),
        ]);

        let mut acc = StreamAccumulator::default();
        // Give the clock something to measure. Without a fallback start this
        // is still reported as zero however long the turn actually took.
        acc.segment_start = Some(Instant::now() - std::time::Duration::from_millis(1500));

        let sink = CollectingSink::new();
        drive_stream(stream, &sink, &mut acc).await.unwrap();

        let elapsed = sink
            .take()
            .into_iter()
            .find_map(|e| match e {
                AgentEvent::ThinkingComplete { elapsed_ms, .. } => Some(elapsed_ms),
                _ => None,
            })
            .expect("no ThinkingComplete was emitted");
        assert!(
            elapsed >= 1500,
            "reasoning with no deltas reported {elapsed}ms, so the header says \"Thought\" with no time"
        );
    }

    #[tokio::test]
    async fn a_burst_that_streamed_is_timed_from_its_first_delta() {
        // The fallback must not displace the real measurement: a burst with
        // deltas is timed from the first of them, not from the stream start.
        use crate::events::CollectingSink;

        let signed = Content::Thinking { text: "reasoning".into(), signature: Some("sig".into()) };
        let stream = make_stream(vec![
            Ok(StreamEvent::ThinkingDelta("reasoning".into())),
            Ok(StreamEvent::ThinkingDone(signed)),
            Ok(done_event()),
        ]);

        let mut acc = StreamAccumulator::default();
        // An hour ago. If this were used the burst would claim to have taken
        // an hour.
        acc.segment_start = Some(Instant::now() - std::time::Duration::from_secs(3600));

        let sink = CollectingSink::new();
        drive_stream(stream, &sink, &mut acc).await.unwrap();

        let elapsed = sink
            .take()
            .into_iter()
            .find_map(|e| match e {
                AgentEvent::ThinkingComplete { elapsed_ms, .. } => Some(elapsed_ms),
                _ => None,
            })
            .expect("no ThinkingComplete was emitted");
        assert!(
            elapsed < 60_000,
            "the burst was timed from the stream start, not its first delta: {elapsed}ms"
        );
    }

    // ── diagnostics: terminal stream/transport failures ─────────────────────

    #[tokio::test]
    async fn a_terminal_transport_failure_is_recorded_privacy_safely_with_full_correlation() {
        use async_trait::async_trait;
        use crate::events::NullSink;
        use crate::tool::ToolQuarantine;
        use std::sync::Arc;

        struct AlwaysFailsClient;
        #[async_trait]
        impl coda_llm::LlmClient for AlwaysFailsClient {
            fn provider_id(&self) -> &str {
                "mock"
            }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                Err(coda_llm::LlmError::Transport(
                    "connection reset while talking to https://user:sk-live-secret@example.com".into(),
                ))
            }
        }

        let dir = tempfile::tempdir().unwrap();
        let logger = coda_diagnostics::Logger::open(
            coda_diagnostics::Options {
                directory: dir.path().to_path_buf(),
                file: None,
                role: coda_diagnostics::ProcessRole::Serve,
                version: "test".into(),
                verbosity: coda_diagnostics::Verbosity::Normal,
            },
            coda_diagnostics::Limits::default(),
        )
        .expect("logger opens");
        let ctx = coda_diagnostics::DiagnosticContext::root(Arc::new(logger), "run-1")
            .with_session("sess-1")
            .with_turn("turn-1");

        let client = AlwaysFailsClient;
        let quarantine = ToolQuarantine::new();
        let mut request = coda_llm::ChatRequest::new("model".to_owned(), vec![]);
        let retry_cfg = RetryConfig { max_transport_retries: 0, max_schema_evictions: 0 };
        let cancel = tokio_util::sync::CancellationToken::new();
        let mut blocked = None;

        let result = coda_diagnostics::scope(
            ctx.clone(),
            stream_with_retries(
                &client,
                &mut request,
                &quarantine,
                &NullSink,
                cancel,
                &retry_cfg,
                None,
                &mut blocked,
            ),
        )
        .await;
        assert!(result.is_err(), "the terminal transport error must still surface to the caller");

        let path = ctx.logger().status().path.expect("a log path");
        let content = std::fs::read_to_string(path).unwrap();
        let lines: Vec<serde_json::Value> = content
            .lines()
            .filter(|l| !l.is_empty())
            .map(|l| serde_json::from_str(l).unwrap())
            .collect();
        let failure = lines
            .iter()
            .find(|l| l["kind"] == "transport_failure")
            .expect("a transport_failure record was written");
        assert_eq!(failure["session_id"], "sess-1");
        assert_eq!(failure["turn_id"], "turn-1");
        assert!(failure["request_id"].is_string(), "each attempt gets its own request id");
        assert_eq!(failure["category"], "transport");

        // Privacy: the raw error text (which embeds a credential) must never
        // appear anywhere in the log, at any field.
        assert!(!content.contains("sk-live-secret"));
        assert!(!content.contains("connection reset"));
    }

    // ── diagnostics: --diagnostic-verbosity gates optional detail only ──────

    #[tokio::test]
    async fn normal_verbosity_omits_optional_model_request_detail_but_keeps_essentials() {
        use async_trait::async_trait;
        use crate::events::NullSink;
        use crate::tool::ToolQuarantine;
        use std::sync::Arc;

        struct AlwaysFailsClient;
        #[async_trait]
        impl coda_llm::LlmClient for AlwaysFailsClient {
            fn provider_id(&self) -> &str {
                "mock"
            }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                Err(coda_llm::LlmError::Transport("transport down".into()))
            }
        }

        let dir = tempfile::tempdir().unwrap();
        let logger = coda_diagnostics::Logger::open(
            coda_diagnostics::Options {
                directory: dir.path().to_path_buf(),
                file: None,
                role: coda_diagnostics::ProcessRole::Serve,
                version: "test".into(),
                verbosity: coda_diagnostics::Verbosity::Normal,
            },
            coda_diagnostics::Limits::default(),
        )
        .expect("logger opens");
        let ctx = coda_diagnostics::DiagnosticContext::root(Arc::new(logger), "run-1")
            .with_session("sess-1")
            .with_turn("turn-1");

        let client = AlwaysFailsClient;
        let quarantine = ToolQuarantine::new();
        let mut request = coda_llm::ChatRequest::new("model".to_owned(), vec![]);
        let retry_cfg = RetryConfig { max_transport_retries: 0, max_schema_evictions: 0 };
        let cancel = tokio_util::sync::CancellationToken::new();
        let mut blocked = None;

        let _ = coda_diagnostics::scope(
            ctx.clone(),
            stream_with_retries(
                &client,
                &mut request,
                &quarantine,
                &NullSink,
                cancel,
                &retry_cfg,
                None,
                &mut blocked,
            ),
        )
        .await;

        let path = ctx.logger().status().path.expect("a log path");
        let content = std::fs::read_to_string(path).unwrap();
        assert!(
            !content.contains("model_request_start") && !content.contains("model_request_end"),
            "optional per-model-request detail must be omitted at Normal verbosity: {content}"
        );
        assert!(
            content.contains("transport_failure"),
            "essential failure events must remain present at every verbosity: {content}"
        );
    }

    #[tokio::test]
    async fn debug_verbosity_includes_optional_model_request_detail() {
        use async_trait::async_trait;
        use crate::events::NullSink;
        use crate::tool::ToolQuarantine;
        use std::sync::Arc;

        struct AlwaysFailsClient;
        #[async_trait]
        impl coda_llm::LlmClient for AlwaysFailsClient {
            fn provider_id(&self) -> &str {
                "mock"
            }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                Err(coda_llm::LlmError::Transport("transport down".into()))
            }
        }

        let dir = tempfile::tempdir().unwrap();
        let logger = coda_diagnostics::Logger::open(
            coda_diagnostics::Options {
                directory: dir.path().to_path_buf(),
                file: None,
                role: coda_diagnostics::ProcessRole::Serve,
                version: "test".into(),
                verbosity: coda_diagnostics::Verbosity::Debug,
            },
            coda_diagnostics::Limits::default(),
        )
        .expect("logger opens");
        let ctx = coda_diagnostics::DiagnosticContext::root(Arc::new(logger), "run-1")
            .with_session("sess-1")
            .with_turn("turn-1");

        let client = AlwaysFailsClient;
        let quarantine = ToolQuarantine::new();
        let mut request = coda_llm::ChatRequest::new("model".to_owned(), vec![]);
        let retry_cfg = RetryConfig { max_transport_retries: 0, max_schema_evictions: 0 };
        let cancel = tokio_util::sync::CancellationToken::new();
        let mut blocked = None;

        let _ = coda_diagnostics::scope(
            ctx.clone(),
            stream_with_retries(
                &client,
                &mut request,
                &quarantine,
                &NullSink,
                cancel,
                &retry_cfg,
                None,
                &mut blocked,
            ),
        )
        .await;

        let path = ctx.logger().status().path.expect("a log path");
        let content = std::fs::read_to_string(path).unwrap();
        assert!(
            content.contains("model_request_start"),
            "optional detail must appear at Debug verbosity: {content}"
        );
        assert!(
            content.contains("model_request_end"),
            "optional detail must appear at Debug verbosity: {content}"
        );
        assert!(
            content.contains("transport_failure"),
            "essential failure events remain present alongside optional detail: {content}"
        );
    }

    #[tokio::test]
    async fn cancellation_is_not_recorded_as_a_failure() {
        use async_trait::async_trait;
        use crate::events::NullSink;
        use crate::tool::ToolQuarantine;
        use std::sync::Arc;

        struct HangingClient;
        #[async_trait]
        impl coda_llm::LlmClient for HangingClient {
            fn provider_id(&self) -> &str {
                "mock"
            }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                Err(coda_llm::LlmError::Cancelled)
            }
        }

        let dir = tempfile::tempdir().unwrap();
        let logger = coda_diagnostics::Logger::open(
            coda_diagnostics::Options {
                directory: dir.path().to_path_buf(),
                file: None,
                role: coda_diagnostics::ProcessRole::Serve,
                version: "test".into(),
                verbosity: coda_diagnostics::Verbosity::Normal,
            },
            coda_diagnostics::Limits::default(),
        )
        .expect("logger opens");
        let ctx = coda_diagnostics::DiagnosticContext::root(Arc::new(logger), "run-1")
            .with_session("sess-1")
            .with_turn("turn-1");

        let client = HangingClient;
        let quarantine = ToolQuarantine::new();
        let mut request = coda_llm::ChatRequest::new("model".to_owned(), vec![]);
        let retry_cfg = RetryConfig::default();
        let cancel = tokio_util::sync::CancellationToken::new();
        let mut blocked = None;

        let _ = coda_diagnostics::scope(
            ctx.clone(),
            stream_with_retries(
                &client,
                &mut request,
                &quarantine,
                &NullSink,
                cancel,
                &retry_cfg,
                None,
                &mut blocked,
            ),
        )
        .await;

        let path = ctx.logger().status().path.expect("a log path");
        let content = std::fs::read_to_string(path).unwrap();
        assert!(
            !content.contains("transport_failure")
                && !content.contains("stream_failure")
                && !content.contains("protocol_failure"),
            "cancellation is a normal user action, not a diagnostic failure: {content}"
        );
    }
}