//! `notify_user` — publish a passive, one-way notification to the user.
//!
//! # Trust model
//! The tool never accepts a source/task/schedule identity as an argument. It
//! resolves the caller's *trusted* identity from `ToolContext` — populated
//! only by the engine's own construction path, never by tool arguments or
//! model output — exactly the same way `schedule_cancel_self` resolves its
//! `ScheduleOrigin`. Precedence:
//!
//! 1. `ctx.schedule_origin` is set → this run was launched by the schedule
//!    runtime (directly or as a nested child of one); the message is
//!    attributed to that scheduled definition.
//! 2. `ctx.caller_task_id` is set and resolves to a real, currently-known
//!    task in `ctx.get_task_manager()` → this run is a subagent; the message
//!    is attributed to that task's own label.
//! 3. `ctx.is_main_context` is `true` (and neither of the above applied) →
//!    the trusted top-level conversation; attributed as `main`.
//! 4. Anything else (no manager wired, an unknown/foreign task id, no main
//!    marker) is refused outright — **fails closed**. A context this tool
//!    cannot positively identify is never silently promoted to `main`.
//!
//! This is deliberately more paranoid than most tools: unlike a read, a
//! wrongly-attributed *notification* would show the user a false label for
//! who is talking to them.

use async_trait::async_trait;
use serde::Deserialize;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::message::PublishError;
use crate::tool::{Tool, ToolContext, ToolContextServiceExt as _, ToolOutcome, ToolResult};

use super::source::resolve_trusted_source;

#[derive(Debug, Deserialize)]
struct NotifyUserInput {
    text: String,
    #[serde(default)]
    context: Option<String>,
    #[serde(default)]
    idempotency_key: Option<String>,
}

pub struct NotifyUserTool;

#[async_trait]
impl Tool for NotifyUserTool {
    fn name(&self) -> &str {
        "notify_user"
    }

    fn description(&self) -> &str {
        "Send a short, passive notification to the user's chat surface. This is a one-way \
         announcement (e.g. \"nightly report ready\") — it does NOT ask a question, does NOT \
         wake or resume the main conversation, and there is no reply channel. Use it sparingly: \
         only for something the user should be told about, not for routine progress chatter. \
         Available to read-only contexts too, since it never mutates anything but the \
         notification outbox."
    }

    fn input_schema_json(&self) -> &str {
        r#"{"type":"object","properties":{"text":{"type":"string","description":"The notification body."},"context":{"type":"string","description":"Optional short additional context (kept separate from the body)."},"idempotency_key":{"type":"string","description":"Optional caller-chosen key; reusing it with the same text is a no-op, reusing it with different text is rejected."}},"required":["text"]}"#
    }

