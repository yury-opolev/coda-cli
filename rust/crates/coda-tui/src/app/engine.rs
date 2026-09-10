//! The engine seam: requests out, notifications in.
//!
//! Split from the loop because this is the only part of the application that
//! knows a wire protocol exists. Everything above it deals in `UiEvent` and
//! `SurfaceAction`, which is what lets the rest be tested without an engine.

use coda_client::{ClientError, Responder};
use coda_proto::messages::{self, method, server_method};
use serde_json::Value;

use super::App;
use crate::state::{PendingPrompt, UiEvent};
use crate::transcript::NoticeLevel;

pub(super) fn prompt_response(
    prompt: &PendingPrompt,
    allowed: bool,
    answer: Option<&str>,
) -> Result<Value, coda_proto::ResponseError> {
    if matches!(prompt, PendingPrompt::Question { .. }) && !allowed {
        return Err(coda_proto::ResponseError {
            code: coda_proto::error_codes::REQUEST_CANCELLED,
            message: "the operator cancelled the question".into(),
            data: None,
        });
    }
    Ok(match prompt {
        PendingPrompt::Permission { .. } => serde_json::json!({ "allow": allowed }),
        PendingPrompt::PlanApproval { .. } => serde_json::json!({ "approve": allowed }),
        PendingPrompt::Question { .. } => serde_json::json!({ "answer": answer.unwrap_or_default() }),
    })
}

/// The `session/resolveRequest` outcome for a decision, or `None` when the
/// decision is a decline.
///
/// A declined question has no `answer` to send: the out-of-band API rejects
/// an empty one precisely so that "the operator said nothing" cannot be
/// mistaken for "the operator answered with an empty string". Declining goes
/// through `session/cancelRequest`, which applies the typed fail-closed
/// default, exactly as dropping the raw responder would.
pub(super) fn rpc_outcome(
    prompt: &PendingPrompt,
    allowed: bool,
    answer: Option<&str>,
) -> Option<Value> {
    match prompt {
        PendingPrompt::Permission { .. } => Some(serde_json::json!({ "allow": allowed })),
        PendingPrompt::PlanApproval { .. } => Some(serde_json::json!({ "approve": allowed })),
        PendingPrompt::Question { .. } => match (allowed, answer) {
            (true, Some(answer)) if !answer.trim().is_empty() => {
                Some(serde_json::json!({ "answer": answer }))
            }
            _ => None,
        },
    }
}

#[cfg(test)]
mod prompt_response_tests {
    use super::*;

    fn question() -> PendingPrompt {
        PendingPrompt::Question {
            question: "Choose".into(), options: vec!["Delete".into(), "Keep".into()],
            multi_select: false, allow_free_text: true,
        }
    }

    #[test]
    fn deliberate_question_cancel_is_an_explicit_cancel_not_an_empty_answer() {
        for answer in [None, Some("Delete")] {
            let error = prompt_response(&question(), false, answer).unwrap_err();
            assert_eq!(error.code, coda_proto::error_codes::REQUEST_CANCELLED);
        }
    }

    #[test]
    fn accepted_question_and_denied_permission_keep_their_wire_shapes() {
        assert_eq!(prompt_response(&question(), true, Some("Keep")).unwrap(),
            serde_json::json!({"answer":"Keep"}));
        let permission = PendingPrompt::Permission { tool: "edit".into(), preview: "file".into() };
        assert_eq!(prompt_response(&permission, false, None).unwrap(),
            serde_json::json!({"allow":false}));
    }

    #[test]
    fn the_out_of_band_path_declines_a_question_rather_than_sending_an_empty_answer() {
        // `session/resolveRequest` refuses an empty answer on purpose: an
        // empty string is not a decision. A decline must reach the engine as
        // a cancellation, which is what `None` asks the caller to do.
        assert_eq!(rpc_outcome(&question(), false, None), None);
        assert_eq!(rpc_outcome(&question(), false, Some("Delete")), None);
        assert_eq!(rpc_outcome(&question(), true, Some("   ")), None);
        assert_eq!(
            rpc_outcome(&question(), true, Some("Keep")),
            Some(serde_json::json!({ "answer": "Keep" }))
        );
    }

