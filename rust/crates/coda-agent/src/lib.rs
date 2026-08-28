//! The Coda agent core.
//!
//! This crate is the porting target for the C# `Coda.Agent` library.  It is
//! built incrementally; this module exposes everything that has landed so far.
//!
//! ## Phase 2 — Agent loop, goals, events, and steering
//!
//! - **`tool`**: the `Tool` trait, `ToolResult`, `ToolOutcome`, `ToolRegistry`,
//!   `ToolNameFilter`, `ToolQuarantine`, and the path sandbox (`ToolContext`).
//! - **`permission`**: `PermissionMode`, `PermissionDecision`,
//!   `PermissionPrompt`, `PermissionPolicy::decide`, `PermissionModeState`,
//!   `PermissionRule`, `PermissionRuleStore`, and the full prompt chain
//!   (`ModePermissionPrompt`, `RulesPermissionPrompt`,
//!   `LiveBypassClassifierPermissionPrompt`, `ClassifierPermissionPrompt`),
//!   plus the `ToolActionClassifier` trait and its LLM-backed implementation.
//! - **`goal`**: `GoalSupervisor`, `GoalBudget`, `GoalVerdict`, `GoalStatus`,
//!   `GoalOutcome`, `GoalJudgePrompt`, `GoalRetryPolicy`, `ForkedAgent`.
//! - **`steering`**: `SteeringInbox`, `SteeringEntry`.
//! - **`events`**: `AgentEvent`, `AgentSink`, `ProtoAdapter`, `NullSink`,
//!   `CollectingSink`, `ToolCallStatus`.
//! - **`agent`**: `AgentLoop`, `AgentLoopBuilder`, `AgentError`.

pub mod agent;
pub mod events;
pub mod goal;
pub mod permission;
pub mod steering;
pub mod tool;

// Convenience re-exports — the most frequently used public surface.
pub use agent::{AgentError, AgentLoop, AgentLoopBuilder};
pub use events::{AgentEvent, AgentSink, CollectingSink, NullSink, ToolCallStatus};
pub use goal::{
    ForkedAgent, GoalBudget, GoalJudgePrompt, GoalOutcome, GoalRetryPolicy, GoalStatus,
    GoalSupervisor, GoalVerdict,
};
pub use permission::{
    PermissionDecision, PermissionMode, PermissionModeState, PermissionPrompt, PermissionRule,
    PermissionRuleStore,
};
pub use steering::{SteeringEntry, SteeringInbox};
pub use tool::{Tool, ToolContext, ToolNameFilter, ToolOutcome, ToolQuarantine, ToolRegistry, ToolResult};
