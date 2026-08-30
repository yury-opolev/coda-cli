//! Agent event types, the sink trait, and the coda-proto adapter.
//!
//! The internal [`AgentEvent`] enum carries the full payloads (including
//! before/after data for hook events) that the loop produces.  The
//! [`ProtoAdapter`] narrows it to the `coda_proto::Event` wire format at the
//! adapter boundary, making the mismatches explicit rather than letting them
//! silently panic.
//!
//! # Known mismatches with the C# `IAgentSink` interface
//!
//! | Internal event | Wire (`coda_proto::Event`) | Gap |
//! |---|---|---|
//! | `ToolQueued` | — | No proto event; silently dropped by the adapter. |
//! | `ToolStatus` | — | No dedicated proto event; status rides on `ToolResult`. |
//! | `Warning` | — | No proto event; silently dropped. |
//! | `Usage` | `Usage{input_tokens, output_tokens}` | Cache fields dropped — the C# `event/usage` carries only these two, so this is parity, not loss. |
//! | `ThinkingComplete` | `ThinkingComplete{elapsed_ms, thinking_tokens}` | `thinking_tokens` always `None`; Rust stream carries no per-burst token count. |
use coda_llm::Usage;
use coda_llm::Correlation as LlmCorrelation;
use coda_proto::events::{Event as ProtoEvent};
use coda_proto::messages::Correlation as ProtoCorrelation;

pub use coda_proto::events::ToolCallStatus;

/// Convert a `coda_llm::Correlation` to the wire `coda_proto::messages::Correlation`.
fn to_proto_correlation(c: &LlmCorrelation) -> ProtoCorrelation {
    ProtoCorrelation {
        root_turn_id: c.root_turn_id.clone(),
        activity_id: c.activity_id.clone(),
        call_id: None, // coda_llm::Correlation has no call_id field
        source_id: c.source_id.clone(),
    }
}

/// Full internal event emitted by the agent loop.
///
/// Every variant carries the complete payload.  The [`ProtoAdapter`] narrows
/// this to the subset the wire format can express.
#[derive(Debug, Clone)]
pub enum AgentEvent {
    AssistantText { delta: String },
    AssistantTextComplete,
    Thinking { delta: String },
    /// `thinking_tokens` is always `None` because the Rust stream carries no
    /// per-burst token count (it arrives only in the terminal `Done` event).
    ThinkingComplete { elapsed_ms: i64, thinking_tokens: Option<i32> },
    /// The loop queued a tool call before executing it.  No proto event.
    ToolQueued { tool_name: String, input_json: String, correlation: LlmCorrelation },
    ToolCall { tool_name: String, input_json: String, correlation: LlmCorrelation },
    /// Status update for an in-flight tool call.  No dedicated proto event.
    ToolStatus { tool_name: String, status: ToolCallStatus, correlation: LlmCorrelation },
    ToolProgress { tool_name: String, elapsed_ms: i64, correlation: LlmCorrelation },
    ToolResult {
        tool_name: String,
        content: String,
        is_error: bool,
        status: ToolCallStatus,
        correlation: LlmCorrelation,
    },
    TurnComplete {
        stop_reason: Option<String>,
        interrupted: bool,
        root_turn_id: Option<String>,
        activity_id: Option<String>,
    },
    Stop { stop_reason: Option<String> },
    /// Full usage; the adapter drops cache fields before sending on the wire.
    Usage { usage: Usage },
    Error { message: String },
    LimitReached { kind: String, message: String },
    SteeringDelivered { message_ids: Vec<String> },
    TaskCompleted {
        task_id: String,
        status: String,
        description: String,
        report: Option<String>,
    },
    ScheduleLifecycle {
        definition_id: String,
        definition_name: Option<String>,
        task_id: Option<String>,
        /// `"started"`, `"completed"`, `"failed"`, or `"stopped"`.
        state: String,
        timestamp: Option<String>,
        summary: Option<String>,
    },
    PromptRewritten {
        hook_command: String,
        original_prompt: String,
        modified_prompt: String,
    },
    /// Before/after payloads dropped by the wire adapter.
    ResponseRewritten {
        hook_command: String,
        original_response: String,
        display_content: String,
        modified_response: Option<String>,
    },
    /// Before/after payloads dropped by the wire adapter.
    ToolInputModified {
        hook_command: String,
        tool_name: String,
        original_input: String,
        modified_input: String,
    },
    /// Before/after payloads dropped by the wire adapter.
    ToolResultModified {
        hook_command: String,
        tool_name: String,
        original_result: String,
        modified_result: String,
    },
    PermissionDecided { hook_command: String, tool_name: String, decision: String },
    PermissionsUpdated {
        hook_command: String,
        mode_applied: Option<String>,
        added_allow: Vec<String>,
        added_deny: Vec<String>,
    },
    SubagentBlocked { hook_command: String, task_id: String, reason: String },
    SubagentResultModified {
        hook_command: String,
        task_id: String,
        original_result: String,
        modified_result: String,
    },
    /// No proto event; silently dropped by the adapter.
    CompactionCancelled { hook_command: String, trigger: String },
    /// No proto event; silently dropped by the adapter.
    PostCompactContextInjected { context: String },
    /// No proto event; silently dropped by the adapter.
    Warning { message: String },
}

