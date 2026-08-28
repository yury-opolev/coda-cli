//! Find files by glob pattern beneath a base directory.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::{context::try_resolve_within_root, Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tools::glob_to_regex;

pub struct GlobTool;

/// Maximum matching paths returned; matches the C# `MaxResults` constant.
const MAX_RESULTS: usize = 500;

#[async_trait]
impl Tool for GlobTool {
    fn name(&self) -> &str {
        "glob"
    }

    fn description(&self) -> &str {
        "Find files by glob pattern (e.g. **/*.cs or src/*.json), \
         relative to the working directory."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "pattern": {"type": "string"},
            "path":    {"type": "string", "description": "Base directory (optional, defaults to cwd)"}
          },
          "required": ["pattern"]
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
        let pattern = match input.get("pattern").and_then(|v| v.as_str()) {
            Some(p) => p,
            None => return ToolResult::error("Missing required 'pattern'."),
        };

        let base_dir = if let Some(p) = input.get("path").and_then(|v| v.as_str()) {
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

        let regex = match glob_to_regex(pattern) {
            Ok(r) => r,
            Err(e) => return ToolResult::error(format!("Invalid glob pattern: {e}")),
        };

        let base_dir_clone = base_dir.clone();
        let matches: Vec<String> = tokio::select! {
            r = tokio::task::spawn_blocking(move || {
                collect_matching_files(&base_dir_clone, &regex)
            }) => match r {
                Ok(m) => m,
                Err(e) => return ToolResult::error(format!("Internal error: {e}")),
            },
            _ = cancel.cancelled() => return ToolResult::error("Cancelled."),
        };

        let mut sorted = matches;
        sorted.sort_by(|a, b| a.to_lowercase().cmp(&b.to_lowercase()));

        let listing = if sorted.is_empty() {
            "(no matches)".to_owned()
        } else {
            sorted.join("\n")
        };

        ToolResult::ok(listing)
    }
}

/// Walk the tree under `base_dir`, collect relative forward-slash paths that
/// match `regex`.  Returns early once `MAX_RESULTS` paths are collected.
/// Inaccessible directories are silently skipped.
fn collect_matching_files(base_dir: &str, regex: &regex::Regex) -> Vec<String> {
    use std::path::PathBuf;

    let base = std::path::Path::new(base_dir);
    let mut result = Vec::new();
    let mut stack: Vec<PathBuf> = vec![base.to_path_buf()];

    while let Some(dir) = stack.pop() {
        let Ok(entries) = std::fs::read_dir(&dir) else { continue };
        for entry in entries.flatten() {
            let path = entry.path();
            let Ok(ftype) = entry.file_type() else { continue };
            if ftype.is_dir() {
                stack.push(path);
            } else {
                let Ok(rel) = path.strip_prefix(base) else { continue };
                let rel_str = rel.to_string_lossy().replace('\\', "/");
                if regex.is_match(&rel_str) {
                    result.push(rel_str);
                    if result.len() >= MAX_RESULTS {
                        return result;
                    }
                }
            }
        }
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolContext;

    fn temp_dir() -> std::path::PathBuf {
        let d = std::env::temp_dir()
            .join(format!("coda-glob-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    // ── sandbox ──────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn rejects_dotdot_traversal_in_path_arg() {
        let root = std::env::current_dir().unwrap();
        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = GlobTool
            .execute(
                &serde_json::json!({"pattern": "*.txt", "path": "../../etc"}),
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
        let result = GlobTool
            .execute(
                &serde_json::json!({"pattern": "*.txt", "path": outside}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    // ── happy path ────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn finds_matching_extension() {
        let dir = temp_dir();
        std::fs::write(dir.join("a.txt"), "").unwrap();
        std::fs::write(dir.join("b.rs"), "").unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = GlobTool
            .execute(
                &serde_json::json!({"pattern": "*.txt"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("a.txt"));
        assert!(!result.content.contains("b.rs"));
    }

    #[tokio::test]
    async fn double_star_descends_into_subdirectories() {
        let dir = temp_dir();
        std::fs::create_dir(dir.join("src")).unwrap();
        std::fs::write(dir.join("src").join("main.rs"), "").unwrap();
        std::fs::write(dir.join("README.md"), "").unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = GlobTool
            .execute(
                &serde_json::json!({"pattern": "**/*.rs"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("main.rs"));
        assert!(!result.content.contains("README.md"));
    }

    #[tokio::test]
    async fn no_matches_returns_sentinel() {
        let dir = temp_dir();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = GlobTool
            .execute(
                &serde_json::json!({"pattern": "*.xyz"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert_eq!(result.content, "(no matches)");
    }

    // ── cap ───────────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn caps_at_max_results() {
        let dir = temp_dir();
        for i in 0..MAX_RESULTS + 20 {
            std::fs::write(dir.join(format!("f{i:04}.txt")), "").unwrap();
        }
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = GlobTool
            .execute(
                &serde_json::json!({"pattern": "*.txt"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(
            result.content.lines().count() <= MAX_RESULTS,
            "must not exceed MAX_RESULTS"
        );
    }
}
