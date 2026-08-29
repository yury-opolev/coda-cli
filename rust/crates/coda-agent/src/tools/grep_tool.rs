//! Regex content search across files with optional glob filtering and context lines.

use async_trait::async_trait;
use regex::Regex;
use tokio_util::sync::CancellationToken;

use crate::tool::{context::try_resolve_within_root, Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tools::glob_to_regex;

pub struct GrepTool;

/// Maximum matching lines before truncation; matches the C# `MaxMatches` constant.
const MAX_MATCHES: usize = 200;
/// Files larger than this are skipped to avoid reading huge binaries into memory.
const MAX_FILE_BYTES: u64 = 8_000_000;
/// Match-line content is truncated to this many chars; matches the C# constant.
const MAX_LINE_CHARS: usize = 200;

#[async_trait]
impl Tool for GrepTool {
    fn name(&self) -> &str {
        "grep"
    }

    fn description(&self) -> &str {
        "Search file contents by regular expression. Optionally filter files by a glob. \
         Returns path:line: match. Supports context_before/context_after for surrounding lines."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "pattern":        {"type": "string"},
            "path":           {"type": "string"},
            "glob":           {"type": "string",  "description": "Optional file glob filter, e.g. **/*.cs"},
            "context_before": {"type": "integer", "description": "Lines of context before each match (default 0)"},
            "context_after":  {"type": "integer", "description": "Lines of context after each match (default 0)"}
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

        let regex = match Regex::new(pattern) {
            Ok(r) => r,
            Err(e) => return ToolResult::error(format!("Invalid regex: {e}")),
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

        let glob_filter = match input.get("glob").and_then(|v| v.as_str()) {
            Some(g) => match glob_to_regex(g) {
                Ok(r) => Some(r),
                Err(e) => return ToolResult::error(format!("Invalid glob: {e}")),
            },
            None => None,
        };

        let context_before = input
            .get("context_before")
            .and_then(|v| v.as_i64())
            .map(|n| n.max(0) as usize)
            .unwrap_or(0);
        let context_after = input
            .get("context_after")
            .and_then(|v| v.as_i64())
            .map(|n| n.max(0) as usize)
            .unwrap_or(0);

        // Collect candidate file paths first (fast directory walk in a
        // blocking thread), then search them one by one with async I/O.
        let base_dir_clone = base_dir.clone();
        let glob_clone = glob_filter.clone();
        let file_paths: Vec<String> = tokio::select! {
            r = tokio::task::spawn_blocking(move || {
                collect_files(&base_dir_clone, glob_clone.as_ref())
            }) => match r {
                Ok(paths) => paths,
                Err(e) => return ToolResult::error(format!("Internal error: {e}")),
            },
            _ = cancel.cancelled() => return ToolResult::error("Cancelled."),
        };

        let mut results = String::new();
        let mut match_count = 0usize;

        for file_path in &file_paths {
            if cancel.is_cancelled() {
                return ToolResult::error("Cancelled.");
            }

            // Size check — skip files larger than 8 MB.
            let meta = match tokio::fs::metadata(file_path).await {
                Ok(m) => m,
                Err(_) => continue,
            };
            if meta.len() > MAX_FILE_BYTES {
                continue;
            }

            let bytes = match tokio::fs::read(file_path).await {
                Ok(b) => b,
                Err(_) => continue,
            };

            // NUL byte in the first kilobyte means binary; skip it.
            if bytes[..bytes.len().min(1024)].contains(&0u8) {
                continue;
            }

            let text = match String::from_utf8(bytes) {
                Ok(t) => t,
                Err(_) => continue,
            };

            let lines: Vec<&str> = text.lines().collect();
            let rel = make_rel(&base_dir, file_path);

            if search_file(
                &lines,
                &regex,
                &rel,
                &mut results,
                &mut match_count,
                context_before,
                context_after,
            ) {
                // MAX_MATCHES reached; the cap message is already appended.
                return ToolResult::ok(results.trim_end_matches('\n').to_owned());
            }
        }

        if match_count == 0 {
            ToolResult::ok("No matches found.")
        } else {
            ToolResult::ok(results.trim_end_matches('\n').to_owned())
        }
    }
}

/// Collect all file paths under `base_dir` that match `glob_filter` (or all
/// files when the filter is absent).  Inaccessible entries are silently skipped.
fn collect_files(base_dir: &str, glob_filter: Option<&Regex>) -> Vec<String> {
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
            } else if ftype.is_symlink() {
                // Skip symlink-to-file entries.  A repo-supplied symlink that
                // points outside the sandbox (e.g. `data -> ~/.ssh/id_rsa`)
                // would otherwise be followed by tokio::fs::read, leaking
                // confidential file content to the LLM.
                continue;
            } else {
                if let Some(filter) = glob_filter {
                    let Ok(rel) = path.strip_prefix(base) else { continue };
                    let rel_str = rel.to_string_lossy().replace('\\', "/");
                    if !filter.is_match(&rel_str) {
                        continue;
                    }
                }
                result.push(path.to_string_lossy().into_owned());
            }
        }
    }
    result
}

