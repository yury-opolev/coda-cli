//! Built-in file-system and search tools for the Coda agent.
//!
//! All tools enforce the path sandbox via `try_resolve_within_root`; no tool
//! may touch a path outside `ToolContext::working_directory` unless bypass mode
//! is active.
//!
//! `built_in_file_tools()` returns them in the recommended registry order:
//! read-only tools first (read_file, list_dir, glob, grep), then mutating
//! tools (write_file, edit_file, notebook_edit).

mod edit_file;
mod glob_tool;
mod grep_tool;
mod list_dir;
mod notebook_edit;
mod read_file;
mod write_file;

use std::sync::Arc;

use regex::Regex;

use crate::tool::Tool;

pub use edit_file::EditTool;
pub use glob_tool::GlobTool;
pub use grep_tool::GrepTool;
pub use list_dir::ListDirTool;
pub use notebook_edit::NotebookEditTool;
pub use read_file::ReadFileTool;
pub use write_file::WriteFileTool;

/// All built-in file and search tools in the recommended registry order.
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
                i += 1; // consume second '*'
                if i + 1 < chars.len() && chars[i + 1] == '/' {
                    // `**/` matches zero or more leading path segments.
                    pattern.push_str("(?:.*/)?");
                    i += 1; // consume '/'
                } else {
                    // Trailing `**` matches anything.
                    pattern.push_str(".*");
                }
            } else {
                // Single `*` stays within one path segment.
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
        // Must not cross a path separator.
        assert!(!re.is_match("dir/hello.txt"));
    }

    #[test]
    fn double_star_slash_matches_across_segments() {
        let re = glob_to_regex("**/*.rs").unwrap();
        assert!(re.is_match("main.rs")); // zero segments
        assert!(re.is_match("src/main.rs")); // one segment
        assert!(re.is_match("a/b/c/lib.rs")); // many segments
        assert!(!re.is_match("src/main.txt")); // wrong extension
    }

    #[test]
    fn question_mark_matches_single_non_separator_char() {
        let re = glob_to_regex("?.txt").unwrap();
        assert!(re.is_match("a.txt"));
        assert!(!re.is_match("ab.txt")); // two chars before the dot
        assert!(!re.is_match("/.txt")); // separator is not allowed
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
        // `a.txt` must not match `aXtxt` (dot not treated as regex `.`).
        let re = glob_to_regex("a.txt").unwrap();
        assert!(!re.is_match("aXtxt"));
        assert!(re.is_match("a.txt"));
    }
}