    #[test]
    fn the_out_of_band_path_carries_a_denial_as_a_real_decision() {
        let permission = PendingPrompt::Permission { tool: "edit".into(), preview: "f".into() };
        assert_eq!(
            rpc_outcome(&permission, false, None),
            Some(serde_json::json!({ "allow": false }))
        );
        let plan = PendingPrompt::PlanApproval { plan: "p".into() };
        assert_eq!(rpc_outcome(&plan, true, None), Some(serde_json::json!({ "approve": true })));
    }
}

impl App {
    pub(super) fn on_server_request(&mut self, method: &str, params: Option<Value>, responder: Responder) {
        let params = params.unwrap_or(Value::Null);
        // The engine stamps its own opaque handle on the frame (additive
        // since this contract stage) so the same request discovered through
        // `session/getPendingRequests` can be recognised as *this* request.
        // It is compared, never parsed: reconstructing a handle from the
        // numeric JSON-RPC id would silently answer a different request after
        // an engine restart.
        let handle = params.get("requestId").and_then(Value::as_str).map(str::to_string);

        let prompt = match method {
            server_method::PERMISSION => serde_json::from_value::<messages::PermissionRequest>(
                params,
            )
            .ok()
            .map(|request| PendingPrompt::Permission {
                tool: request.tool_name,
                preview: request.input_preview,
            }),
            server_method::QUESTION => serde_json::from_value::<messages::QuestionRequest>(params)
                .ok()
                .map(|request| PendingPrompt::Question {
                    question: request.question,
                    options: request.options,
                    multi_select: request.multi_select,
                    allow_free_text: request.allow_free_text,
                }),
            server_method::PLAN_APPROVAL => serde_json::from_value::<
                messages::PlanApprovalRequest,
            >(params)
            .ok()
            .map(|request| PendingPrompt::PlanApproval { plan: request.plan }),
            _ => None,
        };

        match prompt {
            Some(prompt) => {
                // One registry for both descriptions of a request. A second
                // concurrent request — a background subagent asking while a
                // foreground turn waits — queues rather than superseding the
                // first: superseding it failed a decision the engine is still
                // blocked on.
                let instance = self.view.engine_instance_id().map(str::to_string);
                self.pending.on_server_request(handle, instance, prompt, responder);
                self.open_current_prompt();
            }
            None => responder.fail(
                coda_proto::error_codes::METHOD_NOT_FOUND,
                format!("{method} is not supported by this client"),
            ),
        }
    }
    /// Issues a request and deserialises its result.
    ///
    /// Refuses without touching the wire when this session is deliberately
    /// disconnected. Every command path that needs the engine goes through
    /// here, so the gate is one place rather than one per caller — and a
    /// closed connection produces the same typed error a live one would,
    /// which is what keeps each caller's own wording intact.
    pub(super) async fn fetch<T: serde::de::DeserializeOwned>(
        &self,
        rpc_method: &str,
        params: Option<Value>,
    ) -> Result<T, ClientError> {
        if !self.engine_connected() {
            return Err(ClientError::ConnectionClosed);
        }
        let value = self.connection.request(rpc_method, params).await?;
        serde_json::from_value(value).map_err(ClientError::Serde)
    }

    /// The gate a command takes *before* it starts anything.
    ///
    /// [`fetch`](Self::fetch) already refuses a disconnected session, but it
    /// refuses with a transport error, which reads as a fault rather than as
    /// the state the user asked for — and a caller that only reaches it after
    /// changing something local has already acted. This says so up front, in
    /// one wording, for every command that needs an engine to mean anything.
    ///
    /// `what` names the command as the user typed it (`/fork`) or what it was
    /// about to do ("Switching the model").
    pub(in crate::app) fn require_engine(&mut self, what: &str) -> bool {
        if self.engine_connected() {
            return true;
        }
        self.notice(
            format!(
                "{what} needs an engine, and this session is disconnected. Run /provider or \
                 /login to connect one."
            ),
            NoticeLevel::Warning,
        );
        self.dirty = true;
        false
    }

