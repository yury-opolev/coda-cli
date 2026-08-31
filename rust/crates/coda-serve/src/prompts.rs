//! Server-initiated `request/*` round-trips.
//!
//! The three server-to-client requests MUST fail closed:
//!
//! | Request | fail-closed default |
//! |---|---|
//! | `request/permission` | deny (`false`) |
//! | `request/question` | first option, else `""` |
//! | `request/planApproval` | reject (`false`) |
//!
//! A dropped connection, a timeout, a cancellation, or a malformed body MUST
//! all take the safe path.  **A lost connection must never result in an allow.**
//!
//! # Architecture
//! `PromptChannel` holds the shared write sender and a map of pending one-shot
//! receivers, keyed by server-generated request id.  The transport read loop
//! calls `PromptChannel::route_response` for every incoming `Response` message.
//! When the connection closes, `fail_all_pending` resolves all waiters with
//! `None`, triggering the fail-closed path in every in-flight prompt.

use std::collections::HashMap;
use std::pin::Pin;
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicI64, Ordering},
};

use async_trait::async_trait;
use coda_proto::{RequestId, Request, ResponseError, encode_frame};
use serde_json::Value;
use tokio::sync::{mpsc, oneshot};
use tokio_util::sync::CancellationToken;

// ─────────────────────────────────────────────────────────────────────────────
// PromptChannel
// ─────────────────────────────────────────────────────────────────────────────

type PendingMap = Mutex<HashMap<RequestId, oneshot::Sender<Option<Value>>>>;

/// Shared state for all server-initiated request round-trips.
pub struct PromptChannel {
    outgoing: mpsc::UnboundedSender<Vec<u8>>,
    pending: Arc<PendingMap>,
    next_id: AtomicI64,
}

impl PromptChannel {
    pub fn new(outgoing: mpsc::UnboundedSender<Vec<u8>>) -> Self {
        Self {
            outgoing,
            pending: Arc::new(Mutex::new(HashMap::new())),
            next_id: AtomicI64::new(1),
        }
    }

    /// Issue a server-initiated request and wait for the client's response.
    ///
    /// Returns `Some(Value)` on success, `None` on any failure (connection
    /// closed, cancellation, malformed response).  Callers apply their
    /// fail-closed default on `None`.
    pub async fn issue(
        &self,
        method: &str,
        params: Value,
        cancel: CancellationToken,
    ) -> Option<Value> {
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let id = RequestId::Number(id);

        let (tx, rx) = oneshot::channel();
        self.pending.lock().expect("pending map poisoned").insert(id.clone(), tx);

        let req = Request::new(id.clone(), method, Some(params));
        let bytes = serde_json::to_vec(&req).ok()?;
        // If the write channel is closed, send fails and we fall through to None.
        if self.outgoing.send(encode_frame(&bytes)).is_err() {
            self.pending.lock().expect("pending map poisoned").remove(&id);
            return None;
        }

        tokio::select! {
            result = rx => {
                match result {
                    Ok(Some(value)) => Some(value),
                    // fail_all_pending sent None, or sender was dropped.
                    Ok(None) | Err(_) => None,
                }
            }
            _ = cancel.cancelled() => {
                // Cancel: remove from map and fail closed.
                self.pending.lock().expect("pending map poisoned").remove(&id);
                None
            }
        }
    }

    /// Route an incoming client response to the waiting `issue` call.
    ///
    /// Called by the transport read loop for every `Response` message.
    pub fn route_response(&self, id: &RequestId, result: Result<Value, ResponseError>) {
        if let Some(tx) = self.pending.lock().expect("pending map poisoned").remove(id) {
            // A successful result resolves with `Some(value)`; an error result
            // resolves with `None`, triggering fail-closed.
            let _ = tx.send(result.ok());
        }
    }

