//! The agent control loop and its builder.
//!
//! `AgentLoop::run` is the single entry point.  It mutates `history` in place
//! (appending assistant turns, tool-result user turns, and synthetic
//! injections), emits events to `sink`, and returns when the turn completes
//! naturally or due to a limit / cancellation.
//!
//! # Builder
//! `AgentLoopBuilder` constructs the loop from mandatory fields (client,
//! permissions, tools) plus a set of optional extras.  This replaces the C#
//! 20-parameter constructor.
//!
//! # Optional seams
//! Where the loop needs hooks, compaction, subagents, LSP, or task
//! management (later phases), the builder accepts `Option<Arc<…>>` fields and
//! the loop skips those paths when they are absent.

use std::sync::Arc;
use std::time::Duration;

use coda_llm::{
    ChatRequest, Content, Correlation as LlmCorrelation, Effort, Message, Role,
};
use tokio_util::sync::CancellationToken;

use crate::events::{AgentEvent, AgentSink};
use crate::goal::{GoalStatus, GoalSupervisor, last_assistant_text};
use crate::permission::{PermissionMode, PermissionModeState, PermissionPrompt};
use crate::steering::SteeringInbox;
use crate::tool::{ToolQuarantine, ToolRegistry};

pub mod stop;
pub mod stream;
pub mod tools;

use stop::{StopAction, UserQuestionPrompt, decide_stop};
use stream::{RetryConfig, stream_with_retries};
use tools::{BatchContext, ToolBatchResult, run_tools};