/// Search `lines` for `regex`, appending results to `out`.  Returns `true`
/// when `MAX_MATCHES` is reached (the truncation notice is appended before
/// returning).
///
/// When `context_before == 0 && context_after == 0` the output is the simple
/// C#-compatible `path:line: content` format with no group separators.
fn search_file(
    lines: &[&str],
    regex: &Regex,
    rel: &str,
    out: &mut String,
    match_count: &mut usize,
    context_before: usize,
    context_after: usize,
) -> bool {
    // last_emitted: 1-based line number of the last line written to `out`.
    // 0 means nothing has been emitted for this file yet.
    let mut last_emitted = 0usize;
    let mut pending_after = 0usize;
    let context_active = context_before > 0 || context_after > 0;

    for i in 0..lines.len() {
        let line_num = i + 1; // 1-based
        let line = lines[i];

        if regex.is_match(line) {
            let before_start =
                if line_num > context_before { line_num - context_before } else { 1 };
            // Clamp to what hasn't been emitted yet.
            let emit_start = before_start.max(last_emitted + 1);

            // Emit a group separator when context mode is active and there is a
            // gap between this group and the previous one.
            if context_active && last_emitted > 0 && emit_start > last_emitted + 1 {
                out.push_str("--\n");
            }

            // Emit before-context lines not yet output.
            // (last_emitted is updated once to line_num below — updating it
            // per context line would be immediately overwritten.)
            for ctx_i in emit_start..line_num {
                let content = format_line(lines[ctx_i - 1]);
                out.push_str(&format!("{rel}-{ctx_i}- {content}\n"));
            }

            // Emit the matching line.
            let content = format_line(line);
            out.push_str(&format!("{rel}:{line_num}: {content}\n"));
            last_emitted = line_num;
            *match_count += 1;
            pending_after = context_after;

            if *match_count >= MAX_MATCHES {
                out.push_str("… [more matches truncated]\n");
                return true;
            }
        } else if pending_after > 0 {
            let content = format_line(line);
            out.push_str(&format!("{rel}-{line_num}- {content}\n"));
            last_emitted = line_num;
            pending_after -= 1;
        }
    }
    false
}

/// Trim whitespace and truncate to `MAX_LINE_CHARS`, matching C# grep output.
fn format_line(line: &str) -> String {
    let trimmed = line.trim();
    if trimmed.chars().count() > MAX_LINE_CHARS {
        let cutoff = trimmed
            .char_indices()
            .nth(MAX_LINE_CHARS)
            .map(|(i, _)| i)
            .unwrap_or(trimmed.len());
        format!("{}…", &trimmed[..cutoff])
    } else {
        trimmed.to_owned()
    }
}

