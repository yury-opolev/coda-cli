//! The tool contract: the trait every tool implements and the result it returns.

use async_trait::async_trait;
use coda_llm::ToolDefinition;
use tokio_util::sync::CancellationToken;

pub mod context;
pub mod name_filter;
pub mod quarantine;
pub mod registry;

pub use context::{PlanApprover, ToolContext, ToolDescriptor, UserQuestion};
pub use name_filter::ToolNameFilter;
pub use quarantine::ToolQuarantine;
pub use registry::ToolRegistry;

/// The output of running a tool, fed back to the model as a content block.
///
/// Errors are returned as values with `is_error = true` rather than `Err` so
/// the model always receives the outcome. `Result::Err` is reserved for the
/// caller-cancel propagation path only — the loop distinguishes these via the
/// cancellation token, not via a Rust error.
///
/// Note: `shape_delta` (for skill tools that reshape the current turn) will be
/// added when `TurnShape` is defined in the loop spec.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolResult {
    pub content: String,
    pub is_error: bool,
}

impl ToolResult {
    pub fn ok(content: impl Into<String>) -> Self {
        Self { content: content.into(), is_error: false }
    }

    pub fn error(content: impl Into<String>) -> Self {
        Self { content: content.into(), is_error: true }
    }
}

/// Alias kept intentionally separate from `ToolResult` so a future `shape_delta`
/// field can be threaded through the call site without changing every tool impl.
pub type ToolOutcome = ToolResult;

/// An executable tool the model can call.
///
/// Object-safe via `async_trait`; lives in the registry as `Arc<dyn Tool>`.
#[async_trait]
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
        cancel: CancellationToken,
    ) -> ToolOutcome;
    /// The wire definition advertised to the model.
    fn to_definition(&self) -> ToolDefinition {
        ToolDefinition::new(self.name(), self.description(), self.input_schema_json())
    }
}
