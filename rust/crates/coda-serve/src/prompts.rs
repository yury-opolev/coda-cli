//! Server-initiated `request/*` round-trips.
//!
//! The three server-to-client requests MUST fail closed:
//!
//! | Request | fail-closed default |
//! |---|---|
//! | `request/permission` | deny (`false`) |
//! | `request/question` | **no answer** (`AnswerOutcome::NoAnswer`) |
//! | `request/planApproval` | reject (`false`) |
//!
//! A dropped connection, a timeout, a cancellation, or a malformed body MUST
//! all take the safe path.  **A lost connection must never result in an allow,
//! and must never look like the operator picked the first option.**
//!
//! Until Stage D, a question fault silently substituted `options.first()`.
//! That made "the connection died" indistinguishable from "the user chose
//! option 1" — and because `GOAL_CONTINUE_OPTION` is the first option in the
//! goal-escalation list, it also auto-granted budget extensions nobody asked
//! for. The outcome is now [`AnswerOutcome`], and every caller treats
//! `NoAnswer` as a typed abort.
//!
//! # Architecture
//!
//! `PromptChannel` owns the shared write sender plus the single
//! [`PendingRegistry`], which is the one place a request can be resolved —
//! whether the answer arrives as the ordinary JSON-RPC response to the
//! original request or out of band through `session/resolveRequest` /
//! `session/cancelRequest`.
//!
//! Ordering, per round-trip: **register → publish `event/requestPending` →
//! write the `request/*` frame.** A client can therefore never receive a
//! reverse request for something `session/getPendingRequests` does not
//! already know about.
//!
//! A dropped `issue` future (tool ceiling fired, task aborted) withdraws its
//! entry through an RAII guard, so the registry cannot leak a request nobody
//! is waiting on.

use std::pin::Pin;
use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use coda_proto::state::PendingRequestKind;
use coda_proto::{RequestId, Request, ResponseError, encode_frame};
use coda_tool::{AnswerOutcome, NoAnswerReason};
use serde_json::Value;
use tokio::sync::mpsc;
use tokio_util::sync::CancellationToken;

use crate::state::requests::{
    DEFAULT_DISPLAY_TEXT_CAP, PendingRegistry, RequestOutcome, ResolveError, configured_timeout,
    display_text,
};

// ─────────────────────────────────────────────────────────────────────────────
// PromptChannel
// ─────────────────────────────────────────────────────────────────────────────

/// Shared state for all server-initiated request round-trips.
pub struct PromptChannel {
    outgoing: mpsc::UnboundedSender<Vec<u8>>,
    registry: Arc<PendingRegistry>,
    /// Opt-in only (`CODA_SERVE_REQUEST_TIMEOUT`, default off). Captured once
    /// at construction so a mid-session env mutation cannot change the rules
    /// for requests already in flight.
    timeout: Option<Duration>,
}

impl PromptChannel {
    /// Test/legacy constructor: mints its own instance id.
    ///
    /// Production uses [`PromptChannel::with_instance`] so the handles it
    /// mints are bound to the *same* `engineInstanceId` the event bus and
    /// `session/getState` report.
    pub fn new(outgoing: mpsc::UnboundedSender<Vec<u8>>) -> Self {
        Self::with_instance(outgoing, uuid::Uuid::new_v4().to_string())
    }

    pub fn with_instance(
        outgoing: mpsc::UnboundedSender<Vec<u8>>,
        engine_instance_id: impl Into<String>,
    ) -> Self {
        Self {
            outgoing,
            registry: Arc::new(PendingRegistry::new(engine_instance_id)),
            timeout: configured_timeout(),
        }
    }

    /// Test-only: an explicit timeout, so the opt-in path can be exercised
    /// without mutating process-global environment in a parallel test run.
    #[cfg(test)]
    pub(crate) fn with_timeout_for_test(
        outgoing: mpsc::UnboundedSender<Vec<u8>>,
        engine_instance_id: impl Into<String>,
        timeout: Option<Duration>,
    ) -> Self {
        Self {
            outgoing,
            registry: Arc::new(PendingRegistry::new(engine_instance_id)),
            timeout,
        }
    }

    /// The single pending-request registry — shared with `dispatch` so the
    /// out-of-band RPCs resolve the *same* entries as the raw responses.
    pub fn registry(&self) -> &Arc<PendingRegistry> {
        &self.registry
    }