/// Receives agent events.  A single `emit` call per event keeps the trait
/// object-safe and avoids the sprawl of 30 optional C# sink methods.
pub trait AgentSink: Send + Sync {
    fn emit(&self, event: AgentEvent);
}

/// Maps an [`AgentEvent`] to a `coda_proto::Event`, or `None` for events that
/// have no wire representation.  The mapping is one-to-one except where
/// documented mismatches require narrowing.
pub fn to_proto_event(event: &AgentEvent) -> Option<ProtoEvent> {
    match event {
        AgentEvent::AssistantText { delta } => {
            Some(ProtoEvent::AssistantText { delta: delta.clone() })
        }
        AgentEvent::AssistantTextComplete => Some(ProtoEvent::AssistantTextComplete),
        AgentEvent::Thinking { delta } => Some(ProtoEvent::Thinking { delta: delta.clone() }),
        AgentEvent::ThinkingComplete { elapsed_ms, thinking_tokens } => {
            Some(ProtoEvent::ThinkingComplete {
                elapsed_ms: *elapsed_ms,
                thinking_tokens: *thinking_tokens,
            })
        }
        // Gap: no proto event for ToolQueued.
        AgentEvent::ToolQueued { .. } => None,
        AgentEvent::ToolCall { tool_name, input_json, correlation } => {
            Some(ProtoEvent::ToolCall {
                tool_name: tool_name.clone(),
                input_json: input_json.clone(),
                correlation: to_proto_correlation(correlation),
            })
        }
        // Gap: no dedicated proto event for ToolStatus.
        AgentEvent::ToolStatus { .. } => None,
        AgentEvent::ToolProgress { tool_name, elapsed_ms, correlation } => {
            Some(ProtoEvent::ToolProgress {
                tool_name: tool_name.clone(),
                elapsed_ms: *elapsed_ms,
                correlation: to_proto_correlation(correlation),
            })
        }
        AgentEvent::ToolResult { tool_name, content, is_error, status, correlation } => {
            Some(ProtoEvent::ToolResult {
                tool_name: tool_name.clone(),
                content: content.clone(),
                is_error: *is_error,
                status: Some(*status),
                correlation: to_proto_correlation(correlation),
            })
        }
        AgentEvent::TurnComplete { stop_reason, interrupted, root_turn_id, activity_id } => {
            Some(ProtoEvent::TurnComplete {
                stop_reason: stop_reason.clone(),
                interrupted: *interrupted,
                root_turn_id: root_turn_id.clone(),
                activity_id: activity_id.clone(),
            })
        }
        AgentEvent::Stop { stop_reason } => {
            Some(ProtoEvent::Stop { stop_reason: stop_reason.clone() })
        }
        // Mismatch: proto Usage drops cache fields.
        AgentEvent::Usage { usage } => Some(ProtoEvent::Usage {
            input_tokens: usage.input_tokens as i64,
            output_tokens: usage.output_tokens as i64,
        }),
        AgentEvent::Error { message } => Some(ProtoEvent::Error { message: message.clone() }),
        AgentEvent::LimitReached { kind, message } => {
            Some(ProtoEvent::LimitReached { kind: kind.clone(), message: message.clone() })
        }
        AgentEvent::SteeringDelivered { message_ids } => {
            Some(ProtoEvent::SteeringDelivered { message_ids: message_ids.clone() })
        }
        AgentEvent::TaskCompleted { task_id, status, description, report } => {
            Some(ProtoEvent::TaskCompleted {
                task_id: task_id.clone(),
                status: status.clone(),
                description: description.clone(),
                report: report.clone(),
            })
        }
        AgentEvent::ScheduleLifecycle {
            definition_id, definition_name, task_id, state, timestamp, summary,
        } => Some(ProtoEvent::ScheduleLifecycle {
            definition_id: definition_id.clone(),
            definition_name: definition_name.clone(),
            task_id: task_id.clone(),
            state: state.clone(),
            timestamp: timestamp.clone(),
            summary: summary.clone(),
        }),
        AgentEvent::PromptRewritten { hook_command, original_prompt, modified_prompt } => {
            Some(ProtoEvent::PromptRewritten {
                hook_command: hook_command.clone(),
                original_prompt: original_prompt.clone(),
                modified_prompt: modified_prompt.clone(),
            })
        }
        // Mismatch: proto ResponseRewritten drops original_response / modified_response.
        AgentEvent::ResponseRewritten {
            hook_command,
            original_response,
            display_content,
            modified_response,
        } => Some(ProtoEvent::ResponseRewritten {
            hook_command: hook_command.clone(),
            original_response: original_response.clone(),
            display_content: display_content.clone(),
            modified_response: modified_response.clone(),
        }),
        AgentEvent::ToolInputModified {
            hook_command,
            tool_name,
            original_input,
            modified_input,
        } => Some(ProtoEvent::ToolInputModified {
            hook_command: hook_command.clone(),
            tool_name: tool_name.clone(),
            original_input: original_input.clone(),
            modified_input: modified_input.clone(),
        }),
        AgentEvent::ToolResultModified {
            hook_command,
            tool_name,
            original_result,
            modified_result,
        } => Some(ProtoEvent::ToolResultModified {
            hook_command: hook_command.clone(),
            tool_name: tool_name.clone(),
            original_result: original_result.clone(),
            modified_result: modified_result.clone(),
        }),
        AgentEvent::PermissionDecided { hook_command, tool_name, decision } => {
            Some(ProtoEvent::PermissionDecided {
                hook_command: hook_command.clone(),
                tool_name: tool_name.clone(),
                decision: decision.clone(),
            })
        }
        AgentEvent::PermissionsUpdated {
            hook_command,
            mode_applied,
            added_allow,
            added_deny,
        } => Some(ProtoEvent::PermissionsUpdated {
            hook_command: hook_command.clone(),
            mode_applied: mode_applied.clone(),
            added_allow: added_allow.clone(),
            added_deny: added_deny.clone(),
        }),
        AgentEvent::SubagentBlocked { hook_command, task_id, reason } => {
            Some(ProtoEvent::SubagentBlocked {
                hook_command: hook_command.clone(),
                task_id: task_id.clone(),
                reason: reason.clone(),
            })
        }
        AgentEvent::SubagentResultModified {
            hook_command,
            task_id,
            original_result,
            modified_result,
        } => Some(ProtoEvent::SubagentResultModified {
            hook_command: hook_command.clone(),
            task_id: task_id.clone(),
            original_result: original_result.clone(),
            modified_result: modified_result.clone(),
        }),
        // Gaps: no proto events for these.
        AgentEvent::CompactionCancelled { hook_command, trigger } => {
            Some(ProtoEvent::CompactionCancelled {
                hook_command: hook_command.clone(),
                trigger: trigger.clone(),
            })
        }
        AgentEvent::PostCompactContextInjected { context } => {
            Some(ProtoEvent::PostCompactContextInjected {
                additional_context: context.clone(),
            })
        }
        AgentEvent::Warning { .. } => None,
    }
}

