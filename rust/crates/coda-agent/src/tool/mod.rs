//! The tool contract and registry.
//!
//! Core types (`Tool`, `ToolContext`, `ToolResult`, `ToolOutcome`,
//! `ToolDescriptor`) are defined in `coda-tool` and re-exported here so
//! existing `crate::tool::*` paths continue to work unchanged (MINOR 4).

pub mod context;
pub mod name_filter;
pub mod quarantine;
pub mod registry;

// ── Re-exports from coda-tool ─────────────────────────────────────────────────
pub use coda_tool::{Tool, ToolOutcome, ToolResult};
pub use context::{
    OpaqueServiceHandle, PlanApprover, TodoItem, TodoStatus, TodoStore, ToolContext,
    ToolContextServiceExt, ToolDescriptor, UserQuestion,
    is_within_root, resolve_path, try_resolve_within_root,
};
pub use name_filter::ToolNameFilter;
pub use quarantine::ToolQuarantine;
pub use registry::ToolRegistry;