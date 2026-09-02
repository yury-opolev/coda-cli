//! Application state and the reducer that advances it.
//!
//! All mutation flows through [`UiState::apply`]. Keeping transitions in one
//! place means the UI can be tested without a terminal, a running engine, or
//! any async machinery: feed events in, assert on the state that comes out.

use coda_proto::events::ToolCallStatus;
use coda_proto::{Correlation, Event};
use coda_render::tool::{CallStatus, ToolActivity, ToolCall, ToolDisplayMode};

use crate::transcript::{same_call, ActivityKey, Block, NoticeLevel, PermissionDecision, Transcript};

/// What the agent is currently doing, shown in the status line.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Activity {
    /// Connected and awaiting input.
    #[default]
    Ready,
    /// Handshake in progress.
    Initializing,
    /// A turn is running.
    Working,
    /// The model is reasoning.
    Thinking,
    /// Blocked on the user answering a prompt.
    Waiting,
}

impl Activity {
    pub fn label(self) -> &'static str {
        match self {
            Activity::Ready => "ready",
            Activity::Initializing => "starting",
            Activity::Working => "working",
            Activity::Thinking => "thinking",
            Activity::Waiting => "waiting",
        }
    }

    pub fn role(self) -> coda_render::theme::Role {
        use coda_render::theme::Role;
        match self {
            Activity::Ready => Role::OperationalReady,
            Activity::Initializing => Role::OperationalInitializing,
            Activity::Working => Role::OperationalWorking,
            Activity::Thinking => Role::OperationalThinking,
            Activity::Waiting => Role::OperationalWaiting,
        }
    }

    /// Whether this state should show a moving indicator.
    ///
    /// A spinner is a claim that something is happening. `Waiting` is blocked
    /// on the *user* answering a prompt, so spinning there claims progress
    /// that is not being made and invites them to wait for themselves.
    pub fn is_animated(self) -> bool {
        matches!(
            self,
            Activity::Working | Activity::Thinking | Activity::Initializing
        )
    }
}

/// Cumulative token usage for the session.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Usage {
    pub input_tokens: i64,
    pub output_tokens: i64,
    /// Nominal context window, used to show a percentage.
    pub context_limit: i64,
}

impl Usage {
    /// Percentage of the context window consumed, if a limit is known.
    pub fn percent_used(&self) -> Option<u8> {
        if self.context_limit <= 0 {
            return None;
        }
        let ratio = self.input_tokens as f64 / self.context_limit as f64;
        Some((ratio * 100.0).clamp(0.0, 100.0) as u8)
    }
}

/// A user message queued while the agent is busy.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QueuedMessage {
    pub id: Option<String>,
    pub text: String,
}

/// A prompt from the engine awaiting a user decision.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PendingPrompt {
    Permission { tool: String, preview: String },
    Question {
        question: String,
        options: Vec<String>,
        multi_select: bool,
        allow_free_text: bool,
    },
    PlanApproval { plan: String },
}

/// Events the reducer understands.
///
/// Engine notifications and local user actions are unified so that ordering
/// between them is explicit rather than accidental.
#[derive(Debug, Clone)]
pub enum UiEvent {
    /// A notification from the engine.
    Engine(Event),
    /// The handshake completed.
    Connected { session_id: String },
    /// The user submitted text.
    Submitted { text: String },
    /// The submission was queued because a turn was already running.
    Queued { text: String, id: Option<String> },
    /// A turn finished, from the `session/prompt` response.
    TurnFinished { interrupted: bool, error: Option<String> },
    /// The user asked to interrupt.
    InterruptRequested,
    /// The engine asked the user something.
    PromptRequested(PendingPrompt),
    /// The user answered the outstanding prompt.
    PromptAnswered { allowed: bool, answer: Option<String> },
    /// Output produced locally by a slash command.
    CommandOutput { text: String },
    /// A git diff to display with syntax colouring.
    DiffOutput { text: String },
    /// A local status or error line.
    Notice { text: String, level: NoticeLevel },
    /// The transcript was cleared.
    Cleared,
    /// The active model changed.
    ModelChanged { id: String, context_limit: Option<i64> },
    /// Fold or unfold the reasoning block at this index.
    ///
    /// An event rather than a direct call so it passes through the same
    /// invalidation as every other transcript change. Toggling the block
    /// directly left the fold flipped internally while the cached rows —
    /// rebuilt only on a width change — kept drawing the old shape, so a
    /// click did nothing at all once a turn had finished.
    ThinkingFoldToggled { block: usize },
    /// The tool display mode changed.
    DisplayModeChanged(ToolDisplayMode),
    /// Activates assistant-text buffering for the current and future turns.
    ///
    /// **Seam**: this event is emitted by the hook system when an `AgentResponse`
    /// redaction hook is configured.  It is never emitted by the current Rust
    /// front-end because the hook engine lives in `coda-agent` (another agent
    /// is porting that).  The full reducer logic is implemented here so it is
    /// correct and tested, ready for when the hook seam closes.
    EnableAssistantBuffering,
}

/// Everything the UI draws from.
#[derive(Debug)]
pub struct UiState {
    pub transcript: Transcript,
    pub activity: Activity,
    pub usage: Usage,
    pub session_id: Option<String>,
    pub model: Option<String>,
    pub display_mode: ToolDisplayMode,
    /// Messages typed while a turn was running.
    pub queued: Vec<QueuedMessage>,
    /// The prompt currently blocking the turn, if any.
    pub prompt: Option<PendingPrompt>,
    /// Set once the user has asked to quit.
    pub should_quit: bool,
    /// Set while an interrupt has been requested but not yet acknowledged.
    pub interrupting: bool,
    /// When `Some`, assistant text is buffered here instead of being streamed
    /// to the transcript.  Activated by `UiEvent::EnableAssistantBuffering`
    /// (the seam for hook-phase-3 buffering).  Flushed as a completed block on
    /// turn success; withheld on interruption if the hook never rewrote it.
    pub assistant_buffer: Option<String>,
    /// Set when a `ResponseRewritten` engine event replaces the buffer's
    /// contents.  Determines whether to flush or withhold on interruption.
    pub buffer_rewritten_by_hook: bool,
    /// Frame of the working indicator.
    ///
    /// Advanced by the event loop rather than the renderer, so drawing the
    /// same state twice gives the same picture and the render tests stay
    /// deterministic.
    pub spinner: usize,
    /// Timestamp source, injected so tests are deterministic.
    clock: fn() -> String,
}

impl Default for UiState {
    fn default() -> Self {
        Self::new()
    }
}

