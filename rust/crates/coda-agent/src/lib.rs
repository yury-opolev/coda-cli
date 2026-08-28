//! The Coda agent core.
//!
//! This crate is the porting target for the C# `Coda.Agent` library.  It is
//! built incrementally; this module exposes everything that has landed so far.
//!
//! ## Phase 2 — Tool contract and permission system
//!
//! - **`tool`**: the `Tool` trait, `ToolResult`, `ToolOutcome`, `ToolRegistry`,
//!   `ToolNameFilter`, `ToolQuarantine`, and the path sandbox (`ToolContext`).
//! - **`permission`**: `PermissionMode`, `PermissionDecision`,
//!   `PermissionPrompt`, `PermissionPolicy::decide`, `PermissionModeState`,
//!   `PermissionRule`, `PermissionRuleStore`, and the full prompt chain
//!   (`ModePermissionPrompt`, `RulesPermissionPrompt`,
//!   `LiveBypassClassifierPermissionPrompt`, `ClassifierPermissionPrompt`),
//!   plus the `ToolActionClassifier` trait and its LLM-backed implementation.

pub mod permission;
pub mod tool;

// Convenience re-exports — the most frequently used public surface.
pub use permission::{
    PermissionDecision, PermissionMode, PermissionModeState, PermissionPrompt, PermissionRule,
    PermissionRuleStore,
};
pub use tool::{Tool, ToolContext, ToolNameFilter, ToolOutcome, ToolQuarantine, ToolRegistry, ToolResult};
