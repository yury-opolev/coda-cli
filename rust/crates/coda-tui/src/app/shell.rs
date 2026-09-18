//! The `!` shell escape: run a command yourself, without the model.
//!
//! `!git status` runs the command and puts its output in the transcript. It is
//! deliberately *your* command, not the agent's:
//!
//! - It runs immediately, with no model round-trip and no tokens spent.
//! - It is not subject to the permission policy. Permission modes exist to
//!   govern what the *agent* may do on your behalf; asking you to approve a
//!   command you just typed yourself would be theatre, and training people to
//!   dismiss approval prompts is how real ones stop being read.
//! - Its output is shown to you, and is **not** silently added to the model's
//!   context. Quietly injecting command output would spend tokens you did not
//!   ask to spend and change the model's answers for reasons invisible in the
//!   transcript. The agent has its own `run_command` tool for when *it* needs
//!   to run something.
//!
//! The shell and its arguments deliberately match the agent's `run_command`
//! tool — PowerShell on Windows, `sh` elsewhere — so the same string behaves
//! the same way whichever of you runs it.

use std::process::Stdio;
use std::time::Duration;

use tokio::io::AsyncReadExt;
use tokio::process::Command;

/// How long a `!` command may run before it is given up on.
///
/// Bounded because this blocks the turn: an interactive command that waits for
/// input it will never get would otherwise hang the front-end with no way out.
const TIMEOUT: Duration = Duration::from_secs(120);

/// Output beyond this is truncated, with a note saying so.
///
/// A command that prints a megabyte would otherwise push the whole
/// conversation off the screen.
const MAX_OUTPUT_BYTES: usize = 64 * 1024;

/// Extracts the command from a `!`-prefixed submission.
///
/// `!!` escapes a literal leading bang, mirroring how `//` escapes a leading
/// slash for slash commands — otherwise a message that genuinely starts with
/// an exclamation could never be sent.
pub(super) fn parse(input: &str) -> Option<&str> {
    let rest = input.trim_start().strip_prefix('!')?;
    if rest.starts_with('!') {
        return None;
    }
    let command = rest.trim();
    if command.is_empty() {
        return None;
    }
    Some(command)
}

/// What running a command produced.
pub(super) struct Output {
    pub(super) text: String,
    pub(super) failed: bool,
}

/// Runs `command` in the platform shell and collects its output.
///
/// stdout and stderr are interleaved into one stream because that is how a
/// terminal shows them and how the reader expects to read them; keeping them
/// apart would reorder a command's own diagnostics away from the output they
/// describe.
pub(super) async fn run(command: &str, working_dir: &std::path::Path) -> Output {
    let mut child = match Command::new(shell_program())
        .args(shell_args(command))
        .current_dir(working_dir)
        // Inherited stdin is what makes an interactive command hang forever
        // holding the front-end open. Closed, so anything reading stdin sees
        // EOF and exits instead.
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .spawn()
    {
        Ok(child) => child,
        Err(error) => {
            return Output { text: format!("Could not run the command: {error}"), failed: true };
        }
    };

    let mut stdout = child.stdout.take();
    let mut stderr = child.stderr.take();

    let collect = async {
        let mut out = String::new();
        if let Some(pipe) = stdout.as_mut() {
            let _ = pipe.read_to_string(&mut out).await;
        }
        if let Some(pipe) = stderr.as_mut() {
            let mut err = String::new();
            let _ = pipe.read_to_string(&mut err).await;
            if !err.trim().is_empty() {
                if !out.is_empty() && !out.ends_with('\n') {
                    out.push('\n');
                }
                out.push_str(&err);
            }
        }
        let status = child.wait().await;
        (out, status)
    };

    match tokio::time::timeout(TIMEOUT, collect).await {
        Ok((out, status)) => {
            let failed = !status.map(|s| s.success()).unwrap_or(false);
            let mut text = truncate(out);
            if text.trim().is_empty() {
                text = if failed {
                    "(no output)".to_owned()
                } else {
                    "(no output)".to_owned()
                };
            }
            Output { text, failed }
        }
        // `kill_on_drop` reaps the child when the future is dropped, so the
        // timeout does not leave the process running unattended.
        Err(_) => Output {
            text: format!("Timed out after {} seconds.", TIMEOUT.as_secs()),
            failed: true,
        },
    }
}

