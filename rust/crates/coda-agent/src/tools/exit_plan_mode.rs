//! Present the agent's plan for user approval and signal whether to proceed.
//!
//! When no approver is wired (`ctx.plan_approver` is `None`), the tool
//! signals headless mode so the agent remains in plan mode instead of
//! proceeding silently.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

pub struct ExitPlanModeTool;

#[async_trait]
impl Tool for ExitPlanModeTool {
    fn name(&self) -> &str {
        "exit_plan_mode"
    }

    fn description(&self) -> &str {
        "Present the proposed plan to the user for approval. Call this when you have \
         finished researching and have a concrete plan ready. Provide the full plan in \
         markdown format. If approved, you may proceed with implementation."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "plan": {
              "type": "string",
              "description": "The proposed plan in markdown format."
            }
          },
          "required": ["plan"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(
        &self,
        input: &serde_json::Value,
        ctx: &ToolContext,
        cancel: CancellationToken,
    ) -> ToolOutcome {
        let plan = input
            .get("plan")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_owned();

        match &ctx.plan_approver {
            None => ToolResult::ok(
                "No interactive user is available to approve the plan; remaining in plan mode.",
            ),
            Some(approver) => {
                let approved = approver.approve(&plan, cancel).await;
                if approved {
                    ToolResult::ok("Plan approved. You may now proceed with implementation.")
                } else {
                    ToolResult::ok(
                        "Plan was not approved. Continue refining the plan or ask the user \
                         what to change.",
                    )
                }
            }
        }
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use async_trait::async_trait;
    use crate::tool::{context::PlanApprover, ToolContext};

    struct FixedApprover(bool);

    #[async_trait]
    impl PlanApprover for FixedApprover {
        async fn approve(&self, _plan: &str, _cancel: CancellationToken) -> bool {
            self.0
        }
    }

    fn ctx_headless() -> ToolContext {
        ToolContext::new("/")
    }

    fn ctx_with_approver(approved: bool) -> ToolContext {
        ToolContext::new("/").with_plan_approver(Arc::new(FixedApprover(approved)))
    }

    #[tokio::test]
    async fn headless_stays_in_plan_mode() {
        let result = ExitPlanModeTool
            .execute(
                &serde_json::json!({"plan": "## Step 1\nDo stuff."}),
                &ctx_headless(),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(
            result.content.contains("plan mode"),
            "unexpected: {}",
            result.content
        );
    }

    #[tokio::test]
    async fn approved_plan_reports_proceed() {
        let result = ExitPlanModeTool
            .execute(
                &serde_json::json!({"plan": "## Step 1\nDo stuff."}),
                &ctx_with_approver(true),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(
            result.content.contains("approved"),
            "unexpected: {}",
            result.content
        );
    }

    #[tokio::test]
    async fn rejected_plan_reports_not_approved() {
        let result = ExitPlanModeTool
            .execute(
                &serde_json::json!({"plan": "## Step 1\nDo stuff."}),
                &ctx_with_approver(false),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(
            result.content.contains("not approved"),
            "unexpected: {}",
            result.content
        );
    }

    #[tokio::test]
    async fn missing_plan_field_still_works() {
        // plan defaults to empty string — should not error
        let result = ExitPlanModeTool
            .execute(
                &serde_json::json!({}),
                &ctx_with_approver(true),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
    }
}