    /// Issue a server-initiated request and wait for a terminal outcome.
    ///
    /// Always resolves: on connection loss, cancellation, an opt-in timeout or
    /// a malformed reply it returns the kind's fail-closed outcome, tagged
    /// with *why*.
    async fn issue(
        &self,
        method: &str,
        kind: PendingRequestKind,
        display: Value,
        mut params: Value,
        call_id: Option<String>,
        cancel: CancellationToken,
    ) -> RequestOutcome {
        let (numeric_id, handle) = self.registry.mint_id();
        let rx = self.registry.register(numeric_id, kind, display, call_id);
        let mut guard = PendingGuard { registry: &self.registry, numeric_id, armed: true };

        // Additive: the same opaque handle `session/getPendingRequests` lists
        // this request under, so a client that answers the raw round-trip can
        // recognise its own request in the discovery list instead of showing
        // it twice — or dropping the responder, which *declines* it. The
        // client never parses the handle; it only compares it.
        if let Some(object) = params.as_object_mut() {
            object.insert("requestId".to_string(), Value::String(handle));
        }

        let req = Request::new(RequestId::Number(numeric_id), method, Some(params));
        let bytes = match serde_json::to_vec(&req) {
            Ok(b) => b,
            // Unreachable for our own payloads, but never fabricate an answer.
            Err(_) => return guard.resolve_locally(kind, NoAnswerReason::Malformed),
        };
        if self.outgoing.send(encode_frame(&bytes)).is_err() {
            return guard.resolve_locally(kind, NoAnswerReason::Disconnected);
        }

        let timeout = self.timeout;
        let timed_out = async move {
            match timeout {
                Some(d) => tokio::time::sleep(d).await,
                // Never completes: the default really is "wait for the human".
                None => std::future::pending::<()>().await,
            }
        };

        tokio::select! {
            result = rx => {
                guard.armed = false;
                match result {
                    // The registry already published the terminal outcome.
                    Ok(outcome) => outcome,
                    // The sender was dropped without a value: treat as a lost
                    // controller rather than as any kind of grant.
                    Err(_) => RequestOutcome::fail_closed(kind, NoAnswerReason::Disconnected),
                }
            }
            _ = cancel.cancelled() => guard.resolve_locally(kind, NoAnswerReason::Cancelled),
            _ = timed_out => guard.resolve_locally(kind, NoAnswerReason::Timeout),
        }
    }

    /// Route an incoming client response to the waiting `issue` call.
    ///
    /// Called by the transport read loop for every `Response` message. A
    /// reply that does not carry a usable value is a **terminal fault**, not a
    /// retryable one: the client answered, the answer was unusable, so the
    /// kind's fail-closed outcome applies. (`session/resolveRequest` treats a
    /// malformed payload differently — see [`outcome_from_rpc`] — because
    /// there the caller can correct it and try again.)
    pub fn route_response(&self, id: &RequestId, result: Result<Value, ResponseError>) {
        let RequestId::Number(numeric_id) = id else {
            // Reverse-request ids are always numeric; a string id is not one
            // of ours and must not be allowed to resolve anything.
            return;
        };
        let _ = self
            .registry
            .resolve_numeric(*numeric_id, |kind| Ok(parse_wire_response(kind, &result)));
    }

    /// Resolve all in-flight requests fail-closed (connection lost).
    pub fn fail_all_pending(&self) {
        self.registry.fail_all(NoAnswerReason::Disconnected);
    }
}

/// Withdraws a still-pending entry when the `issue` future goes away without
/// a terminal outcome — including when the whole future is dropped (a tool
/// ceiling firing, a cancelled task), where no `select!` branch ever runs.
struct PendingGuard<'a> {
    registry: &'a PendingRegistry,
    numeric_id: i64,
    armed: bool,
}

impl PendingGuard<'_> {
    /// Withdraw and publish the fail-closed outcome for a fault this side of
    /// the wire observed (cancel, timeout, failed write).
    ///
    /// Withdrawal and announcement are one registry call, so no concurrent
    /// registration can be hidden by this one's projection (F4).
    fn resolve_locally(
        &mut self,
        kind: PendingRequestKind,
        reason: NoAnswerReason,
    ) -> RequestOutcome {
        self.armed = false;
        self.registry
            .withdraw_and_publish(self.numeric_id, |_| RequestOutcome::fail_closed(kind, reason))
            .unwrap_or_else(|| RequestOutcome::fail_closed(kind, reason))
    }
}

impl Drop for PendingGuard<'_> {
    fn drop(&mut self) {
        if !self.armed {
            return;
        }
        self.registry.withdraw_and_publish(self.numeric_id, |kind| {
            RequestOutcome::fail_closed(kind, NoAnswerReason::Cancelled)
        });
    }
}

/// Interprets a raw `request/*` response body. Every failure mode is
/// fail-closed and typed.
///
/// # Why a JSON-RPC error is `Declined`, not `Malformed`
///
/// An error reply is the wire signal for "the operator dismissed this" — it is
/// what a controller sends when a prompt is closed with Esc, and what
/// `coda-client`'s `Responder` sends (`REQUEST_CANCELLED`, `-32800`) when it
/// is dropped or explicitly failed. The engine therefore reads it as a
/// **decision**: [`NoAnswerReason::Declined`].
///
/// It is deliberately kept distinct from the two neighbouring facts, because
/// all three are fail-closed but they are not the same story:
///
/// | wire shape | reason | means |
/// |---|---|---|
/// | error reply (any code, incl. `REQUEST_CANCELLED`) | `declined` | a human refused to answer |
/// | `{"answer": ""}` / no `answer` field | `malformed` | the controller tried to answer and produced nothing usable |
/// | EOF / channel closed | `disconnected` | there is no controller any more |
///
/// The distinction is observable: it reaches the client as
/// `event/requestResolved.outcome` (`noAnswer.declined`) and as
/// `lastTurnOutcome.error.category` (`agent.aborted.question.noAnswer.declined`).
/// Collapsing it would make a deliberate refusal indistinguishable from a
/// broken controller.
fn parse_wire_response(
    kind: PendingRequestKind,
    result: &Result<Value, ResponseError>,
) -> RequestOutcome {
    let Ok(value) = result else {
        // The client answered with a JSON-RPC error: an explicit decline.
        // Every code takes this path — the engine never inspects the code to
        // decide whether a refusal counts, and never upgrades one into a
        // grant.
        return RequestOutcome::fail_closed(kind, NoAnswerReason::Declined);
    };
    match kind {
        PendingRequestKind::Permission => RequestOutcome::Permission {
            // SECURITY: any fault (missing field / wrong type) → deny.
            allow: value.get("allow").and_then(Value::as_bool).unwrap_or(false),
        },
        PendingRequestKind::PlanApproval => RequestOutcome::PlanApproval {
            approve: value.get("approve").and_then(Value::as_bool).unwrap_or(false),
        },
        PendingRequestKind::Question => match value.get("answer").and_then(Value::as_str) {
            // An all-whitespace answer is not a decision. It is reported as a
            // malformed reply rather than sent onward as a choice.
            Some(a) if !a.trim().is_empty() => {
                RequestOutcome::Question(AnswerOutcome::Answered(a.to_string()))
            }
            _ => RequestOutcome::Question(AnswerOutcome::NoAnswer(NoAnswerReason::Malformed)),
        },
    }
}