impl UiState {
    pub fn new() -> Self {
        Self {
            transcript: Transcript::new(),
            activity: Activity::Initializing,
            usage: Usage::default(),
            session_id: None,
            model: None,
            display_mode: ToolDisplayMode::default(),
            queued: Vec::new(),
            prompt: None,
            should_quit: false,
            interrupting: false,
            assistant_buffer: None,
            buffer_rewritten_by_hook: false,
            spinner: 0,

            clock: default_timestamp,
        }
    }

    /// Builds a state with a fixed clock, for deterministic tests.
    pub fn with_clock(clock: fn() -> String) -> Self {
        Self {
            clock,
            ..Self::new()
        }
    }

    /// Whether a turn is in flight.
    pub fn is_busy(&self) -> bool {
        matches!(
            self.activity,
            Activity::Working | Activity::Thinking | Activity::Waiting
        )
    }

    /// Advances the state by one event.
    pub fn apply(&mut self, event: UiEvent) {
        match event {
            UiEvent::Engine(event) => self.apply_engine(event),
            UiEvent::Connected { session_id } => {
                self.session_id = Some(session_id);
                self.activity = Activity::Ready;
            }
            UiEvent::Submitted { text } => {
                self.transcript.close_open();
                self.transcript.push(Block::User {
                    text,
                    timestamp: (self.clock)(),
                    pending: false,
                    queue_id: None,
                });
                self.activity = Activity::Working;
                self.interrupting = false;
            }
            UiEvent::Queued { text, id } => {
                self.queued.push(QueuedMessage {
                    id: id.clone(),
                    text: text.clone(),
                });
                self.transcript.push(Block::User {
                    text,
                    timestamp: (self.clock)(),
                    pending: true,
                    queue_id: id,
                });
            }
            UiEvent::TurnFinished { interrupted, error } => {
                self.transcript.close_open();
                self.transcript.finalize_activities(None);
                // Anything still queued never reached the model.
                self.transcript.remove_pending_user();
                self.queued.clear();
                self.activity = Activity::Ready;
                self.interrupting = false;
                self.prompt = None;

                if interrupted {
                    self.notice("Interrupted.", NoticeLevel::Warning);
                } else if let Some(error) = error {
                    self.notice(error, NoticeLevel::Error);
                }
            }
            UiEvent::InterruptRequested => {
                if self.is_busy() {
                    self.interrupting = true;
                }
            }
            UiEvent::PromptRequested(prompt) => {
                self.prompt = Some(prompt);
                self.activity = Activity::Waiting;
            }
            UiEvent::PromptAnswered { allowed, answer } => {
                let prompt = self.prompt.take();
                self.activity = Activity::Working;

                match prompt {
                    Some(PendingPrompt::Permission { tool, preview }) => {
                        self.transcript.push(Block::Permission {
                            tool,
                            preview,
                            decision: if allowed {
                                PermissionDecision::Allowed
                            } else {
                                PermissionDecision::Denied
                            },
                        });
                    }
                    Some(PendingPrompt::Question { question, .. }) => {
                        self.transcript.push(Block::Question { question, answer });
                    }
                    Some(PendingPrompt::PlanApproval { .. }) => {
                        self.notice(
                            if allowed {
                                "Plan approved."
                            } else {
                                "Plan rejected."
                            },
                            NoticeLevel::Info,
                        );
                    }
                    None => {}
                }
            }
            UiEvent::CommandOutput { text } => {
                self.transcript.close_open();
                self.transcript.push(Block::CommandOutput { text });
            }
            UiEvent::DiffOutput { text } => {
                self.transcript.close_open();
                self.transcript.push(Block::Diff { raw: text });
            }
            UiEvent::Notice { text, level } => self.notice(text, level),
            UiEvent::Cleared => {
                self.transcript.clear();
                self.queued.clear();
            }
            UiEvent::ModelChanged { id, context_limit } => {
                self.model = Some(id);
                if let Some(limit) = context_limit {
                    self.usage.context_limit = limit;
                }
            }
            UiEvent::ThinkingFoldToggled { block } => {
                self.transcript.toggle_fold(block);
            }
            UiEvent::DisplayModeChanged(mode) => self.display_mode = mode,
            UiEvent::EnableAssistantBuffering => {
                // Activate buffering; an empty buffer means "buffering on, no text yet".
                if self.assistant_buffer.is_none() {
                    self.assistant_buffer = Some(String::new());
                    self.buffer_rewritten_by_hook = false;
                }
            }
        }
    }

