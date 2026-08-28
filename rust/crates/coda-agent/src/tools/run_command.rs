//! Run a shell command with a timeout, bounded output capture, and
//! process-tree kill on timeout or cancellation.
//!
//! Shell choice: PowerShell on Windows, `sh` on Unix. The description says
//! "PowerShell" because the primary target is Windows.
//!
//! Security: no shell-injection string filtering is applied. The permission
//! layer is the control. String filtering gives false assurance and would
//! silently block valid commands.

use std::process::Stdio;
use std::time::Duration;

use async_trait::async_trait;
use tokio::io::AsyncReadExt;
use tokio_util::sync::CancellationToken;

use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

pub struct RunCommandTool;

/// Default timeout: 10 minutes, matching the C# `DefaultTimeout`.
pub const DEFAULT_TIMEOUT_SECS: u64 = 600;

/// Environment variable for overriding the timeout (whole seconds; ≤ 0 = infinite).
pub const TIMEOUT_ENV: &str = "CODA_RUN_COMMAND_TIMEOUT";

/// Hard cap on combined stdout + stderr, matching the C# `MaxChars`.
const MAX_CHARS: usize = 30_000;

#[async_trait]
impl Tool for RunCommandTool {
    fn name(&self) -> &str {
        "run_command"
    }

    fn description(&self) -> &str {
        "Run a shell command in the working directory and return combined stdout/stderr. \
         Requires permission."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "The command line to run"
            },
            "timeoutSeconds": {
              "type": "integer",
              "description": "Override the default 600-second timeout for this command only."
            }
          },
          "required": ["command"]
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
        let command = match input.get("command").and_then(|v| v.as_str()) {
            Some(c) if !c.trim().is_empty() => c,
            _ => return ToolResult::error("Missing required 'command'."),
        };

        let per_call = input.get("timeoutSeconds").and_then(|v| v.as_i64());
        let timeout_secs =
            resolve_timeout(per_call, std::env::var(TIMEOUT_ENV).ok().as_deref());
        let timeout_dur = if timeout_secs == u64::MAX {
            // "No timeout": use one week as a practical stand-in for infinity so
            // we don't need to special-case the select! arms.
            Duration::from_secs(7 * 24 * 3600)
        } else {
            Duration::from_secs(timeout_secs)
        };

        run_command(command, &ctx.working_directory, timeout_dur, cancel).await
    }
}

/// Compute the effective timeout in seconds (u64::MAX = no timeout).
///
/// Priority: per-call argument > env-var override > default (600 s).
pub fn resolve_timeout(per_call: Option<i64>, env: Option<&str>) -> u64 {
    if let Some(n) = per_call {
        if n > 0 {
            return n as u64;
        }
    }
    if let Some(raw) = env {
        if let Ok(n) = raw.trim().parse::<i64>() {
            return if n <= 0 { u64::MAX } else { n as u64 };
        }
    }
    DEFAULT_TIMEOUT_SECS
}

// ── Shell invocation helpers ──────────────────────────────────────────────────

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
    vec![
        "-NonInteractive".into(),
        "-NoProfile".into(),
        "-Command".into(),
        command.into(),
    ]
}

#[cfg(not(windows))]
fn shell_args(command: &str) -> Vec<String> {
    vec!["-c".into(), command.into()]
}

// ── Process-tree kill ─────────────────────────────────────────────────────────

/// Kill the process identified by `pid` and its entire descendant tree.
///
/// On Windows, `taskkill /F /T` terminates the job tree reliably.
/// On Unix, the child process was placed in its own process group (via
/// `setpgid(0,0)` in the `pre_exec` hook) so we can signal the whole group
/// with `kill(-pgid, SIGKILL)`.  Without the process-group signal, grandchildren
/// spawned by the shell would continue running after timeout or cancellation.
async fn kill_tree(pid: u32) {
    #[cfg(windows)]
    {
        // The child often exits on its own first, and taskkill then writes a
        // "process not found" line straight to our console. That is expected,
        // so its output is discarded rather than shown to the user.
        let _ = tokio::process::Command::new("taskkill")
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .args(["/F", "/T", "/PID", &pid.to_string()])
            .status()
            .await;
    }
    #[cfg(not(windows))]
    {
        // The process group ID equals `pid` because pre_exec called setpgid(0,0).
        // Sending SIGKILL to the negative pgid terminates every member of the
        // group, including grandchildren.
        //
        // Safety: `pid` is a valid PID obtained from `child.id()` before the
        // child is waited on; the process group is guaranteed to exist at this
        // point because we're in the timeout/cancel branch before wait returns.
        unsafe {
            libc::kill(-(pid as libc::pid_t), libc::SIGKILL);
        }
    }
}

// ── Output reading ────────────────────────────────────────────────────────────

