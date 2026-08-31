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
//!
//! ## Phase 3 — Built-in tools
//!
//! - **`todos`**: `TodoItem`, `TodoStatus`, `TodoStore`.
//! - **`tools`**: all built-in tools; `built_in_tools()` returns the full set,
//!   `built_in_file_tools()` returns the Phase-2 subset.
//!
//! ## Phase 5 — Subagents, hooks, compaction, output styles
//!
//! - **`subagents`**: `SubagentHost`, `SubagentDefinition`, `BuiltInAgents`,
//!   `SubagentFactory`, `SubagentRequest`.
//! - **`hooks`**: `HookRunner`, `UserHook`, `HookScope`, `HookEventPolicy`,
//!   `HookMatcher`, `HookContentHash`, `HookTrustStore`, `HookTrustGuard`,
//!   and the decision result types.
//! - **`compaction`**: `CompactionService`, `CompactionPrompts`,
//!   `TokenEstimator`, `CompactionPolicy`, `compaction_tail_start`.
//! - **`output_styles`**: `OutputStyle`, `BuiltInOutputStyles`.

pub mod agent;
pub mod compaction;
pub mod events;
pub mod goal;
pub mod hooks;
pub mod lsp;
pub mod output_styles;
pub mod permission;
pub mod scheduling;
pub mod session;
pub mod steering;
pub mod subagents;
pub mod tasks;
pub mod todos;
pub mod tool;
pub mod tools;

// Convenience re-exports — the most frequently used public surface.
pub use agent::{AgentError, AgentLoop, AgentLoopBuilder};
pub use compaction::{CompactionPolicy, CompactionService, TokenEstimator, compaction_tail_start};
pub use events::{AgentEvent, AgentSink, CollectingSink, NullSink, ToolCallStatus};
pub use goal::{
    ForkedAgent, GoalBudget, GoalJudgePrompt, GoalOutcome, GoalRetryPolicy, GoalStatus,
    GoalSupervisor, GoalVerdict,
};
pub use hooks::{
    HookContentHash, HookEventPolicy, HookMatcher, HookRunEntry, HookRunLog, HookRunner,
    HookScope, HookTrustGuard, HookTrustStore, InMemoryHookTrustStore, UserHook,
};
pub use output_styles::{BuiltInOutputStyles, DynOutputStyle, OutputStyle};
pub use permission::{
    PermissionDecision, PermissionMode, PermissionModeState, PermissionPrompt, PermissionRule,
    PermissionRuleStore,
};
pub use scheduling::{
    NullScheduleLifecycleSink, ScheduleLifecycleEvent, ScheduleLifecycleSink,
    ScheduleRuntimeSnapshot, ScheduleRuntimeState, ScheduleRuntimeStatus, ScheduleRuntimeView,
    ScheduleRuntime, ScheduledAgentRunner, TaskManagerRunner,
};
pub use session::{
    AuditToolCall, AuditTurn, BundleTurn, ImportError, SessionAuditStore, SessionBundle,
    SessionBundleService, SessionSummary, SessionTranscriptStore, StoredSession,
    fork as fork_session, new_session_id, rewind as rewind_session, session_id_is_valid,
};
pub use steering::{SteeringEntry, SteeringInbox};
pub use subagents::{BuiltInAgents, SubagentDefinition, SubagentFactory, SubagentRequest, SubagentRegistry, PluginAgentLoader};
pub use todos::{TodoItem, TodoStatus, TodoStore};
pub use tool::{
    PlanApprover, Tool, ToolContext, ToolDescriptor, ToolNameFilter, ToolOutcome, ToolQuarantine,
    ToolRegistry, ToolResult, UserQuestion,
};
pub use tools::{built_in_file_tools, built_in_tools};