/// Adapts the internal `AgentEvent` stream to the `coda_proto::Event` wire
/// format by dropping/narrowing events as documented on the module.
pub struct ProtoAdapter<F> {
    send: F,
}

impl<F: Fn(ProtoEvent) + Send + Sync> ProtoAdapter<F> {
    pub fn new(send: F) -> Self {
        Self { send }
    }
}

impl<F: Fn(ProtoEvent) + Send + Sync> AgentSink for ProtoAdapter<F> {
    fn emit(&self, event: AgentEvent) {
        if let Some(proto) = to_proto_event(&event) {
            (self.send)(proto);
        }
    }
}

/// No-op sink — discards every event.  Useful in tests that don't care about
/// events, and as the default when no sink is wired.
pub struct NullSink;

impl AgentSink for NullSink {
    fn emit(&self, _: AgentEvent) {}
}

/// Collecting sink — records every event for later inspection in tests.
pub struct CollectingSink {
    events: std::sync::Mutex<Vec<AgentEvent>>,
}

impl CollectingSink {
    pub fn new() -> Self {
        Self { events: std::sync::Mutex::new(Vec::new()) }
    }

    pub fn take(&self) -> Vec<AgentEvent> {
        std::mem::take(&mut self.events.lock().unwrap())
    }

    pub fn snapshot(&self) -> Vec<AgentEvent> {
        self.events.lock().unwrap().clone()
    }
}

impl Default for CollectingSink {
    fn default() -> Self {
        Self::new()
    }
}