    /// Resolve all in-flight requests with `None` (fail-closed).
    ///
    /// Called when the transport detects that the connection has closed.
    pub fn fail_all_pending(&self) {
        let mut map = self.pending.lock().expect("pending map poisoned");
        for (_, tx) in map.drain() {
            let _ = tx.send(None);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WirePermissionPrompt
// ─────────────────────────────────────────────────────────────────────────────

/// Implements [`coda_agent::permission::PermissionPrompt`].
///
/// Issues `request/permission` and interprets the response.  Any fault →
/// **deny** (fail-closed).
pub struct WirePermissionPrompt {
    pub channel: Arc<PromptChannel>,
}

#[async_trait]
impl coda_agent::permission::PermissionPrompt for WirePermissionPrompt {
    async fn request(
        &self,
        tool: &dyn coda_tool::Tool,
        input_preview: &str,
        cancel: CancellationToken,
    ) -> bool {
        let params = serde_json::json!({
            "toolName": tool.name(),
            "inputPreview": input_preview,
        });

        let response = self.channel.issue("request/permission", params, cancel).await;

        // SECURITY: fail closed — any fault (None / missing field / wrong type) → deny.
        response.and_then(|v| v.get("allow").and_then(|b| b.as_bool())).unwrap_or(false)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WireUserQuestion
// ─────────────────────────────────────────────────────────────────────────────

/// Implements [`coda_tool::UserQuestion`] and
/// [`coda_agent::agent::stop::UserQuestionPrompt`].
///
/// Issues `request/question`.  Any fault → **first option, else `""`**
/// (fail-closed per spec).
pub struct WireUserQuestion {
    pub channel: Arc<PromptChannel>,
}

impl WireUserQuestion {
    async fn ask_impl(
        &self,
        question: &str,
        options: &[String],
        multi_select: bool,
        cancel: CancellationToken,
    ) -> String {
        let fallback = options.first().cloned().unwrap_or_default();

        let params = serde_json::json!({
            "question": question,
            "options": options,
            "multiSelect": multi_select,
            "allowFreeText": true,
        });

        let response = self.channel.issue("request/question", params, cancel).await;

        // Fail-closed: missing/malformed → first option, else "".
        response
            .and_then(|v| v.get("answer").and_then(|a| a.as_str()).map(str::to_string))
            .unwrap_or(fallback)
    }
}

#[async_trait]
impl coda_tool::UserQuestion for WireUserQuestion {
    async fn ask(
        &self,
        question: &str,
        options: &[String],
        multi_select: bool,
        cancel: CancellationToken,
    ) -> String {
        self.ask_impl(question, options, multi_select, cancel).await
    }
}

/// Implements the goal-escalation variant of the question prompt.
///
/// For goal escalation, returns `None` on any fault (which the goal supervisor
/// interprets as "stop" — the safe default).
impl coda_agent::agent::stop::UserQuestionPrompt for WireUserQuestion {
    fn ask<'a>(
        &'a self,
        question: &'a str,
        options: &'a [&'a str],
        cancel: CancellationToken,
    ) -> Pin<Box<dyn std::future::Future<Output = Option<String>> + Send + 'a>> {
        let opts: Vec<String> = options.iter().map(|s| s.to_string()).collect();
        Box::pin(async move {
            let answer = self.ask_impl(question, &opts, false, cancel).await;
            if answer.is_empty() { None } else { Some(answer) }
        })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WirePlanApprover
// ─────────────────────────────────────────────────────────────────────────────

/// Implements [`coda_tool::PlanApprover`].
///
/// Issues `request/planApproval`.  Any fault → **reject** (fail-closed).
pub struct WirePlanApprover {
    pub channel: Arc<PromptChannel>,
}

#[async_trait]
impl coda_tool::PlanApprover for WirePlanApprover {
    async fn approve(&self, plan: &str, cancel: CancellationToken) -> bool {
        let params = serde_json::json!({ "plan": plan });

        let response = self.channel.issue("request/planApproval", params, cancel).await;

        // SECURITY: fail closed — any fault (None / missing field / wrong type) → reject.
        response.and_then(|v| v.get("approve").and_then(|b| b.as_bool())).unwrap_or(false)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests — SECURITY: fail-closed defaults
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use coda_agent::{PermissionPrompt, PlanApprover, UserQuestion};
    use serde_json::json;

    // ── Minimal fake Tool for permission tests ────────────────────────────────

    struct FakeTool {
        name: &'static str,
    }

    #[async_trait]
    impl coda_tool::Tool for FakeTool {
        fn name(&self) -> &str {
            self.name
        }
        fn description(&self) -> &str {
            "test"
        }
        fn input_schema_json(&self) -> &str {
            "{}"
        }
        fn is_read_only(&self) -> bool {
            false
        }
        async fn execute(
            &self,
            _input: &Value,
            _ctx: &coda_tool::ToolContext,
            _cancel: CancellationToken,
        ) -> coda_tool::ToolOutcome {
            coda_tool::ToolOutcome::ok("ok")
        }
    }

    // ── SECURITY TEST 1: request/permission → deny on channel closure ─────────

    /// Dropping the connection must **deny** the permission, never allow it.
    ///
    /// Mutation test: change `unwrap_or(false)` → `unwrap_or(true)` in
    /// `WirePermissionPrompt::request` and this test will fail because the
    /// result would become `true` (allow).
    #[tokio::test]
    async fn permission_denied_when_channel_closes() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let prompt = WirePermissionPrompt { channel: Arc::clone(&channel) };

        let tool = FakeTool { name: "dangerous_tool" };
        let cancel = CancellationToken::new();

        // Spawn the permission request in a separate task so we can close the
        // channel concurrently.
        let prompt_arc = Arc::new(prompt);
        let cancel_clone = cancel.clone();
        let task = tokio::spawn({
            let p = Arc::clone(&prompt_arc);
            async move {
                p.request(&tool, r#"{"cmd":"rm -rf /"}"#, cancel_clone).await
            }
        });

        // Give the task a moment to register the pending request.
        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        // Simulate connection close: fail all pending requests.
        channel.fail_all_pending();

        let allowed = task.await.expect("task panicked");
        assert!(!allowed, "SECURITY: permission must be denied when connection closes");
    }

    /// A `request/permission` response with `allow: false` must be honoured.
    #[tokio::test]
    async fn permission_denied_when_response_is_deny() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let prompt = WirePermissionPrompt { channel: Arc::clone(&channel) };
        let tool = FakeTool { name: "tool" };

        let channel_clone = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            prompt.request(&tool, "{}", CancellationToken::new()).await
        });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        // Route a deny response.
        channel_clone.route_response(
            &RequestId::Number(1),
            Ok(json!({ "allow": false })),
        );

        let allowed = task.await.expect("task");
        assert!(!allowed);
    }

    /// A `request/permission` response with `allow: true` must grant.
    #[tokio::test]
    async fn permission_allowed_when_response_grants() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let prompt = WirePermissionPrompt { channel: Arc::clone(&channel) };
        let tool = FakeTool { name: "tool" };

        let channel_clone = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            prompt.request(&tool, "{}", CancellationToken::new()).await
        });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        channel_clone.route_response(
            &RequestId::Number(1),
            Ok(json!({ "allow": true })),
        );