    fn is_read_only(&self) -> bool {
        // A read-only poll subagent may still want to tell the user
        // something; this never mutates conversation state, tools, or files.
        true
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        let input: NotifyUserInput = match serde_json::from_value(input.clone()) {
            Ok(i) => i,
            Err(e) => return ToolResult::error(format!("Invalid input: {e}")),
        };

        let Some(bus) = ctx.get_message_bus() else {
            return ToolResult::error("The notification bus is not available in this context.");
        };

        let source = match resolve_trusted_source(ctx) {
            Ok(s) => s,
            Err(msg) => return ToolResult::error(msg),
        };

        match bus.publish_user(&source, input.text, input.context, input.idempotency_key) {
            Ok(receipt) => {
                let note = if receipt.deduplicated {
                    " (deduplicated: an identical notification with this key was already accepted)"
                } else {
                    ""
                };
                ToolResult::ok(format!(
                    "Notification accepted (id={}, cursor={}){note}. This only confirms the bus \
                     took custody of it — it does not confirm the user has seen it.",
                    receipt.id, receipt.cursor
                ))
            }
            Err(PublishError::Closed) => {
                ToolResult::error("The notification bus is closed; no further notifications can be sent.")
            }
            Err(PublishError::EmptyBody) => ToolResult::error("The notification text must not be empty."),
            Err(PublishError::BodyTooLong) => ToolResult::error(format!(
                "The notification text exceeds the {}-character limit.",
                crate::message::MAX_BODY_CHARS
            )),
            Err(PublishError::ContextTooLong) => ToolResult::error(format!(
                "The optional context exceeds the {}-character limit.",
                crate::message::MAX_CONTEXT_CHARS
            )),
            Err(PublishError::IdempotencyKeyTooLong) => ToolResult::error(format!(
                "The idempotency_key exceeds the {}-character limit.",
                crate::message::MAX_IDEMPOTENCY_KEY_CHARS
            )),
            Err(PublishError::IdempotencyConflict) => ToolResult::error(
                "This idempotency key was already used with different notification text. Use a \
                 new key, or resend the exact same text to get the original receipt.",
            ),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::message::MessageBus;
    use crate::tasks::{TaskExecutionMode, TaskKind, TaskManager};
    use crate::tool::ScheduleOrigin;
    use serde_json::json;
    use std::sync::Arc;

    fn input(text: &str) -> Value {
        json!({ "text": text })
    }

    #[tokio::test]
    async fn unknown_caller_is_denied_not_promoted_to_main() {
        // No schedule_origin, no caller_task_id, no is_main_context: must fail closed.
        let bus = Arc::new(MessageBus::new());
        let ctx = ToolContext::new(".").with_message_bus(bus.clone());
        let out = NotifyUserTool.execute(&input("hello"), &ctx, CancellationToken::new()).await;
        assert!(out.is_error, "{}", out.content);
        assert_eq!(bus.cursor(), 0, "nothing must be published on a denied call");
    }

    #[tokio::test]
    async fn missing_bus_is_an_explicit_error() {
        let ctx = ToolContext::new(".").with_main_context();
        let out = NotifyUserTool.execute(&input("hello"), &ctx, CancellationToken::new()).await;
        assert!(out.is_error, "{}", out.content);
        assert!(out.content.contains("not available"), "{}", out.content);
    }

    #[tokio::test]
    async fn trusted_main_context_publishes_as_main() {
        let bus = Arc::new(MessageBus::new());
        let ctx = ToolContext::new(".").with_message_bus(bus.clone()).with_main_context();
        let out = NotifyUserTool.execute(&input("hello from main"), &ctx, CancellationToken::new()).await;
        assert!(!out.is_error, "{}", out.content);
        let since = bus.user_since(0, None);
        assert_eq!(since.messages.len(), 1);
        assert_eq!(since.messages[0].source_kind, "main");
    }

    #[tokio::test]
    async fn scheduled_root_run_publishes_with_schedule_attribution() {
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

        let out = NotifyUserTool.execute(&input("report ready"), &ctx, CancellationToken::new()).await;
        assert!(!out.is_error, "{}", out.content);
        let since = bus.user_since(0, None);
        assert_eq!(since.messages.len(), 1);
        assert_eq!(since.messages[0].source_kind, "scheduledTask");
        assert_eq!(since.messages[0].label, "nightly audit");
    }

    #[tokio::test]
    async fn scheduled_context_without_a_task_manager_is_denied_not_trusted_on_origin_alone() {
        // `schedule_origin` being set says "this run started under a
        // schedule" — it must never, by itself, be enough to attribute a
        // notification: the caller_task_id still has to be verified.
        let bus = Arc::new(MessageBus::new());
        let ctx = ToolContext::new(".")
            .with_message_bus(bus.clone())
            .with_caller_task_id("task-unverified")
            .with_schedule_origin(ScheduleOrigin::new("def-nightly", Some("nightly audit".into())));

        let out = NotifyUserTool.execute(&input("report ready"), &ctx, CancellationToken::new()).await;
        assert!(out.is_error, "{}", out.content);
        assert_eq!(bus.cursor(), 0, "nothing must be published without a verified task id");
    }

    #[tokio::test]
    async fn scheduled_context_with_unknown_task_id_is_denied() {
        // A manager IS wired, but the claimed caller_task_id does not exist
        // in it — the scheduled branch must reject exactly like the
        // subagent branch does, not bypass verification because
        // `schedule_origin` is set.
        let bus = Arc::new(MessageBus::new());
        let manager = TaskManager::with_defaults("session");
        let ctx = ToolContext::new(".")
            .with_message_bus(bus.clone())
            .with_task_manager(manager)
            .with_caller_task_id("task-does-not-exist")
            .with_schedule_origin(ScheduleOrigin::new("def-nightly", Some("nightly audit".into())));

        let out = NotifyUserTool.execute(&input("report ready"), &ctx, CancellationToken::new()).await;
        assert!(out.is_error, "{}", out.content);
        assert_eq!(bus.cursor(), 0);
    }

    #[tokio::test]
    async fn scheduled_nested_child_retains_its_own_task_id_not_the_root_scheduled_task() {
        // The schedule runtime propagates the SAME `schedule_origin` to a
        // nested child of a scheduled root, but the child has its OWN
        // registered task id — that own id, not the root's, must end up in
        // the published message.
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

        let out = NotifyUserTool.execute(&input("child of a scheduled root"), &ctx, CancellationToken::new()).await;
        assert!(!out.is_error, "{}", out.content);
        let since = bus.user_since(0, None);
        assert_eq!(since.messages.len(), 1);
        assert_eq!(since.messages[0].source_kind, "scheduledTask");
        assert_eq!(since.messages[0].task_id.as_deref(), Some(child.id.as_str()));
        assert_ne!(since.messages[0].task_id.as_deref(), Some(root.id.as_str()));
        assert_eq!(since.messages[0].schedule_definition_id.as_deref(), Some("def-nightly"));
    }

    #[tokio::test]
    async fn nested_subagent_child_publishes_with_its_own_task_attribution_not_root() {
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

        let out = NotifyUserTool.execute(&input("child says hi"), &ctx, CancellationToken::new()).await;
        assert!(!out.is_error, "{}", out.content);
        let since = bus.user_since(0, None);
        assert_eq!(since.messages.len(), 1);
        assert_eq!(since.messages[0].source_kind, "subagent");
        assert_eq!(since.messages[0].label, "nested child work");
    }

    #[tokio::test]
    async fn read_only_flag_is_true_so_read_only_children_retain_the_tool() {
        assert!(NotifyUserTool.is_read_only());
    }

    #[tokio::test]
    async fn unknown_task_id_is_denied() {
        let bus = Arc::new(MessageBus::new());
        let manager = TaskManager::with_defaults("session");
        let ctx = ToolContext::new(".")
            .with_message_bus(bus.clone())
            .with_task_manager(manager)
            .with_caller_task_id("task-does-not-exist");
        let out = NotifyUserTool.execute(&input("hi"), &ctx, CancellationToken::new()).await;
        assert!(out.is_error, "{}", out.content);
        assert_eq!(bus.cursor(), 0);
    }

    #[tokio::test]
    async fn oversize_and_idempotency_errors_surface_from_the_bus() {
        let bus = Arc::new(MessageBus::new());
        let ctx = ToolContext::new(".").with_message_bus(bus.clone()).with_main_context();

        let huge = "x".repeat(crate::message::MAX_BODY_CHARS + 1);
        let out = NotifyUserTool.execute(&input(&huge), &ctx, CancellationToken::new()).await;
        assert!(out.is_error);

        let out1 = NotifyUserTool
            .execute(&json!({"text": "same", "idempotency_key": "k"}), &ctx, CancellationToken::new())
            .await;
        assert!(!out1.is_error, "{}", out1.content);
        let out2 = NotifyUserTool
            .execute(&json!({"text": "different", "idempotency_key": "k"}), &ctx, CancellationToken::new())
            .await;
        assert!(out2.is_error, "{}", out2.content);
    }
}
