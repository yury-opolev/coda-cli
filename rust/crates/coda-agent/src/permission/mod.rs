//! Permission system: mode, decision, and the prompt trait.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::Tool;

pub mod classifier;
pub mod mode_state;
pub mod policy;
pub mod prompts;
pub mod rules;

pub use classifier::{ForkedAgent, LlmToolActionClassifier, ToolActionClassifier, ToolActionVerdict};
pub use mode_state::PermissionModeState;
pub use policy::decide;
pub use prompts::{ClassifierPermissionPrompt, LiveBypassClassifierPermissionPrompt, ModePermissionPrompt, RulesPermissionPrompt};
pub use rules::{PermissionRule, PermissionRuleStore};

/// How tool-permission decisions are made for a run.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum PermissionMode {
    /// Ask the user before every mutating tool (the interactive default).
    Default = 0,
    /// Auto-allow file edits/writes; still ask before running commands.
    AcceptEdits = 1,
    /// Read-only: deny all mutating tools (no changes made).
    Plan = 2,
    /// Allow everything without asking ("yolo" / --dangerously-skip-permissions).
    BypassPermissions = 3,
}

/// The outcome of a permission policy evaluation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PermissionDecision {
    Allow,
    Deny,
    Ask,
}

/// A single layer of the permission prompt chain.
///
/// The loop calls `request` only for **non-read-only** tools. Read-only tools
/// bypass the chain entirely. Each implementation either makes a decision or
/// delegates to an inner prompt.
#[async_trait]
pub trait PermissionPrompt: Send + Sync {
    /// Returns `true` (allow) or `false` (deny) for the given tool and its
    /// raw JSON input. `input_preview` is the verbatim JSON string — some
    /// implementations inspect it for rule matching.
    async fn request(
        &self,
        tool: &dyn Tool,
        input_preview: &str,
        cancel: CancellationToken,
    ) -> bool;
}
