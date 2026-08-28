//! Shared SSE streaming pump.
//!
//! Both provider clients share the same streaming pattern: read raw bytes,
//! carry incomplete UTF-8 sequences across chunk boundaries, decode SSE events
//! through a protocol-specific decoder, and forward results over a channel.
//! A single implementation here means a fix — to the carry buffer, the idle
//! timeout, or the cancellation check — reaches both providers at once.

use std::time::Duration;

use futures_util::StreamExt;
use tokio::sync::mpsc;

use crate::anthropic::protocol::AnthropicDecoder;
use crate::anthropic::StreamEvent;
use crate::error::LlmError;
use crate::sse::SseDecoder;

/// How long a stream may stall before it is treated as dead.
const STREAM_IDLE_TIMEOUT: Duration = Duration::from_secs(120);

/// Decodes SSE events from one provider into the neutral [`StreamEvent`] type.
pub(crate) trait ProtocolDecoder: Send + 'static {
    /// Translates one SSE event (name + data) into zero or more stream events.
    fn decode(&mut self, name: &str, data: &str) -> Result<Vec<StreamEvent>, LlmError>;

    /// Returns `true` once the protocol has received its terminal event.
    fn finished(&self) -> bool;
}

// Delegate to the inherent methods using UFCS so neither call recurses into
// the trait impl itself.
impl ProtocolDecoder for AnthropicDecoder {
    fn decode(&mut self, name: &str, data: &str) -> Result<Vec<StreamEvent>, LlmError> {
        AnthropicDecoder::decode(self, name, data)
    }

    fn finished(&self) -> bool {
        AnthropicDecoder::finished(self)
    }
}

/// Reads `response`, decodes SSE events through `decoder`, and forwards them on `tx`.
///
/// UTF-8 safety: transport chunks split at arbitrary byte offsets, so a
/// multi-byte character may be split across two chunks. The `carry` buffer holds
/// any incomplete trailing sequence until the next chunk completes it.
pub(crate) async fn pump<D: ProtocolDecoder>(
    response: reqwest::Response,
    mut decoder: D,
    tx: mpsc::Sender<Result<StreamEvent, LlmError>>,
) {
    let mut body = response.bytes_stream();
    let mut sse = SseDecoder::new();
    let mut carry: Vec<u8> = Vec::new();

    loop {
        // A stalled stream must not hang the agent forever, and a consumer
        // that went away should wake us immediately rather than after the
        // idle timeout.
        let chunk = tokio::select! {
            biased;
            _ = tx.closed() => return,
            next = tokio::time::timeout(STREAM_IDLE_TIMEOUT, body.next()) => match next {
                Ok(Some(Ok(chunk))) => chunk,
                Ok(Some(Err(e))) => {
                    let _ = tx.send(Err(LlmError::Transport(e.to_string()))).await;
                    return;
                }
                Ok(None) => break,
                Err(_) => {
                    let _ = tx.send(Err(LlmError::Transport(format!(
                        "the stream stalled for {}s",
                        STREAM_IDLE_TIMEOUT.as_secs()
                    )))).await;
                    return;
                }
            },
        };

        // Transport chunks split at arbitrary byte offsets, which routinely
        // lands mid-character for any non-ASCII text. Decode only the valid
        // prefix and carry the remainder forward.
        carry.extend_from_slice(&chunk);
        let text = match std::str::from_utf8(&carry) {
            Ok(text) => {
                let text = text.to_string();
                carry.clear();
                text
            }
            Err(error) if error.error_len().is_none() => {
                // A truncated trailing sequence: valid so far, more to come.
                let valid = error.valid_up_to();
                let text = String::from_utf8_lossy(&carry[..valid]).into_owned();
                carry.drain(..valid);
                text
            }
            Err(error) => {
                let _ = tx
                    .send(Err(LlmError::Protocol(format!("invalid UTF-8: {error}"))))
                    .await;
                return;
            }
        };

        for event in sse.push(&text) {
            match decoder.decode(&event.name, &event.data) {
                Ok(decoded) => {
                    for e in decoded {
                        if tx.send(Ok(e)).await.is_err() {
                            return;
                        }
                    }
                }
                Err(error) => {
                    let _ = tx.send(Err(error)).await;
                    return;
                }
            }
        }
    }

    // Bytes left over at EOF are a genuinely truncated character.
    if !carry.is_empty() {
        let _ = tx
            .send(Err(LlmError::Protocol(
                "the stream ended mid-character".into(),
            )))
            .await;
        return;
    }

    // Flush an event left buffered by a stream that ended without a blank line.
    if let Some(event) = sse.finish() {
        if let Ok(decoded) = decoder.decode(&event.name, &event.data) {
            for e in decoded {
                if tx.send(Ok(e)).await.is_err() {
                    return;
                }
            }
        }
    }

    if !decoder.finished() {
        let _ = tx.send(Err(LlmError::IncompleteStream)).await;
    }
}
