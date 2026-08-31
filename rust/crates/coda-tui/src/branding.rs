//! Product branding: the wordmark, the startup banner, and the exit summary.
//!
//! Ported from the C# `Branding`, `Banner` and `ExitSummaryRenderer`. These are
//! not decoration — the banner tells you which provider and model you are about
//! to spend money with, and the exit summary tells you what you spent and how
//! to get back to the session. Their absence was the first thing noticed when
//! the Rust build was actually used.

pub const PRODUCT_NAME: &str = "Coda";
pub const TAGLINE: &str = "an agentic coding assistant";

/// The six-line Unicode wordmark spelling "Coda".
pub const WORDMARK: &[&str] = &[
    " ┌───┐      ┌┐",
    " │┬─┐│┌──┐┌─┘│┌──┐",
    " ││ └┘│┬┐││┬┐││┬┐│",
    " ││ ┌┐││││││││││││",
    " │└─┴││└┴││└┴││└┴└┐",
    " └───┘└──┘└──┘└───┘",
];

/// The crate version, matching the C# `Branding.Version`.
pub fn version() -> &'static str {
    env!("CARGO_PKG_VERSION")
}

/// What the exit summary reports.
#[derive(Debug, Clone, Default)]
pub struct ExitSummary {
    pub duration: std::time::Duration,
    pub message_count: usize,
    pub provider_id: String,
    pub model: String,
    pub effort: Option<String>,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub session_id: Option<String>,
    pub working_directory: String,
}

impl ExitSummary {
    pub fn total_tokens(&self) -> u64 {
        self.input_tokens + self.output_tokens
    }
}

/// Formats a duration the way the C# does: `1h 02m 03s`, `2m 03s`, or `4s`.
pub fn format_duration(d: std::time::Duration) -> String {
    let secs = d.as_secs();
    let (h, m, s) = (secs / 3600, (secs % 3600) / 60, secs % 60);
    if h >= 1 {
        format!("{h}h {m:02}m {s:02}s")
    } else if m >= 1 {
        format!("{m}m {s:02}s")
    } else {
        format!("{s}s")
    }
}

/// Quotes an argument for a copy-pasteable command line.
///
/// Follows the Windows `CommandLineToArgvW` convention, which also parses
/// correctly inside a POSIX shell's double quotes for filesystem paths: the run
/// of backslashes immediately before the closing quote is doubled so the quote
/// is not escaped by accident. Without that, a root path renders as the broken
/// `"C:\"` rather than `"C:\\"`.
pub fn quote_argument(value: &str) -> String {
    if !value.is_empty() && !value.contains([' ', '\t', '"']) {
        return value.to_string();
    }

    let mut out = String::with_capacity(value.len() + 2);
    out.push('"');
    let mut backslashes = 0usize;
    for ch in value.chars() {
        match ch {
            '\\' => {
                backslashes += 1;
                out.push('\\');
            }
            '"' => {
                // Escape the run of backslashes, then the quote itself.
                for _ in 0..backslashes {
                    out.push('\\');
                }
                backslashes = 0;
                out.push('\\');
                out.push('"');
            }
            other => {
                backslashes = 0;
                out.push(other);
            }
        }
    }
    // A trailing run of backslashes would escape the closing quote.
    for _ in 0..backslashes {
        out.push('\\');
    }
    out.push('"');
    out
}

/// The wordmark rows, for callers that colour them separately.
pub fn wordmark_lines() -> Vec<String> {
    WORDMARK.iter().map(|l| (*l).to_string()).collect()
}

/// The startup banner's detail lines, without the wordmark.
pub fn startup_detail_lines(
    working_directory: &str,
    provider: Option<&str>,
    model: Option<&str>,
) -> Vec<String> {
    let mut lines = Vec::new();
    lines.push(String::new());
    lines.push(format!("Welcome to {PRODUCT_NAME} v{}", version()));
    lines.push(TAGLINE.to_string());
    lines.push(String::new());
    lines.push(format!("cwd: {working_directory}"));
    match provider {
        Some(p) => lines.push(format!("provider: {p}   model: {}", model.unwrap_or("—"))),
        None => lines.push("not signed in — run /login".to_string()),
    }
    lines.push("Type /help for commands, or /exit to quit.".to_string());
    lines
}

/// The startup banner, as plain lines ready to print.
pub fn startup_lines(
    working_directory: &str,
    provider: Option<&str>,
    model: Option<&str>,
) -> Vec<String> {
    let mut lines = wordmark_lines();
    lines.extend(startup_detail_lines(working_directory, provider, model));
    lines
}

