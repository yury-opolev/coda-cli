//! The engine seam: requests out, notifications in.
//!
//! Split from the loop because this is the only part of the application that
//! knows a wire protocol exists. Everything above it deals in `UiEvent` and
//! `SurfaceAction`, which is what lets the rest be tested without an engine.

use coda_client::{ClientError, Engine, Inbound, Responder};
use coda_proto::messages::{self, method, server_method};
use coda_proto::Event;
use serde_json::Value;

use super::App;
use crate::browsers::TaskOutcome;
use crate::config;
use crate::state::{PendingPrompt, UiEvent};
use crate::transcript::NoticeLevel;

impl App {
    pub(super) fn on_inbound(&mut self, message: Inbound) {
        match message {
            Inbound::Notification { method, params } => {
                let event = Event::parse(&method, params.as_ref());
                if let Event::TaskCompleted {
                    task_id,
                    status,
                    description,
                    report,
                } = &event
                {
                    self.task_outcomes.insert(
                        task_id.clone(),
                        TaskOutcome {
                            status: status.clone(),
                            description: description.clone(),
                            report: report.clone(),
                        },
                    );
                }
                self.apply(UiEvent::Engine(event));
            }
            Inbound::Request {
                method,
                params,
                responder,
            } => self.on_server_request(&method, params, responder),
        }
    }
    pub(super) fn on_server_request(&mut self, method: &str, params: Option<Value>, responder: Responder) {
        let params = params.unwrap_or(Value::Null);

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
                // Only one prompt can be outstanding; a second would leave the
                // first unanswered and hang the turn.
                if let Some(previous) = self.pending_responder.take() {
                    previous.fail(
                        coda_proto::error_codes::INTERNAL_ERROR,
                        "superseded by another request",
                    );
                    // Retire the superseded surface too. Its exclusivity would
                    // otherwise refuse the replacement, leaving a stale prompt
                    // on screen bound to a responder that has already failed.
                    self.retire_prompt_surface();
                }
                self.pending_responder = Some(responder);
                // The surface is pushed alongside the state so the two cannot
                // disagree about whether a prompt is open. Exclusive modality
                // then guarantees it stays on top and cannot be dismissed
                // without an answer, which the engine is blocked awaiting.
                self.surfaces
                    .push(Box::new(crate::surface::prompt::PromptSurface::new(
                        prompt.clone(),
                    )));
                self.apply(UiEvent::PromptRequested(prompt));
            }
            None => responder.fail(
                coda_proto::error_codes::METHOD_NOT_FOUND,
                format!("{method} is not supported by this client"),
            ),
        }
    }
    /// Issues a request and deserialises its result.
    pub(super) async fn fetch<T: serde::de::DeserializeOwned>(
        &self,
        rpc_method: &str,
        params: Option<Value>,
    ) -> Result<T, ClientError> {
        let value = self.connection.request(rpc_method, params).await?;
        serde_json::from_value(value).map_err(ClientError::Serde)
    }
    /// Applies a permission mode to the running session and persists it.
    ///
    /// Both halves matter and neither is sufficient. Telling the engine makes
    /// the change take effect on the next tool call, which is why this no
    /// longer asks the user to restart. Writing it to settings makes it
    /// survive one. Reporting a failure to persist while the live mode did
    /// change would be the confusing outcome, so both are reported.
    ///
    /// `canonical` is the settings spelling; the engine accepts it as an
    /// alias, so one value serves both.
    pub(super) async fn apply_permission_mode(&mut self, canonical: &str) -> bool {
        let applied = self
            .fetch::<Value>(
                method::SET_PERMISSION_MODE,
                Some(serde_json::json!({ "mode": canonical })),
            )
            .await
            .ok()
            .and_then(|value| value.get("ok").and_then(Value::as_bool))
            .unwrap_or(false);

        if !applied {
            self.notice(
                "The engine did not accept the permission mode; it is unchanged.",
                NoticeLevel::Error,
            );
            return false;
        }

        let paths = self.paths.clone();
        let mode = canonical.to_string();
        let saved = tokio::task::spawn_blocking(move || -> Result<(), config::ConfigError> {
            let mut settings = config::Settings::load(&paths)?;
            settings.set_permission_mode(&mode);
            settings.save()
        })
        .await;

        if !matches!(saved, Ok(Ok(()))) {
            self.notice(
                "Applied for this session, but it could not be saved for the next one.",
                NoticeLevel::Warning,
            );
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
    pub(super) fn answer_prompt(&mut self, allowed: bool, answer: Option<String>) {
        let Some(prompt) = self.state.prompt.clone() else {
            return;
        };
        self.dirty = true;

        if let Some(responder) = self.pending_responder.take() {
            let reply = match &prompt {
                PendingPrompt::Permission { .. } => {
                    serde_json::to_value(messages::PermissionResponse { allow: allowed })
                }
                PendingPrompt::PlanApproval { .. } => {
                    serde_json::to_value(messages::PlanApprovalResponse { approve: allowed })
                }
                PendingPrompt::Question { .. } => {
                    serde_json::to_value(messages::QuestionResponse {
                        answer: answer.clone().unwrap_or_default(),
                    })
                }
            };
            match reply {
                Ok(value) => responder.respond(value),
                Err(error) => responder.fail(
                    coda_proto::error_codes::INTERNAL_ERROR,
                    error.to_string(),
                ),
            }
        }

        self.apply(UiEvent::PromptAnswered { allowed, answer });
    }

    /// Restarts the engine in place, resuming the current session.
    ///
    /// Settings are read once at engine start, so anything that changes them
    /// only takes effect across a restart. `initialize` accepts a session id,
    /// so the conversation survives.
    pub(super) async fn restart_engine(&mut self) {
        let session_id = self.state.session_id.clone();

        let (engine, inbound) = match Engine::spawn(self.engine_command.clone()) {
            Ok(pair) => pair,
            Err(error) => {
                return self.notice(
                    format!("Could not restart the engine: {error}"),
                    NoticeLevel::Error,
                )
            }
        };

        let connection = engine.connection();
        let mut params = messages::InitializeParams::new("coda-tui");
        if let Some(session_id) = session_id {
            params = params.resume(session_id);
        }

        let params = serde_json::to_value(params).unwrap_or_default();
        let mut result = connection.request(method::INITIALIZE, Some(params)).await;

        // A session that has not been written to disk yet cannot be resumed:
        // the engine refuses an id it cannot find, which is right for
        // `--resume` but wrong here. Restarting early in a conversation — a
        // model switch before anything has been saved — then failed outright
        // and the switch quietly did not take. Retry without the id so the
        // restart succeeds, and say what was given up.
        let lost_history = matches!(&result, Err(error) if is_session_not_found(error));
        if lost_history {
            let fresh = serde_json::to_value(messages::InitializeParams::new("coda-tui"))
                .unwrap_or_default();
            result = connection.request(method::INITIALIZE, Some(fresh)).await;
        }

        match result {
            Ok(value) => {
                let initialized: messages::InitializeResult =
                    serde_json::from_value(value).unwrap_or(messages::InitializeResult {
                        protocol_version: coda_proto::PROTOCOL_VERSION.to_string(),
                        session_id: String::new(),
                        server_info: "coda".into(),
                        telemetry_log_path: None,
                    });

                self.connection = connection;
                self.restarted = Some((engine, inbound));
                if !initialized.session_id.is_empty() {
                    self.state.session_id = Some(initialized.session_id);
                }
                if lost_history {
                    self.notice(
                        "Engine restarted, but this session had not been saved yet, so it \
                         starts without the earlier conversation.",
                        NoticeLevel::Warning,
                    );
                } else {
                    self.notice("Engine restarted.", NoticeLevel::Info);
                }
            }
            Err(error) => self.notice(
                format!("The restarted engine rejected the handshake: {error}"),
                NoticeLevel::Error,
            ),
        }
    }
}

/// Whether a failed handshake was the engine refusing an unknown session.
///
/// Singled out because it is the one failure worth retrying: the session is
/// simply not on disk yet, and starting fresh beats leaving the restart
/// undone. Every other error still surfaces as an error.
fn is_session_not_found(error: &coda_client::ClientError) -> bool {
    const SESSION_NOT_FOUND: i64 = -32002;
    matches!(error, coda_client::ClientError::Rpc(e) if e.code == SESSION_NOT_FOUND)
}