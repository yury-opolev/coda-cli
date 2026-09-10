//! Loading a session's system prompt from either an inline flag or a file.
//!
//! Pure filesystem + string handling, shared by `coda serve`'s bootstrap, the
//! interactive frontend, and headless `coda run` — none of which should need
//! three copies of "read this file as UTF-8, mutually exclusive with the
//! inline flag".

use std::path::Path;

use anyhow::{Context, Result};

/// Resolves the system prompt from either an inline string or a file.
///
/// The two are mutually exclusive (enforced by the CLI parser). A file is
/// read as UTF-8; a non-UTF-8 file is a hard error rather than a silent lossy
/// read. Returns `None` when neither flag was given.
pub fn resolve_system_prompt(inline: Option<&str>, file: Option<&Path>) -> Result<Option<String>> {
    if let Some(text) = inline {
        return Ok(Some(text.to_owned()));
    }
    if let Some(path) = file {
        let content = std::fs::read(path)
            .with_context(|| format!("failed to read system prompt file: {}", path.display()))?;
        let text = String::from_utf8(content).with_context(|| {
            format!("system prompt file is not valid UTF-8: {}", path.display())
        })?;
        return Ok(Some(text));
    }
    Ok(None)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn inline_wins_over_a_file() {
        let result = resolve_system_prompt(Some("inline text"), None).expect("resolve");
        assert_eq!(result.as_deref(), Some("inline text"));
    }

    #[test]
    fn neither_given_is_none() {
        let result = resolve_system_prompt(None, None).expect("resolve");
        assert!(result.is_none());
    }

    #[test]
    fn reads_from_a_file() {
        let dir = tempfile::tempdir().expect("tempdir");
        let path = dir.path().join("system-prompt.txt");
        std::fs::write(&path, "from file").expect("write");
        let result = resolve_system_prompt(None, Some(&path)).expect("resolve");
        assert_eq!(result.as_deref(), Some("from file"));
    }

    #[test]
    fn a_missing_file_is_an_error() {
        let result = resolve_system_prompt(None, Some(Path::new("no-such-file.txt")));
        assert!(result.is_err(), "missing file must be an error");
    }
}
