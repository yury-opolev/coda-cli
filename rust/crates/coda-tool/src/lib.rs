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
    AnswerOutcome, NoAnswerReason, OpaqueServiceHandle, PlanApprover, ServiceMap, ToolContext,
    ToolDescriptor, UserQuestion,
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
            None => None,
        }
    }
}

/// Alias kept intentionally separate from `ToolResult` so a future `shape_delta`
/// field can be threaded through the call site without changing every tool impl.
pub type ToolOutcome = ToolResult;

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