    fn apply_engine(&mut self, event: Event) {
        match event {
            Event::AssistantText { delta } => {
                if delta.is_empty() {
                    return;
                }
                self.activity = Activity::Working;
                // Buffering mode: accumulate instead of streaming to the transcript.
                if let Some(buf) = self.assistant_buffer.as_mut() {
                    buf.push_str(&delta);
                    return;
                }
                match self.transcript.open_tail() {
                    Some(Block::Assistant { text, .. }) => text.push_str(&delta),
                    _ => {
                        self.transcript.close_open();
                        self.transcript.push(Block::Assistant {
                            text: delta,
                            complete: false,
                        });
                    }
                }
            }
            Event::AssistantTextComplete => {
                // While buffering, the buffer is flushed on TurnComplete, not here.
                if self.assistant_buffer.is_none() {
                    if let Some(Block::Assistant { complete, .. }) = self.transcript.open_tail() {
                        *complete = true;
                    }
                }
            }
            Event::Thinking { delta } => {
                // Suppress thinking display during a buffered turn (C# rule).
                if self.assistant_buffer.is_some() {
                    return;
                }
                self.activity = Activity::Thinking;
                match self.transcript.open_tail() {
                    Some(Block::Thinking { text, .. }) => text.push_str(&delta),
                    _ => {
                        self.transcript.close_open();
                        self.transcript.push(Block::Thinking {
                            text: delta,
                            elapsed_ms: 0,
                            tokens: None,
                            complete: false,
                            expanded: false,
                        });
                    }
                }
            }
            Event::ThinkingComplete {
                elapsed_ms,
                thinking_tokens,
            } => {
                // Suppress during buffered turn.
                if self.assistant_buffer.is_some() {
                    return;
                }
                if let Some(Block::Thinking {
                    elapsed_ms: elapsed,
                    tokens,
                    complete,
                    ..
                }) = self.transcript.open_tail()
                {
                    *elapsed = elapsed_ms;
                    *tokens = thinking_tokens;
                    *complete = true;
                }
                self.activity = Activity::Working;
            }
            Event::ToolCall {
                tool_name,
                input_json,
                correlation,
            } => {
                self.activity = Activity::Working;
                let key = ActivityKey::from_correlation(&correlation);
                let index = self.batch_for_new_call(&key);
                if let Some(Block::Tools { activity, calls, .. }) =
                    self.transcript.blocks_mut().get_mut(index)
                {
                    activity.calls.push(ToolCall::new(tool_name, input_json));
                    calls.push(correlation);
                }
            }
            Event::ToolProgress {
                tool_name,
                elapsed_ms,
                correlation,
            } => {
                if let Some((block, call)) = self.locate_call(&correlation, &tool_name) {
                    if let Some(Block::Tools { activity, .. }) =
                        self.transcript.blocks_mut().get_mut(block)
                    {
                        if let Some(call) = activity.calls.get_mut(call) {
                            call.elapsed_ms = Some(elapsed_ms);
                        }
                    }
                }
            }
            Event::ToolResult {
                tool_name,
                content,
                is_error,
                status,
                correlation,
            } => {
                let status = map_status(status, is_error);
                match self.locate_call(&correlation, &tool_name) {
                    Some((block, call)) => {
                        if let Some(Block::Tools { activity, .. }) =
                            self.transcript.blocks_mut().get_mut(block)
                        {
                            if let Some(call) = activity.calls.get_mut(call) {
                                call.result = Some(content);
                                call.is_error = is_error;
                                call.status = status;
                            }
                        }
                    }
                    None => {
                        // A result with no matching call still deserves to be
                        // shown rather than silently dropped.
                        let key = ActivityKey::from_correlation(&correlation);
                        let index = self.batch_for_new_call(&key);
                        if let Some(Block::Tools { activity, calls, .. }) =
                            self.transcript.blocks_mut().get_mut(index)
                        {
                            let mut call = ToolCall::new(&tool_name, "{}");
                            call.result = Some(content);
                            call.is_error = is_error;
                            call.status = status;
                            activity.calls.push(call);
                            calls.push(correlation);
                        }
                    }
                }
            }
            Event::TurnComplete {
                interrupted,
                root_turn_id,
                ..
            } => {
                self.transcript.close_open();
                self.transcript.finalize_activities(root_turn_id.as_deref());
                // Anything still queued never reached the model.
                self.transcript.remove_pending_user();
                self.queued.clear();

                // Flush or withhold the assistant buffer.
                // Rule (from C# UiReducer.HandleTurnCompleted / HandleTurnInterrupted):
                // - success → always flush (show the buffered text as a completed block)
                // - interrupted → flush only if the hook already ran (buffer was rewritten);
                //   otherwise withhold to avoid surfacing raw unreviewed model output.
                if interrupted {
                    self.flush_or_withhold_buffer();
                } else {
                    self.flush_buffer();
                }
                self.assistant_buffer = None;
                self.buffer_rewritten_by_hook = false;

                self.activity = Activity::Ready;
                self.interrupting = false;
                if interrupted {
                    self.notice("Interrupted.", NoticeLevel::Warning);
                }
            }
            Event::Usage {
                input_tokens,
                output_tokens,
            } => {
                self.usage.input_tokens = input_tokens;
                self.usage.output_tokens = output_tokens;
            }
            Event::Error { message } => {
                self.transcript.close_open();
                self.notice(message, NoticeLevel::Error);
            }
            Event::LimitReached { message, .. } => {
                self.transcript.close_open();
                self.notice(message, NoticeLevel::Warning);
            }
            Event::SteeringDelivered { message_ids } => {
                // Delivered messages stop being "pending" in the transcript.
                self.queued
                    .retain(|m| !m.id.as_ref().is_some_and(|id| message_ids.contains(id)));
                self.transcript.mark_delivered(&message_ids);
            }
            Event::TaskCompleted {
                description,
                status,
                ..
            } => {
                let level = if status == "failed" {
                    NoticeLevel::Error
                } else {
                    NoticeLevel::Info
                };
                self.notice(format!("Task {status}: {description}"), level);
            }
            Event::PromptRewritten { hook_command, .. } => {
                self.notice(
                    format!("Prompt rewritten by hook: {hook_command}"),
                    NoticeLevel::Info,
                );
            }
            Event::PermissionDecided {
                tool_name, decision, ..
            } => {
                self.transcript.push(Block::Permission {
                    tool: tool_name,
                    preview: "(decided by hook)".to_string(),
                    decision: if decision == "allow" {
                        PermissionDecision::Allowed
                    } else {
                        PermissionDecision::Denied
                    },
                });
            }
            Event::SubagentBlocked { reason, .. } => {
                self.notice(format!("Subagent blocked: {reason}"), NoticeLevel::Warning);
            }
            Event::SubagentResultModified { .. } => {
                // MINOR 8: SubagentResultModified is an informational hook event;
                // the TUI has no UI to display hook payloads, so it is silently
                // accepted (like ToolInputModified and ToolResultModified).
            }
            Event::CompactionCancelled { hook_command, .. } => {
                // Worth surfacing: the user asked for compaction (or it was
                // triggered automatically) and a hook prevented it, so the
                // context is still full and the next turn may hit the limit.
                self.notice(
                    format!("Compaction cancelled by hook: {hook_command}"),
                    NoticeLevel::Warning,
                );
            }
            Event::PostCompactContextInjected { .. } => {
                // Informational: a hook added context after compaction. The
                // content lands in the history rather than the transcript.
            }
            // Informational events with no transcript representation.
            Event::Stop { .. }
            | Event::StreamProgress { .. }
            | Event::ScheduleLifecycle { .. }
            | Event::ToolInputModified { .. }
            | Event::ToolResultModified { .. }
            | Event::PermissionsUpdated { .. }
            | Event::Unknown { .. } => {}
            // When a display-mutating AgentResponse hook rewrites the assistant
            // response, replace the buffer with the hook's display content so
            // the final render uses the cleaned output.  When buffering is off,
            // the text was already streamed to the transcript — this is a no-op.
            // Rule from C# UiReducer: ResponseRewrittenEvent.
            Event::ResponseRewritten { display_content, .. } => {
                if self.assistant_buffer.is_some() {
                    self.assistant_buffer = Some(display_content);
                    self.buffer_rewritten_by_hook = true;
                }
            }
        }
    }

