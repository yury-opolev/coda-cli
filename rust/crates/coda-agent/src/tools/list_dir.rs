//! List the immediate children of a directory with a hard entry cap.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::{context::try_resolve_within_root, Tool, ToolContext, ToolOutcome, ToolResult};

pub struct ListDirTool;

/// Maximum entries returned; matches the C# `MaxEntries` constant.
const MAX_ENTRIES: usize = 200;

#[async_trait]
impl Tool for ListDirTool {
    fn name(&self) -> &str {
        "list_dir"
    }

    fn description(&self) -> &str {
        "List files and subdirectories of a directory \
         (defaults to the working directory)."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "path": {"type": "string", "description": "Directory path (optional, defaults to cwd)"}
          }
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
        let dir = if let Some(p) = input.get("path").and_then(|v| v.as_str()) {
            match try_resolve_within_root(
                &ctx.working_directory,
                p,
                ctx.allow_outside_working_directory,
                ctx.granted_directories.as_ref(),
            ) {
                Ok(d) => d,
                Err(e) => return ToolResult::error(e),
            }
        } else {
            ctx.working_directory.clone()
        };

        // Open the directory stream asynchronously.
        let mut read_dir = tokio::select! {
            r = tokio::fs::read_dir(&dir) => match r {
                Ok(rd) => rd,
                Err(_) => return ToolResult::error(format!("Directory not found: {dir}")),
            },
            _ = cancel.cancelled() => return ToolResult::error("Cancelled."),
        };

        // Collect all entries, separating directories from files so we can sort
        // each group independently and list directories first — matching C#.
        let mut dir_names: Vec<String> = Vec::new();
        let mut file_names: Vec<String> = Vec::new();

        loop {
            let entry = tokio::select! {
                r = read_dir.next_entry() => match r {
                    Ok(Some(e)) => e,
                    Ok(None) => break,
                    Err(_) => break,
                },
                _ = cancel.cancelled() => return ToolResult::error("Cancelled."),
            };

            let name = entry.file_name().to_string_lossy().into_owned();
            let is_dir =
                entry.file_type().await.map(|ft| ft.is_dir()).unwrap_or(false);
            if is_dir {
                dir_names.push(name + "/");
            } else {
                file_names.push(name);
            }
        }

        // Case-insensitive sort matching C# `StringComparer.OrdinalIgnoreCase`.
        dir_names.sort_by(|a, b| a.to_lowercase().cmp(&b.to_lowercase()));
        file_names.sort_by(|a, b| a.to_lowercase().cmp(&b.to_lowercase()));

        // Shared count across both lists, same as C# (dirs use quota first).
        let mut lines: Vec<String> = Vec::new();
        let mut count = 0usize;
        for d in dir_names {
            if count >= MAX_ENTRIES {
                break;
            }
            lines.push(d);
            count += 1;
        }
        for f in file_names {
            if count >= MAX_ENTRIES {
                break;
            }
            lines.push(f);
            count += 1;
        }

        let listing = if lines.is_empty() {
            "(empty directory)".to_owned()
        } else {
            lines.join("\n")
        };

        ToolResult::ok(listing)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolContext;

    fn temp_dir() -> std::path::PathBuf {
        let d = std::env::temp_dir()
            .join(format!("coda-ls-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    // ── sandbox ──────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn rejects_dotdot_traversal() {
        let root = std::env::current_dir().unwrap();
        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = ListDirTool
            .execute(
                &serde_json::json!({"path": "../../etc"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn rejects_absolute_path_outside_root() {
        let root = std::env::current_dir().unwrap();
        let outside = if cfg!(windows) { r"C:\Windows" } else { "/etc" };
        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = ListDirTool
            .execute(
                &serde_json::json!({"path": outside}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    // ── happy path ────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn lists_directories_before_files() {
        let dir = temp_dir();
        std::fs::create_dir(dir.join("subdir")).unwrap();
        std::fs::write(dir.join("file.txt"), "").unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result =
            ListDirTool.execute(&serde_json::json!({}), &ctx, CancellationToken::new()).await;
        assert!(!result.is_error);
        let dir_pos = result.content.find("subdir/").unwrap();
        let file_pos = result.content.find("file.txt").unwrap();
        assert!(dir_pos < file_pos, "directories must precede files");
    }

    #[tokio::test]
    async fn empty_directory_reports_empty() {
        let dir = temp_dir();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result =
            ListDirTool.execute(&serde_json::json!({}), &ctx, CancellationToken::new()).await;
        assert!(!result.is_error);
        assert_eq!(result.content, "(empty directory)");
    }

    // ── cap ───────────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn caps_at_max_entries() {
        let dir = temp_dir();
        for i in 0..MAX_ENTRIES + 10 {
            std::fs::write(dir.join(format!("f{i:04}.txt")), "").unwrap();
        }
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result =
            ListDirTool.execute(&serde_json::json!({}), &ctx, CancellationToken::new()).await;
        assert!(!result.is_error);
        assert_eq!(
            result.content.lines().count(),
            MAX_ENTRIES,
            "must return exactly MAX_ENTRIES lines"
        );
    }
}
