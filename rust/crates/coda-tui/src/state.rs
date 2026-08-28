//! Application state and the reducer that advances it.
//!
//! All mutation flows through [`UiState::apply`]. Keeping transitions in one
//! place means the UI can be tested without a terminal, a running engine, or
//! any async machinery: feed events in, assert on the state that comes out.

use coda_proto::events::ToolCallStatus;
use coda_proto::{Correlation, Event};
use coda_render::tool::{CallStatus, ToolActivity, ToolCall, ToolDisplayMode};

use crate::transcript::{Block, NoticeLevel, PermissionDecision, Transcript};

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
    /// A local status or error line.
    Notice { text: String, level: NoticeLevel },
    /// The transcript was cleared.
    Cleared,
    /// The active model changed.
    ModelChanged { id: String, context_limit: Option<i64> },
    /// The tool display mode changed.
    DisplayModeChanged(ToolDisplayMode),
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
    /// Correlation ids of tool calls, parallel to the calls in the open batch.
    open_calls: Vec<Correlation>,
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
            open_calls: Vec::new(),
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
                });
                self.activity = Activity::Working;
                self.interrupting = false;
            }
            UiEvent::Queued { text, id } => {
                self.queued.push(QueuedMessage {
                    id,
                    text: text.clone(),
                });
                self.transcript.push(Block::User {
                    text,
                    timestamp: (self.clock)(),
                    pending: true,
                });
            }
            UiEvent::TurnFinished { interrupted, error } => {
                self.transcript.close_open();
                self.activity = Activity::Ready;
                self.interrupting = false;
                self.prompt = None;
                self.open_calls.clear();

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
            UiEvent::Notice { text, level } => self.notice(text, level),
            UiEvent::Cleared => {
                self.transcript.clear();
                self.queued.clear();
                self.open_calls.clear();
            }
            UiEvent::ModelChanged { id, context_limit } => {
                self.model = Some(id);
                if let Some(limit) = context_limit {
                    self.usage.context_limit = limit;
                }
            }
            UiEvent::DisplayModeChanged(mode) => self.display_mode = mode,
        }
    }

    fn apply_engine(&mut self, event: Event) {
        match event {
            Event::AssistantText { delta } => {
                if delta.is_empty() {
                    return;
                }
                self.activity = Activity::Working;
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
                if let Some(Block::Assistant { complete, .. }) = self.transcript.open_tail() {
                    *complete = true;
                }
            }
            Event::Thinking { delta } => {
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
                        });
                    }
                }
            }
            Event::ThinkingComplete {
                elapsed_ms,
                thinking_tokens,
            } => {
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
                self.ensure_open_batch();
                if let Some(Block::Tools(activity)) = self.transcript.open_tail() {
                    activity.calls.push(ToolCall::new(tool_name, input_json));
                    self.open_calls.push(correlation);
                }
            }
            Event::ToolProgress {
                elapsed_ms,
                correlation,
                ..
            } => {
                if let Some(index) = self.find_call(&correlation, None) {
                    if let Some(Block::Tools(activity)) = self.transcript.open_tail() {
                        if let Some(call) = activity.calls.get_mut(index) {
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
                let index = self.find_call(&correlation, Some(&tool_name));
                if let Some(Block::Tools(activity)) = self.transcript.open_tail() {
                    let call = match index.and_then(|i| activity.calls.get_mut(i)) {
                        Some(call) => call,
                        None => {
                            // A result with no matching call still deserves to
                            // be shown rather than silently dropped.
                            activity.calls.push(ToolCall::new(&tool_name, "{}"));
                            self.open_calls.push(correlation);
                            activity.calls.last_mut().expect("just pushed")
                        }
                    };
                    call.result = Some(content);
                    call.is_error = is_error;
                    call.status = map_status(status, is_error);
                }
            }
            Event::TurnComplete { interrupted, .. } => {
                self.transcript.close_open();
                self.open_calls.clear();
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
                self.mark_delivered(&message_ids);
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
            // Informational events with no transcript representation.
            Event::Stop { .. }
            | Event::StreamProgress { .. }
            | Event::ScheduleLifecycle { .. }
            | Event::ResponseRewritten { .. }
            | Event::ToolInputModified { .. }
            | Event::ToolResultModified { .. }
            | Event::PermissionsUpdated { .. }
            | Event::Unknown { .. } => {}
        }
    }

    /// Opens a tool batch if the tail is not already one.
    fn ensure_open_batch(&mut self) {
        if matches!(self.transcript.open_tail(), Some(Block::Tools(_))) {
            return;
        }
        self.transcript.close_open();
        self.transcript.push(Block::Tools(ToolActivity::default()));
        self.open_calls.clear();
    }

    /// Locates a call within the open batch.
    ///
    /// Exact correlation is preferred. When the engine omitted ids, falls back
    /// to the most recent unfinished call with the same tool name, which is the
    /// best available guess and matches how a single-threaded turn behaves.
    fn find_call(&self, correlation: &Correlation, tool_name: Option<&str>) -> Option<usize> {
        if correlation.call_id.is_some() {
            if let Some(index) = self
                .open_calls
                .iter()
                .position(|c| c.call_id == correlation.call_id)
            {
                return Some(index);
            }
        }

        let Some(Block::Tools(activity)) = self.transcript.blocks().last() else {
            return None;
        };
        let name = tool_name?;
        activity
            .calls
            .iter()
            .rposition(|c| c.name == name && !c.status.is_terminal())
    }

    /// Drops the `[pending]` marker from delivered queued messages.
    fn mark_delivered(&mut self, _ids: &[String]) {
        // The transcript keeps queued messages in submission order, so the
        // oldest pending blocks are the ones that were just delivered.
        let mut remaining = _ids.len();
        for block in self.transcript.blocks_mut() {
            if remaining == 0 {
                break;
            }
            if let Block::User { pending, .. } = block {
                if *pending {
                    *pending = false;
                    remaining -= 1;
                }
            }
        }
    }

    fn notice(&mut self, text: impl Into<String>, level: NoticeLevel) {
        self.transcript.push(Block::Notice {
            text: text.into(),
            level,
        });
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
            Block::Tools(activity) => Some(activity),
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
    fn a_full_turn_produces_the_expected_block_sequence() {
        let mut state = state();
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
                Block::Tools(_) => "tools",
                Block::Notice { .. } => "notice",
                _ => "other",
            })
            .collect();

        assert_eq!(kinds, vec!["user", "assistant", "tools"]);
        assert_eq!(state.activity, Activity::Ready);
        assert!(!state.is_busy());
    }
}