    /// A gated, bounded, deserialised engine read.
    ///
    /// The combination every interactive command needs and none of them may
    /// re-invent: refused outright when there is no engine, and given up on
    /// when there is one that has stopped answering. An unbounded await here
    /// happens *inline in the event loop*, so a peer that never replies takes
    /// the keyboard and the redraw with it.
    pub(in crate::app) async fn ask<T: serde::de::DeserializeOwned>(
        &self,
        rpc_method: &str,
        params: Option<Value>,
    ) -> Result<T, super::serve::ReadFailure> {
        self.bounded(self.fetch::<T>(rpc_method, params)).await
    }
    /// Applies a permission mode to the running session and persists it.
    ///
    /// Both halves matter and neither is sufficient. Telling the engine makes
    /// the change take effect on the next tool call, which is why this no
    /// longer asks the user to restart. Writing it to settings makes it
    /// survive one. Reporting a failure to persist while the live mode did
    /// change would be the confusing outcome, so both are reported — and an
    /// API-only session, which cannot write the file the engine reads, says
    /// so rather than writing this machine's copy and calling it saved.
    ///
    /// `canonical` is the settings spelling; the engine accepts it as an
    /// alias, so one value serves both.
    pub(super) async fn apply_permission_mode(&mut self, canonical: &str) -> bool {
        // Three outcomes, not two. "The engine refused" and "the engine never
        // answered" are different facts: only the first justifies telling the
        // user the mode is unchanged, because after a transport failure this
        // client does not know whether the change took.
        let applied = match self
            .fetch::<Value>(
                method::SET_PERMISSION_MODE,
                Some(serde_json::json!({ "mode": canonical })),
            )
            .await
        {
            Ok(value) => value.get("ok").and_then(Value::as_bool).unwrap_or(false),
            Err(error) => {
                self.notice(
                    format!(
                        "The engine did not answer the permission-mode change: {error}. \
                         Whether it took effect is unknown; ask with /permissions."
                    ),
                    NoticeLevel::Error,
                );
                return false;
            }
        };

        if !applied {
            self.notice(
                "The engine did not accept the permission mode; it is unchanged.",
                NoticeLevel::Error,
            );
            return false;
        }

        let mode = canonical.to_string();
        match self.persist_engine_default(move |settings| settings.set_permission_mode(&mode)).await
        {
            super::settings::Saved::Ok => {}
            super::settings::Saved::Refused => {
                let note = self.not_saved_remotely();
                self.notice(format!("Applied for this session.{note}"), NoticeLevel::Info);
            }
            super::settings::Saved::Failed(_) => self.notice(
                "Applied for this session, but it could not be saved for the next one.",
                NoticeLevel::Warning,
            ),
        }
        true
    }
    /// Removes an open prompt surface, wherever it sits in the stack.
    ///
    /// Used when a prompt is superseded or cancelled by the engine rather than
    /// answered by the user: the surface must go even though nothing was
    /// answered, and its own exclusivity would otherwise keep it there.
    pub(super) fn retire_prompt_surface(&mut self) {
        while self
            .surfaces
            .top()
            .is_some_and(|s| s.as_any().is::<crate::surface::prompt::PromptSurface>())
        {
            self.surfaces.pop();
        }
    }
    /// Split from the surface deliberately: the responder is engine state, so
    /// a surface must not hold it. The surface decides what the answer is and
    /// this sends it.
    ///
    /// Delivery is asynchronous now because a request this client did not
    /// receive the raw round-trip for — one discovered after a reconnect —
    /// can only be answered by an RPC. The reducer is advanced first so the
    /// screen never waits on the network to acknowledge a decision the user
    /// has already made.
    pub(super) async fn answer_prompt(&mut self, allowed: bool, answer: Option<String>) {
        let Some(prompt) = self.state.prompt.clone() else {
            return;
        };
        self.dirty = true;
        self.apply(UiEvent::PromptAnswered { allowed, answer: answer.clone() });
        self.deliver_answer(&prompt, allowed, answer.as_deref()).await;
        // A queued decision — a background subagent asking while a foreground
        // turn waits — takes the screen next rather than being forgotten.
        self.open_current_prompt();
    }

