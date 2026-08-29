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
    /// True while a thinking burst is in progress (between ThinkingDelta and
    /// the corresponding ThinkingDone).
    thinking_burst_open: bool,
    /// Marks when the current thinking burst opened, for `elapsed_ms`.
    thinking_burst_start: Option<Instant>,
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
        match event? {
            StreamEvent::TextDelta(text) => {
                acc.text.push_str(&text);
                sink.emit(AgentEvent::AssistantText { delta: text });
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
                    let elapsed_ms = acc
                        .thinking_burst_start
                        .take()
                        .map(|t| t.elapsed().as_millis() as i64)
                        .unwrap_or(0);
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

    loop {
        // Respect caller cancel before each attempt.
        if cancel.is_cancelled() {
            return Err(LlmError::Cancelled);
        }

        let stream = client.stream(request.clone()).await?;

        // Race the stream against caller cancel.  Cancellation works by DROPPING
        // the ResponseStream: the transport layer sees the receiver disappear and
        // aborts the in-flight HTTP request.
        let drive_result = tokio::select! {
            r = drive_stream(stream, sink, &mut acc) => r,
            _ = cancel.cancelled() => return Err(LlmError::Cancelled),
        };

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

            Err(err) => return Err(err),
        }
    }
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
}