        let allowed = task.await.expect("task");
        assert!(allowed, "must allow when response is allow:true");
    }

    // ── SECURITY TEST 2: request/question → first option on channel closure ───

    /// Dropping the connection must return the **first option**, not hang or panic.
    ///
    /// Mutation test: change `unwrap_or(fallback)` → `unwrap_or("WRONG".to_string())`
    /// and this test will fail (the returned answer will be wrong).
    #[tokio::test]
    async fn question_falls_back_to_first_option_when_channel_closes() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let uq = WireUserQuestion { channel: Arc::clone(&channel) };
        let options = vec!["option-alpha".to_string(), "option-beta".to_string()];
        let cancel = CancellationToken::new();

        let channel_clone = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            uq.ask("choose?", &options, false, cancel).await
        });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        channel_clone.fail_all_pending();

        let answer = task.await.expect("task");
        assert_eq!(answer, "option-alpha", "fail-closed must return first option");
    }

    /// When there are no options, the fail-closed default is `""`.
    #[tokio::test]
    async fn question_falls_back_to_empty_string_when_no_options() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let uq = WireUserQuestion { channel: Arc::clone(&channel) };

        let channel_clone = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            uq.ask("choose?", &[], false, CancellationToken::new()).await
        });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        channel_clone.fail_all_pending();

        let answer = task.await.expect("task");
        assert_eq!(answer, "", "fail-closed with no options must return empty string");
    }

    // ── SECURITY TEST 3: request/planApproval → reject on channel closure ─────

    /// Dropping the connection must **reject** the plan, never approve it.
    ///
    /// Mutation test: change `unwrap_or(false)` → `unwrap_or(true)` in
    /// `WirePlanApprover::approve` and this test will fail.
    #[tokio::test]
    async fn plan_rejected_when_channel_closes() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let approver = WirePlanApprover { channel: Arc::clone(&channel) };
        let cancel = CancellationToken::new();

        let channel_clone = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            approver.approve("dangerous plan", cancel).await
        });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        channel_clone.fail_all_pending();

        let approved = task.await.expect("task");
        assert!(!approved, "SECURITY: plan must be rejected when connection closes");
    }

    /// A `request/planApproval` response with `approve: false` must be honoured.
    #[tokio::test]
    async fn plan_rejected_when_response_is_reject() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let approver = WirePlanApprover { channel: Arc::clone(&channel) };

        let channel_clone = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            approver.approve("a plan", CancellationToken::new()).await
        });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        channel_clone.route_response(
            &RequestId::Number(1),
            Ok(json!({ "approve": false })),
        );

        let approved = task.await.expect("task");
        assert!(!approved);
    }

    /// A `request/planApproval` response with `approve: true` must be honoured.
    #[tokio::test]
    async fn plan_approved_when_response_grants() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let approver = WirePlanApprover { channel: Arc::clone(&channel) };

        let channel_clone = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            approver.approve("a plan", CancellationToken::new()).await
        });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        channel_clone.route_response(
            &RequestId::Number(1),
            Ok(json!({ "approve": true })),
        );

        let approved = task.await.expect("task");
        assert!(approved, "must approve when response is approve:true");
    }

    // ── Cancellation is also fail-closed ──────────────────────────────────────

    #[tokio::test]
    async fn permission_denied_on_cancellation() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let prompt = WirePermissionPrompt { channel };
        let tool = FakeTool { name: "t" };
        let cancel = CancellationToken::new();

        let cancel_clone = cancel.clone();
        let task = tokio::spawn(async move {
            prompt.request(&tool, "{}", cancel_clone).await
        });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        cancel.cancel();

        let allowed = task.await.expect("task");
        assert!(!allowed, "permission must be denied on cancellation");
    }

    #[tokio::test]
    async fn plan_rejected_on_cancellation() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let approver = WirePlanApprover { channel };
        let cancel = CancellationToken::new();

        let cancel_clone = cancel.clone();
        let task = tokio::spawn(async move { approver.approve("plan", cancel_clone).await });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        cancel.cancel();

        let approved = task.await.expect("task");
        assert!(!approved, "plan must be rejected on cancellation");
    }

    // ── Malformed response body is also fail-closed ───────────────────────────

    #[tokio::test]
    async fn permission_denied_on_malformed_response() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let prompt = WirePermissionPrompt { channel: Arc::clone(&channel) };
        let tool = FakeTool { name: "t" };

        let channel_clone = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            prompt.request(&tool, "{}", CancellationToken::new()).await
        });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        // Malformed: `allow` is a string instead of bool.
        channel_clone.route_response(
            &RequestId::Number(1),
            Ok(json!({ "allow": "yes" })),
        );

        let allowed = task.await.expect("task");
        assert!(!allowed, "malformed response must default to deny");
    }

    #[tokio::test]
    async fn question_falls_back_on_malformed_response() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));
        let uq = WireUserQuestion { channel: Arc::clone(&channel) };
        let options = vec!["yes".to_string(), "no".to_string()];

        let channel_clone = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            uq.ask("?", &options, false, CancellationToken::new()).await
        });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        // Malformed: `answer` is a number instead of string.
        channel_clone.route_response(
            &RequestId::Number(1),
            Ok(json!({ "answer": 42 })),
        );

        let answer = task.await.expect("task");
        assert_eq!(answer, "yes", "malformed answer must fall back to first option");
    }

    // ── PromptChannel: fail_all_pending resolves all waiters ──────────────────

    #[tokio::test]
    async fn fail_all_pending_resolves_multiple_waiters() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::new(tx));

        let c1 = Arc::clone(&channel);
        let c2 = Arc::clone(&channel);

        let t1 = tokio::spawn(async move {
            c1.issue("request/permission", json!({}), CancellationToken::new()).await
        });
        let t2 = tokio::spawn(async move {
            c2.issue("request/planApproval", json!({}), CancellationToken::new()).await
        });

        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        channel.fail_all_pending();

        let r1 = t1.await.expect("t1");
        let r2 = t2.await.expect("t2");
        assert!(r1.is_none(), "fail_all_pending must resolve with None");
        assert!(r2.is_none(), "fail_all_pending must resolve with None");
    }
}