/// The exit summary, as plain lines ready to print.
pub fn exit_lines(summary: &ExitSummary) -> Vec<String> {
    let mut lines: Vec<String> = WORDMARK.iter().map(|l| (*l).to_string()).collect();
    lines.push(String::new());
    lines.push("Session summary".to_string());
    lines.push(format!(
        "coda: v{}   Duration: {}   Messages: {}",
        version(),
        format_duration(summary.duration),
        summary.message_count
    ));
    lines.push(format!(
        "provider: {} · model: {} · effort: {}",
        summary.provider_id,
        summary.model,
        summary.effort.as_deref().unwrap_or("auto")
    ));
    lines.push(format!(
        "Tokens: {} in · {} out · {} total",
        summary.input_tokens,
        summary.output_tokens,
        summary.total_tokens()
    ));

    lines.push(String::new());
    match &summary.session_id {
        Some(id) => {
            lines.push("Resume from this directory:".to_string());
            lines.push(format!("cd {}", quote_argument(&summary.working_directory)));
            lines.push(format!("coda --resume {}", quote_argument(id)));
        }
        // Say so explicitly: silently omitting the resume hint would leave the
        // user assuming the session was saved.
        None => lines.push("This session was not saved.".to_string()),
    }
    lines
}

/// Writes the exit summary, after the alternate screen has been left.
pub fn print_exit(summary: &ExitSummary) {
    println!();
    for line in exit_lines(summary) {
        println!("{line}");
    }
    println!();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_wordmark_is_the_same_six_lines_as_the_c_sharp() {
        assert_eq!(WORDMARK.len(), 6);
        assert!(WORDMARK.iter().all(|l| !l.trim().is_empty()));
    }

    #[test]
    fn durations_format_like_the_c_sharp() {
        use std::time::Duration;
        assert_eq!(format_duration(Duration::from_secs(4)), "4s");
        assert_eq!(format_duration(Duration::from_secs(123)), "2m 03s");
        assert_eq!(format_duration(Duration::from_secs(3723)), "1h 02m 03s");
        assert_eq!(format_duration(Duration::from_secs(0)), "0s");
    }

    /// A root path must not render as `"C:\"`, which escapes the closing quote
    /// and produces a command line that will not parse.
    #[test]
    fn a_trailing_backslash_is_doubled_so_the_quote_survives() {
        assert_eq!(quote_argument(r"C:\projects with space\"), r#""C:\projects with space\\""#);
    }

    #[test]
    fn an_argument_without_spaces_is_left_unquoted() {
        assert_eq!(quote_argument("simple"), "simple");
        assert_eq!(quote_argument(r"C:\projects\coda"), r"C:\projects\coda");
    }

    #[test]
    fn an_embedded_quote_is_escaped() {
        assert_eq!(quote_argument(r#"a "b" c"#), r#""a \"b\" c""#);
    }

    #[test]
    fn the_startup_banner_names_the_provider_and_model() {
        let lines = startup_lines("/tmp/project", Some("github-copilot"), Some("claude-opus-5"));
        let joined = lines.join("\n");
        assert!(joined.contains("github-copilot"), "{joined}");
        assert!(joined.contains("claude-opus-5"), "{joined}");
        assert!(joined.contains("/tmp/project"), "{joined}");
    }

    /// Not being signed in must be stated, not left blank — otherwise the first
    /// sign of trouble is a failed turn.
    #[test]
    fn the_startup_banner_says_when_not_signed_in() {
        let lines = startup_lines("/tmp", None, None);
        assert!(lines.iter().any(|l| l.contains("/login")), "{lines:?}");
    }

    #[test]
    fn the_exit_summary_reports_usage_and_how_to_resume() {
        let summary = ExitSummary {
            duration: std::time::Duration::from_secs(75),
            message_count: 12,
            provider_id: "github-copilot".into(),
            model: "claude-opus-5".into(),
            effort: Some("high".into()),
            input_tokens: 1200,
            output_tokens: 340,
            session_id: Some("abc123".into()),
            working_directory: r"C:\work".into(),
        };
        let joined = exit_lines(&summary).join("\n");
        assert!(joined.contains("1m 15s"), "{joined}");
        assert!(joined.contains("1540 total"), "{joined}");
        assert!(joined.contains("coda --resume abc123"), "{joined}");
        assert!(joined.contains("high"), "{joined}");
    }

    #[test]
    fn an_unsaved_session_says_so_rather_than_omitting_the_hint() {
        let summary = ExitSummary { session_id: None, ..Default::default() };
        let joined = exit_lines(&summary).join("\n");
        assert!(joined.contains("not saved"), "{joined}");
        assert!(!joined.contains("--resume"), "must not offer a resume that cannot work");
    }

    #[test]
    fn effort_defaults_to_auto_when_unset() {
        let summary = ExitSummary { effort: None, ..Default::default() };
        assert!(exit_lines(&summary).join("\n").contains("effort: auto"));
    }
}