/// Read from `reader` until EOF, storing at most `cap` bytes.  Always drains
/// the pipe (to prevent buffer-full deadlock) even after the cap is hit.
async fn drain_capped(
    mut reader: impl tokio::io::AsyncRead + Unpin,
    cap: usize,
) -> Vec<u8> {
    let mut buf: Vec<u8> = Vec::with_capacity(cap.min(65_536));
    let mut tmp = [0u8; 8_192];
    loop {
        match reader.read(&mut tmp).await {
            Ok(0) | Err(_) => break,
            Ok(n) => {
                let stored = buf.len();
                if stored < cap {
                    let take = n.min(cap - stored);
                    buf.extend_from_slice(&tmp[..take]);
                }
                // Continue reading past the cap so the pipe drains and the
                // child is not blocked on a full write buffer.
            }
        }
    }
    buf
}

// ── Core executor ─────────────────────────────────────────────────────────────

async fn run_command(
    command: &str,
    working_dir: &str,
    timeout: Duration,
    cancel: CancellationToken,
) -> ToolOutcome {
    let program = shell_program();
    let args = shell_args(command);

    let mut cmd = tokio::process::Command::new(program);
    cmd.args(&args)
        .current_dir(working_dir)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true); // ensures the child is killed if this future is dropped

    // On Unix, place the child in its own process group so that kill_tree can
    // signal every grandchild with a single kill(-pgid, SIGKILL).
    // setpgid(0, 0) sets the child's pgid to its own PID.
    #[cfg(unix)]
    unsafe {
        use std::os::unix::process::CommandExt;
        cmd.pre_exec(|| {
            libc::setpgid(0, 0);
            Ok(())
        });
    }

    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => return ToolResult::error(format!("Failed to start process: {e}")),
    };

    let pid = child.id();
    let stdout_reader = child.stdout.take().expect("stdout is piped");
    let stderr_reader = child.stderr.take().expect("stderr is piped");

    // Allocate each stream's cap generously so combined output fits MAX_CHARS.
    let stream_cap = MAX_CHARS * 4; // 4 bytes/char worst-case

    let work = async move {
        // Read both pipes concurrently with the wait so a full pipe buffer never
        // deadlocks the child.
        let (exit_res, out_bytes, err_bytes) = tokio::join!(
            child.wait(),
            drain_capped(stdout_reader, stream_cap),
            drain_capped(stderr_reader, stream_cap),
        );
        (exit_res, out_bytes, err_bytes)
    };

    tokio::select! {
        (exit_res, out_bytes, err_bytes) = work => {
            let exit_code = exit_res
                .map(|s| s.code().unwrap_or(-1))
                .unwrap_or(-1);

            let stdout = String::from_utf8_lossy(&out_bytes).into_owned();
            let stderr = String::from_utf8_lossy(&err_bytes).into_owned();
            let combined = format!("{stdout}{stderr}");
            let text = cap_chars(combined);
            let content = format!("exit code: {exit_code}\n{text}");
            let content = content.trim_end().to_owned();

            if exit_code != 0 {
                ToolResult::error(content)
            } else {
                ToolResult::ok(content)
            }
        }
        _ = tokio::time::sleep(timeout) => {
            if let Some(p) = pid { kill_tree(p).await; }
            ToolResult::error(format!(
                "Command timed out after {}s.",
                timeout.as_secs()
            ))
        }
        _ = cancel.cancelled() => {
            if let Some(p) = pid { kill_tree(p).await; }
            ToolResult::error("Cancelled.")
        }
    }
}

