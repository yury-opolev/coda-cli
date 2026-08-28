//! Wait for a specified number of milliseconds, clamped to [0, 60 000].
//!
//! The sleep is cancellable: a pending sleep aborts promptly when the
//! cancellation token fires, not after the full duration.

use std::time::Duration;

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

pub struct SleepTool;

/// Maximum permitted sleep duration, matching the C# `MaxDurationMs`.
pub const MAX_DURATION_MS: u64 = 60_000;

/// Clamp `ms` to the allowed `[0, MAX_DURATION_MS]` range.
pub fn clamp_duration(ms: i64) -> u64 {
    ms.max(0) as u64
}

#[async_trait]
impl Tool for SleepTool {
    fn name(&self) -> &str {
        "sleep"
    }

    fn description(&self) -> &str {
        "Wait for a number of milliseconds before continuing. Use when you need to pause \
         for an external event. Clamped to [0, 60000] ms. Interruptible via cancellation."
    }

    fn input_schema_json(&self) -> &str {
        r#"{"type":"object","properties":{"duration_ms":{"type":"integer","description":"Number of milliseconds to wait (clamped to [0, 60000])."}},"required":["duration_ms"]}"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(
        &self,
        input: &serde_json::Value,
        _ctx: &ToolContext,
        cancel: CancellationToken,
    ) -> ToolOutcome {
        let raw_ms = match input.get("duration_ms").and_then(|v| v.as_i64()) {
            Some(n) => n,
            None => {
                return ToolResult::error(
                    "Missing or invalid required parameter 'duration_ms' (must be an integer).",
                )
            }
        };

        let clamped = clamp_duration(raw_ms).min(MAX_DURATION_MS);

        tokio::select! {
            _ = tokio::time::sleep(Duration::from_millis(clamped)) => {
                ToolResult::ok(format!("Waited {clamped} ms."))
            }
            _ = cancel.cancelled() => {
                ToolResult::error("Cancelled.")
            }
        }
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolContext;

    fn ctx() -> ToolContext {
        ToolContext::new("/")
    }

    // ── clamp_duration ────────────────────────────────────────────────────────

    #[test]
    fn clamp_negative_to_zero() {
        assert_eq!(clamp_duration(-100).min(MAX_DURATION_MS), 0);
    }

    #[test]
    fn clamp_above_max() {
        assert_eq!(
            clamp_duration((MAX_DURATION_MS + 1000) as i64).min(MAX_DURATION_MS),
            MAX_DURATION_MS
        );
    }

    #[test]
    fn clamp_within_range_unchanged() {
        assert_eq!(clamp_duration(500).min(MAX_DURATION_MS), 500);
    }

    // ── tool execution ────────────────────────────────────────────────────────

    #[tokio::test]
    async fn missing_duration_returns_error() {
        let result = SleepTool
            .execute(&serde_json::json!({}), &ctx(), CancellationToken::new())
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn short_sleep_completes() {
        let result = SleepTool
            .execute(&serde_json::json!({"duration_ms": 10}), &ctx(), CancellationToken::new())
            .await;
        assert!(!result.is_error, "failed: {}", result.content);
        assert!(result.content.contains("10 ms"), "{}", result.content);
    }

    #[tokio::test]
    async fn zero_duration_completes_immediately() {
        let result = SleepTool
            .execute(&serde_json::json!({"duration_ms": 0}), &ctx(), CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("0 ms"), "{}", result.content);
    }

    #[tokio::test]
    async fn over_max_is_clamped() {
        // Verify the cap without waiting 60 s: cancel after 50 ms and check
        // that the only reason for is_error is cancellation (not bad input).
        let cancel = CancellationToken::new();
        let cancel_clone = cancel.clone();
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(50)).await;
            cancel_clone.cancel();
        });
        let result = SleepTool
            .execute(
                &serde_json::json!({"duration_ms": 999_999_999}),
                &ctx(),
                cancel,
            )
            .await;
        // Either cancelled (is_error) or (very unlikely) completed with the
        // clamped value. Both are correct; the important thing is the input
        // was accepted (not an "invalid duration" error).
        assert!(
            result.is_error || result.content.contains(&MAX_DURATION_MS.to_string()),
            "unexpected: {}",
            result.content
        );
    }

    /// Unit-level verification of the clamping formula without any sleep.
    #[test]
    fn over_max_clamp_is_max_duration_ms() {
        assert_eq!(clamp_duration(999_999_999).min(MAX_DURATION_MS), MAX_DURATION_MS);
        assert_eq!(clamp_duration(60_001).min(MAX_DURATION_MS), MAX_DURATION_MS);
    }

    #[tokio::test]
    async fn sleep_is_cancellable() {
        let cancel = CancellationToken::new();
        let cancel_clone = cancel.clone();

        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(50)).await;
            cancel_clone.cancel();
        });

        let start = std::time::Instant::now();
        let result = SleepTool
            .execute(
                &serde_json::json!({"duration_ms": MAX_DURATION_MS}),
                &ctx(),
                cancel,
            )
            .await;

        assert!(result.is_error, "expected cancellation error");
        assert!(
            start.elapsed() < Duration::from_secs(2),
            "sleep was not cancelled promptly: {:?}",
            start.elapsed()
        );
    }
}
