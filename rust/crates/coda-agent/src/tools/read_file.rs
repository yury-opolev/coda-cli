//! Read a UTF-8 text file with optional line-range windowing and a hard
//! character cap that keeps the response from blowing the model's context.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::{context::try_resolve_within_root, Tool, ToolContext, ToolOutcome, ToolResult};

pub struct ReadFileTool;

/// Hard cap on returned characters; matches the C# `MaxChars` constant.
const MAX_CHARS: usize = 100_000;

#[async_trait]
impl Tool for ReadFileTool {
    fn name(&self) -> &str {
        "read_file"
    }

    fn description(&self) -> &str {
        "Read a UTF-8 text file. Path is relative to the working directory. \
         Supply offset/limit to read a slice of lines; line numbers are prepended \
         when windowing is active."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "path":   {"type": "string",  "description": "File path to read"},
            "offset": {"type": "integer", "description": "First line to return (1-based, default 1)"},
            "limit":  {"type": "integer", "description": "Maximum number of lines to return"}
          },
          "required": ["path"]
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
        let path = match input.get("path").and_then(|v| v.as_str()) {
            Some(p) => p,
            None => return ToolResult::error("Missing required 'path'."),
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

        let bytes = tokio::select! {
            r = tokio::fs::read(&full) => match r {
                Ok(b) => b,
                Err(_) => return ToolResult::error(format!("File not found: {full}")),
            },
            _ = cancel.cancelled() => return ToolResult::error("Cancelled."),
        };

        // NUL byte in the first kilobyte is the canonical binary-file signal.
        if bytes[..bytes.len().min(1024)].contains(&0u8) {
            return ToolResult::error(format!("File appears to be binary: {full}"));
        }

        let text = match String::from_utf8(bytes) {
            Ok(t) => t,
            Err(_) => return ToolResult::error(format!("File is not valid UTF-8: {full}")),
        };

        let offset =
            input.get("offset").and_then(|v| v.as_i64()).map(|n| n.max(1) as usize).unwrap_or(1);
        let limit = input.get("limit").and_then(|v| v.as_i64()).map(|n| n.max(0) as usize);
        let windowing = offset > 1 || limit.is_some();

        let output = if windowing {
            let lines: Vec<&str> = text.lines().collect();
            let start = (offset - 1).min(lines.len());
            let end = match limit {
                Some(n) => (start + n).min(lines.len()),
                None => lines.len(),
            };
            // Width of the widest line-number for consistent alignment.
            let width = end.to_string().len();
            lines[start..end]
                .iter()
                .enumerate()
                .map(|(i, l)| format!("{:>width$}: {l}", start + i + 1))
                .collect::<Vec<_>>()
                .join("\n")
        } else {
            text
        };

        ToolResult::ok(cap_at_max_chars(output))
    }
}

/// Truncate `s` to at most `MAX_CHARS` bytes (aligned to a char boundary) and
/// append a canonical notice so the model knows the file was longer.
fn cap_at_max_chars(s: String) -> String {
    if s.len() <= MAX_CHARS {
        return s;
    }
    // Walk back from the byte limit to the nearest valid char boundary.
    let mut cutoff = MAX_CHARS;
    while cutoff > 0 && !s.is_char_boundary(cutoff) {
        cutoff -= 1;
    }
    format!("{}\n{}", &s[..cutoff], super::OUTPUT_TRUNCATED)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolContext;

    fn make_ctx(dir: &str) -> ToolContext {
        ToolContext::new(dir)
    }

    fn temp_dir() -> std::path::PathBuf {
        let d = std::env::temp_dir()
            .join(format!("coda-read-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    // ── sandbox ──────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn rejects_dotdot_traversal() {
        let root = std::env::current_dir().unwrap();
        let result = ReadFileTool
            .execute(
                &serde_json::json!({"path": "../../etc/passwd"}),
                &make_ctx(&root.to_string_lossy()),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error, "dotdot traversal must be rejected");
    }

    #[tokio::test]
    async fn rejects_absolute_path_outside_root() {
        let root = std::env::current_dir().unwrap();
        let outside =
            if cfg!(windows) { r"C:\Windows\System32\drivers\etc\hosts" } else { "/etc/passwd" };
        let result = ReadFileTool
            .execute(
                &serde_json::json!({"path": outside}),
                &make_ctx(&root.to_string_lossy()),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error, "absolute outside path must be rejected");
    }

    // ── happy path ────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn reads_plain_file() {
        let dir = temp_dir();
        std::fs::write(dir.join("hello.txt"), "line one\nline two\nline three").unwrap();
        let ctx = make_ctx(&dir.to_string_lossy());
        let result = ReadFileTool
            .execute(&serde_json::json!({"path": "hello.txt"}), &ctx, CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("line one"));
        assert!(result.content.contains("line three"));
    }

    #[tokio::test]
    async fn windowing_returns_numbered_slice() {
        let dir = temp_dir();
        std::fs::write(dir.join("lines.txt"), "a\nb\nc\nd\ne").unwrap();
        let ctx = make_ctx(&dir.to_string_lossy());
        let result = ReadFileTool
            .execute(
                &serde_json::json!({"path": "lines.txt", "offset": 2, "limit": 3}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        // Lines 2-4 with 1-based numbers.
        assert!(result.content.contains("2: b"));
        assert!(result.content.contains("3: c"));
        assert!(result.content.contains("4: d"));
        assert!(!result.content.contains("1: a"));
        assert!(!result.content.contains("5: e"));
    }

    // ── caps and binary detection ─────────────────────────────────────────────

    #[tokio::test]
    async fn truncates_output_at_max_chars() {
        let dir = temp_dir();
        // A file slightly larger than the cap.
        std::fs::write(dir.join("big.txt"), "x".repeat(MAX_CHARS + 500)).unwrap();
        let ctx = make_ctx(&dir.to_string_lossy());
        let result = ReadFileTool
            .execute(&serde_json::json!({"path": "big.txt"}), &ctx, CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("truncated"), "must mention truncation");
        assert!(result.content.len() < MAX_CHARS + 200);
    }

    #[tokio::test]
    async fn rejects_binary_file() {
        let dir = temp_dir();
        let mut bytes = b"hello world".to_vec();
        bytes.push(0); // NUL byte
        std::fs::write(dir.join("bin.dat"), &bytes).unwrap();
        let ctx = make_ctx(&dir.to_string_lossy());
        let result = ReadFileTool
            .execute(&serde_json::json!({"path": "bin.dat"}), &ctx, CancellationToken::new())
            .await;
        assert!(result.is_error, "binary file must be rejected");
        assert!(result.content.contains("binary"));
    }
}