/// Truncate a string to at most `MAX_CHARS` characters, appending a note when
/// truncation occurs.
fn cap_chars(s: String) -> String {
    if s.chars().count() <= MAX_CHARS {
        return s;
    }
    let cutoff = s
        .char_indices()
        .nth(MAX_CHARS)
        .map(|(i, _)| i)
        .unwrap_or(s.len());
    format!("{}\n… [truncated, {} chars total]", &s[..cutoff], s.chars().count())
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolContext;

    fn ctx() -> ToolContext {
        ToolContext::new(std::env::current_dir().unwrap().to_string_lossy().as_ref())
    }

    // ── resolve_timeout ───────────────────────────────────────────────────────

    #[test]
    fn per_call_positive_wins() {
        assert_eq!(resolve_timeout(Some(120), Some("300")), 120);
    }

    #[test]
    fn env_override_is_used_when_no_per_call() {
        assert_eq!(resolve_timeout(None, Some("42")), 42);
    }

    #[test]
    fn env_zero_or_negative_means_infinite() {
        assert_eq!(resolve_timeout(None, Some("0")), u64::MAX);
        assert_eq!(resolve_timeout(None, Some("-1")), u64::MAX);
    }

    #[test]
    fn default_is_600s() {
        assert_eq!(resolve_timeout(None, None), DEFAULT_TIMEOUT_SECS);
    }

    // ── cap_chars ─────────────────────────────────────────────────────────────

    #[test]
    fn cap_chars_short_string_passes_through() {
        let s = "hello".to_string();
        assert_eq!(cap_chars(s.clone()), s);
    }

    #[test]
    fn cap_chars_long_string_is_truncated() {
        let s = "x".repeat(MAX_CHARS + 500);
        let capped = cap_chars(s);
        assert!(capped.contains("truncated"));
        assert!(capped.chars().count() < MAX_CHARS + 200);
    }

    // ── execution ─────────────────────────────────────────────────────────────

    #[cfg(windows)]
    const ECHO_CMD: &str = "Write-Output 'hello world'";
    #[cfg(not(windows))]
    const ECHO_CMD: &str = "echo 'hello world'";

    #[cfg(windows)]
    const FAIL_CMD: &str = "exit 42";
    #[cfg(not(windows))]
    const FAIL_CMD: &str = "exit 42";

    #[cfg(windows)]
    const SLEEP_LONG_CMD: &str = "Start-Sleep -Seconds 120";
    #[cfg(not(windows))]
    const SLEEP_LONG_CMD: &str = "sleep 120";

    #[cfg(windows)]
    const BIG_OUTPUT_CMD: &str = "Write-Output (\"x\" * 35000)";
    #[cfg(not(windows))]
    const BIG_OUTPUT_CMD: &str = "printf '%0.s x' {1..35000}";

    #[tokio::test]
    async fn runs_echo_and_captures_output() {
        let result = RunCommandTool
            .execute(&serde_json::json!({"command": ECHO_CMD}), &ctx(), CancellationToken::new())
            .await;
        assert!(!result.is_error, "failed: {}", result.content);
        assert!(result.content.contains("hello world"), "{}", result.content);
    }

    #[tokio::test]
    async fn non_zero_exit_sets_error_flag() {
        let result = RunCommandTool
            .execute(&serde_json::json!({"command": FAIL_CMD}), &ctx(), CancellationToken::new())
            .await;
        assert!(result.is_error, "expected error: {}", result.content);
        // exit code must appear in the output
        assert!(result.content.contains("exit code:"), "{}", result.content);
    }

    #[tokio::test]
    async fn uses_working_directory() {
        let dir = std::env::temp_dir()
            .join(format!("coda-run-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());

        #[cfg(windows)]
        let cmd = "New-Item -ItemType File -Name 'wdcheck.txt' | Out-Null";
        #[cfg(not(windows))]
        let cmd = "touch wdcheck.txt";

        let result = RunCommandTool
            .execute(&serde_json::json!({"command": cmd}), &ctx, CancellationToken::new())
            .await;
        assert!(!result.is_error, "command failed: {}", result.content);
        assert!(dir.join("wdcheck.txt").exists(), "file not created in working dir");
        std::fs::remove_dir_all(&dir).ok();
    }

    #[tokio::test]
    async fn timeout_kills_long_running_command() {
        let result = RunCommandTool
            .execute(
                &serde_json::json!({"command": SLEEP_LONG_CMD, "timeoutSeconds": 2}),
                &ctx(),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error, "expected timeout error: {}", result.content);
        assert!(
            result.content.contains("timed out"),
            "expected 'timed out' in: {}",
            result.content
        );
    }

    #[tokio::test]
    async fn cancellation_token_aborts_command() {
        let cancel = CancellationToken::new();
        let cancel_clone = cancel.clone();

        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(200)).await;
            cancel_clone.cancel();
        });

        let start = std::time::Instant::now();
        let result = RunCommandTool
            .execute(&serde_json::json!({"command": SLEEP_LONG_CMD}), &ctx(), cancel)
            .await;

        assert!(result.is_error, "expected cancel error: {}", result.content);
        assert!(
            start.elapsed() < Duration::from_secs(5),
            "cancellation was too slow: {:?}",
            start.elapsed()
        );
    }

    #[tokio::test]
    async fn output_is_bounded_at_max_chars() {
        let result = RunCommandTool
            .execute(&serde_json::json!({"command": BIG_OUTPUT_CMD}), &ctx(), CancellationToken::new())
            .await;
        assert!(!result.is_error, "command failed: {}", result.content);
        assert!(
            result.content.contains("truncated"),
            "expected truncation marker: {}",
            &result.content[..result.content.len().min(200)]
        );
        // Total content must not vastly exceed the cap.
        assert!(
            result.content.chars().count() < MAX_CHARS + 500,
            "output too long: {} chars",
            result.content.chars().count()
        );
    }

    #[tokio::test]
    async fn missing_command_returns_error() {
        let result = RunCommandTool
            .execute(&serde_json::json!({}), &ctx(), CancellationToken::new())
            .await;
        assert!(result.is_error);
    }
}