/// Compute the forward-slash relative path of `file_path` under `base_dir`.
fn make_rel(base_dir: &str, file_path: &str) -> String {
    let rel = if file_path.len() > base_dir.len() {
        file_path[base_dir.len()..].trim_start_matches(['/', '\\'])
    } else {
        file_path
    };
    rel.replace('\\', "/")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolContext;

    fn temp_dir() -> std::path::PathBuf {
        let d = std::env::temp_dir()
            .join(format!("coda-grep-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    // ── HIGH-2 symlink-escape exploit test ───────────────────────────────────
    //
    // EXPLOIT (before fix): a symlink inside the sandbox root that points at a
    // file outside (e.g. a repo-shipped `data -> ~/.ssh/id_rsa`) is silently
    // followed by tokio::fs::read, leaking confidential file content to the LLM.
    //
    // After the fix, collect_files skips any DirEntry whose file_type is_symlink().
    // On Windows symlink creation may require elevated privileges; if it fails
    // with PermissionDenied the test is explicitly skipped with a message.

    #[tokio::test]
    async fn symlink_to_outside_file_is_not_read() {
        let root = temp_dir();
        let outside = std::env::temp_dir()
            .join(format!("coda-grep-outside-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&outside).unwrap();
        std::fs::write(outside.join("secret.txt"), "GREP_SECRET_7x9q").unwrap();

        let link = root.join("link.txt");
        match make_symlink(&outside.join("secret.txt"), &link) {
            Err(e)
                if e.kind() == std::io::ErrorKind::PermissionDenied
                    || e.raw_os_error() == Some(1314) /* ERROR_PRIVILEGE_NOT_HELD */ =>
            {
                eprintln!(
                    "SKIP symlink_to_outside_file_is_not_read: \
                     symlink creation needs elevated privileges ({e})"
                );
                std::fs::remove_dir_all(&root).ok();
                std::fs::remove_dir_all(&outside).ok();
                return;
            }
            Err(e) => panic!("Failed to create test symlink: {e}"),
            Ok(()) => {}
        }

        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = GrepTool
            .execute(
                &serde_json::json!({"pattern": "GREP_SECRET_7x9q"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;

        // Before fix: grep follows the symlink and exposes the secret.
        // After fix: the symlink entry is skipped; result is "No matches found."
        assert!(
            !result.content.contains("GREP_SECRET_7x9q"),
            "EXPLOIT: symlink to outside file was read; content: {}",
            result.content
        );

        std::fs::remove_dir_all(&root).ok();
        std::fs::remove_dir_all(&outside).ok();
    }

    /// Platform-portable symlink creation for tests.
    fn make_symlink(target: &std::path::Path, link: &std::path::Path) -> std::io::Result<()> {
        #[cfg(unix)]
        {
            std::os::unix::fs::symlink(target, link)
        }
        #[cfg(windows)]
        {
            std::os::windows::fs::symlink_file(target, link)
        }
        #[cfg(not(any(unix, windows)))]
        {
            Err(std::io::Error::new(
                std::io::ErrorKind::Unsupported,
                "symlinks unsupported on this platform",
            ))
        }
    }

    // ── sandbox ──────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn rejects_dotdot_traversal_in_path_arg() {
        let root = std::env::current_dir().unwrap();
        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = GrepTool
            .execute(
                &serde_json::json!({"pattern": "foo", "path": "../../etc"}),
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
        let result = GrepTool
            .execute(
                &serde_json::json!({"pattern": "foo", "path": outside}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    // ── happy path ────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn finds_match_in_file() {
        let dir = temp_dir();
        std::fs::write(dir.join("a.txt"), "hello world\nfoo bar\nbaz").unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = GrepTool
            .execute(
                &serde_json::json!({"pattern": "foo"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("foo bar"));
        assert!(result.content.contains("a.txt"));
    }

    #[tokio::test]
    async fn glob_filter_limits_to_matching_files() {
        let dir = temp_dir();
        std::fs::write(dir.join("a.txt"), "needle").unwrap();
        std::fs::write(dir.join("b.rs"), "needle").unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = GrepTool
            .execute(
                &serde_json::json!({"pattern": "needle", "glob": "*.txt"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("a.txt"));
        assert!(!result.content.contains("b.rs"));
    }

    #[tokio::test]
    async fn skips_binary_files() {
        let dir = temp_dir();
        let bytes = b"match here\0".to_vec(); // NUL → binary
        std::fs::write(dir.join("bin.dat"), &bytes).unwrap();
        // A plain text file in the same dir to confirm search runs at all.
        std::fs::write(dir.join("text.txt"), "match here").unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = GrepTool
            .execute(
                &serde_json::json!({"pattern": "match here"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(!result.content.contains("bin.dat"), "binary file must be skipped");
        assert!(result.content.contains("text.txt"));
    }

    #[tokio::test]
    async fn no_match_returns_sentinel() {
        let dir = temp_dir();
        std::fs::write(dir.join("a.txt"), "hello world").unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = GrepTool
            .execute(
                &serde_json::json!({"pattern": "NOPE"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert_eq!(result.content, "No matches found.");
    }

    // ── cap ───────────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn caps_at_max_matches() {
        let dir = temp_dir();
        // One file with more than MAX_MATCHES matching lines.
        let content = "match\n".repeat(MAX_MATCHES + 10);
        std::fs::write(dir.join("many.txt"), &content).unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = GrepTool
            .execute(
                &serde_json::json!({"pattern": "match"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("truncated"), "must mention truncation");
        // The number of match lines must not exceed the cap.
        let match_lines: Vec<_> =
            result.content.lines().filter(|l| l.contains(':') && !l.starts_with("…")).collect();
        assert!(match_lines.len() <= MAX_MATCHES);
    }

    // ── context lines ─────────────────────────────────────────────────────────

    #[test]
    fn context_lines_with_separator() {
        // Two widely separated matches; verify `--` appears between groups.
        let lines: Vec<&str> = vec!["a", "b", "MATCH1", "c", "d", "e", "MATCH2", "f"];
        let regex = Regex::new("MATCH").unwrap();
        let mut out = String::new();
        let mut count = 0usize;
        search_file(&lines, &regex, "f.txt", &mut out, &mut count, 1, 1);
        assert!(out.contains("--"), "groups must be separated: {out}");
        assert!(out.contains("MATCH1"));
        assert!(out.contains("MATCH2"));
    }

    #[test]
    fn adjacent_context_groups_are_merged_without_separator() {
        // Two matches whose context windows overlap — no `--` separator.
        let lines: Vec<&str> = vec!["a", "MATCH1", "MATCH2", "b"];
        let regex = Regex::new("MATCH").unwrap();
        let mut out = String::new();
        let mut count = 0usize;
        search_file(&lines, &regex, "f.txt", &mut out, &mut count, 1, 1);
        assert!(!out.contains("--"), "adjacent groups must not get a separator: {out}");
    }

    #[test]
    fn no_context_produces_no_separator() {
        // Without context, even non-adjacent matches must not emit `--`.
        let lines: Vec<&str> = vec!["MATCH", "skip", "skip", "MATCH"];
        let regex = Regex::new("MATCH").unwrap();
        let mut out = String::new();
        let mut count = 0usize;
        search_file(&lines, &regex, "f.txt", &mut out, &mut count, 0, 0);
        assert!(!out.contains("--"), "no context → no separator: {out}");
    }
}
