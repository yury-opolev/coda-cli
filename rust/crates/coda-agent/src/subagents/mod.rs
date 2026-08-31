//! Subagent host: spawn nested `AgentLoop` runs.
//!
//! Matches C# `SubagentHost.cs`, `Subagents/BuiltInAgents.cs`,
//! `Subagents/SubagentDefinition.cs`, `Subagents/SubagentRegistry.cs`.
//!
//! # Nesting model
//! - Depth 0: main agent.
//! - Depth 1: direct subagent (may spawn depth-2 grandchildren).
//! - Depth 2: grandchild — no task-management tools, no further nesting.
//! - Depth ≥ `MAX_SUBAGENT_DEPTH`: rejected with a clear error.
//!
//! Read-only definitions (e.g. `explore`) never receive task-management tools
//! at any depth and cannot spawn children.
//!
//! # Concurrency
//! A session-wide semaphore limits the number of simultaneously running
//! subagents.  When the limit is hit the request blocks until a slot becomes
//! free.  Background subagents are registered with `TaskManager` so they
//! survive a context switch and can be monitored/stopped.

pub mod host;
pub mod plugin_loader;
pub mod registry;

pub use host::SubagentHost;
pub use plugin_loader::PluginAgentLoader;
pub use registry::SubagentRegistry;

use std::sync::Arc;

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::events::AgentSink;

// ─────────────────────────────────────────────────────────────────────────────
// Maximum nesting depth
// ─────────────────────────────────────────────────────────────────────────────

/// Depth-2 grandchildren and read-only definitions never get task-management
/// tools, preventing unbounded recursion.
pub const MAX_SUBAGENT_DEPTH: u32 = 2;

/// Maximum simultaneously running subagents (sessions-wide semaphore guard).
pub const MAX_CONCURRENT_SUBAGENTS: usize = 10;

// ─────────────────────────────────────────────────────────────────────────────
// SubagentDefinition
// ─────────────────────────────────────────────────────────────────────────────

/// Describes a named subagent type: its system-prompt body and capability constraints.
///
/// Matches C# `SubagentDefinition`. Uses owned `String` fields so both built-in
/// (static) and plugin-loaded (dynamic) definitions share one type.
#[derive(Debug, Clone)]
pub struct SubagentDefinition {
    pub agent_type: String,
    pub description: String,
    pub system_prompt_body: String,
    /// When `true`, only read-only tools are offered to this agent type.
    pub read_only_tools_only: bool,
    /// Optional model override for this subagent type.
    pub default_model: Option<String>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Built-in agents
// ─────────────────────────────────────────────────────────────────────────────

fn general_purpose_def() -> SubagentDefinition {
    SubagentDefinition {
        agent_type: "general-purpose".to_owned(),
        description: "A general-purpose autonomous subagent with full tool access.".to_owned(),
        system_prompt_body: "You are a subagent launched to complete a single, self-contained task \
            autonomously. Use the available tools (read_file, list_dir, glob, grep, \
            edit_file, write_file, run_command) to do the work, then finish with a \
            concise report of what you found or changed — that report is your only \
            return value to the caller, so make it self-sufficient."
            .to_owned(),
        read_only_tools_only: false,
        default_model: None,
    }
}

fn explore_def() -> SubagentDefinition {
    SubagentDefinition {
        agent_type: "explore".to_owned(),
        description: "A read-only research subagent that investigates and reports findings."
            .to_owned(),
        system_prompt_body: "You are an Explore subagent. Investigate the codebase to answer the request. \
            Use only read-only tools; do NOT modify anything. \
            Report your findings concisely as your final message — that report is your only output."
            .to_owned(),
        read_only_tools_only: true,
        default_model: None,
    }
}

pub struct BuiltInAgents;

impl BuiltInAgents {
    /// Resolve a subagent type name to its built-in definition.
    /// Unknown or empty names fall back to `general-purpose`.
    pub fn resolve(agent_type: Option<&str>) -> SubagentDefinition {
        match agent_type {
            Some(t) if !t.is_empty() => {
                if t.eq_ignore_ascii_case("general-purpose") {
                    return general_purpose_def();
                }
                if t.eq_ignore_ascii_case("explore") {
                    return explore_def();
                }
                general_purpose_def() // unknown → general-purpose
            }
            _ => general_purpose_def(),
        }
    }

    pub fn is_builtin(agent_type: Option<&str>) -> bool {
        match agent_type {
            Some(t) if !t.is_empty() => {
                t.eq_ignore_ascii_case("general-purpose") || t.eq_ignore_ascii_case("explore")
            }
            _ => false,
        }
    }

    pub fn all() -> Vec<SubagentDefinition> {
        vec![general_purpose_def(), explore_def()]
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SubagentRequest
// ─────────────────────────────────────────────────────────────────────────────

/// Everything needed to launch a subagent.
#[derive(Debug, Clone)]
pub struct SubagentRequest {
    pub agent_type: String,
    pub prompt: String,
    pub task_id: String,
    pub depth: u32,
    /// Optional model override.
    pub model: Option<String>,
    /// Whether to run in the foreground (blocking) or background.
    pub foreground: bool,
    /// For background spawns: used as `parent_task_id` when registering the task
    /// so the authorization tree is correct. Null means the main agent is the parent.
    pub caller_task_id: Option<String>,
}

impl SubagentRequest {
    pub fn foreground(agent_type: impl Into<String>, prompt: impl Into<String>, task_id: impl Into<String>, depth: u32) -> Self {
        Self {
            agent_type: agent_type.into(),
            prompt: prompt.into(),
            task_id: task_id.into(),
            depth,
            model: None,
            foreground: true,
            caller_task_id: None,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SubagentFactory trait (injected into ToolContext)
// ─────────────────────────────────────────────────────────────────────────────

/// Seam that allows the `task` tool to spawn nested agent loops without
/// creating a circular dependency on `AgentLoop` inside `ToolContext`.
#[async_trait]
pub trait SubagentFactory: Send + Sync {
    /// Spawn a subagent and return its final text output.
    ///
    /// For background subagents, returns immediately with the task id wrapped
    /// in a result string.  For foreground subagents, blocks until the loop
    /// completes.
    async fn spawn(
        &self,
        request: SubagentRequest,
        sink: Arc<dyn AgentSink>,
        cancel: CancellationToken,
    ) -> Result<String, String>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolve_general_purpose_is_default() {
        let def = BuiltInAgents::resolve(None);
        assert_eq!(def.agent_type, "general-purpose");

        let def2 = BuiltInAgents::resolve(Some("unknown-type"));
        assert_eq!(def2.agent_type, "general-purpose");
    }

    #[test]
    fn resolve_explore_is_read_only() {
        let def = BuiltInAgents::resolve(Some("explore"));
        assert!(def.read_only_tools_only);
    }

    #[test]
    fn resolve_is_case_insensitive() {
        let def = BuiltInAgents::resolve(Some("EXPLORE"));
        assert_eq!(def.agent_type, "explore");
    }

    #[test]
    fn is_builtin_returns_correct_values() {
        assert!(BuiltInAgents::is_builtin(Some("general-purpose")));
        assert!(BuiltInAgents::is_builtin(Some("explore")));
        assert!(!BuiltInAgents::is_builtin(Some("unknown")));
        assert!(!BuiltInAgents::is_builtin(None));
    }

    #[test]
    fn all_returns_both_builtins() {
        assert_eq!(BuiltInAgents::all().len(), 2);
    }
}