// ─────────────────────────────────────────────────────────────────────────────
// Error type
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, thiserror::Error)]
pub enum AgentError {
    #[error("operation cancelled")]
    Cancelled,
    #[error("LLM error: {0}")]
    Llm(#[from] coda_llm::LlmError),
    #[error("{0}")]
    Other(#[from] anyhow::Error),
}

// ─────────────────────────────────────────────────────────────────────────────
// Correlation / activity tracking
// ─────────────────────────────────────────────────────────────────────────────

/// Generates correlation IDs for a tool-call batch, stamped on each
/// `Content::ToolUse` before it is appended to history.
pub(crate) struct ToolActivity {
    root_turn_id: String,
    activity_id: String,
}

impl ToolActivity {
    pub fn new() -> Self {
        Self {
            root_turn_id: uuid::Uuid::new_v4().to_string(),
            activity_id: uuid::Uuid::new_v4().to_string(),
        }
    }

    pub fn for_call(&self, call_id: &str) -> LlmCorrelation {
        LlmCorrelation {
            root_turn_id: Some(self.root_turn_id.clone()),
            activity_id: Some(self.activity_id.clone()),
            source_id: Some(call_id.to_owned()),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AgentLoop
// ─────────────────────────────────────────────────────────────────────────────

/// The immutable configuration of an agent run loop.
///
/// Build with [`AgentLoopBuilder`]; call [`AgentLoop::run`] to start a turn.
pub struct AgentLoop {
    // Core services (always present).
    client: Arc<dyn coda_llm::LlmClient>,
    permission_prompt: Arc<dyn PermissionPrompt>,
    tools: Arc<ToolRegistry>,
    quarantine: Arc<ToolQuarantine>,

    // Permissions.
    permission_mode: PermissionMode,
    permission_mode_state: Option<Arc<PermissionModeState>>,

    // Optional services.
    steering: Option<Arc<SteeringInbox>>,
    // Seam: user-question prompt (later phase).
    user_question: Option<Arc<dyn UserQuestionPrompt>>,

    // Configuration.
    model: String,
    system_prompt: Option<String>,
    max_tokens: u32,
    max_iterations: usize,
    effort: Option<Effort>,
    working_directory: String,
    granted_directories: Option<std::collections::HashSet<String>>,

    // Tuning knobs.
    tool_max_duration: Option<Duration>,
    tool_progress_interval: Duration,
    max_transport_retries: u32,
    max_schema_evictions: u32,
    /// Seam for stop hooks (later phase).
    #[allow(dead_code)]
    max_stop_continuations: u32,
}

impl AgentLoop {
    /// Begin an agent turn.
    ///
    /// - `history` is mutated in place (assistant turns, tool results, and
    ///   synthetic user messages are appended).
    /// - `goal` is owned by this call; the returned [`GoalStatus`] reflects the
    ///   final state when a goal was active.
    /// - Returns `Err(AgentError::Cancelled)` only for the **caller** cancel;
    ///   tool-ceiling timeouts are contained as error results.
    pub async fn run(
        &self,
        history: &mut Vec<Message>,
        sink: &dyn AgentSink,
        goal: Option<GoalSupervisor>,
        cancel: CancellationToken,
    ) -> Result<GoalStatus, AgentError> {
        // Pending post-sampling hook tasks — drained on EVERY exit path
        // (§8 item 27).  The inner function returns via the single exit point
        // below, which always drains before returning.
        let mut pending_hook_tasks: Vec<tokio::task::JoinHandle<()>> = Vec::new();
        let result =
            self.execute_loop(history, sink, goal, cancel, &mut pending_hook_tasks).await;
        // §8 item 27: always drain, regardless of Ok/Err.
        for handle in pending_hook_tasks {
            let _ = handle.await;
        }
        result
    }

    async fn execute_loop(
        &self,
        history: &mut Vec<Message>,
        sink: &dyn AgentSink,
        mut goal: Option<GoalSupervisor>,
        cancel: CancellationToken,
        _pending_hook_tasks: &mut Vec<tokio::task::JoinHandle<()>>,
    ) -> Result<GoalStatus, AgentError> {
        let mut stop_continuations: u32 = 0;
        let mut activity = ToolActivity::new();
        let mut blocked_compaction_at: Option<usize> = None;

        let retry_cfg = RetryConfig {
            max_transport_retries: self.max_transport_retries,
            max_schema_evictions: self.max_schema_evictions,
        };

        for iteration in 0_usize.. {
            // 1. Cooperative pause gate (no-op seam; later phase adds the gate).

            // 2. Iteration bound check (non-goal only, §1.2).
            if goal.is_none() && iteration >= self.max_iterations {
                break;
            }

            // 3. Proactive compaction (goal runs only; no-op seam, later phase).

            // 4a. LSP diagnostics injection (no-op seam; later phase).
            // Only after the first tool cycle (iteration > 0).

            // 4b. Steering injection (§1.2 step 4).
            if let Some(steering) = &self.steering {
                let steers = steering.take_all_for_delivery();
                if !steers.is_empty() {
                    let text = steers
                        .iter()
                        .map(|e| e.text.as_str())
                        .collect::<Vec<_>>()
                        .join("\n\n");
                    history.push(Message::user(text));
                    sink.emit(AgentEvent::SteeringDelivered {
                        message_ids: steers.iter().map(|e| e.id.clone()).collect(),
                    });
                }
            }

            // 4c. Task-completion injection (no-op seam; later phase).
            // 4d. Deferred-tools reminder (no-op seam; later phase).

            // 5. Wire tool definitions.
            let tool_defs = self.quarantine.filter(self.tools.definitions());

            // 6. Build ChatRequest.
            let mut request = ChatRequest::new(self.model.clone(), history.clone())
                .with_max_tokens(self.max_tokens)
                .with_effort(self.effort)
                .with_tools(tool_defs.clone());
            if let Some(sys) = &self.system_prompt {
                request = request.with_system(sys.clone());
            }

            // 7. Stream with retry arms.
            let mut acc = stream_with_retries(
                &*self.client,
                &mut request,
                &self.quarantine,
                sink,
                cancel.clone(),
                &retry_cfg,
                None, // compaction seam (later phase)
                &mut blocked_compaction_at,
            )
            .await
            .map_err(AgentError::Llm)?;

            // 8. Stamp correlation on tool_use blocks, then assemble assistant message.
            if !acc.tool_uses.is_empty() {
                activity = ToolActivity::new(); // fresh activity per tool batch
            }
            for block in &mut acc.tool_uses {
                if let Content::ToolUse { id, correlation, .. } = block {
                    *correlation = activity.for_call(id);
                }
            }

            // §3 strict block order: redacted-thinking → signed-thinking → text → tool_use.
            // (Unsigned thinking blocks are collected but filtered out here.)
            let mut assistant_content: Vec<Content> = Vec::new();
            for block in acc.redacted_thinking_blocks.drain(..) {
                assistant_content.push(block);
            }
            for block in acc.thinking_blocks.drain(..) {
                // Skip unsigned thinking blocks (§1.4: "skip unsigned, `:829`").
                if matches!(&block, Content::Thinking { signature: Some(_), .. }) {
                    assistant_content.push(block);
                }
            }
            if !acc.text.is_empty() {
                assistant_content.push(Content::Text(acc.text.clone()));
            }
            for block in acc.tool_uses.drain(..) {
                assistant_content.push(block);
            }

            history.push(Message::new(Role::Assistant, assistant_content));

            // §8 item 28: persist after assistant turn commit (seam; no-op here).

            // 9. Fire post-sampling hooks (no-op seam; no tasks spawned here).
            // Later phase: pending_hook_tasks.extend(hooks.fire_post_sampling(...));

            // 10. Stop decision vs. tool execution.
            // Drive off PRESENCE of tool calls, not stop_reason (§8 item 2).
            let tool_uses_in_history: Vec<Content> = history
                .last()
                .iter()
                .flat_map(|m| m.content.iter())
                .filter(|b| matches!(b, Content::ToolUse { .. }))
                .cloned()
                .collect();

            if tool_uses_in_history.is_empty() {
                // No tool calls → stop-decision ladder (§1.5).
                let recent_text = last_assistant_text(history);

                let action = decide_stop(
                    acc.stop_reason.as_deref(),
                    &recent_text,
                    &mut goal,
                    &mut stop_continuations,
                    self.steering.as_deref(),
                    sink,
                    cancel.clone(),
                    self.user_question.as_deref(),
                )
                .await
                .map_err(|_| AgentError::Cancelled)?;

                match action {
                    StopAction::Continue { nudge } => {
                        if !nudge.is_empty() {
                            history.push(Message::user(nudge));
                        }
                        continue;
                    }
                    StopAction::Stop => {
                        // Emit Stop + optional LimitReached.
                        sink.emit(AgentEvent::Stop {
                            stop_reason: acc.stop_reason.clone(),
                        });
                        if acc.stop_reason.as_deref() == Some("max_tokens") {
                            sink.emit(AgentEvent::LimitReached {
                                kind: "max_tokens".into(),
                                message: "The response was truncated (max_tokens reached).".into(),
                            });
                        }
                        return Ok(goal_status(&goal));
                    }
                }
            } else {
                // Tool calls present → run them serially (§1.6).
                let batch_ctx = BatchContext {
                    tools: &*self.tools,
                    permission_prompt: &*self.permission_prompt,
                    permission_mode: self.permission_mode,
                    permission_mode_state: self.permission_mode_state.as_deref(),
                    steering: self.steering.as_deref(),
                    working_directory: &self.working_directory,
                    granted_directories: self.granted_directories.as_ref(),
                    tool_max_duration: self.tool_max_duration,
                    tool_progress_interval: self.tool_progress_interval,
                };

                let ToolBatchResult { result_blocks, abort_reason } =
                    run_tools(&tool_uses_in_history, &activity, sink, &batch_ctx, cancel.clone())
                        .await?;

                history.push(Message::new(Role::User, result_blocks));

                // §8 item 28: persist after tool results (seam; no-op here).

                if let Some(reason) = abort_reason {
                    // PreToolUse hook returned continue:false (§1.5).
                    sink.emit(AgentEvent::Stop {
                        stop_reason: Some("hook_abort".into()),
                    });
                    sink.emit(AgentEvent::LimitReached {
                        kind: "hook_abort".into(),
                        message: format!("A hook stopped the run: {reason}"),
                    });
                    return Ok(goal_status(&goal));
                }
                // Loop continues for the next sampling iteration.
            }
        }

        // Non-goal path only: max_iterations hit (§8 item 25).
        history.push(Message::assistant(
            "(stopped: reached the maximum tool iterations)",
        ));
        sink.emit(AgentEvent::LimitReached {
            kind: "max_tool_iterations".into(),
            message: format!(
                "Reached the maximum of {} tool iterations.",
                self.max_iterations
            ),
        });
        Ok(goal_status(&goal))
    }
}

fn goal_status(goal: &Option<GoalSupervisor>) -> GoalStatus {
    goal.as_ref().map(|g| g.status()).unwrap_or_else(GoalStatus::none)
}

// ─────────────────────────────────────────────────────────────────────────────
// Builder
// ─────────────────────────────────────────────────────────────────────────────

/// Builder for [`AgentLoop`].
///
/// Only `client`, `permission_prompt`, and `tools` are mandatory; everything
/// else has a sensible default.  The builder compiles to nothing at runtime —
/// it is a pure constructor pattern.
pub struct AgentLoopBuilder {
    client: Arc<dyn coda_llm::LlmClient>,
    permission_prompt: Arc<dyn PermissionPrompt>,
    tools: Arc<ToolRegistry>,
    quarantine: Arc<ToolQuarantine>,
    permission_mode: PermissionMode,
    permission_mode_state: Option<Arc<PermissionModeState>>,
    steering: Option<Arc<SteeringInbox>>,
    user_question: Option<Arc<dyn UserQuestionPrompt>>,
    model: String,
    system_prompt: Option<String>,
    max_tokens: u32,
    max_iterations: usize,
    effort: Option<Effort>,
    working_directory: String,
    granted_directories: Option<std::collections::HashSet<String>>,
    tool_max_duration: Option<Duration>,
    tool_progress_interval: Duration,
    max_transport_retries: u32,
    max_schema_evictions: u32,
    max_stop_continuations: u32,
}

impl AgentLoopBuilder {
    pub fn new(
        client: Arc<dyn coda_llm::LlmClient>,
        permission_prompt: Arc<dyn PermissionPrompt>,
        tools: Arc<ToolRegistry>,
    ) -> Self {
        Self {
            client,
            permission_prompt,
            tools,
            quarantine: Arc::new(ToolQuarantine::new()),
            permission_mode: PermissionMode::Default,
            permission_mode_state: None,
            steering: None,
            user_question: None,
            model: "claude-opus-4-5".into(),
            system_prompt: None,
            max_tokens: 4096,
            max_iterations: 20,
            effort: None,
            working_directory: String::new(),
            granted_directories: None,
            tool_max_duration: Some(Duration::from_secs(30 * 60)),
            tool_progress_interval: Duration::from_secs(15),
            max_transport_retries: 2,
            max_schema_evictions: 3,
            max_stop_continuations: 3,
        }
    }

    pub fn with_model(mut self, model: impl Into<String>) -> Self {
        self.model = model.into();
        self
    }

    pub fn with_system_prompt(mut self, prompt: impl Into<String>) -> Self {
        self.system_prompt = Some(prompt.into());
        self
    }

    pub fn with_max_tokens(mut self, n: u32) -> Self {
        self.max_tokens = n;
        self
    }

    pub fn with_max_iterations(mut self, n: usize) -> Self {
        self.max_iterations = n;
        self
    }

    pub fn with_effort(mut self, effort: Option<Effort>) -> Self {
        self.effort = effort;
        self
    }

    pub fn with_working_directory(mut self, dir: impl Into<String>) -> Self {
        self.working_directory = dir.into();
        self
    }

    pub fn with_permission_mode(mut self, mode: PermissionMode) -> Self {
        self.permission_mode = mode;
        self
    }

    pub fn with_permission_mode_state(mut self, state: Arc<PermissionModeState>) -> Self {
        self.permission_mode_state = Some(state);
        self
    }

    pub fn with_quarantine(mut self, q: Arc<ToolQuarantine>) -> Self {
        self.quarantine = q;
        self
    }

    pub fn with_steering(mut self, s: Arc<SteeringInbox>) -> Self {
        self.steering = Some(s);
        self
    }

    pub fn with_user_question(mut self, uq: Arc<dyn UserQuestionPrompt>) -> Self {
        self.user_question = Some(uq);
        self
    }

    pub fn with_tool_max_duration(mut self, d: Option<Duration>) -> Self {
        self.tool_max_duration = d;
        self
    }

    pub fn with_max_transport_retries(mut self, n: u32) -> Self {
        self.max_transport_retries = n;
        self
    }

    pub fn with_max_schema_evictions(mut self, n: u32) -> Self {
        self.max_schema_evictions = n;
        self
    }

    pub fn build(self) -> AgentLoop {
        AgentLoop {
            client: self.client,
            permission_prompt: self.permission_prompt,
            tools: self.tools,
            quarantine: self.quarantine,
            permission_mode: self.permission_mode,
            permission_mode_state: self.permission_mode_state,
            steering: self.steering,
            user_question: self.user_question,
            model: self.model,
            system_prompt: self.system_prompt,
            max_tokens: self.max_tokens,
            max_iterations: self.max_iterations,
            effort: self.effort,
            working_directory: self.working_directory,
            granted_directories: self.granted_directories,
            tool_max_duration: self.tool_max_duration,
            tool_progress_interval: self.tool_progress_interval,
            max_transport_retries: self.max_transport_retries,
            max_schema_evictions: self.max_schema_evictions,
            max_stop_continuations: self.max_stop_continuations,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;
    use std::sync::{Arc, Mutex};
    use std::time::Duration;

    use async_trait::async_trait;
    use coda_llm::anthropic::StreamEvent;
    use coda_llm::{Content, Correlation, LlmError, Message, Role, Usage};
    use coda_proto::events::ToolCallStatus;
    use tokio_util::sync::CancellationToken;

    use crate::events::{AgentEvent, CollectingSink, NullSink};
    use crate::permission::PermissionPrompt;
    use crate::tool::{ToolContext, ToolOutcome, ToolRegistry, ToolResult};

    // ── Mock LLM client ─────────────────────────────────────────────────────

    struct MockLlmClient {
        /// Each call to `stream` pops the next scripted sequence.
        sequences: Mutex<VecDeque<Vec<Result<StreamEvent, LlmError>>>>,
    }

    impl MockLlmClient {
        fn new(sequences: Vec<Vec<Result<StreamEvent, LlmError>>>) -> Arc<Self> {
            Arc::new(Self {
                sequences: Mutex::new(sequences.into_iter().collect()),
            })
        }
    }

    #[async_trait]
    impl coda_llm::LlmClient for MockLlmClient {
        fn provider_id(&self) -> &str {
            "mock"
        }
        async fn stream(&self, _: ChatRequest) -> Result<coda_llm::ResponseStream, LlmError> {
            let events = self
                .sequences
                .lock()
                .unwrap()
                .pop_front()
                .expect("MockLlmClient ran out of scripted sequences");
            let (tx, rx) = tokio::sync::mpsc::channel(64);
            tokio::spawn(async move {
                for ev in events {
                    let _ = tx.send(ev).await;
                }
            });
            Ok(coda_llm::ResponseStream::new(rx))
        }
    }

    // ── Mock Tool ───────────────────────────────────────────────────────────

    struct MockTool {
        tool_name: &'static str,
        read_only: bool,
        results: Mutex<VecDeque<ToolResult>>,
        /// Records (call_index, name) when a tool is executed.
        call_log: Arc<Mutex<Vec<String>>>,
        /// Optional delay to simulate slow tools.
        delay: Option<Duration>,
    }

    impl MockTool {
        fn new(name: &'static str, read_only: bool, log: Arc<Mutex<Vec<String>>>) -> Arc<Self> {
            Arc::new(Self {
                tool_name: name,
                read_only,
                results: Mutex::new(VecDeque::new()),
                call_log: log,
                delay: None,
            })
        }

        #[allow(dead_code)]
        fn with_result(self: Arc<Self>, result: ToolResult) -> Arc<Self> {
            self.results.lock().unwrap().push_back(result);
            self
        }

        fn with_delay(mut self: Arc<Self>, d: Duration) -> Arc<Self> {
            Arc::get_mut(&mut self).unwrap().delay = Some(d);
            self
        }
    }

    #[async_trait]
    impl crate::tool::Tool for MockTool {
        fn name(&self) -> &str {
            self.tool_name
        }
        fn description(&self) -> &str {
            self.tool_name
        }
        fn input_schema_json(&self) -> &str {
            "{}"
        }
        fn is_read_only(&self) -> bool {
            self.read_only
        }
        async fn execute(
            &self,
            _input: &serde_json::Value,
            _ctx: &ToolContext,
            cancel: CancellationToken,
        ) -> ToolOutcome {
            self.call_log.lock().unwrap().push(self.tool_name.to_owned());
            if let Some(d) = self.delay {
                tokio::select! {
                    _ = tokio::time::sleep(d) => {}
                    _ = cancel.cancelled() => {
                        return ToolResult::error("cancelled by token");
                    }
                }
            }
            self.results
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or_else(|| ToolResult::ok("ok"))
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// Coerce `Arc<T: Tool>` to `Arc<dyn Tool>` to satisfy `ToolRegistry::new`.
    fn dyn_tool(t: Arc<impl crate::tool::Tool + 'static>) -> Arc<dyn crate::tool::Tool> { t }

    /// A permission prompt that always allows.
    struct AllowAll;
    #[async_trait]
    impl PermissionPrompt for AllowAll {
        async fn request(
            &self,
            _: &dyn crate::tool::Tool,
            _: &str,
            _: CancellationToken,
        ) -> bool {
            true
        }
    }

    /// A permission prompt that denies the named tool(s), allows the rest.
    struct DenyNamed(Vec<&'static str>);
    #[async_trait]
    impl PermissionPrompt for DenyNamed {
        async fn request(
            &self,
            tool: &dyn crate::tool::Tool,
            _: &str,
            _: CancellationToken,
        ) -> bool {
            !self.0.contains(&tool.name())
        }
    }

    fn done() -> StreamEvent {
        StreamEvent::Done {
            stop_reason: Some("end_turn".into()),
            usage: Usage { input_tokens: 10, output_tokens: 5, ..Usage::ZERO },
        }
    }

    fn done_max_tokens() -> StreamEvent {
        StreamEvent::Done {
            stop_reason: Some("max_tokens".into()),
            usage: Usage::ZERO,
        }
    }

    fn tool_use_event(id: &str, name: &str) -> StreamEvent {
        StreamEvent::ToolUse(Content::ToolUse {
            id: id.into(),
            name: name.into(),
            input_json: "{}".into(),
            correlation: Correlation::default(),
        })
    }

    fn make_loop(
        client: Arc<dyn coda_llm::LlmClient>,
        permission_prompt: Arc<dyn PermissionPrompt>,
        tools: Arc<ToolRegistry>,
    ) -> AgentLoop {
        AgentLoopBuilder::new(client, permission_prompt, tools)
            .with_max_iterations(20)
            .with_tool_max_duration(None)
            .build()
    }

    // ── §8 item 1: tools execute serially ───────────────────────────────────

    #[tokio::test]
    async fn tools_execute_serially_in_requested_order() {
        let log = Arc::new(Mutex::new(Vec::new()));

        // Script: one turn with two tool calls, then a final text-only turn.
        let client = MockLlmClient::new(vec![
            vec![
                Ok(tool_use_event("t1", "tool_a")),
                Ok(tool_use_event("t2", "tool_b")),
                Ok(done()),
            ],
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([
            dyn_tool(MockTool::new("tool_a", true, log.clone())),
            dyn_tool(MockTool::new("tool_b", true, log.clone())),
        ]));

        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();

        let order = log.lock().unwrap().clone();
        assert_eq!(order, vec!["tool_a", "tool_b"], "tools must run in requested order");
    }

    // ── §8 item 2: loop drives off tool-call presence ───────────────────────

    #[tokio::test]
    async fn loop_continues_on_tool_calls_regardless_of_stop_reason() {
        let log = Arc::new(Mutex::new(Vec::new()));

        // Iteration 0: has tool_use + stop_reason="end_turn" (not "tool_use").
        // Iteration 1: no tool calls → natural stop.
        let client = MockLlmClient::new(vec![
            vec![
                Ok(tool_use_event("t1", "tool_a")),
                // stop_reason = "end_turn" (NOT "tool_use") — loop must still execute tools.
                Ok(StreamEvent::Done {
                    stop_reason: Some("end_turn".into()),
                    usage: Usage::ZERO,
                }),
            ],
            vec![Ok(StreamEvent::TextDelta("all done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([dyn_tool(MockTool::new("tool_a", true, log.clone()))]));
        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();

        assert_eq!(log.lock().unwrap().clone(), vec!["tool_a"], "tool must execute despite end_turn stop_reason");
    }

    // ── §8 item 3: assistant block order ────────────────────────────────────

    #[tokio::test]
    async fn assistant_block_order_redacted_signed_text_tool() {
        let signed = Content::Thinking {
            text: "thinking".into(),
            signature: Some("sig".into()),
        };
        let unsigned = Content::Thinking { text: "unsigned".into(), signature: None };
        let redacted = Content::RedactedThinking { data: "opaque".into() };

        let log = Arc::new(Mutex::new(Vec::new()));

        let client = MockLlmClient::new(vec![
            vec![
                // Redacted thinking
                Ok(StreamEvent::ThinkingDone(redacted.clone())),
                // Signed thinking
                Ok(StreamEvent::ThinkingDelta("thinking".into())),
                Ok(StreamEvent::ThinkingDone(signed.clone())),
                // Unsigned thinking — must be skipped in history
                Ok(StreamEvent::ThinkingDelta("unsigned".into())),
                Ok(StreamEvent::ThinkingDone(unsigned)),
                // Text
                Ok(StreamEvent::TextDelta("answer".into())),
                // Tool use
                Ok(tool_use_event("t1", "tool_a")),
                Ok(done()),
            ],
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([dyn_tool(MockTool::new("tool_a", true, log.clone()))]));
        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();

        // Find the first assistant message (after the initial user message).
        let assistant_msg = history.iter().find(|m| m.role == Role::Assistant).unwrap();
        let content = &assistant_msg.content;

        assert!(
            matches!(&content[0], Content::RedactedThinking { .. }),
            "block 0 must be RedactedThinking"
        );
        assert!(
            matches!(&content[1], Content::Thinking { signature: Some(_), .. }),
            "block 1 must be signed Thinking"
        );
        assert!(
            matches!(&content[2], Content::Text(_)),
            "block 2 must be Text"
        );
        assert!(
            matches!(&content[3], Content::ToolUse { .. }),
            "block 3 must be ToolUse"
        );
        assert_eq!(content.len(), 4, "unsigned thinking must be filtered out");
    }

    // ── §8 item 4: correlation stamping ─────────────────────────────────────

    #[tokio::test]
    async fn tool_use_blocks_are_correlation_stamped() {
        let log = Arc::new(Mutex::new(Vec::new()));

        let client = MockLlmClient::new(vec![
            vec![Ok(tool_use_event("call-1", "tool_a")), Ok(done())],
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([dyn_tool(MockTool::new("tool_a", true, log))]));
        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();

        let assistant_msg = history.iter().find(|m| m.role == Role::Assistant).unwrap();
        let has_stamped = assistant_msg.content.iter().any(|b| {
            matches!(b, Content::ToolUse { correlation, .. } if !correlation.is_empty())
        });
        assert!(has_stamped, "tool_use blocks must have correlation IDs stamped");
    }

    // ── §8 item 5: transport retry only when nothing emitted ─────────────────

    #[tokio::test]
    async fn transport_retry_fires_when_nothing_emitted() {
        let sink = CollectingSink::new();

        // First call: transport error before anything emitted → retry.
        // Second call: succeeds with text.
        let client = MockLlmClient::new(vec![
            vec![Err(LlmError::Transport("reset".into()))],
            vec![
                Ok(StreamEvent::TextDelta("hello".into())),
                Ok(done()),
            ],
        ]);

        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_max_transport_retries(2)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("hi")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        let events = sink.take();
        let text_events: Vec<&str> = events
            .iter()
            .filter_map(|e| {
                if let AgentEvent::AssistantText { delta } = e { Some(delta.as_str()) } else { None }
            })
            .collect();
        // Exactly one "hello" — no duplicate from retry.
        assert_eq!(text_events, vec!["hello"], "text must appear exactly once after transport retry");
    }

    #[tokio::test]
    async fn no_transport_retry_after_text_emitted() {
        let sink = CollectingSink::new();

        // First call: text then transport error → mid-stream failure, no retry.
        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("partial".into())),
            Err(LlmError::Transport("mid-stream reset".into())),
        ]]);

        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_max_transport_retries(2)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("hi")];
        let result = agent.run(&mut history, &sink, None, CancellationToken::new()).await;

        assert!(result.is_err(), "mid-stream error after text must propagate");
        let text_events: Vec<_> = sink
            .take()
            .into_iter()
            .filter(|e| matches!(e, AgentEvent::AssistantText { .. }))
            .collect();
        assert_eq!(text_events.len(), 1, "text must appear exactly once — no retry after text emitted");
    }

    // ── §8 item 7: schema eviction ───────────────────────────────────────────

    #[tokio::test]
    async fn schema_eviction_quarantines_offending_tool_only() {
        let quarantine = Arc::new(ToolQuarantine::new());

        // First call: 400 with body naming 'bad_tool'.
        // Second call (after eviction): succeeds.
        let client = MockLlmClient::new(vec![
            vec![Err(LlmError::Api {
                status: 400,
                message: "bad schema".into(),
                kind: coda_llm::FailureKind::Permanent,
                retry_after: None,
                body: Some("Invalid schema for tool 'bad_tool': x".into()),
            })],
            vec![Ok(StreamEvent::TextDelta("ok".into())), Ok(done())],
        ]);

        let log = Arc::new(Mutex::new(Vec::<String>::new()));
        let tools = Arc::new(ToolRegistry::new([
            dyn_tool(MockTool::new("good_tool", true, log.clone())),
            dyn_tool(MockTool::new("bad_tool", true, log.clone())),
        ]));

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_quarantine(quarantine.clone())
            .with_max_schema_evictions(3)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();

        assert!(quarantine.is_quarantined("bad_tool"), "bad_tool must be quarantined");
        assert!(!quarantine.is_quarantined("good_tool"), "good_tool must survive");
    }

    // ── §8 item 8: cancellation and ceiling ──────────────────────────────────

    #[tokio::test]
    async fn caller_cancel_propagates_and_unwinds_turn() {
        let cancel = CancellationToken::new();
        let cancel_clone = cancel.clone();

        // Tool that cancels the caller token when it starts.
        struct CancellingTool(CancellationToken);
        #[async_trait]
        impl crate::tool::Tool for CancellingTool {
            fn name(&self) -> &str { "cancel_tool" }
            fn description(&self) -> &str { "cancels" }
            fn input_schema_json(&self) -> &str { "{}" }
            fn is_read_only(&self) -> bool { true }
            async fn execute(
                &self,
                _: &serde_json::Value,
                _: &ToolContext,
                _cancel: CancellationToken,
            ) -> ToolOutcome {
                self.0.cancel(); // cancel the OUTER caller token
                ToolResult::ok("done")
            }
        }

        let client = MockLlmClient::new(vec![vec![
            Ok(tool_use_event("t1", "cancel_tool")),
            Ok(done()),
        ]]);

        let tools = Arc::new(ToolRegistry::new([Arc::new(CancellingTool(cancel_clone))
            as Arc<dyn crate::tool::Tool>]));

        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        let result = agent.run(&mut history, &NullSink, None, cancel).await;

        assert!(
            matches!(result, Err(AgentError::Cancelled)),
            "caller cancel must propagate, got: {result:?}"
        );
    }

    #[tokio::test]
    async fn tool_ceiling_produces_error_result_session_survives() {
        let log = Arc::new(Mutex::new(Vec::new()));

        // Tool that sleeps longer than the ceiling.
        let slow_tool = MockTool::new("slow_tool", true, log.clone())
            .with_delay(Duration::from_millis(500));

        let client = MockLlmClient::new(vec![
            vec![Ok(tool_use_event("t1", "slow_tool")), Ok(done())],
            vec![Ok(StreamEvent::TextDelta("session survived".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([slow_tool as Arc<dyn crate::tool::Tool>]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            // Very short ceiling: 10ms < 500ms tool sleep.
            .with_tool_max_duration(Some(Duration::from_millis(10)))
            .build();

        let mut history = vec![Message::user("go")];
        let result = agent.run(&mut history, &NullSink, None, CancellationToken::new()).await;

        assert!(result.is_ok(), "session must survive a ceiling timeout");
        // The tool result in history should be an error.
        let tool_result_msg = history.iter().find(|m| {
            m.role == Role::User
                && m.content
                    .iter()
                    .any(|b| matches!(b, Content::ToolResult { is_error: true, .. }))
        });
        assert!(tool_result_msg.is_some(), "history must contain the ceiling error result");
    }

    // ── §8 item 15: denied tool yields error, batch continues ───────────────

    #[tokio::test]
    async fn denied_tool_yields_error_result_and_batch_continues() {
        let log = Arc::new(Mutex::new(Vec::new()));
        let sink = CollectingSink::new();

        // Three tools: A (read-only, allowed), B (writable, denied), C (read-only, allowed).
        let client = MockLlmClient::new(vec![
            vec![
                Ok(tool_use_event("t1", "tool_a")),
                Ok(tool_use_event("t2", "tool_b")),
                Ok(tool_use_event("t3", "tool_c")),
                Ok(done()),
            ],
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([
            dyn_tool(MockTool::new("tool_a", true, log.clone())),
            dyn_tool(MockTool::new("tool_b", false, log.clone())), // writable → goes through permission
            dyn_tool(MockTool::new("tool_c", true, log.clone())),
        ]));

        // Deny only tool_b.
        let agent = AgentLoopBuilder::new(
            client,
            Arc::new(DenyNamed(vec!["tool_b"])),
            tools,
        )
        .with_tool_max_duration(None)
        .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        // tool_a and tool_c must have executed.
        let executed = log.lock().unwrap().clone();
        assert!(executed.contains(&"tool_a".to_owned()), "tool_a must execute");
        assert!(executed.contains(&"tool_c".to_owned()), "tool_c must execute");

        // tool_b must have a denied result.
        let events = sink.take();
        let denied = events.iter().any(|e| {
            matches!(e, AgentEvent::ToolResult { tool_name, is_error: true, .. } if tool_name == "tool_b")
        });
        assert!(denied, "tool_b must have a denial error result");
    }

    // ── §8 item 25: max_iterations soft stop ─────────────────────────────────

    #[tokio::test]
    async fn max_iterations_emits_limit_reached_and_closing_message() {
        let log = Arc::new(Mutex::new(Vec::new()));
        let sink = CollectingSink::new();

        // LLM always returns a tool call; loop should stop after max_iterations.
        let client = MockLlmClient::new(vec![
            vec![Ok(tool_use_event("t1", "tool_a")), Ok(done())],
            vec![Ok(tool_use_event("t1", "tool_a")), Ok(done())],
            vec![Ok(tool_use_event("t1", "tool_a")), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([dyn_tool(MockTool::new("tool_a", true, log))]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_max_iterations(2)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        let events = sink.take();
        let limit = events.iter().any(|e| {
            matches!(e, AgentEvent::LimitReached { kind, .. } if kind == "max_tool_iterations")
        });
        assert!(limit, "max_tool_iterations LimitReached must be emitted");

        let closing_msg = history.iter().rev().find(|m| m.role == Role::Assistant);
        assert!(
            closing_msg
                .map(|m| m.text().contains("maximum tool iterations"))
                .unwrap_or(false),
            "closing assistant message must mention maximum tool iterations"
        );
    }

    // ── §8 item 26: max_tokens emits LimitReached ────────────────────────────

    #[tokio::test]
    async fn max_tokens_stop_emits_limit_reached() {
        let sink = CollectingSink::new();

        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("truncated".into())),
            Ok(done_max_tokens()),
        ]]);

        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        let events = sink.take();
        let limit = events.iter().any(|e| {
            matches!(e, AgentEvent::LimitReached { kind, .. } if kind == "max_tokens")
        });
        assert!(limit, "max_tokens LimitReached must be emitted");
    }

    // ── §8 item 27: pending hook tasks drained on every exit path ───────────

    #[tokio::test]
    async fn pending_tasks_are_drained_on_normal_exit() {
        // With no hooks implemented, pending_hook_tasks is always empty.
        // This test verifies the loop completes without panic.
        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("done".into())),
            Ok(done()),
        ]]);
        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("hi")];
        // Just verifying it returns OK (drain path exercised).
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();
    }

    // ── §8 item 22: steering injection in iteration ──────────────────────────

    #[tokio::test]
    async fn steering_is_injected_into_history() {
        let steering = Arc::new(SteeringInbox::new());
        let sink = CollectingSink::new();

        // Pre-queue a steering message before the loop starts.
        steering.enqueue("pivot your approach").unwrap();

        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("done".into())),
            Ok(done()),
        ]]);

        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_steering(steering)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        // Steering text must appear as a User message before the assistant response.
        let steering_msg = history
            .iter()
            .find(|m| m.role == Role::User && m.text().contains("pivot your approach"));
        assert!(steering_msg.is_some(), "steering message must be injected into history");

        let events = sink.take();
        let delivered = events.iter().any(|e| matches!(e, AgentEvent::SteeringDelivered { .. }));
        assert!(delivered, "SteeringDelivered event must be emitted");
    }

    // ── §8 item 23: steering mid-batch skips remaining tools ─────────────────
    //
    // When a steering message arrives BETWEEN tools in a batch, the tool
    // that triggered the steering check and all subsequent tools are marked
    // Skipped.  We test this by having tool_a enqueue a message during its
    // execution, which causes tool_b to be skipped before it starts.

    #[tokio::test]
    async fn steering_mid_batch_skips_remaining_tools() {
        let steering = Arc::new(SteeringInbox::new());
        let log = Arc::new(Mutex::new(Vec::<String>::new()));
        let sink = CollectingSink::new();

        // A tool that enqueues a steering message when it executes.
        struct SteeringTool {
            name: &'static str,
            inbox: Arc<SteeringInbox>,
            log: Arc<Mutex<Vec<String>>>,
        }
        #[async_trait]
        impl crate::tool::Tool for SteeringTool {
            fn name(&self) -> &str { self.name }
            fn description(&self) -> &str { self.name }
            fn input_schema_json(&self) -> &str { "{}" }
            fn is_read_only(&self) -> bool { true }
            async fn execute(&self, _: &serde_json::Value, _: &ToolContext, _: CancellationToken) -> ToolOutcome {
                self.log.lock().unwrap().push(self.name.to_owned());
                self.inbox.enqueue("operator steering arrived mid-batch").unwrap();
                ToolResult::ok("done")
            }
        }

        let client = MockLlmClient::new(vec![
            vec![
                Ok(tool_use_event("t1", "tool_a")),
                Ok(tool_use_event("t2", "tool_b")),
                Ok(done()),
            ],
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([
            Arc::new(SteeringTool { name: "tool_a", inbox: steering.clone(), log: log.clone() })
                as Arc<dyn crate::tool::Tool>,
            dyn_tool(MockTool::new("tool_b", true, log.clone())),
        ]));

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_steering(steering)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        let executed = log.lock().unwrap().clone();
        // tool_a ran (and enqueued steering); tool_b must be skipped.
        assert!(executed.contains(&"tool_a".to_owned()), "tool_a must have executed");
        assert!(!executed.contains(&"tool_b".to_owned()), "tool_b must be skipped by steering preempt");

        let events = sink.take();
        let skipped = events.iter().filter(|e| {
            matches!(e, AgentEvent::ToolResult { status: ToolCallStatus::Skipped, .. })
        }).count();
        assert_eq!(skipped, 1, "tool_b should have exactly one Skipped result");
    }
}