/// Builds a [`RequestOutcome`] from an out-of-band `session/resolveRequest`
/// payload, validating shape **against the pending request's own kind**.
///
/// Unlike [`parse_wire_response`], an unusable payload here is an error the
/// caller can correct: the entry stays outstanding.
pub fn outcome_from_rpc(
    kind: PendingRequestKind,
    outcome: &Value,
) -> Result<RequestOutcome, ResolveError> {
    if let Some(declared) = outcome.get("kind").and_then(Value::as_str) {
        if declared != kind.as_str() {
            return Err(ResolveError::KindMismatch { expected: kind });
        }
    }
    match kind {
        PendingRequestKind::Permission => outcome
            .get("allow")
            .and_then(Value::as_bool)
            .map(|allow| RequestOutcome::Permission { allow })
            .ok_or_else(|| ResolveError::MalformedOutcome {
                detail: "a permission outcome requires a boolean `allow`".into(),
            }),
        PendingRequestKind::PlanApproval => outcome
            .get("approve")
            .and_then(Value::as_bool)
            .map(|approve| RequestOutcome::PlanApproval { approve })
            .ok_or_else(|| ResolveError::MalformedOutcome {
                detail: "a plan-approval outcome requires a boolean `approve`".into(),
            }),
        PendingRequestKind::Question => match outcome.get("answer").and_then(Value::as_str) {
            Some(a) if !a.trim().is_empty() => {
                Ok(RequestOutcome::Question(AnswerOutcome::Answered(a.to_string())))
            }
            Some(_) => Err(ResolveError::MalformedOutcome {
                detail: "an empty answer is not a decision; use session/cancelRequest to decline"
                    .into(),
            }),
            None => Err(ResolveError::MalformedOutcome {
                detail: "a question outcome requires a non-empty string `answer`".into(),
            }),
        },
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
        // The discovery display is bounded: a snapshot carrying a whole tool
        // input verbatim would be unbounded state.
        let display = serde_json::json!({
            "toolName": tool.name(),
            "inputPreview": display_text(input_preview, DEFAULT_DISPLAY_TEXT_CAP),
        });

        let outcome = self
            .channel
            .issue(
                "request/permission",
                PendingRequestKind::Permission,
                display,
                params,
                None,
                cancel,
            )
            .await;

        matches!(outcome, RequestOutcome::Permission { allow: true })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WireUserQuestion
// ─────────────────────────────────────────────────────────────────────────────

/// Implements [`coda_tool::UserQuestion`] and
/// [`coda_agent::agent::stop::UserQuestionPrompt`].
///
/// Issues `request/question`. Any fault → [`AnswerOutcome::NoAnswer`] with a
/// typed reason. **Never** the first option, never an empty string.
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
    ) -> AnswerOutcome {
        let params = serde_json::json!({
            "question": question,
            "options": options,
            "multiSelect": multi_select,
            "allowFreeText": true,
        });
        let display = serde_json::json!({
            "question": display_text(question, DEFAULT_DISPLAY_TEXT_CAP),
            "options": options,
            "multiSelect": multi_select,
            "allowFreeText": true,
        });

        match self
            .channel
            .issue(
                "request/question",
                PendingRequestKind::Question,
                display,
                params,
                None,
                cancel,
            )
            .await
        {
            RequestOutcome::Question(answer) => answer,
            // Structurally impossible (the registry validates kind), but a
            // fallback that invented an answer would be exactly the bug this
            // change exists to remove.
            _ => AnswerOutcome::NoAnswer(NoAnswerReason::Malformed),
        }
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
    ) -> AnswerOutcome {
        self.ask_impl(question, options, multi_select, cancel).await
    }
}

