//! Built-in tools for the Coda agent.
//!
//! `built_in_file_tools()` returns the seven filesystem/search tools from Phase 2.
//! `built_in_tools()` returns the full set including all Phase-3 tools.
//!
//! All tools enforce the path sandbox via `try_resolve_within_root`; no tool
//! may touch a path outside `ToolContext::working_directory` unless bypass mode
//! is active.

mod ask_user_question;
mod edit_file;
mod exit_plan_mode;
mod git_worktree;
mod glob_tool;
mod grep_tool;
mod list_dir;
mod notebook_edit;
mod read_file;
mod run_command;
mod sleep_tool;
mod todo_write;
mod tool_search_tool;
mod web_fetch;
mod web_search;
mod write_file;

use std::sync::Arc;

use regex::Regex;

use crate::tool::Tool;

pub use ask_user_question::AskUserQuestionTool;
pub use edit_file::EditTool;
pub use exit_plan_mode::ExitPlanModeTool;
pub use git_worktree::GitWorktreeTool;
pub use glob_tool::GlobTool;
pub use grep_tool::GrepTool;
pub use list_dir::ListDirTool;
pub use notebook_edit::NotebookEditTool;
pub use read_file::ReadFileTool;
pub use run_command::{RunCommandTool, DEFAULT_TIMEOUT_SECS, TIMEOUT_ENV};
pub use sleep_tool::{SleepTool, MAX_DURATION_MS};
pub use todo_write::TodoWriteTool;
pub use tool_search_tool::ToolSearchTool;
pub use web_fetch::{WebFetchTool, html_to_text, is_allowed_url};
pub use web_search::{DuckDuckGoBackend, SearchBackend, SearchResult, WebSearchTool};
pub use write_file::WriteFileTool;

/// All built-in file and search tools (Phase 2 set), in the recommended registry order.
pub fn built_in_file_tools() -> Vec<Arc<dyn Tool>> {
    vec![
        Arc::new(ReadFileTool),
        Arc::new(ListDirTool),
        Arc::new(GlobTool),
        Arc::new(GrepTool),
        Arc::new(WriteFileTool),
        Arc::new(EditTool),
        Arc::new(NotebookEditTool),
    ]
}

/// All built-in tools (Phase 2 + Phase 3), in the recommended registry order.
///
/// Read-only tools come first, then mutating tools, then agent-support tools.
pub fn built_in_tools() -> Vec<Arc<dyn Tool>> {
    vec![
        // ── read-only file tools ──────────────────────────────────────────────
        Arc::new(ReadFileTool),
        Arc::new(ListDirTool),
        Arc::new(GlobTool),
        Arc::new(GrepTool),
        // ── mutating file tools ───────────────────────────────────────────────
        Arc::new(WriteFileTool),
        Arc::new(EditTool),
        Arc::new(NotebookEditTool),
        // ── shell ─────────────────────────────────────────────────────────────
        Arc::new(RunCommandTool),
        // ── network ───────────────────────────────────────────────────────────
        Arc::new(WebFetchTool::new()),
        Arc::new(WebSearchTool::new_default()),
        // ── agent support ──────────────────────────────────────────────────────
        Arc::new(TodoWriteTool),
        Arc::new(AskUserQuestionTool),
        Arc::new(ExitPlanModeTool),
        Arc::new(SleepTool),
        Arc::new(ToolSearchTool),
        Arc::new(GitWorktreeTool),
    ]
}

/// Translate a glob pattern to a case-insensitive regex that matches the
/// forward-slash-normalised relative path of a file.
///
/// Semantics match the C# `GlobTool.GlobToRegex` exactly:
/// - `**/`  → zero or more path-segment prefixes (`(?:.*/)?`)
/// - `**`   → any characters including separators (`.*`)
/// - `*`    → any characters within one path segment (`[^/]*`)
/// - `?`    → any single non-separator character (`[^/]`)
/// - other  → regex-escaped literal
pub(crate) fn glob_to_regex(glob: &str) -> Result<Regex, regex::Error> {
    let normalized = glob.replace('\\', "/");
    let chars: Vec<char> = normalized.chars().collect();
    let mut pattern = String::from("(?i)^");
    let mut i = 0;
    while i < chars.len() {
        let c = chars[i];
        if c == '*' {
            if i + 1 < chars.len() && chars[i + 1] == '*' {
                i += 1;
                if i + 1 < chars.len() && chars[i + 1] == '/' {
                    pattern.push_str("(?:.*/)?");
                    i += 1;
                } else {
                    pattern.push_str(".*");
                }
            } else {
                pattern.push_str("[^/]*");
            }
        } else if c == '?' {
            pattern.push_str("[^/]");
        } else {
            pattern.push_str(&regex::escape(&c.to_string()));
        }
        i += 1;
    }
    pattern.push('$');
    Regex::new(&pattern)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn single_star_matches_within_one_segment() {
        let re = glob_to_regex("*.txt").unwrap();
        assert!(re.is_match("hello.txt"));
        assert!(!re.is_match("hello.rs"));
        assert!(!re.is_match("dir/hello.txt"));
    }

    #[test]
    fn double_star_slash_matches_across_segments() {
        let re = glob_to_regex("**/*.rs").unwrap();
        assert!(re.is_match("main.rs"));
        assert!(re.is_match("src/main.rs"));
        assert!(re.is_match("a/b/c/lib.rs"));
        assert!(!re.is_match("src/main.txt"));
    }

    #[test]
    fn question_mark_matches_single_non_separator_char() {
        let re = glob_to_regex("?.txt").unwrap();
        assert!(re.is_match("a.txt"));
        assert!(!re.is_match("ab.txt"));
        assert!(!re.is_match("/.txt"));
    }

    #[test]
    fn pattern_matching_is_case_insensitive() {
        let re = glob_to_regex("*.TXT").unwrap();
        assert!(re.is_match("hello.txt"));
        assert!(re.is_match("hello.TXT"));
    }

    #[test]
    fn literal_pattern_matches_exact_relative_path() {
        let re = glob_to_regex("Cargo.toml").unwrap();
        assert!(re.is_match("Cargo.toml"));
        assert!(!re.is_match("Cargo.lock"));
    }

    #[test]
    fn dot_is_matched_as_literal_not_wildcard() {
        let re = glob_to_regex("a.txt").unwrap();
        assert!(!re.is_match("aXtxt"));
        assert!(re.is_match("a.txt"));
    }

    #[test]
    fn built_in_tools_includes_all_phase3_tools() {
        let tools = built_in_tools();
        let names: Vec<&str> = tools.iter().map(|t| t.name()).collect();
        for expected in &[
            "run_command",
            "web_fetch",
            "web_search",
            "todo_write",
            "ask_user_question",
            "exit_plan_mode",
            "sleep",
            "tool_search",
            "git_worktree",
        ] {
            assert!(names.contains(expected), "missing tool: {expected}");
        }
    }

    #[test]
    fn built_in_file_tools_still_works() {
        let tools = built_in_file_tools();
        assert_eq!(tools.len(), 7);
    }
}

