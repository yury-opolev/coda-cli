//! `ask_main` — an asynchronous, accepted-only handoff into the trusted main
//! conversation's own inbox.
//!
//! # Trust model
//! Identity resolution is shared with `notify_user` via
//! [`super::source::resolve_trusted_source`] — no source/task/schedule
//! identity is ever accepted as a tool argument, only derived from the
//! trusted `ToolContext` the engine itself constructed. Unlike `notify_user`,
//! this tool additionally REFUSES a resolved `MessageSource::Main`: main
//! asking main would be a self-question loop with no consumer ever able to
//! answer it, so it fails closed rather than silently accepting it.
//!
//! # What this is, and is not
//! This is an **accepted-only handoff**, never a request/response call:
//! - The caller (a task, subagent, or scheduled run) never waits for an
//!   answer — `execute` returns as soon as the bus takes custody.
//! - The caller never holds a permit, lock, or any other resource while a
//!   question is outstanding; there is nothing to release later.
//! - There is no reply channel back to this specific call. If the main
//!   conversation acts on the request at all, it happens on its own
//!   schedule, at its own next iteration boundary (see
//!   `crate::agent::AgentLoop` step 4c) — never by preempting whatever the
//!   main conversation is already doing.
//! - This is explicitly **not** a new user instruction: the injected text the
//!   main conversation eventually sees (`MainMessage::injected_text`) always
//!   carries that disclaimer verbatim.

use async_trait::async_trait;
use serde::Deserialize;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::message::{AskMainError, MessageSource};
use crate::tool::{Tool, ToolContext, ToolContextServiceExt as _, ToolOutcome, ToolResult};

use super::source::resolve_trusted_source;

#[derive(Debug, Deserialize)]
struct AskMainInput {
    text: String,
    #[serde(default)]
    context: Option<String>,
    #[serde(default)]
    idempotency_key: Option<String>,
}

pub struct AskMainTool;

#[async_trait]
impl Tool for AskMainTool {
    fn name(&self) -> &str {
        "ask_main"
    }

    fn description(&self) -> &str {
        "Send a request into the main conversation's own inbox from a background task, \
         subagent, or scheduled run. This is an asynchronous, ACCEPTED-ONLY handoff: it does \
         NOT wait for a reply, does NOT block this run's own progress while pending, and there \
         is no reply channel back to this call. The main conversation may act on it at its own \
         next iteration boundary — this call never blocks waiting for that, and the request is \
         NOT a new user instruction: it is explicitly a background task request the main agent \
         may choose how (and whether) to act on. Cannot be called from the main context itself."
    }

    fn input_schema_json(&self) -> &str {
        r#"{"type":"object","properties":{"text":{"type":"string","description":"The request body."},"context":{"type":"string","description":"Optional short additional context (kept separate from the body)."},"idempotency_key":{"type":"string","description":"Optional caller-chosen key; reusing it with the same text is a no-op, reusing it with different text is rejected."}},"required":["text"]}"#
    }