    /// Chooses the batch a newly seen call belongs to.
    ///
    /// A batch may only be extended while it is still the last block: once
    /// assistant text or anything else follows, later calls open a new batch,
    /// which is what keeps interleaved text and tools in the right order.
    fn batch_for_new_call(&mut self, key: &ActivityKey) -> usize {
        if let Some(Block::Tools {
            activity,
            key: existing,
            ..
        }) = self.transcript.blocks().last()
        {
            if !activity.complete && existing == key {
                return self.transcript.len() - 1;
            }
        }

        self.transcript.close_open();
        self.transcript.push(Block::Tools {
            activity: ToolActivity::default(),
            key: key.clone(),
            calls: Vec::new(),
        });
        self.transcript.len() - 1
    }

    /// Finds the block and call index a correlation refers to.
    ///
    /// Scans backwards for the batch that already owns this exact call, so a
    /// result still lands correctly when assistant text has since opened a new
    /// batch. Falls back to the most recent unfinished call of the same name
    /// when the engine omitted correlation ids.
    fn locate_call(&self, correlation: &Correlation, tool_name: &str) -> Option<(usize, usize)> {
        let blocks = self.transcript.blocks();

        if correlation.call_id.is_some() {
            for (block_index, block) in blocks.iter().enumerate().rev() {
                let Block::Tools { calls, .. } = block else {
                    continue;
                };
                if let Some(call_index) =
                    calls.iter().position(|known| same_call(known, correlation))
                {
                    return Some((block_index, call_index));
                }
            }
            return None;
        }

        // No id to match on: the newest unfinished call of this name is the
        // only defensible guess, and matches a single-threaded turn.
        for (block_index, block) in blocks.iter().enumerate().rev() {
            let Block::Tools { activity, .. } = block else {
                continue;
            };
            if let Some(call_index) = activity
                .calls
                .iter()
                .rposition(|call| call.name == tool_name && !call.status.is_terminal())
            {
                return Some((block_index, call_index));
            }
        }
        None
    }

    fn notice(&mut self, text: impl Into<String>, level: NoticeLevel) {
        self.transcript.push(Block::Notice {
            text: text.into(),
            level,
        });
    }

    /// Flushes the assistant buffer as a completed block when non-empty.
    ///
    /// An empty buffer produces no block (a turn with only tool calls should
    /// not leave an empty assistant block behind).  No-op when not buffering.
    fn flush_buffer(&mut self) {
        if let Some(buf) = self.assistant_buffer.take() {
            if !buf.is_empty() {
                self.transcript.push(Block::Assistant {
                    text: buf,
                    complete: true,
                });
            }
        }
        self.assistant_buffer = None;
        self.buffer_rewritten_by_hook = false;
    }

    /// On interruption or error: flushes the buffer only if the hook already
    /// ran and rewrote the content; otherwise withholds the raw model text and
    /// adds a notice.
    ///
    /// Prevents surfacing unreviewed model output when an AgentResponse
    /// redaction hook was supposed to inspect it but the turn ended first.
    /// Rule from C# `UiReducer.FlushOrWithholdAssistantBuffer`.
    fn flush_or_withhold_buffer(&mut self) {
        let Some(buf) = self.assistant_buffer.take() else {
            return;
        };
        if !self.buffer_rewritten_by_hook && !buf.is_empty() {
            // Withhold: show a notice instead of the raw text.
            self.notice(
                "[response withheld — interrupted before the redaction hook ran]",
                NoticeLevel::Warning,
            );
        } else {
            // Hook ran (or buffer is empty): show what the hook put in.
            if !buf.is_empty() {
                self.transcript.push(Block::Assistant {
                    text: buf,
                    complete: true,
                });
            }
        }
        self.assistant_buffer = None;
        self.buffer_rewritten_by_hook = false;
    }
}

fn map_status(status: Option<ToolCallStatus>, is_error: bool) -> CallStatus {
    match status {
        Some(ToolCallStatus::Pending) => CallStatus::Pending,
        Some(ToolCallStatus::AwaitingApproval) => CallStatus::AwaitingApproval,
        Some(ToolCallStatus::Running) => CallStatus::Running,
        Some(ToolCallStatus::Succeeded) => CallStatus::Succeeded,
        Some(ToolCallStatus::Failed) => CallStatus::Failed,
        Some(ToolCallStatus::Cancelled) => CallStatus::Cancelled,
        Some(ToolCallStatus::Skipped) => CallStatus::Skipped,
        // The engine may omit the status; the error flag still tells us enough.
        None if is_error => CallStatus::Failed,
        None => CallStatus::Succeeded,
    }
}

