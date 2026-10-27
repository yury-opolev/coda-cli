//! Shared `Tool` trait, `ToolContext`, `ToolResult`, and path sandbox.
//!
//! This is a deliberately thin leaf crate that carries no engine-specific
//! dependencies (no task manager, no LSP, no agent loop).  Both `coda-agent`
//! and `coda-mcp` depend on it, which breaks the previous
//! `coda-mcp → coda-agent` edge that caused the whole engine to be compiled
//! transitively for the TUI (MINOR 4).
//!
//! # Dependency direction
//! ```text
//! coda-agent ──depends on──► coda-tool
//! coda-mcp   ──depends on──► coda-tool   (no longer on coda-agent)
//! ```

pub mod context;
pub mod sandbox;

pub use context::{
    AnswerOutcome, NoAnswerReason, OpaqueServiceHandle, PlanApprover, ScheduleOrigin, ServiceMap,
    ToolContext, ToolDescriptor, UserQuestion,
};
pub use sandbox::{is_within_root, resolve_path, try_resolve_within_root};

/// A control signal a tool can raise alongside its result.
///
/// Ordinary failures are values (`is_error = true`) the model is expected to
/// read and react to. A control signal is different in kind: it says the run
/// itself must not continue. It exists so a terminal outcome is a **typed
/// path** rather than a tool-name special case or a forged marker string in
/// `content` that some future caller could produce by accident.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ToolControl {
    /// Stop the run now. The result block is still recorded (a `tool_use`
    /// with no matching `tool_result` is not a valid conversation), but the
    /// loop must **not** issue a follow-up model request on the strength of
    /// it, and no caller may report the run as a success.
    ///
    /// `reason` is a stable classification token (e.g.
    /// `"question.noAnswer.disconnected"`), never free provider text.
    AbortRun { reason: String },
    /// The tool registered its work as a managed task that keeps running
    /// after `execute` returns. The batch executor waits for it under the
    /// yield contract: it returns the task's own result when it finishes
    /// within `wait`, and a task handle otherwise. `None` waits until the
    /// task finishes (still bounded by the per-tool ceiling and cut short by
    /// a user "background now"). `content` of the carrying result is unused.
    RunningTask { task_id: String, wait: Option<std::time::Duration> },
    /// Attach to an already running task (`task_wait`): the executor waits up
    /// to `wait` for it under the same rules as `RunningTask` — cut short by
    /// a user/parent message or a cancelled turn — but never stops, promotes
    /// or otherwise touches the task, and never skips the rest of the batch.
    /// `content` of the carrying result is what the model sees when the wait
    /// ends before the task does.
    AttachTask { task_id: String, wait: std::time::Duration },
}

/// The output of running a tool, fed back to the model as a content block.
///
/// Errors are returned as values with `is_error = true` rather than `Err` so
/// the model always receives the outcome.  `Result::Err` is reserved for the
/// caller-cancel propagation path only — the loop distinguishes these via the
/// cancellation token, not via a Rust error.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolResult {
    pub content: String,
    pub is_error: bool,
    /// `None` for every ordinary result. See [`ToolControl`].
    pub control: Option<ToolControl>,
}

impl ToolResult {
    pub fn ok(content: impl Into<String>) -> Self {
        Self { content: content.into(), is_error: false, control: None }
    }

    pub fn error(content: impl Into<String>) -> Self {
        Self { content: content.into(), is_error: true, control: None }
    }

    /// A terminal failure: the recorded result plus an explicit instruction
    /// to stop the run. Always an error result — an abort is never a success.
    pub fn abort(reason: impl Into<String>, content: impl Into<String>) -> Self {
        Self {
            content: content.into(),
            is_error: true,
            control: Some(ToolControl::AbortRun { reason: reason.into() }),
        }
    }

    /// The abort reason, when this result demands the run stop.
    pub fn abort_reason(&self) -> Option<&str> {
        match &self.control {
            Some(ToolControl::AbortRun { reason }) => Some(reason.as_str()),
            Some(ToolControl::RunningTask { .. }) | Some(ToolControl::AttachTask { .. }) | None => {
                None
            }
        }
    }

    /// A result asking the executor to wait (bounded) on a running task.
    /// `content` is the answer used if the wait ends first.
    pub fn attach_task(
        task_id: impl Into<String>,
        wait: std::time::Duration,
        content: impl Into<String>,
    ) -> Self {
        Self {
            content: content.into(),
            is_error: false,
            control: Some(ToolControl::AttachTask { task_id: task_id.into(), wait }),
        }
    }

    /// The task id and wait, when this result attaches to a running task.
    pub fn attached_task(&self) -> Option<(&str, std::time::Duration)> {
        match &self.control {
            Some(ToolControl::AttachTask { task_id, wait }) => Some((task_id.as_str(), *wait)),
            _ => None,
        }
    }

    /// A placeholder result handing a running managed task to the executor.
    pub fn running_task(task_id: impl Into<String>, wait: Option<std::time::Duration>) -> Self {
        let task_id = task_id.into();
        Self {
            content: format!("Running as task {task_id}."),
            is_error: false,
            control: Some(ToolControl::RunningTask { task_id, wait }),
        }
    }

    /// The task id and wait budget, when this result hands over a running task.
    pub fn running_task_handle(&self) -> Option<(&str, Option<std::time::Duration>)> {
        match &self.control {
            Some(ToolControl::RunningTask { task_id, wait }) => Some((task_id.as_str(), *wait)),
            _ => None,
        }
    }
}

/// Alias kept intentionally separate from `ToolResult` so a future `shape_delta`
/// field can be threaded through the call site without changing every tool impl.
pub type ToolOutcome = ToolResult;

/// A live, changing set of tools (e.g. MCP servers that reconnect).
///
/// Queried on every registry lookup, so changes show up without rebuilding
/// the registry.
pub trait ToolSource: Send + Sync {
    fn tools(&self) -> Vec<std::sync::Arc<dyn Tool>>;
}

/// An executable tool the model can call.
///
/// Object-safe via `async_trait`; lives in the registry as `Arc<dyn Tool>`.
#[async_trait::async_trait]
pub trait Tool: Send + Sync {
    fn name(&self) -> &str;
    fn description(&self) -> &str;
    /// JSON Schema for the tool's arguments, as a JSON string.
    fn input_schema_json(&self) -> &str;
    /// Read-only tools bypass the permission gate entirely.
    fn is_read_only(&self) -> bool;
    /// Hidden from the inline tool list; discovered on demand via `tool_search`.
    fn should_defer(&self) -> bool {
        false
    }
    /// Curated capability phrase for semantic search; `None` falls back to name and description.
    fn search_hint(&self) -> Option<&str> {
        None
    }
    /// Whether the agent loop may run a call of this tool as a managed task
    /// (non-blocking runtime, generic fallback): awaited under the yield
    /// contract and handed to the background when it runs long. Only for
    /// tools whose work is self-contained and may finish after the model has
    /// moved on (e.g. a remote MCP call); never for tools that edit files or
    /// talk to the operator inline.
    fn detachable(&self) -> bool {
        false
    }
    async fn execute(
        &self,
        input: &serde_json::Value,
        ctx: &ToolContext,
        cancel: tokio_util::sync::CancellationToken,
    ) -> ToolOutcome;
    /// The wire definition advertised to the model.
    fn to_definition(&self) -> coda_llm::ToolDefinition {
        coda_llm::ToolDefinition::new(self.name(), self.description(), self.input_schema_json())
    }
}
