//! Pure mapping from (PermissionMode, tool) → PermissionDecision.
//!
//! No I/O, no state, no async. Every permission prompt layer calls this to
//! convert a mode into a concrete grant/deny/ask before consulting the user.

use crate::permission::{PermissionDecision, PermissionMode};
use crate::tool::Tool;

/// Decide whether a tool is allowed, denied, or needs the user's input.
///
/// Read-only tools are always allowed, short-circuiting before the mode check.
/// For mutating tools the mode determines the policy.
pub fn decide(mode: PermissionMode, tool: &dyn Tool) -> PermissionDecision {
    if tool.is_read_only() {
        return PermissionDecision::Allow;
    }
    match mode {
        PermissionMode::BypassPermissions => PermissionDecision::Allow,
        PermissionMode::Plan => PermissionDecision::Deny,
        PermissionMode::AcceptEdits => {
            if is_edit(tool.name()) {
                PermissionDecision::Allow
            } else {
                PermissionDecision::Ask
            }
        }
        PermissionMode::Default => PermissionDecision::Ask,
    }
}

/// File-mutation tools that `AcceptEdits` auto-allows (rather than asking).
fn is_edit(name: &str) -> bool {
    name == "edit_file" || name == "write_file"
}

#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
    use tokio_util::sync::CancellationToken;

    use crate::permission::{PermissionDecision::*, PermissionMode::*};
    use crate::tool::{ToolContext, ToolOutcome, ToolResult};

    struct MockTool {
        name: &'static str,
        read_only: bool,
    }

    #[async_trait]
    impl crate::tool::Tool for MockTool {
        fn name(&self) -> &str {
            self.name
        }
        fn description(&self) -> &str {
            ""
        }
        fn input_schema_json(&self) -> &str {
            "{}"
        }
        fn is_read_only(&self) -> bool {
            self.read_only
        }
        async fn execute(
            &self,
            _: &serde_json::Value,
            _: &ToolContext,
            _: CancellationToken,
        ) -> ToolOutcome {
            ToolResult::ok("")
        }
    }

    fn tool(name: &'static str, read_only: bool) -> MockTool {
        MockTool { name, read_only }
    }

    // Spec §8 item 9: read-only tools always allow, regardless of mode.
    #[test]
    fn read_only_always_allows_in_all_modes() {
        let ro = tool("read_file", true);
        for mode in [Default, AcceptEdits, Plan, BypassPermissions] {
            assert_eq!(
                decide(mode, &ro),
                Allow,
                "read-only must Allow in mode {mode:?}"
            );
        }
    }

    // Spec §8 item 10: PermissionPolicy truth table.
    #[test]
    fn default_mode_asks_for_every_mutating_tool() {
        assert_eq!(decide(Default, &tool("run_command", false)), Ask);
        assert_eq!(decide(Default, &tool("edit_file", false)), Ask);
        assert_eq!(decide(Default, &tool("write_file", false)), Ask);
    }

    #[test]
    fn accept_edits_allows_edit_and_write_file() {
        assert_eq!(decide(AcceptEdits, &tool("edit_file", false)), Allow);
        assert_eq!(decide(AcceptEdits, &tool("write_file", false)), Allow);
    }

    #[test]
    fn accept_edits_asks_for_non_edit_tools() {
        assert_eq!(decide(AcceptEdits, &tool("run_command", false)), Ask);
        assert_eq!(decide(AcceptEdits, &tool("delete_file", false)), Ask);
    }

    #[test]
    fn plan_mode_denies_all_mutating_tools() {
        assert_eq!(decide(Plan, &tool("edit_file", false)), Deny);
        assert_eq!(decide(Plan, &tool("run_command", false)), Deny);
        assert_eq!(decide(Plan, &tool("write_file", false)), Deny);
    }

    #[test]
    fn bypass_allows_all_mutating_tools() {
        assert_eq!(decide(BypassPermissions, &tool("run_command", false)), Allow);
        assert_eq!(decide(BypassPermissions, &tool("rm_rf", false)), Allow);
    }

    #[test]
    fn bypass_with_read_only_also_allows() {
        assert_eq!(decide(BypassPermissions, &tool("read_file", true)), Allow);
    }
}