fn default_timestamp() -> String {
    use time::OffsetDateTime;
    let now = OffsetDateTime::now_local().unwrap_or_else(|_| OffsetDateTime::now_utc());
    format!("{:02}:{:02}", now.hour(), now.minute())
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_proto::events::ToolCallStatus;

    fn fixed_clock() -> String {
        "09:41".to_string()
    }

    fn state() -> UiState {
        UiState::with_clock(fixed_clock)
    }

    fn correlation(call_id: &str) -> Correlation {
        Correlation {
            root_turn_id: Some("t1".into()),
            activity_id: Some("a1".into()),
            call_id: Some(call_id.into()),
            source_id: Some("root:t1".into()),
        }
    }

    fn assistant_text(state: &UiState) -> Option<&str> {
        state.transcript.blocks().iter().find_map(|b| match b {
            Block::Assistant { text, .. } => Some(text.as_str()),
            _ => None,
        })
    }

    fn tools(state: &UiState) -> Option<&ToolActivity> {
        state.transcript.blocks().iter().find_map(|b| match b {
            Block::Tools { activity, .. } => Some(activity),
            _ => None,
        })
    }

    #[test]
    fn starts_in_the_initializing_activity() {
        assert_eq!(state().activity, Activity::Initializing);
    }

    #[test]
    fn becomes_ready_once_connected() {
        let mut state = state();
        state.apply(UiEvent::Connected {
            session_id: "s1".into(),
        });
        assert_eq!(state.activity, Activity::Ready);
        assert_eq!(state.session_id.as_deref(), Some("s1"));
        assert!(!state.is_busy());
    }

    #[test]
    fn a_submission_appends_a_user_block_and_starts_working() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "hi".into() });

        assert_eq!(state.activity, Activity::Working);
        assert!(state.is_busy());
        match &state.transcript.blocks()[0] {
            Block::User {
                text,
                timestamp,
                pending,
                ..
            } => {
                assert_eq!(text, "hi");
                assert_eq!(timestamp, "09:41");
                assert!(!pending);
            }
            other => panic!("expected a user block, got {other:?}"),
        }
    }

    #[test]
    fn assistant_deltas_accumulate_into_one_block() {
        let mut state = state();
        for delta in ["Hel", "lo ", "world"] {
            state.apply(UiEvent::Engine(Event::AssistantText {
                delta: delta.into(),
            }));
        }
        assert_eq!(assistant_text(&state), Some("Hello world"));
        assert_eq!(state.transcript.len(), 1);
    }

    #[test]
    fn an_empty_delta_does_not_open_a_block() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: String::new() }));
        assert!(state.transcript.is_empty());
    }

    #[test]
    fn completing_assistant_text_closes_the_block() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "hi".into() }));
        state.apply(UiEvent::Engine(Event::AssistantTextComplete));

        assert!(state.transcript.open_tail().is_none());
    }

    #[test]
    fn text_after_a_completed_block_starts_a_new_one() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "one".into() }));
        state.apply(UiEvent::Engine(Event::AssistantTextComplete));
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "two".into() }));

        assert_eq!(state.transcript.len(), 2);
    }

    #[test]
    fn thinking_deltas_accumulate_and_set_the_activity() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "hmm".into() }));
        assert_eq!(state.activity, Activity::Thinking);

        state.apply(UiEvent::Engine(Event::Thinking { delta: "...".into() }));
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 2500,
            thinking_tokens: Some(90),
        }));

        match &state.transcript.blocks()[0] {
            Block::Thinking {
                text,
                elapsed_ms,
                tokens,
                complete,
                ..
            } => {
                assert_eq!(text, "hmm...");
                assert_eq!(*elapsed_ms, 2500);
                assert_eq!(*tokens, Some(90));
                assert!(complete);
            }
            other => panic!("expected a thinking block, got {other:?}"),
        }
        assert_eq!(state.activity, Activity::Working);
    }

    #[test]
    fn tool_calls_group_into_one_batch() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "grep".into(),
            input_json: "{}".into(),
            correlation: correlation("c2"),
        }));

        assert_eq!(state.transcript.len(), 1);
        assert_eq!(tools(&state).expect("a batch").calls.len(), 2);
    }

    #[test]
    fn a_tool_result_is_matched_to_its_call_by_correlation_id() {
        let mut state = state();
        for (name, id) in [("read_file", "c1"), ("grep", "c2")] {
            state.apply(UiEvent::Engine(Event::ToolCall {
                tool_name: name.into(),
                input_json: "{}".into(),
                correlation: correlation(id),
            }));
        }

        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "grep".into(),
            content: "found".into(),
            is_error: false,
            status: Some(ToolCallStatus::Succeeded),
            correlation: correlation("c2"),
        }));

        let calls = &tools(&state).expect("a batch").calls;
        assert_eq!(calls[0].status, CallStatus::Running, "first call untouched");
        assert_eq!(calls[1].status, CallStatus::Succeeded);
        assert_eq!(calls[1].result.as_deref(), Some("found"));
    }

    #[test]
    fn two_calls_to_the_same_tool_stay_distinct() {
        let mut state = state();
        for id in ["c1", "c2"] {
            state.apply(UiEvent::Engine(Event::ToolCall {
                tool_name: "read_file".into(),
                input_json: "{}".into(),
                correlation: correlation(id),
            }));
        }
        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "read_file".into(),
            content: "second".into(),
            is_error: false,
            status: Some(ToolCallStatus::Succeeded),
            correlation: correlation("c2"),
        }));

        let calls = &tools(&state).expect("a batch").calls;
        assert_eq!(calls[0].result, None);
        assert_eq!(calls[1].result.as_deref(), Some("second"));
    }

    #[test]
    fn a_result_without_correlation_ids_falls_back_to_the_tool_name() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: Correlation::default(),
        }));
        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "read_file".into(),
            content: "body".into(),
            is_error: false,
            status: None,
            correlation: Correlation::default(),
        }));

        let calls = &tools(&state).expect("a batch").calls;
        assert_eq!(calls.len(), 1, "should not have created a second call");
        assert_eq!(calls[0].result.as_deref(), Some("body"));
    }

    #[test]
    fn an_orphan_result_is_still_shown() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "never_called".into(),
            content: "surprise".into(),
            is_error: true,
            status: None,
            correlation: correlation("zz"),
        }));

        let calls = &tools(&state).expect("a batch").calls;
        assert_eq!(calls.len(), 2);
        assert_eq!(calls[1].name, "never_called");
        assert_eq!(calls[1].status, CallStatus::Failed);
    }

    #[test]
    fn a_missing_status_is_inferred_from_the_error_flag() {
        assert_eq!(map_status(None, false), CallStatus::Succeeded);
        assert_eq!(map_status(None, true), CallStatus::Failed);
    }

    #[test]
    fn tool_progress_updates_the_elapsed_time() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "run_command".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::ToolProgress {
            tool_name: "run_command".into(),
            elapsed_ms: 1500,
            correlation: correlation("c1"),
        }));

        assert_eq!(tools(&state).expect("a batch").calls[0].elapsed_ms, Some(1500));
    }

    #[test]
    fn turn_complete_closes_the_batch_and_returns_to_ready() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        }));

        assert!(tools(&state).expect("a batch").complete);
        assert_eq!(state.activity, Activity::Ready);
    }

    #[test]
    fn an_interrupted_turn_adds_a_warning() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: None,
            interrupted: true,
            root_turn_id: None,
            activity_id: None,
        }));

        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::Notice { level: NoticeLevel::Warning, .. })
        ));
    }

    #[test]
    fn an_engine_error_becomes_an_error_notice() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Error {
            message: "provider returned 400".into(),
        }));

        match state.transcript.blocks().last() {
            Some(Block::Notice { text, level }) => {
                assert_eq!(text, "provider returned 400");
                assert_eq!(*level, NoticeLevel::Error);
            }
            other => panic!("expected an error notice, got {other:?}"),
        }
    }

    #[test]
    fn a_limit_is_reported_as_a_warning_not_an_error() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::LimitReached {
            kind: "max_tokens".into(),
            message: "hit the cap".into(),
        }));
        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::Notice { level: NoticeLevel::Warning, .. })
        ));
    }

    #[test]
    fn usage_events_update_the_counters() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Usage {
            input_tokens: 1200,
            output_tokens: 300,
        }));
        assert_eq!(state.usage.input_tokens, 1200);
        assert_eq!(state.usage.output_tokens, 300);
    }

    #[test]
    fn usage_percentage_needs_a_known_context_limit() {
        let mut usage = Usage {
            input_tokens: 50_000,
            output_tokens: 0,
            context_limit: 0,
        };
        assert_eq!(usage.percent_used(), None);

        usage.context_limit = 200_000;
        assert_eq!(usage.percent_used(), Some(25));
    }

    #[test]
    fn usage_percentage_is_clamped_at_one_hundred() {
        let usage = Usage {
            input_tokens: 400_000,
            output_tokens: 0,
            context_limit: 200_000,
        };
        assert_eq!(usage.percent_used(), Some(100));
    }

    #[test]
    fn queued_messages_appear_as_pending_user_blocks() {
        let mut state = state();
        state.apply(UiEvent::Queued {
            text: "later".into(),
            id: Some("s1".into()),
        });

        assert_eq!(state.queued.len(), 1);
        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::User { pending: true, .. })
        ));
    }

    #[test]
    fn delivered_steering_clears_the_pending_marker() {
        let mut state = state();
        state.apply(UiEvent::Queued {
            text: "later".into(),
            id: Some("s1".into()),
        });
        state.apply(UiEvent::Engine(Event::SteeringDelivered {
            message_ids: vec!["s1".into()],
        }));

        assert!(state.queued.is_empty());
        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::User { pending: false, .. })
        ));
    }

    #[test]
    fn a_permission_prompt_moves_to_waiting_then_records_the_decision() {
        let mut state = state();
        state.apply(UiEvent::PromptRequested(PendingPrompt::Permission {
            tool: "run_command".into(),
            preview: "rm -rf /".into(),
        }));
        assert_eq!(state.activity, Activity::Waiting);
        assert!(state.prompt.is_some());

        state.apply(UiEvent::PromptAnswered {
            allowed: false,
            answer: None,
        });

        assert!(state.prompt.is_none());
        assert_eq!(state.activity, Activity::Working);
        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::Permission { decision: PermissionDecision::Denied, .. })
        ));
    }

    #[test]
    fn a_question_records_its_answer() {
        let mut state = state();
        state.apply(UiEvent::PromptRequested(PendingPrompt::Question {
            question: "Which?".into(),
            options: vec!["a".into()],
            multi_select: false,
            allow_free_text: true,
        }));
        state.apply(UiEvent::PromptAnswered {
            allowed: true,
            answer: Some("a".into()),
        });

        match state.transcript.blocks().last() {
            Some(Block::Question { question, answer }) => {
                assert_eq!(question, "Which?");
                assert_eq!(answer.as_deref(), Some("a"));
            }
            other => panic!("expected a question block, got {other:?}"),
        }
    }

    #[test]
    fn interrupt_is_ignored_when_idle() {
        let mut state = state();
        state.apply(UiEvent::Connected {
            session_id: "s".into(),
        });
        state.apply(UiEvent::InterruptRequested);
        assert!(!state.interrupting);
    }

    #[test]
    fn interrupt_is_recorded_while_busy() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "go".into() });
        state.apply(UiEvent::InterruptRequested);
        assert!(state.interrupting);

        state.apply(UiEvent::TurnFinished {
            interrupted: true,
            error: None,
        });
        assert!(!state.interrupting);
    }

    #[test]
    fn a_failed_turn_surfaces_its_error() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "go".into() });
        state.apply(UiEvent::TurnFinished {
            interrupted: false,
            error: Some("boom".into()),
        });

        assert_eq!(state.activity, Activity::Ready);
        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::Notice { text, level: NoticeLevel::Error }) if text == "boom"
        ));
    }

    #[test]
    fn clearing_empties_the_transcript_and_the_queue() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "hi".into() });
        state.apply(UiEvent::Queued {
            text: "later".into(),
            id: None,
        });
        state.apply(UiEvent::Cleared);

        assert!(state.transcript.is_empty());
        assert!(state.queued.is_empty());
    }

    #[test]
    fn changing_the_model_updates_the_context_limit() {
        let mut state = state();
        state.apply(UiEvent::ModelChanged {
            id: "gpt-5".into(),
            context_limit: Some(400_000),
        });
        assert_eq!(state.model.as_deref(), Some("gpt-5"));
        assert_eq!(state.usage.context_limit, 400_000);
    }

    #[test]
    fn unknown_events_are_ignored_without_disturbing_state() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "hi".into() }));
        let before = state.transcript.len();

        state.apply(UiEvent::Engine(Event::Unknown {
            method: "event/futureThing".into(),
            params: None,
        }));
        state.apply(UiEvent::Engine(Event::StreamProgress {
            phase: "progress".into(),
            chunks: 1,
            chars: 2,
            elapsed_ms: 3,
        }));

        assert_eq!(state.transcript.len(), before);
        assert_eq!(assistant_text(&state), Some("hi"));
    }

    #[test]
    fn a_result_still_lands_after_text_opened_a_new_batch() {
        // Interleaved text is common; the result for an earlier call must be
        // routed back to the batch that owns it, not appended to the new one.
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::AssistantText {
            delta: "thinking about it".into(),
        }));
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "grep".into(),
            input_json: "{}".into(),
            correlation: correlation("c2"),
        }));

        // The late result for the *first* call arrives now.
        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "read_file".into(),
            content: "file body".into(),
            is_error: false,
            status: Some(ToolCallStatus::Succeeded),
            correlation: correlation("c1"),
        }));

        let batches: Vec<&ToolActivity> = state
            .transcript
            .blocks()
            .iter()
            .filter_map(|b| match b {
                Block::Tools { activity, .. } => Some(activity),
                _ => None,
            })
            .collect();

        assert_eq!(batches.len(), 2, "text should have opened a second batch");
        assert_eq!(batches[0].calls.len(), 1, "no orphan was appended");
        assert_eq!(
            batches[0].calls[0].result.as_deref(),
            Some("file body"),
            "the result did not reach the batch that owns the call"
        );
        assert_eq!(batches[1].calls.len(), 1);
        assert!(batches[1].calls[0].result.is_none());
    }

    #[test]
    fn interleaved_text_and_tools_keep_their_transcript_order() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "read_file".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::AssistantText {
            delta: "between".into(),
        }));
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "grep".into(),
            input_json: "{}".into(),
            correlation: correlation("c2"),
        }));

        let kinds: Vec<&str> = state
            .transcript
            .blocks()
            .iter()
            .map(|b| match b {
                Block::Tools { .. } => "tools",
                Block::Assistant { .. } => "assistant",
                _ => "other",
            })
            .collect();

        assert_eq!(kinds, vec!["tools", "assistant", "tools"]);
    }

    #[test]
    fn an_interrupted_batch_resolves_its_unfinished_calls() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "run_command".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: None,
            interrupted: true,
            root_turn_id: Some("t1".into()),
            activity_id: None,
        }));

        let calls = &tools(&state).expect("a batch").calls;
        assert_eq!(
            calls[0].status,
            CallStatus::Cancelled,
            "a running call must not stay running after the turn ends"
        );
    }

    #[test]
    fn finalising_resolves_every_open_batch_not_just_the_last() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "a".into(),
            input_json: "{}".into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "x".into() }));
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "b".into(),
            input_json: "{}".into(),
            correlation: correlation("c2"),
        }));
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: None,
            interrupted: true,
            root_turn_id: Some("t1".into()),
            activity_id: None,
        }));

        for block in state.transcript.blocks() {
            if let Block::Tools { activity, .. } = block {
                assert!(activity.complete, "a batch was left open");
                for call in &activity.calls {
                    assert!(
                        call.status.is_terminal(),
                        "call {:?} was left unresolved",
                        call.name
                    );
                }
            }
        }
    }

    #[test]
    fn undelivered_queued_messages_are_dropped_when_the_turn_ends() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "go".into() });
        state.apply(UiEvent::Queued {
            text: "never delivered".into(),
            id: Some("s1".into()),
        });
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        }));

        assert!(state.queued.is_empty());
        assert!(
            !state
                .transcript
                .blocks()
                .iter()
                .any(|b| matches!(b, Block::User { pending: true, .. })),
            "an undelivered message was left in the transcript"
        );
    }

    #[test]
    fn steering_delivery_matches_by_id_not_position() {
        let mut state = state();
        state.apply(UiEvent::Submitted { text: "go".into() });
        for id in ["s1", "s2", "s3"] {
            state.apply(UiEvent::Queued {
                text: id.to_string(),
                id: Some(id.to_string()),
            });
        }

        // Only the middle one is delivered.
        state.apply(UiEvent::Engine(Event::SteeringDelivered {
            message_ids: vec!["s2".into()],
        }));

        let pending: Vec<&str> = state
            .transcript
            .blocks()
            .iter()
            .filter_map(|b| match b {
                Block::User {
                    text,
                    pending: true,
                    ..
                } => Some(text.as_str()),
                _ => None,
            })
            .collect();

        assert_eq!(pending, vec!["s1", "s3"], "the wrong message was promoted");
    }

    #[test]
    fn a_delivery_for_an_unknown_id_promotes_nothing() {
        let mut state = state();
        state.apply(UiEvent::Queued {
            text: "queued".into(),
            id: Some("s1".into()),
        });
        state.apply(UiEvent::Engine(Event::SteeringDelivered {
            message_ids: vec!["other".into()],
        }));

        assert!(matches!(
            state.transcript.blocks().last(),
            Some(Block::User { pending: true, .. })
        ));
    }

    #[test]
    fn a_full_turn_produces_the_expected_block_sequence() {        let mut state = state();
        state.apply(UiEvent::Connected {
            session_id: "s1".into(),
        });
        state.apply(UiEvent::Submitted {
            text: "fix the build".into(),
        });
        state.apply(UiEvent::Engine(Event::AssistantText {
            delta: "Looking at it.".into(),
        }));
        state.apply(UiEvent::Engine(Event::AssistantTextComplete));
        state.apply(UiEvent::Engine(Event::ToolCall {
            tool_name: "run_command".into(),
            input_json: r#"{"command":"cargo build"}"#.into(),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::ToolResult {
            tool_name: "run_command".into(),
            content: "ok".into(),
            is_error: false,
            status: Some(ToolCallStatus::Succeeded),
            correlation: correlation("c1"),
        }));
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        }));

        let kinds: Vec<&str> = state
            .transcript
            .blocks()
            .iter()
            .map(|b| match b {
                Block::User { .. } => "user",
                Block::Assistant { .. } => "assistant",
                Block::Tools { .. } => "tools",
                Block::Notice { .. } => "notice",
                _ => "other",
            })
            .collect();

        assert_eq!(kinds, vec!["user", "assistant", "tools"]);
        assert_eq!(state.activity, Activity::Ready);
        assert!(!state.is_busy());
    }

    #[test]
    fn diff_output_event_adds_a_diff_block_to_the_transcript() {
        let raw = "diff --git a/foo.rs b/foo.rs\n\
            --- a/foo.rs\n\
            +++ b/foo.rs\n\
            @@ -1 +1 @@\n\
            -old\n\
            +new\n";
        let mut state = state();
        state.apply(UiEvent::DiffOutput { text: raw.to_string() });
        assert!(
            matches!(state.transcript.blocks().last(), Some(Block::Diff { .. })),
            "expected a Diff block"
        );
    }

    #[test]
    fn thinking_complete_without_prior_delta_is_a_no_op() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 500,
            thinking_tokens: None,
        }));
        assert!(
            state.transcript.is_empty(),
            "a ThinkingComplete with no preceding delta must not create a block"
        );
    }

    #[test]
    fn a_second_thinking_burst_does_not_append_to_the_completed_first_burst() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "first".into() }));
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 100,
            thinking_tokens: None,
        }));
        // Second burst starts a new block, not an append.
        state.apply(UiEvent::Engine(Event::Thinking { delta: "second".into() }));
        assert_eq!(state.transcript.len(), 2, "expected two separate thinking blocks");
        match &state.transcript.blocks()[1] {
            Block::Thinking { text, complete, .. } => {
                assert_eq!(text, "second");
                assert!(!complete, "second burst should still be open");
            }
            other => panic!("expected Thinking block, got {other:?}"),
        }
    }

    #[test]
    fn turn_finished_finalizes_an_open_thinking_block() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "reasoning".into() }));
        // Turn ends without an explicit ThinkingComplete.
        state.apply(UiEvent::TurnFinished {
            interrupted: false,
            error: None,
        });
        match &state.transcript.blocks()[0] {
            Block::Thinking { complete, .. } => {
                assert!(complete, "thinking block must be marked complete when the turn ends");
            }
            other => panic!("expected Thinking block, got {other:?}"),
        }
    }

    #[test]
    fn already_complete_thinking_block_elapsed_ms_is_not_clobbered_by_turn_finish() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "reasoning".into() }));
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 1000,
            thinking_tokens: Some(42),
        }));
        state.apply(UiEvent::TurnFinished {
            interrupted: false,
            error: None,
        });
        match &state.transcript.blocks()[0] {
            Block::Thinking { elapsed_ms, tokens, .. } => {
                assert_eq!(*elapsed_ms, 1000, "frozen elapsed_ms must not be clobbered");
                assert_eq!(*tokens, Some(42), "frozen tokens must not be clobbered");
            }
            other => panic!("expected Thinking block, got {other:?}"),
        }
    }

    // ---- assistant buffering (withhold-on-interrupt) --------------------

    fn start_buffered_turn(state: &mut UiState) {
        state.apply(UiEvent::EnableAssistantBuffering);
        state.apply(UiEvent::Submitted { text: "go".into() });
    }

    #[test]
    fn enable_buffering_sets_the_buffer_to_empty_string() {
        let mut state = state();
        state.apply(UiEvent::EnableAssistantBuffering);
        assert!(state.assistant_buffer.is_some());
        assert_eq!(state.assistant_buffer.as_deref(), Some(""));
    }

    #[test]
    fn assistant_deltas_accumulate_in_buffer_when_buffering() {
        let mut state = state();
        start_buffered_turn(&mut state);
        for delta in ["Hel", "lo", " world"] {
            state.apply(UiEvent::Engine(Event::AssistantText { delta: delta.into() }));
        }
        // Nothing in the transcript yet.
        assert!(assistant_text(&state).is_none());
        assert_eq!(state.assistant_buffer.as_deref(), Some("Hello world"));
    }

    #[test]
    fn thinking_is_suppressed_during_buffered_turn() {
        let mut state = state();
        start_buffered_turn(&mut state);
        state.apply(UiEvent::Engine(Event::Thinking { delta: "reasoning".into() }));
        state.apply(UiEvent::Engine(Event::ThinkingComplete {
            elapsed_ms: 100,
            thinking_tokens: None,
        }));
        assert!(
            !state.transcript.blocks().iter().any(|b| matches!(b, Block::Thinking { .. })),
            "thinking blocks must be suppressed during buffered turns"
        );
    }

    #[test]
    fn buffer_is_flushed_as_complete_block_on_successful_turn() {
        let mut state = state();
        start_buffered_turn(&mut state);
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "answer".into() }));
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        }));
        assert!(state.assistant_buffer.is_none(), "buffer must be cleared after flush");
        assert_eq!(
            assistant_text(&state),
            Some("answer"),
            "buffered text must appear in the transcript on success"
        );
        match state.transcript.blocks().iter().find(|b| matches!(b, Block::Assistant { .. })) {
            Some(Block::Assistant { complete, .. }) => assert!(*complete, "flushed block must be complete"),
            _ => panic!("no assistant block found"),
        }
    }

    #[test]
    fn empty_buffer_on_success_produces_no_assistant_block() {
        let mut state = state();
        start_buffered_turn(&mut state);
        // No deltas — turn ends immediately.
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        }));
        assert!(
            !state.transcript.blocks().iter().any(|b| matches!(b, Block::Assistant { .. })),
            "an empty buffer must not produce an assistant block"
        );
    }

    #[test]
    fn buffer_is_withheld_on_interrupt_when_hook_never_ran() {
        let mut state = state();
        start_buffered_turn(&mut state);
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "secret".into() }));
        // Turn interrupted, hook never ran.
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: None,
            interrupted: true,
            root_turn_id: None,
            activity_id: None,
        }));
        assert!(state.assistant_buffer.is_none());
        // Raw text must NOT appear.
        assert!(
            assistant_text(&state).is_none(),
            "withheld buffer must not show raw model text"
        );
        // Instead a warning notice.
        assert!(
            state.transcript.blocks().iter().any(|b| {
                matches!(b, Block::Notice { level: NoticeLevel::Warning, text }
                    if text.contains("withheld"))
            }),
            "expected a 'withheld' notice"
        );
    }

    #[test]
    fn buffer_is_flushed_on_interrupt_when_hook_already_rewrote_it() {
        let mut state = state();
        start_buffered_turn(&mut state);
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "raw".into() }));
        // Hook rewrites the buffer.
        state.apply(UiEvent::Engine(Event::ResponseRewritten {
            hook_command: "redact".into(),
            original_response: "raw".into(),
            display_content: "sanitized".into(),
            modified_response: None,
        }));
        assert!(state.buffer_rewritten_by_hook);
        // Now interrupt.
        state.apply(UiEvent::Engine(Event::TurnComplete {
            stop_reason: None,
            interrupted: true,
            root_turn_id: None,
            activity_id: None,
        }));
        // Hook's version must appear.
        assert_eq!(
            assistant_text(&state),
            Some("sanitized"),
            "hook-rewritten buffer must be flushed even on interrupt"
        );
        assert!(state.assistant_buffer.is_none());
        assert!(!state.buffer_rewritten_by_hook);
    }

    #[test]
    fn response_rewritten_while_not_buffering_is_a_noop() {
        let mut state = state();
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "normal".into() }));
        // No buffering active; ResponseRewritten must not change anything.
        state.apply(UiEvent::Engine(Event::ResponseRewritten {
            hook_command: "redact".into(),
            original_response: "normal".into(),
            display_content: "changed".into(),
            modified_response: None,
        }));
        assert!(state.assistant_buffer.is_none());
        assert_eq!(assistant_text(&state), Some("normal"));
    }

    #[test]
    fn enable_buffering_is_idempotent() {
        let mut state = state();
        state.apply(UiEvent::EnableAssistantBuffering);
        state.apply(UiEvent::Engine(Event::AssistantText { delta: "first".into() }));
        // Second enable must not reset the buffer.
        state.apply(UiEvent::EnableAssistantBuffering);
        assert_eq!(
            state.assistant_buffer.as_deref(),
            Some("first"),
            "second EnableAssistantBuffering must not clear accumulated text"
        );
    }

    #[test]
    fn folding_goes_through_the_reducer_so_the_layout_is_rebuilt() {
        // The fold must be an event, not a direct call on the transcript.
        // Everything that changes rendered rows invalidates the cached layout
        // by passing through here; a direct call skipped that, so the block
        // flipped internally and the screen stayed exactly as it was --
        // meaning a click did nothing at all on a finished turn.
        let mut state = state();
        state.apply(UiEvent::Engine(Event::Thinking { delta: "reasoning".into() }));

        let expanded = |s: &UiState| match &s.transcript.blocks()[0] {
            Block::Thinking { expanded, .. } => *expanded,
            other => panic!("expected a thinking block, got {other:?}"),
        };
        assert!(!expanded(&state), "reasoning should start folded");

        state.apply(UiEvent::ThinkingFoldToggled { block: 0 });
        assert!(expanded(&state), "the event did not open the block");

        state.apply(UiEvent::ThinkingFoldToggled { block: 0 });
        assert!(!expanded(&state), "the event did not close the block again");
    }

    #[test]
    fn folding_a_block_that_is_not_there_is_ignored() {
        let mut state = state();
        state.apply(UiEvent::ThinkingFoldToggled { block: 99 });
        assert!(state.transcript.blocks().is_empty());
    }
}