/// Implements the goal-escalation variant of the question prompt.
///
/// Returns the same typed outcome: the goal supervisor grants an extension
/// only for a real answer, never for a fault.
impl coda_agent::agent::stop::UserQuestionPrompt for WireUserQuestion {
    fn ask<'a>(
        &'a self,
        question: &'a str,
        options: &'a [&'a str],
        cancel: CancellationToken,
    ) -> Pin<Box<dyn std::future::Future<Output = AnswerOutcome> + Send + 'a>> {
        let opts: Vec<String> = options.iter().map(|s| s.to_string()).collect();
        Box::pin(async move { self.ask_impl(question, &opts, false, cancel).await })
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
        let display = serde_json::json!({ "plan": display_text(plan, DEFAULT_DISPLAY_TEXT_CAP) });

        let outcome = self
            .channel
            .issue(
                "request/planApproval",
                PendingRequestKind::PlanApproval,
                display,
                params,
                None,
                cancel,
            )
            .await;

        matches!(outcome, RequestOutcome::PlanApproval { approve: true })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests — SECURITY: fail-closed defaults, exactly-once resolution
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use coda_agent::agent::stop::{GOAL_CONTINUE_OPTION, GOAL_STOP_OPTION};
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

    fn channel() -> (Arc<PromptChannel>, mpsc::UnboundedReceiver<Vec<u8>>) {
        let (tx, rx) = mpsc::unbounded_channel::<Vec<u8>>();
        (Arc::new(PromptChannel::with_instance(tx, "engine-test")), rx)
    }

    /// Waits until the engine has actually registered a pending request,
    /// rather than sleeping for an arbitrary interval.
    async fn await_pending(channel: &PromptChannel) -> coda_proto::state::PendingRequestDto {
        for _ in 0..2000 {
            if let Some(dto) = channel.registry().list().into_iter().next() {
                return dto;
            }
            tokio::time::sleep(std::time::Duration::from_millis(1)).await;
        }
        panic!("no request became pending");
    }

    // ── SECURITY TEST 1: request/permission → deny on channel closure ─────────

    /// F7 (review): the `callId` field is reserved and must stay *absent*
    /// rather than becoming an invented or partially-correct correlation
    /// value. The matching capability says so out loud, so a client cannot
    /// build a correlation feature on a field that is never populated.
    #[tokio::test]
    async fn a_pending_request_never_claims_a_tool_call_correlation_it_does_not_have() {
        let (channel, _rx) = channel();
        let prompt = Arc::new(WirePermissionPrompt { channel: Arc::clone(&channel) });
        let tool = FakeTool { name: "dangerous_tool" };
        let task = tokio::spawn({
            let p = Arc::clone(&prompt);
            async move { p.request(&tool, "{}", CancellationToken::new()).await }
        });

        let dto = await_pending(&channel).await;
        assert_eq!(dto.call_id, None, "the seam carries no tool-call id; none may be invented");
        let v = serde_json::to_value(&dto).unwrap();
        assert!(v.get("callId").is_none(), "unavailable must be omitted, never null: {v}");
        assert!(dto.turn_id.is_none() || dto.turn_id.is_some(), "turnId is the correlation offered");

        let catalog = crate::capabilities::capability_catalog();
        let entry = &catalog["requests.callCorrelation"];
        assert!(!entry.supported, "the contract must not promise correlation it cannot deliver");
        assert!(entry.reason.as_deref().is_some_and(|r| r.contains("callId")));

        channel.fail_all_pending();
        assert!(!task.await.expect("task"));
    }

    /// The new discovery surface must not become a side channel for anything
    /// credential-shaped that the request payload happened to contain.
    #[tokio::test]
    async fn a_pending_request_display_never_carries_a_credential_shaped_value() {
        let (channel, _rx) = channel();
        let prompt = Arc::new(WirePermissionPrompt { channel: Arc::clone(&channel) });
        let tool = FakeTool { name: "run_command" };
        let task = tokio::spawn({
            let p = Arc::clone(&prompt);
            async move {
                p.request(&tool, r#"{"cmd":"deploy"}"#, CancellationToken::new()).await
            }
        });

        let dto = await_pending(&channel).await;
        let v = serde_json::to_value(&dto).unwrap();
        for banned in ["signature", "apiKey", "token", "headers", "env", "authorization"] {
            assert!(v.get(banned).is_none(), "{banned} must never appear on a pending request: {v}");
            assert!(
                v["display"].get(banned).is_none(),
                "{banned} must never appear in a pending request display: {v}"
            );
        }

        channel.fail_all_pending();
        assert!(!task.await.expect("task"));
    }

    #[tokio::test]
    async fn permission_denied_when_channel_closes() {
        let (channel, _rx) = channel();
        let prompt = WirePermissionPrompt { channel: Arc::clone(&channel) };
        let tool = FakeTool { name: "dangerous_tool" };

        let task = tokio::spawn({
            let p = Arc::new(prompt);
            async move { p.request(&tool, r#"{"cmd":"rm -rf /"}"#, CancellationToken::new()).await }
        });

        await_pending(&channel).await;
        channel.fail_all_pending();

        assert!(!task.await.expect("task"), "SECURITY: connection loss must deny");
    }

    #[tokio::test]
    async fn permission_denied_when_response_is_deny() {
        let (channel, _rx) = channel();
        let prompt = WirePermissionPrompt { channel: Arc::clone(&channel) };
        let tool = FakeTool { name: "tool" };

        let c = Arc::clone(&channel);
        let task =
            tokio::spawn(async move { prompt.request(&tool, "{}", CancellationToken::new()).await });

        await_pending(&channel).await;
        c.route_response(&RequestId::Number(1), Ok(json!({ "allow": false })));
        assert!(!task.await.expect("task"));
    }

    #[tokio::test]
    async fn permission_allowed_when_response_grants() {
        let (channel, _rx) = channel();
        let prompt = WirePermissionPrompt { channel: Arc::clone(&channel) };
        let tool = FakeTool { name: "tool" };

        let c = Arc::clone(&channel);
        let task =
            tokio::spawn(async move { prompt.request(&tool, "{}", CancellationToken::new()).await });

        await_pending(&channel).await;
        c.route_response(&RequestId::Number(1), Ok(json!({ "allow": true })));
        assert!(task.await.expect("task"), "must allow when response is allow:true");
    }

    /// A reply that arrives twice must not be able to turn a denial into an
    /// allow: the entry is already gone.
    #[tokio::test]
    async fn a_duplicate_permission_reply_cannot_grant_after_a_denial() {
        let (channel, _rx) = channel();
        let prompt = WirePermissionPrompt { channel: Arc::clone(&channel) };
        let tool = FakeTool { name: "tool" };

        let c = Arc::clone(&channel);
        let task =
            tokio::spawn(async move { prompt.request(&tool, "{}", CancellationToken::new()).await });

        await_pending(&channel).await;
        c.route_response(&RequestId::Number(1), Ok(json!({ "allow": false })));
        c.route_response(&RequestId::Number(1), Ok(json!({ "allow": true })));

        assert!(!task.await.expect("task"), "SECURITY: a replayed reply must not grant");
        assert!(channel.registry().is_empty());
    }

    // ── SECURITY TEST 2: request/question → typed NoAnswer, never option 1 ────

    /// The regression this whole change exists for.
    #[tokio::test]
    async fn a_lost_connection_is_no_answer_not_the_first_option() {
        let (channel, _rx) = channel();
        let uq = WireUserQuestion { channel: Arc::clone(&channel) };
        let options = vec!["Delete".to_string(), "Keep".to_string()];

        let task = tokio::spawn(async move {
            uq.ask("Delete the production database?", &options, false, CancellationToken::new())
                .await
        });

        await_pending(&channel).await;
        channel.fail_all_pending();

        assert_eq!(
            task.await.expect("task"),
            AnswerOutcome::NoAnswer(NoAnswerReason::Disconnected),
            "SECURITY: a dropped connection must never read as 'the user chose Delete'"
        );
    }

    #[tokio::test]
    async fn a_cancelled_question_is_no_answer_with_a_cancelled_reason() {
        let (channel, _rx) = channel();
        let uq = WireUserQuestion { channel: Arc::clone(&channel) };
        let cancel = CancellationToken::new();
        let options = vec!["Delete".to_string(), "Keep".to_string()];

        let c = cancel.clone();
        let task = tokio::spawn(async move { uq.ask("q?", &options, false, c).await });

        await_pending(&channel).await;
        cancel.cancel();

        assert_eq!(task.await.expect("task"), AnswerOutcome::NoAnswer(NoAnswerReason::Cancelled));
        assert!(channel.registry().is_empty(), "a cancelled request must not leak");
    }

    #[tokio::test]
    async fn a_malformed_answer_is_no_answer_not_an_empty_success() {
        for body in [json!({}), json!({ "answer": 42 }), json!({ "answer": "   " })] {
            let (channel, _rx) = channel();
            let uq = WireUserQuestion { channel: Arc::clone(&channel) };
            let options = vec!["Delete".to_string(), "Keep".to_string()];

            let c = Arc::clone(&channel);
            let task = tokio::spawn(async move {
                uq.ask("q?", &options, false, CancellationToken::new()).await
            });

            await_pending(&channel).await;
            c.route_response(&RequestId::Number(1), Ok(body.clone()));

            assert_eq!(
                task.await.expect("task"),
                AnswerOutcome::NoAnswer(NoAnswerReason::Malformed),
                "body {body} must not become an answer"
            );
        }
    }

    #[tokio::test]
    async fn an_error_response_is_an_explicit_decline() {
        let (channel, _rx) = channel();
        let uq = WireUserQuestion { channel: Arc::clone(&channel) };
        let options = vec!["Delete".to_string()];

        let c = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            uq.ask("q?", &options, false, CancellationToken::new()).await
        });

        await_pending(&channel).await;
        c.route_response(
            &RequestId::Number(1),
            Err(ResponseError { code: -32000, message: "user closed the prompt".into(), data: None }),
        );

        assert_eq!(task.await.expect("task"), AnswerOutcome::NoAnswer(NoAnswerReason::Declined));
    }

    /// The signal a controller sends when the operator **deliberately**
    /// dismisses a question (Esc in the TUI) is a JSON-RPC error reply with
    /// `REQUEST_CANCELLED`. It must classify as `declined` — an intentional
    /// refusal to answer.
    ///
    /// It must specifically **not** be `malformed` (which is what `{"answer":
    /// ""}` means: the controller tried to answer and produced nothing usable)
    /// and not `disconnected` (which means the controller is gone). All three
    /// are fail-closed, but they are different facts: `declined` is the only
    /// one that says a human made a decision, and it is what
    /// `lastTurnOutcome.error.category` reports back as
    /// `agent.aborted.question.noAnswer.declined`.
    #[tokio::test]
    async fn a_deliberate_cancellation_classifies_as_declined_not_malformed_or_disconnected() {
        let (channel, _rx) = channel();
        let uq = WireUserQuestion { channel: Arc::clone(&channel) };
        let options = vec!["Delete".to_string(), "Keep".to_string()];

        let c = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            uq.ask("Delete the production database?", &options, false, CancellationToken::new())
                .await
        });

        await_pending(&channel).await;
        c.route_response(
            &RequestId::Number(1),
            Err(ResponseError {
                code: coda_proto::jsonrpc::error_codes::REQUEST_CANCELLED,
                message: "request cancelled".into(),
                data: None,
            }),
        );

        let outcome = task.await.expect("task");
        assert_eq!(
            outcome,
            AnswerOutcome::NoAnswer(NoAnswerReason::Declined),
            "a deliberate cancellation is a decision, not a fault"
        );
        assert_ne!(outcome, AnswerOutcome::NoAnswer(NoAnswerReason::Malformed));
        assert_ne!(outcome, AnswerOutcome::NoAnswer(NoAnswerReason::Disconnected));
        assert_ne!(
            outcome,
            AnswerOutcome::Answered("Delete".into()),
            "SECURITY: cancelling must never select the first option"
        );
        assert_ne!(outcome, AnswerOutcome::Answered(String::new()));
    }

    /// The two shapes are deliberately different facts, and the classification
    /// must keep them apart. This is the exact pair the TUI moved between:
    /// it used to reply `{"answer": ""}` on cancel and now sends
    /// `REQUEST_CANCELLED`.
    #[test]
    fn a_cancellation_error_and_an_empty_answer_are_classified_differently() {
        use coda_proto::jsonrpc::error_codes::REQUEST_CANCELLED;

        let cancelled: Result<Value, ResponseError> = Err(ResponseError {
            code: REQUEST_CANCELLED,
            message: "request cancelled".into(),
            data: None,
        });
        assert_eq!(
            parse_wire_response(PendingRequestKind::Question, &cancelled),
            RequestOutcome::Question(AnswerOutcome::NoAnswer(NoAnswerReason::Declined))
        );

        let empty_answer: Result<Value, ResponseError> = Ok(json!({ "answer": "" }));
        assert_eq!(
            parse_wire_response(PendingRequestKind::Question, &empty_answer),
            RequestOutcome::Question(AnswerOutcome::NoAnswer(NoAnswerReason::Malformed)),
            "an empty string is an attempt to answer that produced nothing, not a decision"
        );
    }

    /// The same signal on the other two reverse requests keeps their own
    /// fail-closed defaults: cancelling is never a grant.
    #[test]
    fn a_cancellation_error_denies_a_permission_and_rejects_a_plan() {
        let cancelled: Result<Value, ResponseError> = Err(ResponseError {
            code: coda_proto::jsonrpc::error_codes::REQUEST_CANCELLED,
            message: "request cancelled".into(),
            data: None,
        });
        assert_eq!(
            parse_wire_response(PendingRequestKind::Permission, &cancelled),
            RequestOutcome::Permission { allow: false },
            "SECURITY: a cancelled permission prompt is a deny"
        );
        assert_eq!(
            parse_wire_response(PendingRequestKind::PlanApproval, &cancelled),
            RequestOutcome::PlanApproval { approve: false },
            "SECURITY: a cancelled plan approval is a reject"
        );
    }

    /// A cancellation must survive as a *label* too: `event/requestResolved`
    /// and `lastTurnOutcome` both key off it, and a client distinguishing
    /// "the operator said no" from "the pipe died" reads exactly this string.
    #[test]
    fn a_declined_question_is_labelled_no_answer_declined() {
        let outcome =
            RequestOutcome::Question(AnswerOutcome::NoAnswer(NoAnswerReason::Declined));
        assert_eq!(outcome.label(), "noAnswer.declined");
        assert_ne!(outcome.label(), "noAnswer.malformed");
        assert_ne!(outcome.label(), "noAnswer.disconnected");
    }

    #[tokio::test]
    async fn a_real_answer_is_carried_through_verbatim() {
        let (channel, _rx) = channel();
        let uq = WireUserQuestion { channel: Arc::clone(&channel) };
        let options = vec!["Delete".to_string(), "Keep".to_string()];

        let c = Arc::clone(&channel);
        let task = tokio::spawn(async move {
            uq.ask("q?", &options, false, CancellationToken::new()).await
        });

        await_pending(&channel).await;
        c.route_response(&RequestId::Number(1), Ok(json!({ "answer": "Keep" })));

        assert_eq!(task.await.expect("task"), AnswerOutcome::Answered("Keep".into()));
    }

    /// The goal-escalation seam takes the same path: `GOAL_CONTINUE_OPTION` is
    /// first in the list, so a fault must not silently select it.
    #[tokio::test]
    async fn the_goal_escalation_seam_never_returns_the_continue_option_on_a_fault() {
        let (channel, _rx) = channel();
        let uq = Arc::new(WireUserQuestion { channel: Arc::clone(&channel) });

        let u = Arc::clone(&uq);
        let task = tokio::spawn(async move {
            coda_agent::agent::stop::UserQuestionPrompt::ask(
                u.as_ref(),
                "goal not met — continue?",
                &[GOAL_CONTINUE_OPTION, GOAL_STOP_OPTION],
                CancellationToken::new(),
            )
            .await
        });

        await_pending(&channel).await;
        channel.fail_all_pending();

        let outcome = task.await.expect("task");
        assert_eq!(outcome, AnswerOutcome::NoAnswer(NoAnswerReason::Disconnected));
        assert!(outcome.answered().is_none(), "a fault yields no answer at all");
    }

    // ── Timeout: opt-in only ─────────────────────────────────────────────

    #[tokio::test]
    async fn an_opt_in_timeout_produces_a_typed_timeout_outcome() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::with_timeout_for_test(
            tx,
            "engine-test",
            Some(Duration::from_millis(30)),
        ));
        let uq = WireUserQuestion { channel: Arc::clone(&channel) };
        let options = vec!["Delete".to_string(), "Keep".to_string()];

        let outcome = uq.ask("q?", &options, false, CancellationToken::new()).await;
        assert_eq!(outcome, AnswerOutcome::NoAnswer(NoAnswerReason::Timeout));
        assert!(channel.registry().is_empty(), "a timed-out request must not stay pending");
    }

    /// With no timeout configured (the default), the request simply stays
    /// outstanding — a slow human is not cancelled.
    #[tokio::test]
    async fn with_no_timeout_configured_a_request_waits_indefinitely() {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let channel = Arc::new(PromptChannel::with_timeout_for_test(tx, "engine-test", None));
        let uq = WireUserQuestion { channel: Arc::clone(&channel) };
        let options = vec!["Delete".to_string()];

        let mut task =
            Box::pin(async move { uq.ask("q?", &options, false, CancellationToken::new()).await });
        let raced = tokio::time::timeout(Duration::from_millis(120), &mut task).await;
        assert!(raced.is_err(), "the default must be to keep waiting for the human");
        assert_eq!(channel.registry().list().len(), 1, "and the request stays discoverable");
    }

    // ── RAII: a dropped issue future never leaks a pending entry ─────────

    #[tokio::test]
    async fn dropping_the_request_future_withdraws_the_pending_entry() {
        let (channel, _rx) = channel();
        let uq = WireUserQuestion { channel: Arc::clone(&channel) };
        let options = vec!["Delete".to_string()];

        {
            let mut fut = Box::pin(uq.ask("q?", &options, false, CancellationToken::new()));
            // Drive it far enough to register, then drop it outright — the
            // shape a tool wall-clock ceiling produces.
            let _ = tokio::time::timeout(Duration::from_millis(50), &mut fut).await;
            assert_eq!(channel.registry().list().len(), 1);
        }
        assert!(channel.registry().is_empty(), "a dropped request future must not leak an entry");
    }

    // ── SECURITY TEST 3: request/planApproval → reject on channel closure ─────

    #[tokio::test]
    async fn plan_rejected_when_channel_closes() {
        let (channel, _rx) = channel();
        let approver = WirePlanApprover { channel: Arc::clone(&channel) };

        let task = tokio::spawn(async move {
            approver.approve("dangerous plan", CancellationToken::new()).await
        });
        await_pending(&channel).await;
        channel.fail_all_pending();

        assert!(!task.await.expect("task"), "SECURITY: connection loss must reject");
    }

    #[tokio::test]
    async fn plan_rejected_when_response_is_reject() {
        let (channel, _rx) = channel();
        let approver = WirePlanApprover { channel: Arc::clone(&channel) };

        let c = Arc::clone(&channel);
        let task =
            tokio::spawn(async move { approver.approve("a plan", CancellationToken::new()).await });
        await_pending(&channel).await;
        c.route_response(&RequestId::Number(1), Ok(json!({ "approve": false })));
        assert!(!task.await.expect("task"));
    }

    #[tokio::test]
    async fn plan_approved_when_response_grants() {
        let (channel, _rx) = channel();
        let approver = WirePlanApprover { channel: Arc::clone(&channel) };

        let c = Arc::clone(&channel);
        let task =
            tokio::spawn(async move { approver.approve("a plan", CancellationToken::new()).await });
        await_pending(&channel).await;
        c.route_response(&RequestId::Number(1), Ok(json!({ "approve": true })));
        assert!(task.await.expect("task"), "must approve when response is approve:true");
    }

    #[tokio::test]
    async fn permission_denied_on_cancellation() {
        let (channel, _rx) = channel();
        let prompt = WirePermissionPrompt { channel: Arc::clone(&channel) };
        let tool = FakeTool { name: "t" };
        let cancel = CancellationToken::new();

        let c = cancel.clone();
        let task = tokio::spawn(async move { prompt.request(&tool, "{}", c).await });
        await_pending(&channel).await;
        cancel.cancel();

        assert!(!task.await.expect("task"), "SECURITY: cancellation must deny");
    }

    // ── Discovery payloads are bounded and secret-free ───────────────────

    /// The raw `request/*` frame and `session/getPendingRequests` must
    /// describe the same request in a way a client can *prove*, not guess.
    ///
    /// Without the handle on the frame, a client that answers the raw
    /// round-trip and also polls the discovery list either renders the prompt
    /// twice or drops the original responder — and dropping a responder
    /// declines the request. The handle is opaque: the client compares it, it
    /// never reconstructs it from the numeric JSON-RPC id.
    #[tokio::test]
    async fn the_raw_request_frame_carries_the_same_public_handle_the_registry_lists() {
        let (channel, mut rx) = channel();
        let prompt = Arc::new(WirePermissionPrompt { channel: Arc::clone(&channel) });
        let tool = FakeTool { name: "edit" };
        let task = tokio::spawn({
            let p = Arc::clone(&prompt);
            async move { p.request(&tool, "src/main.rs", CancellationToken::new()).await }
        });

        let dto = await_pending(&channel).await;
        let frame = rx.recv().await.expect("a request frame was written");
        let text = String::from_utf8_lossy(&frame);
        let body = text.split("\r\n\r\n").nth(1).expect("framed body");
        let request: serde_json::Value = serde_json::from_str(body).expect("json body");

        assert_eq!(request["method"], "request/permission");
        assert_eq!(
            request["params"]["requestId"].as_str(),
            Some(dto.request_id.as_str()),
            "the frame must carry the handle the discovery list uses"
        );
        // The pre-existing fields are untouched: this is additive only.
        assert_eq!(request["params"]["toolName"], "edit");
        assert_eq!(request["params"]["inputPreview"], "src/main.rs");
        // The JSON-RPC id stays numeric, exactly as a legacy client expects.
        assert!(request["id"].is_number(), "the raw request id stays numeric");

        channel.fail_all_pending();
        let _ = task.await;
    }

    #[tokio::test]
    async fn a_question_request_carries_its_handle() {
        let (channel, mut rx) = channel();
        let question = Arc::new(WireUserQuestion { channel: Arc::clone(&channel) });
        let task = tokio::spawn({
            let q = Arc::clone(&question);
            async move {
                coda_tool::UserQuestion::ask(
                    q.as_ref(),
                    "Which?",
                    &["a".to_string()],
                    false,
                    CancellationToken::new(),
                )
                .await
            }
        });
        let dto = await_pending(&channel).await;
        let frame = rx.recv().await.expect("frame");
        let text = String::from_utf8_lossy(&frame);
        let request: serde_json::Value =
            serde_json::from_str(text.split("\r\n\r\n").nth(1).expect("body")).expect("json");
        assert_eq!(request["params"]["requestId"].as_str(), Some(dto.request_id.as_str()));
        assert_eq!(request["params"]["question"], "Which?");
        channel.fail_all_pending();
        let _ = task.await;
    }

    #[tokio::test]
    async fn a_plan_approval_request_carries_its_handle() {
        let (channel, mut rx) = channel();
        let approver = Arc::new(WirePlanApprover { channel: Arc::clone(&channel) });
        let task = tokio::spawn({
            let a = Arc::clone(&approver);
            async move { a.approve("the plan", CancellationToken::new()).await }
        });
        let dto = await_pending(&channel).await;
        let frame = rx.recv().await.expect("frame");
        let text = String::from_utf8_lossy(&frame);
        let request: serde_json::Value =
            serde_json::from_str(text.split("\r\n\r\n").nth(1).expect("body")).expect("json");
        assert_eq!(request["params"]["requestId"].as_str(), Some(dto.request_id.as_str()));
        assert_eq!(request["params"]["plan"], "the plan");
        channel.fail_all_pending();
        let _ = task.await;
    }

    #[tokio::test]
    async fn a_pending_permission_is_discoverable_with_a_bounded_preview() {
        let (channel, _rx) = channel();
        let prompt = WirePermissionPrompt { channel: Arc::clone(&channel) };
        let huge = "y".repeat(DEFAULT_DISPLAY_TEXT_CAP + 1000);
        let tool = FakeTool { name: "run_command" };

        let task = tokio::spawn({
            let p = Arc::new(prompt);
            async move { p.request(&tool, &huge, CancellationToken::new()).await }
        });

        let dto = await_pending(&channel).await;
        assert_eq!(dto.kind, PendingRequestKind::Permission);
        assert_eq!(dto.fail_closed_default, "deny");
        assert!(dto.request_id.starts_with("req-engine-test-"));
        assert_eq!(dto.display["toolName"], "run_command");
        assert_eq!(dto.display["inputPreview"]["omittedReason"], "tooLarge");
        assert_eq!(
            dto.display["inputPreview"]["text"].as_str().unwrap().len(),
            DEFAULT_DISPLAY_TEXT_CAP
        );

        channel.fail_all_pending();
        let _ = task.await;
    }

    // ── outcome_from_rpc: kind validation ────────────────────────────────

    #[test]
    fn an_rpc_outcome_declaring_the_wrong_kind_is_refused() {
        let err = outcome_from_rpc(
            PendingRequestKind::Permission,
            &json!({ "kind": "question", "answer": "Delete" }),
        )
        .expect_err("declared kind must be checked");
        assert!(matches!(err, ResolveError::KindMismatch { .. }));
    }

    #[test]
    fn an_rpc_outcome_missing_its_required_field_is_refused_not_defaulted() {
        assert!(matches!(
            outcome_from_rpc(PendingRequestKind::Permission, &json!({})),
            Err(ResolveError::MalformedOutcome { .. })
        ));
        assert!(matches!(
            outcome_from_rpc(PendingRequestKind::PlanApproval, &json!({ "allow": true })),
            Err(ResolveError::MalformedOutcome { .. })
        ));
        assert!(matches!(
            outcome_from_rpc(PendingRequestKind::Question, &json!({ "answer": "" })),
            Err(ResolveError::MalformedOutcome { .. })
        ));
    }

    #[test]
    fn a_wellformed_rpc_outcome_is_accepted_for_each_kind() {
        assert_eq!(
            outcome_from_rpc(PendingRequestKind::Permission, &json!({ "allow": true })).unwrap(),
            RequestOutcome::Permission { allow: true }
        );
        assert_eq!(
            outcome_from_rpc(PendingRequestKind::PlanApproval, &json!({ "approve": false }))
                .unwrap(),
            RequestOutcome::PlanApproval { approve: false }
        );
        assert_eq!(
            outcome_from_rpc(PendingRequestKind::Question, &json!({ "answer": "Keep" })).unwrap(),
            RequestOutcome::Question(AnswerOutcome::Answered("Keep".into()))
        );
    }
}