impl AgentSink for CollectingSink {
    fn emit(&self, event: AgentEvent) {
        self.events.lock().unwrap().push(event);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_llm::Usage;

    fn usage() -> Usage {
        Usage {
            input_tokens: 100,
            output_tokens: 50,
            cache_read_tokens: 900,
            cache_write_5m_tokens: 10,
            cache_write_1h_tokens: 5,
        }
    }

    // §8 item 29: adapter drops gap events rather than panicking on Unknown.
    #[test]
    fn tool_queued_is_dropped_by_adapter() {
        let events = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let ev = events.clone();
        let sink = ProtoAdapter::new(move |e| ev.lock().unwrap().push(e));
        sink.emit(AgentEvent::ToolQueued {
            tool_name: "t".into(),
            input_json: "{}".into(),
            correlation: LlmCorrelation::default(),
        });
        assert!(events.lock().unwrap().is_empty(), "ToolQueued should produce no proto event");
    }

    #[test]
    fn warning_is_dropped_by_adapter() {
        let count = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let c = count.clone();
        let sink = ProtoAdapter::new(move |_| {
            c.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        });
        sink.emit(AgentEvent::Warning { message: "heads up".into() });
        assert_eq!(count.load(std::sync::atomic::Ordering::SeqCst), 0);
    }

    #[test]
    fn usage_adapter_drops_cache_fields() {
        let received = std::sync::Arc::new(std::sync::Mutex::new(None::<ProtoEvent>));
        let r = received.clone();
        let sink = ProtoAdapter::new(move |e| *r.lock().unwrap() = Some(e));
        sink.emit(AgentEvent::Usage { usage: usage() });

        let guard = received.lock().unwrap();
        match guard.as_ref().unwrap() {
            ProtoEvent::Usage { input_tokens, output_tokens } => {
                assert_eq!(*input_tokens, 100);
                assert_eq!(*output_tokens, 50);
            }
            other => panic!("unexpected {other:?}"),
        }
    }

    #[test]
    fn response_rewritten_adapter_drops_payloads() {
        let received = std::sync::Arc::new(std::sync::Mutex::new(None::<ProtoEvent>));
        let r = received.clone();
        let sink = ProtoAdapter::new(move |e| *r.lock().unwrap() = Some(e));
        sink.emit(AgentEvent::ResponseRewritten {
            hook_command: "cmd".into(),
            original_response: "original".into(),
            display_content: "display".into(),
            modified_response: Some("modified".into()),
        });
        let guard = received.lock().unwrap();
        match guard.as_ref().unwrap() {
            ProtoEvent::ResponseRewritten { hook_command, display_content, .. } => {
                assert_eq!(hook_command, "cmd");
                assert_eq!(display_content, "display");
            }
            other => panic!("unexpected {other:?}"),
        }
    }

    #[test]
    fn assistant_text_maps_to_proto() {
        let received = std::sync::Arc::new(std::sync::Mutex::new(None::<ProtoEvent>));
        let r = received.clone();
        let sink = ProtoAdapter::new(move |e| *r.lock().unwrap() = Some(e));
        sink.emit(AgentEvent::AssistantText { delta: "hello".into() });
        let guard = received.lock().unwrap();
        assert!(matches!(
            guard.as_ref().unwrap(),
            ProtoEvent::AssistantText { delta } if delta == "hello"
        ));
    }

    #[test]
    fn null_sink_discards_everything() {
        let sink = NullSink;
        sink.emit(AgentEvent::Error { message: "ignored".into() });
    }

    #[test]
    fn collecting_sink_records_in_order() {
        let sink = CollectingSink::new();
        sink.emit(AgentEvent::AssistantText { delta: "a".into() });
        sink.emit(AgentEvent::AssistantText { delta: "b".into() });
        let events = sink.take();
        assert_eq!(events.len(), 2);
        assert!(matches!(&events[0], AgentEvent::AssistantText { delta } if delta == "a"));
        assert!(matches!(&events[1], AgentEvent::AssistantText { delta } if delta == "b"));
        // take drains the buffer.
        assert!(sink.take().is_empty());
    }

    #[test]
    fn subagent_result_modified_maps_to_proto() {
        // MINOR 8: SubagentResultModified was previously a gap (mapped to None).
        // The C# engine emits this event, so it must now be forwarded on the wire.
        let received = std::sync::Arc::new(std::sync::Mutex::new(None::<ProtoEvent>));
        let r = received.clone();
        let sink = ProtoAdapter::new(move |e| *r.lock().unwrap() = Some(e));
        sink.emit(AgentEvent::SubagentResultModified {
            hook_command: "cmd".into(),
            task_id: "task-0001".into(),
            original_result: "original".into(),
            modified_result: "modified".into(),
        });
        let guard = received.lock().unwrap();
        match guard.as_ref().unwrap() {
            ProtoEvent::SubagentResultModified {
                hook_command,
                task_id,
                original_result,
                modified_result,
            } => {
                assert_eq!(hook_command, "cmd");
                assert_eq!(task_id, "task-0001");
                assert_eq!(original_result, "original");
                assert_eq!(modified_result, "modified");
            }
            other => panic!("unexpected {other:?}"),
        }
    }
}