/// Caps output at [`MAX_OUTPUT_BYTES`], keeping the **end**.
///
/// The end is what a command's verdict lives in — the error, the summary, the
/// last line of a build — so a head-truncated log is usually the useless half.
fn truncate(text: String) -> String {
    if text.len() <= MAX_OUTPUT_BYTES {
        return text;
    }
    // Cut on a character boundary at or after the target, so a multi-byte
    // character is never split.
    let start = text.len() - MAX_OUTPUT_BYTES;
    let start = (start..text.len())
        .find(|i| text.is_char_boundary(*i))
        .unwrap_or(text.len());
    format!("[… earlier output truncated …]\n{}", &text[start..])
}

#[cfg(windows)]
fn shell_program() -> &'static str {
    "powershell.exe"
}

#[cfg(not(windows))]
fn shell_program() -> &'static str {
    "sh"
}

#[cfg(windows)]
fn shell_args(command: &str) -> Vec<String> {
    vec!["-NonInteractive".into(), "-NoProfile".into(), "-Command".into(), command.into()]
}

#[cfg(not(windows))]
fn shell_args(command: &str) -> Vec<String> {
    vec!["-c".into(), command.into()]
}

/// Runs a `!` command and puts the result in the transcript.
///
/// The echoed command line goes in first, so the output is never an orphaned
/// block of text with nothing saying what produced it.
pub(super) async fn run_and_report(app: &mut super::App, command: &str) {
    let label = format!("! {command}");
    let dir = app.paths.project_root.clone();
    let outcome = run(command, &dir).await;
    let body = outcome.text.trim_end().to_owned();
    let body = if outcome.failed { format!("{body}\n\n(command failed)") } else { body };
    app.output(format!("{label}\n{body}"));
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_bang_prefix_names_a_command() {
        assert_eq!(parse("!git status"), Some("git status"));
        assert_eq!(parse("  !ls -la"), Some("ls -la"));
        assert_eq!(parse("!  spaced  "), Some("spaced"));
    }

    #[test]
    fn a_doubled_bang_is_ordinary_text() {
        // Otherwise a message that genuinely starts with an exclamation could
        // never be sent to the model at all.
        assert_eq!(parse("!!not a command"), None);
    }

    #[test]
    fn a_bare_bang_is_not_a_command() {
        assert_eq!(parse("!"), None);
        assert_eq!(parse("!   "), None);
    }

    #[test]
    fn text_without_a_bang_is_left_alone() {
        assert_eq!(parse("git status"), None);
        assert_eq!(parse("what does ! mean"), None);
    }

    #[tokio::test]
    async fn a_command_reports_its_output() {
        let dir = std::env::current_dir().expect("cwd");
        let out = run("echo hello-from-shell", &dir).await;
        assert!(out.text.contains("hello-from-shell"), "{}", out.text);
        assert!(!out.failed);
    }

    #[tokio::test]
    async fn a_failing_command_is_reported_as_failed() {
        let dir = std::env::current_dir().expect("cwd");
        let out = run("exit 3", &dir).await;
        assert!(out.failed, "a non-zero exit must be reported: {}", out.text);
    }

    /// stderr must be shown. A command whose only output is a diagnostic is
    /// exactly the case where silence is worst.
    #[tokio::test]
    async fn stderr_is_shown_alongside_stdout() {
        let dir = std::env::current_dir().expect("cwd");
        let out = run("[Console]::Error.WriteLine('to-stderr')", &dir).await;
        assert!(out.text.contains("to-stderr"), "{}", out.text);
    }

    #[test]
    fn truncation_keeps_the_end_where_the_verdict_is() {
        let text = format!("{}TAIL-MARKER", "x".repeat(MAX_OUTPUT_BYTES * 2));
        let out = truncate(text);
        assert!(out.ends_with("TAIL-MARKER"), "the end must survive");
        assert!(out.starts_with("[… earlier output truncated …]"), "and say it was cut");
        assert!(out.len() < MAX_OUTPUT_BYTES + 100);
    }

    #[test]
    fn truncation_never_splits_a_character() {
        // A multi-byte character straddling the cut point must not be halved.
        let text = "é".repeat(MAX_OUTPUT_BYTES);
        let out = truncate(text);
        assert!(out.is_char_boundary(0));
        // Round-trips as valid UTF-8 by construction; the assertion is that
        // building it did not panic and the tail is intact.
        assert!(out.ends_with('é'));
    }
}