    fn is_read_only(&self) -> bool {
        // Never mutates conversation state, tools, or files directly — only
        // the main inbox, an outbox the caller cannot itself observe or act
        // upon. A read-only subagent may still legitimately need to hand a
        // question to the main conversation.
        true
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        let input: AskMainInput = match serde_json::from_value(input.clone()) {
            Ok(i) => i,
            Err(e) => return ToolResult::error(format!("Invalid input: {e}")),
        };

        let Some(bus) = ctx.get_message_bus() else {
            return ToolResult::error("The message bus is not available in this context.");
        };

        let source = match resolve_trusted_source(ctx) {
            Ok(s) => s,
            Err(msg) => return ToolResult::error(msg),
        };

        if matches!(source, MessageSource::Main) {
            return ToolResult::error(
                "ask_main cannot be called from the main context itself — that would be a \
                 self-question loop with nobody able to answer it. This tool is for background \
                 tasks, subagents, or scheduled runs asking the main conversation, not for main \
                 asking itself.",
            );
        }

        match bus.publish_main(&source, input.text, input.context, input.idempotency_key) {
            Ok(receipt) => {
                let note = if receipt.deduplicated {
                    " (deduplicated: an identical request with this key was already accepted)"
                } else {
                    ""
                };
                ToolResult::ok(format!(
                    "Accepted (id={}, seq={}, status={:?}){note}. This only confirms the main \
                     inbox took custody of it for later delivery at its own iteration boundary — \
                     it does NOT confirm the main conversation has read or acted on it, and \
                     there is no reply channel back to this call.",
                    receipt.id, receipt.seq, receipt.status
                ))
            }
            Err(AskMainError::Closed) => {
                ToolResult::error("The message bus is closed; no further requests can be sent.")
            }
            Err(AskMainError::EmptyBody) => ToolResult::error("The request text must not be empty."),
            Err(AskMainError::BodyTooLong) => ToolResult::error(format!(
                "The request text exceeds the {}-character limit.",
                crate::message::MAX_BODY_CHARS
            )),
            Err(AskMainError::ContextTooLong) => ToolResult::error(format!(
                "The optional context exceeds the {}-character limit.",
                crate::message::MAX_CONTEXT_CHARS
            )),
            Err(AskMainError::IdempotencyKeyTooLong) => ToolResult::error(format!(
                "The idempotency_key exceeds the {}-character limit.",
                crate::message::MAX_IDEMPOTENCY_KEY_CHARS
            )),
            Err(AskMainError::IdempotencyConflict) => ToolResult::error(
                "This idempotency key was already used with different request text. Use a new \
                 key, or resend the exact same text to get the original receipt.",
            ),
            Err(AskMainError::QueueFull) => ToolResult::error(format!(
                "The main inbox is full ({} pending requests); try again once the main \
                 conversation has processed some of the pending items.",
                crate::message::MAIN_QUEUE_CAPACITY
            )),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::message::{MessageBus, MAIN_QUEUE_CAPACITY};
    use crate::tasks::{TaskExecutionMode, TaskKind, TaskManager};
    use crate::tool::ScheduleOrigin;
    use serde_json::json;
    use std::sync::Arc;

    fn input(text: &str) -> Value {
        json!({ "text": text })
    }

    #[tokio::test]
    async fn is_read_only_flag_is_true() {
        assert!(AskMainTool.is_read_only());
    }

    #[tokio::test]
    async fn unknown_caller_is_denied_not_promoted_to_main() {
        let bus = Arc::new(MessageBus::new());
        let ctx = ToolContext::new(".").with_message_bus(bus.clone());
        let out = AskMainTool.execute(&input("hello"), &ctx, CancellationToken::new()).await;
        assert!(out.is_error, "{}", out.content);
        assert_eq!(bus.pending_main_len(), 0, "nothing must be queued on a denied call");
    }

    #[tokio::test]
    async fn missing_bus_is_an_explicit_error() {
        let ctx = ToolContext::new(".").with_main_context();
        let out = AskMainTool.execute(&input("hello"), &ctx, CancellationToken::new()).await;
        assert!(out.is_error, "{}", out.content);
        assert!(out.content.contains("not available"), "{}", out.content);
    }

    /// KEY discriminator vs. `notify_user`: a trusted main context is
    /// REFUSED outright — asking main from main would be a self-question
    /// loop nobody can ever answer.
    #[tokio::test]
    async fn main_context_is_refused_no_self_question_loop() {
        let bus = Arc::new(MessageBus::new());
        let ctx = ToolContext::new(".").with_message_bus(bus.clone()).with_main_context();
        let out = AskMainTool.execute(&input("hello from main"), &ctx, CancellationToken::new()).await;
        assert!(out.is_error, "{}", out.content);
        assert!(out.content.contains("self-question"), "{}", out.content);
        assert_eq!(bus.pending_main_len(), 0);
    }

    #[tokio::test]
    async fn scheduled_root_run_is_accepted_with_schedule_attribution() {
        let bus = Arc::new(MessageBus::new());
        let manager = TaskManager::with_defaults("session");
        let task = manager
            .register(TaskKind::Scheduled, "nightly audit run", None, TaskExecutionMode::Background)
            .unwrap();
        let ctx = ToolContext::new(".")
            .with_message_bus(bus.clone())
            .with_task_manager(manager)
            .with_caller_task_id(task.id.clone())
            .with_schedule_origin(ScheduleOrigin::new("def-nightly", Some("nightly audit".into())));

        let out = AskMainTool.execute(&input("please review X"), &ctx, CancellationToken::new()).await;
        assert!(!out.is_error, "{}", out.content);
        assert_eq!(bus.pending_main_len(), 1);
        let drained = bus.take_main_for_delivery();
        assert_eq!(drained[0].source_kind, "scheduledTask");
        assert_eq!(drained[0].label, "nightly audit");
        assert_eq!(drained[0].task_id.as_deref(), Some(task.id.as_str()));
        assert_eq!(drained[0].schedule_definition_id.as_deref(), Some("def-nightly"));
    }

    #[tokio::test]
    async fn scheduled_nested_child_retains_its_own_task_id_not_the_root() {
        let bus = Arc::new(MessageBus::new());
        let manager = TaskManager::with_defaults("session");
        let root = manager
            .register(TaskKind::Scheduled, "nightly audit run", None, TaskExecutionMode::Background)
            .unwrap();
        let child = manager
            .register(TaskKind::Subagent, "nested audit worker", Some(&root.id), TaskExecutionMode::Background)
            .unwrap();

        let ctx = ToolContext::new(".")
            .with_message_bus(bus.clone())
            .with_task_manager(manager)
            .with_caller_task_id(child.id.clone())
            .with_schedule_origin(ScheduleOrigin::new("def-nightly", Some("nightly audit".into())));

        let out = AskMainTool.execute(&input("child of a scheduled root"), &ctx, CancellationToken::new()).await;
        assert!(!out.is_error, "{}", out.content);
        let drained = bus.take_main_for_delivery();
        assert_eq!(drained[0].task_id.as_deref(), Some(child.id.as_str()));
        assert_ne!(drained[0].task_id.as_deref(), Some(root.id.as_str()));
    }

    #[tokio::test]
    async fn nested_subagent_child_is_accepted_with_its_own_task_attribution() {
        let bus = Arc::new(MessageBus::new());
        let manager = TaskManager::with_defaults("session");
        let root = manager
            .register(TaskKind::Subagent, "root task", None, TaskExecutionMode::Background)
            .unwrap();
        let child = manager
            .register(TaskKind::Subagent, "nested child work", Some(&root.id), TaskExecutionMode::Background)
            .unwrap();

        let ctx = ToolContext::new(".")
            .with_message_bus(bus.clone())
            .with_task_manager(manager)
            .with_caller_task_id(child.id.clone());

        let out = AskMainTool.execute(&input("child asks main"), &ctx, CancellationToken::new()).await;
        assert!(!out.is_error, "{}", out.content);
        let drained = bus.take_main_for_delivery();
        assert_eq!(drained[0].source_kind, "subagent");
        assert_eq!(drained[0].label, "nested child work");
    }

    #[tokio::test]
    async fn oversize_and_idempotency_errors_surface_from_the_bus() {
        let bus = Arc::new(MessageBus::new());
        let manager = TaskManager::with_defaults("session");
        let task = manager
            .register(TaskKind::Subagent, "worker", None, TaskExecutionMode::Background)
            .unwrap();
        let ctx = ToolContext::new(".")
            .with_message_bus(bus.clone())
            .with_task_manager(manager)
            .with_caller_task_id(task.id.clone());

        let huge = "x".repeat(crate::message::MAX_BODY_CHARS + 1);
        let out = AskMainTool.execute(&input(&huge), &ctx, CancellationToken::new()).await;
        assert!(out.is_error);

        let out1 = AskMainTool
            .execute(&json!({"text": "same", "idempotency_key": "k"}), &ctx, CancellationToken::new())
            .await;
        assert!(!out1.is_error, "{}", out1.content);
        let out2 = AskMainTool
            .execute(&json!({"text": "different", "idempotency_key": "k"}), &ctx, CancellationToken::new())
            .await;
        assert!(out2.is_error, "{}", out2.content);
    }

    #[tokio::test]
    async fn queue_full_is_explicit_and_preserves_pending() {
        let bus = Arc::new(MessageBus::new());
        let manager = TaskManager::with_defaults("session");
        for i in 0..MAIN_QUEUE_CAPACITY {
            let task = manager
                .register(TaskKind::Subagent, format!("worker {i}"), None, TaskExecutionMode::Background)
                .unwrap();
            bus.publish_main(
                &crate::message::MessageSource::Subagent { task_id: task.id.clone(), label: format!("worker {i}") },
                format!("m{i}"),
                None,
                None,
            )
            .unwrap();
        }
        assert_eq!(bus.pending_main_len(), MAIN_QUEUE_CAPACITY);

        let task = manager
            .register(TaskKind::Subagent, "overflow worker", None, TaskExecutionMode::Background)
            .unwrap();
        let ctx = ToolContext::new(".")
            .with_message_bus(bus.clone())
            .with_task_manager(manager)
            .with_caller_task_id(task.id.clone());

        let out = AskMainTool.execute(&input("one too many"), &ctx, CancellationToken::new()).await;
        assert!(out.is_error, "{}", out.content);
        assert_eq!(bus.pending_main_len(), MAIN_QUEUE_CAPACITY, "no pending item may be lost");
    }
}
