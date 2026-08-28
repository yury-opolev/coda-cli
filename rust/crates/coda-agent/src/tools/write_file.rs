//! Create or overwrite a UTF-8 file; parent directories are created on demand.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::{context::try_resolve_within_root, Tool, ToolContext, ToolOutcome, ToolResult};

pub struct WriteFileTool;

#[async_trait]
impl Tool for WriteFileTool {
    fn name(&self) -> &str {
        "write_file"
    }

    fn description(&self) -> &str {
        "Create or overwrite a UTF-8 text file with the given content."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "path":    {"type": "string", "description": "File path to write"},
            "content": {"type": "string", "description": "Full file content"}
          },
          "required": ["path", "content"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        false
    }

    async fn execute(
        &self,
        input: &serde_json::Value,
        ctx: &ToolContext,
        cancel: CancellationToken,
    ) -> ToolOutcome {
        let path = match input.get("path").and_then(|v| v.as_str()) {
            Some(p) => p,
            None => return ToolResult::error("Missing required 'path' and/or 'content'."),
        };
        let content = match input.get("content").and_then(|v| v.as_str()) {
            Some(c) => c,
            None => return ToolResult::error("Missing required 'path' and/or 'content'."),
        };

        let full = match try_resolve_within_root(
            &ctx.working_directory,
            path,
            ctx.allow_outside_working_directory,
            ctx.granted_directories.as_ref(),
        ) {
            Ok(p) => p,
            Err(e) => return ToolResult::error(e),
        };

        if let Some(parent) = std::path::Path::new(&full).parent() {
            if let Err(e) = tokio::fs::create_dir_all(parent).await {
                return ToolResult::error(format!("Cannot create parent directories: {e}"));
            }
        }

        let byte_len = content.len();
        tokio::select! {
            r = tokio::fs::write(&full, content) => match r {
                Ok(_) => ToolResult::ok(format!("Wrote {byte_len} bytes to {full}")),
                Err(e) => ToolResult::error(format!("Cannot write {full}: {e}")),
            },
            _ = cancel.cancelled() => ToolResult::error("Cancelled."),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolContext;

    fn temp_dir() -> std::path::PathBuf {
        let d = std::env::temp_dir()
            .join(format!("coda-write-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    // ── sandbox ──────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn rejects_dotdot_traversal() {
        let root = std::env::current_dir().unwrap();
        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = WriteFileTool
            .execute(
                &serde_json::json!({"path": "../../outside.txt", "content": "x"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn rejects_absolute_path_outside_root() {
        let root = std::env::current_dir().unwrap();
        let outside = if cfg!(windows) {
            r"C:\Windows\Temp\coda-test-write.txt"
        } else {
            "/etc/coda-test-write.txt"
        };
        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = WriteFileTool
            .execute(
                &serde_json::json!({"path": outside, "content": "x"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    // ── happy path ────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn writes_file_and_reports_byte_count() {
        let dir = temp_dir();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = WriteFileTool
            .execute(
                &serde_json::json!({"path": "out.txt", "content": "hello"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert_eq!(std::fs::read_to_string(dir.join("out.txt")).unwrap(), "hello");
        assert!(result.content.contains('5'.to_string().as_str())); // 5 bytes
    }

    #[tokio::test]
    async fn creates_parent_directories() {
        let dir = temp_dir();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = WriteFileTool
            .execute(
                &serde_json::json!({"path": "a/b/c/out.txt", "content": "nested"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(dir.join("a/b/c/out.txt").exists());
    }
}