    /// Adopts a freshly booted engine as this app's connection.
    ///
    /// Shared by the model/provider restart and by `/resume`, so the two
    /// cannot drift apart on what a new engine process invalidates: a new
    /// process means a new `engineInstanceId`, hence a new event fence, no
    /// outstanding decisions (their responders died with the old connection)
    /// and a conversation that must be re-read rather than assumed.
    ///
    /// Adopting is also what *reconnects* the session, and that is set here
    /// rather than by each caller. It used to be set only on the sign-in path,
    /// so a `/resume` after a failed replacement built a managed engine this
    /// process owned and then refused to talk to it: inbound was left unread,
    /// `fetch` refused, and the only way out was to restart Coda.
    pub(in crate::app) fn adopt_engine(&mut self, booted: crate::api::boot::Booted) {
        let crate::api::boot::Booted {
            engine, inbound, connection, initialize, session_id, ..
        } = booted;

        self.connection = connection;
        // Staged, not swapped: the loop replaces its inbound stream between
        // iterations, so the old instance's frames are never interleaved with
        // the new one's mid-await.
        self.restarted = Some((engine, inbound));
        self.view = crate::api::ServeView::new();
        self.view.on_initialize(&initialize);
        self.pending.clear();
        self.needs_resync = true;
        self.needs_rehydrate = true;
        // A different process reads its own settings and publishes its own
        // catalogue; the previous one's is not evidence about this one.
        self.forget_described_config();
        // A new process is not the old one's outage: whatever the previous
        // engine was failing to answer says nothing about this one, so the
        // retry schedules start over rather than making the first read of a
        // healthy engine wait out a backoff earned by its predecessor.
        self.resync_recovery.on_success();
        self.rehydrate_recovery.on_success();

        if let Some(ctx) = coda_diagnostics::current() {
            crate::diagnostics::record_engine_log_path(
                &ctx.with_session(session_id.clone()),
                initialize.telemetry_log_path.as_deref(),
            );
        }
        if !session_id.is_empty() {
            self.state.session_id = Some(session_id);
        }
        self.engine_log_path = initialize.telemetry_log_path;
        self.header_id_selected = false;
        self.selection.clear();
        self.dragging = false;
        // Last, and once: the engine is initialised and this session is
        // talking to it again.
        self.set_engine_connected(true);
    }

    /// Records whether an engine is answering, and tells the screen.
    ///
    /// One place, because three things have to agree: the loop (which stops
    /// reading a closed channel and stops polling for a recovery that cannot
    /// happen), every command that needs the engine, and the status line. A
    /// caller that set the flag without the event left a green "ready" and a
    /// model name over a session that had just signed out.
    ///
    /// The event is applied even when the flag does not change: adopting a
    /// replacement while already connected is still a different process, with
    /// no turn of its own, and the screen has to say so.
    pub(in crate::app) fn set_engine_connected(&mut self, connected: bool) {
        self.engine_connected = connected;
        self.apply(if connected {
            UiEvent::EngineAdopted
        } else {
            UiEvent::EngineDisconnected
        });
    }

    /// Whether an action that would start an engine may run right now.
    ///
    /// One guard for every non-authentication entry point that boots a child —
    /// `/resume`, the sessions browser, anything added later — because the
    /// window it closes is not obvious from any one of them: a sign-out's
    /// transaction runs on its own task and cannot be cancelled, so between
    /// "the engine was stopped" and "the credential was deleted" the composer
    /// is live and a resume would spawn a child that outlives the credential
    /// it was started with.
    ///
    /// The authentication flow's *own* replacement boot does not come through
    /// here: that one is the transaction's authorized continuation, started
    /// after the commit landed and told explicitly which account to use.
    pub(in crate::app) fn engine_start_allowed(&mut self, what: &str) -> bool {
        if !self.auth.is_active() {
            return true;
        }
        self.notice(
            format!(
                "{what} would start an engine, and a sign-in or sign-out is in progress — so \
                 nothing was started. Wait for it to finish and try again."
            ),
            NoticeLevel::Warning,
        );
        self.dirty = true;
        false
    }

}
