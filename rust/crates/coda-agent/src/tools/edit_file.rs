//! Exact-string replacement in a file (the Edit tool).
//!
//! The critical uniqueness invariant: `old_string` must appear **exactly once**
//! in the file unless `replace_all` is true.  A non-unique match without
//! `replace_all` is a hard error — not a silent first-occurrence replacement.
//! This prevents the model from accidentally clobbering unintended sites when
//! its search string is ambiguous.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::{context::try_resolve_within_root, Tool, ToolContext, ToolOutcome, ToolResult};

pub struct EditTool;

#[async_trait]
impl Tool for EditTool {
    fn name(&self) -> &str {
        "edit_file"
    }

    fn description(&self) -> &str {
        "Replace an exact string in a file. By default old_string must appear \
         exactly once; set replace_all to replace every occurrence."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "path":        {"type": "string"},
            "old_string":  {"type": "string"},
            "new_string":  {"type": "string"},
            "replace_all": {"type": "boolean"}
          },
          "required": ["path", "old_string", "new_string"]
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
            None => {
                return ToolResult::error(
                    "Missing required 'path', 'old_string' and/or 'new_string'.",
                )
            }
        };
        let old_string = match input.get("old_string").and_then(|v| v.as_str()) {
            Some(s) => s,
            None => {
                return ToolResult::error(
                    "Missing required 'path', 'old_string' and/or 'new_string'.",
                )
            }
        };
        let new_string = match input.get("new_string").and_then(|v| v.as_str()) {
            Some(s) => s,
            None => {
                return ToolResult::error(
                    "Missing required 'path', 'old_string' and/or 'new_string'.",
                )
            }
        };
        let replace_all =
            input.get("replace_all").and_then(|v| v.as_bool()).unwrap_or(false);

        let full = match try_resolve_within_root(
            &ctx.working_directory,
            path,
            ctx.allow_outside_working_directory,
            ctx.granted_directories.as_ref(),
        ) {
            Ok(p) => p,
            Err(e) => return ToolResult::error(e),
        };

        if old_string == new_string {
            return ToolResult::error("old_string and new_string are identical.");
        }

        let content = tokio::select! {
            r = tokio::fs::read_to_string(&full) => match r {
                Ok(c) => c,
                Err(_) => return ToolResult::error(format!("File not found: {full}")),
            },
            _ = cancel.cancelled() => return ToolResult::error("Cancelled."),
        };

        let count = count_occurrences(&content, old_string);
        if count == 0 {
            return ToolResult::error("old_string was not found in the file.");
        }
        if count > 1 && !replace_all {
            return ToolResult::error(format!(
                "old_string is not unique ({count} matches). \
                 Provide more context or set replace_all."
            ));
        }

        let updated = if replace_all {
            content.replace(old_string, new_string)
        } else {
            replace_first(&content, old_string, new_string)
        };

        let replacements = if replace_all { count } else { 1 };

        tokio::select! {
            r = tokio::fs::write(&full, &updated) => match r {
                Ok(_) => ToolResult::ok(format!("Edited {full} ({replacements} replacement(s)).")),
                Err(e) => ToolResult::error(format!("Cannot write {full}: {e}")),
            },
            _ = cancel.cancelled() => ToolResult::error("Cancelled."),
        }
    }
}

/// Count non-overlapping occurrences of `needle` in `haystack`.
/// An empty needle always returns 0 (matching C# behaviour).
fn count_occurrences(haystack: &str, needle: &str) -> usize {
    if needle.is_empty() {
        return 0;
    }
    let mut count = 0;
    let mut start = 0;
    while let Some(pos) = haystack[start..].find(needle) {
        count += 1;
        start += pos + needle.len();
    }
    count
}

/// Replace only the first occurrence of `old` in `text`.
fn replace_first(text: &str, old: &str, new: &str) -> String {
    match text.find(old) {
        Some(pos) => format!("{}{}{}", &text[..pos], new, &text[pos + old.len()..]),
        None => text.to_owned(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolContext;

    fn temp_dir() -> std::path::PathBuf {
        let d = std::env::temp_dir()
            .join(format!("coda-edit-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    // ── sandbox ──────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn rejects_dotdot_traversal() {
        let root = std::env::current_dir().unwrap();
        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = EditTool
            .execute(
                &serde_json::json!({"path": "../../x.txt", "old_string": "a", "new_string": "b"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn rejects_absolute_path_outside_root() {
        let root = std::env::current_dir().unwrap();
        let outside =
            if cfg!(windows) { r"C:\Windows\Temp\coda-edit.txt" } else { "/tmp/coda-edit.txt" };
        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = EditTool
            .execute(
                &serde_json::json!({"path": outside, "old_string": "a", "new_string": "b"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    // ── uniqueness invariant ──────────────────────────────────────────────────

    #[tokio::test]
    async fn fails_when_old_string_not_found() {
        let dir = temp_dir();
        std::fs::write(dir.join("f.txt"), "hello world").unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = EditTool
            .execute(
                &serde_json::json!({"path": "f.txt", "old_string": "NOPE", "new_string": "x"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
        assert!(result.content.contains("not found"));
    }

    #[tokio::test]
    async fn fails_when_not_unique_and_replace_all_is_false() {
        let dir = temp_dir();
        std::fs::write(dir.join("f.txt"), "foo foo foo").unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = EditTool
            .execute(
                &serde_json::json!({"path": "f.txt", "old_string": "foo", "new_string": "bar"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error, "non-unique match without replace_all must fail");
        // Error message must name the count so the caller understands why it failed.
        assert!(
            result.content.contains('3'),
            "error must include the match count (got: {:?})",
            result.content
        );
    }

    #[tokio::test]
    async fn replaces_a_unique_occurrence() {
        let dir = temp_dir();
        std::fs::write(dir.join("f.txt"), "hello world").unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = EditTool
            .execute(
                &serde_json::json!({"path": "f.txt", "old_string": "world", "new_string": "rust"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert_eq!(std::fs::read_to_string(dir.join("f.txt")).unwrap(), "hello rust");
    }

    #[tokio::test]
    async fn replace_all_replaces_every_occurrence() {
        let dir = temp_dir();
        std::fs::write(dir.join("f.txt"), "foo foo foo").unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = EditTool
            .execute(
                &serde_json::json!({
                    "path": "f.txt",
                    "old_string": "foo",
                    "new_string": "bar",
                    "replace_all": true
                }),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert_eq!(std::fs::read_to_string(dir.join("f.txt")).unwrap(), "bar bar bar");
        assert!(result.content.contains("3 replacement"));
    }

    // ── unit tests for helpers ────────────────────────────────────────────────

    #[test]
    fn count_occurrences_non_overlapping() {
        assert_eq!(count_occurrences("foo foo foo", "foo"), 3);
        assert_eq!(count_occurrences("aaa", "aa"), 1); // non-overlapping
        assert_eq!(count_occurrences("hello", ""), 0); // empty needle → 0
        assert_eq!(count_occurrences("", "foo"), 0);
    }

    #[test]
    fn replace_first_replaces_only_the_first() {
        assert_eq!(replace_first("a a a", "a", "b"), "b a a");
        assert_eq!(replace_first("hello", "NOPE", "x"), "hello"); // not found
    }
}
