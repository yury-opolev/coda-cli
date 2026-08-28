//! The provider interface the engine programs against.
//!
//! Streaming is exposed as a channel receiver rather than an async iterator so
//! that a client can be driven from a spawned task and cancelled by dropping
//! the receiver, which is what the agent loop needs for interrupts.

use tokio::sync::mpsc;

use crate::anthropic::StreamEvent;
use crate::error::LlmError;
use crate::message::{ChatRequest, ModelInfo};

/// A streaming response.
///
/// Dropping the stream cancels the underlying request.
#[derive(Debug)]
pub struct ResponseStream {
    receiver: mpsc::Receiver<Result<StreamEvent, LlmError>>,
}

impl ResponseStream {
    pub fn new(receiver: mpsc::Receiver<Result<StreamEvent, LlmError>>) -> Self {
        Self { receiver }
    }

    /// Awaits the next event, or `None` when the response is complete.
    pub async fn next(&mut self) -> Option<Result<StreamEvent, LlmError>> {
        self.receiver.recv().await
    }

    /// Drains the whole stream, collecting text and tool calls.
    ///
    /// Convenient for tests and one-shot calls; the agent loop consumes events
    /// incrementally instead.
    pub async fn collect(mut self) -> Result<CompletedResponse, LlmError> {
        let mut response = CompletedResponse::default();

        while let Some(event) = self.next().await {
            match event? {
                StreamEvent::TextDelta(text) => response.text.push_str(&text),
                StreamEvent::ThinkingDelta(text) => response.thinking.push_str(&text),
                StreamEvent::ThinkingDone(block) => response.blocks.push(block),
                StreamEvent::ToolUse(block) => response.blocks.push(block),
                StreamEvent::Done { stop_reason, usage } => {
                    response.stop_reason = stop_reason;
                    response.usage = usage;
                    response.finished = true;
                }
            }
        }

        // A stream that ends without a terminal event was truncated; treating
        // it as a complete answer would silently lose the tail.
        if !response.finished {
            return Err(LlmError::IncompleteStream);
        }
        Ok(response)
    }
}

/// A fully drained response.
#[derive(Debug, Default, Clone, PartialEq)]
pub struct CompletedResponse {
    pub text: String,
    pub thinking: String,
    /// Completed non-text blocks, in arrival order.
    pub blocks: Vec<crate::message::Content>,
    pub stop_reason: Option<String>,
    pub usage: crate::message::Usage,
    finished: bool,
}

impl CompletedResponse {
    pub fn tool_uses(&self) -> impl Iterator<Item = &crate::message::Content> {
        self.blocks
            .iter()
            .filter(|block| matches!(block, crate::message::Content::ToolUse { .. }))
    }
}

/// A chat provider.
#[async_trait::async_trait]
pub trait LlmClient: Send + Sync {
    /// Stable identifier, e.g. `anthropic-api-key` or `github-copilot`.
    fn provider_id(&self) -> &str;

    /// Starts a streaming completion.
    async fn stream(&self, request: ChatRequest) -> Result<ResponseStream, LlmError>;

    /// Lists available models, from cache where the provider allows it.
    async fn list_models(&self) -> Result<Vec<ModelInfo>, LlmError> {
        Ok(Vec::new())
    }

    /// Lists models, bypassing any cache.
    async fn refresh_models(&self) -> Result<Vec<ModelInfo>, LlmError> {
        self.list_models().await
    }

    /// Counts tokens for a request, where the provider offers it.
    async fn count_tokens(&self, _request: &ChatRequest) -> Result<Option<u32>, LlmError> {
        Ok(None)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::message::{Content, Correlation, Usage};

    fn stream_of(events: Vec<Result<StreamEvent, LlmError>>) -> ResponseStream {
        let (tx, rx) = mpsc::channel(32);
        tokio::spawn(async move {
            for event in events {
                if tx.send(event).await.is_err() {
                    break;
                }
            }
        });
        ResponseStream::new(rx)
    }

    fn done() -> StreamEvent {
        StreamEvent::Done {
            stop_reason: Some("end_turn".into()),
            usage: Usage {
                input_tokens: 10,
                output_tokens: 5,
                ..Usage::ZERO
            },
        }
    }

    #[tokio::test]
    async fn collects_text_deltas_in_order() {
        let stream = stream_of(vec![
            Ok(StreamEvent::TextDelta("Hello".into())),
            Ok(StreamEvent::TextDelta(", world".into())),
            Ok(done()),
        ]);

        let response = stream.collect().await.expect("should complete");
        assert_eq!(response.text, "Hello, world");
        assert_eq!(response.stop_reason.as_deref(), Some("end_turn"));
        assert_eq!(response.usage.output_tokens, 5);
    }

    #[tokio::test]
    async fn collects_tool_calls() {
        let tool = Content::ToolUse {
            id: "t1".into(),
            name: "read_file".into(),
            input_json: "{}".into(),
            correlation: Correlation::default(),
        };
        let stream = stream_of(vec![Ok(StreamEvent::ToolUse(tool.clone())), Ok(done())]);

        let response = stream.collect().await.expect("should complete");
        assert_eq!(response.tool_uses().count(), 1);
        assert_eq!(response.blocks[0], tool);
    }

    #[tokio::test]
    async fn separates_thinking_from_answer_text() {
        let stream = stream_of(vec![
            Ok(StreamEvent::ThinkingDelta("reasoning".into())),
            Ok(StreamEvent::TextDelta("answer".into())),
            Ok(done()),
        ]);

        let response = stream.collect().await.expect("should complete");
        assert_eq!(response.thinking, "reasoning");
        assert_eq!(response.text, "answer");
    }

    #[tokio::test]
    async fn a_stream_without_a_terminal_event_is_an_error() {
        let stream = stream_of(vec![Ok(StreamEvent::TextDelta("truncated".into()))]);

        let error = stream
            .collect()
            .await
            .expect_err("a truncated stream must not look complete");
        assert!(matches!(error, LlmError::IncompleteStream));
    }

    #[tokio::test]
    async fn an_error_event_propagates() {
        let stream = stream_of(vec![
            Ok(StreamEvent::TextDelta("partial".into())),
            Err(LlmError::Transport("reset".into())),
        ]);

        let error = stream.collect().await.expect_err("should fail");
        assert!(matches!(error, LlmError::Transport(_)));
    }

    #[tokio::test]
    async fn events_can_be_consumed_incrementally() {
        let mut stream = stream_of(vec![
            Ok(StreamEvent::TextDelta("a".into())),
            Ok(StreamEvent::TextDelta("b".into())),
            Ok(done()),
        ]);

        let mut seen = Vec::new();
        while let Some(event) = stream.next().await {
            seen.push(event.expect("no error"));
        }
        assert_eq!(seen.len(), 3);
    }

    #[tokio::test]
    async fn an_empty_stream_reports_truncation() {
        let stream = stream_of(vec![]);
        assert!(matches!(
            stream.collect().await,
            Err(LlmError::IncompleteStream)
        ));
    }
}
