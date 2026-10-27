//! Run a shell command as a managed task, with bounded output capture and
//! process-tree kill on stop, cancellation or an explicit work deadline.
//!
//! Shell choice: PowerShell on Windows, `sh` on Unix. The description names
//! whichever it is, because the model has nothing else to go on and will
//! otherwise reach for bash — `for i in $(seq 1 100)` fails on PowerShell
//! with a parser error, costing a whole turn to discover.
//!
//! # Non-blocking runtime (Phase 1)
//! When the context carries a `TaskManager` (every real agent loop), the
//! command is registered as a `TaskKind::Shell` task, its output is streamed
//! into the task's ring and secret-redacted log, and `execute` returns at once
//! with a [`ToolResult::running_task`] hand-off. The batch executor then waits
//! under the yield contract (`waitSeconds`, default 20 s, clamped to 0–30 s in
//! the main conversation): a command that finishes in time returns exactly the
//! result it always did; one that does not keeps running and the model gets a
//! task handle. Waiting less never kills the work. `timeoutSeconds` is a
//! separate, optional *work deadline*: the process tree is killed after it.
//!
//! Without a `TaskManager` (direct callers and tests) the command runs inline,
//! as before.
//!
//! Security: no shell-injection string filtering is applied. The permission
//! layer is the control. String filtering gives false assurance and would
//! silently block valid commands.

use std::process::Stdio;
use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use tokio::io::AsyncReadExt;
use tokio_util::sync::CancellationToken;

use crate::tasks::{ManagedTask, TaskExecutionMode, TaskKind, TaskManager, TaskOutputChannel};
use crate::tool::{Tool, ToolContext, ToolContextServiceExt, ToolOutcome, ToolResult};

pub struct RunCommandTool;

/// Inline (no task manager) default timeout: 10 minutes, matching the C#
/// `DefaultTimeout`. Task-backed commands have no default deadline.
pub const DEFAULT_TIMEOUT_SECS: u64 = 600;

/// Environment variable for overriding the timeout (whole seconds; ≤ 0 = infinite).
/// For task-backed commands it sets the default work deadline.
pub const TIMEOUT_ENV: &str = "CODA_RUN_COMMAND_TIMEOUT";

/// Default yield budget for the main conversation (decision D1).
pub const DEFAULT_WAIT_SECS: u64 = 20;

/// Upper bound on any requested yield budget (decision D1). This bound is what
/// guarantees a user message reaches the model within seconds.
pub const MAX_WAIT_SECS: u64 = 30;

/// Set to `0`/`false`/`off` to make the main conversation wait for commands
/// to finish instead of yielding. An explicit `waitSeconds` still applies.
/// Headless `coda run` no longer needs it: it parks until its tasks finish.
pub const YIELD_ENV: &str = "CODA_RUN_COMMAND_YIELD";

/// Hard cap on combined stdout + stderr, matching the C# `MaxChars`.
const MAX_CHARS: usize = 30_000;

/// Longest task description derived from the command line.
const MAX_DESCRIPTION_CHARS: usize = 120;

#[async_trait]
impl Tool for RunCommandTool {
    fn name(&self) -> &str {
        "run_command"
    }

    fn description(&self) -> &str {
        // Names the shell per platform, which is more than the C# original
        // did — it hard-coded "(PowerShell)" everywhere.
        #[cfg(windows)]
        {
            concat!(
                "Run a shell command (PowerShell) in the working directory and ",
                "return combined stdout/stderr. Requires permission. ",
                "Commands run as background tasks: if one is still running after ",
                "waitSeconds (default 20, max 30; 0 = return immediately), you get ",
                "its task id and output so far while it keeps running, and you are ",
                "notified when it finishes - do not poll or sleep. Use waitSeconds: 0 ",
                "for known long jobs (builds, reviews, servers). Use task_stop to stop one."
            )
        }
        #[cfg(not(windows))]
        {
            concat!(
                "Run a shell command (sh) in the working directory and return ",
                "combined stdout/stderr. Requires permission. ",
                "Commands run as background tasks: if one is still running after ",
                "waitSeconds (default 20, max 30; 0 = return immediately), you get ",
                "its task id and output so far while it keeps running, and you are ",
                "notified when it finishes - do not poll or sleep. Use waitSeconds: 0 ",
                "for known long jobs (builds, reviews, servers). Use task_stop to stop one."
            )
        }
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "The command line to run"
            },
            "waitSeconds": {
              "type": "integer",
              "minimum": 0,
              "maximum": 30,
              "description": "How long to wait for the command before returning a task handle (default 20, clamped to 0-30). Waiting less never stops the command."
            },
            "timeoutSeconds": {
              "type": "integer",
              "description": "Optional work deadline: kill the command (and its process tree) after this many seconds. Independent of waitSeconds."
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
        let env_timeout = std::env::var(TIMEOUT_ENV).ok();

        let Some(manager) = ctx.get_task_manager() else {
            let timeout_secs = resolve_timeout(per_call, env_timeout.as_deref());
            let timeout_dur = if timeout_secs == u64::MAX {
                // "No timeout": use one week as a practical stand-in for infinity so
                // we don't need to special-case the select! arms.
                Duration::from_secs(7 * 24 * 3600)
            } else {
                Duration::from_secs(timeout_secs)
            };
            return run_command(command, &ctx.working_directory, timeout_dur, cancel).await;
        };

        let requested_wait = input.get("waitSeconds").and_then(parse_seconds);
        // Yield whenever the caller can be told about the completion later:
        // the main conversation (unless yielding is switched off) and every
        // agent running as a task with an inbox (subagents, scheduled runs).
        let owner_has_inbox = ctx
            .caller_task_id
            .as_deref()
            .and_then(|id| manager.find_task(id))
            .is_some_and(|t| t.steering().is_some());
        let yields = owner_has_inbox
            || (ctx.is_main_context && yield_enabled(std::env::var(YIELD_ENV).ok().as_deref()));
        let wait = resolve_wait(requested_wait, yields);
        let deadline = resolve_work_deadline(per_call, env_timeout.as_deref());

        start_shell_task(
            Arc::clone(manager),
            command,
            &ctx.working_directory,
            ctx.caller_task_id.as_deref(),
            deadline,
        )
        .map(|task| ToolResult::running_task(task.id.clone(), wait))
        .unwrap_or_else(|message| ToolResult::error(message))
    }
}

/// Reads a whole-seconds argument the model may send as an integer or a float.
fn parse_seconds(value: &serde_json::Value) -> Option<i64> {
    value.as_i64().or_else(|| value.as_f64().map(|f| f.floor() as i64))
}

/// The yield budget for one call, clamped to `[0, MAX_WAIT_SECS]` (D1).
///
/// `None` means "wait until the command finishes" and is only chosen when the
/// caller cannot receive a pushed completion (a context with no inbox, or the
/// main conversation with yielding disabled) and did not ask for a budget.
pub fn resolve_wait(requested: Option<i64>, yields_by_default: bool) -> Option<Duration> {
    let secs = match requested {
        Some(n) => n.clamp(0, MAX_WAIT_SECS as i64) as u64,
        None if yields_by_default => DEFAULT_WAIT_SECS,
        None => return None,
    };
    Some(Duration::from_secs(secs))
}

fn yield_enabled(env: Option<&str>) -> bool {
    !matches!(
        env.map(|v| v.trim().to_ascii_lowercase()).as_deref(),
        Some("0" | "false" | "off" | "no")
    )
}

/// The optional work deadline for a task-backed command: the per-call
/// `timeoutSeconds` when positive, else a positive env override, else none.
pub fn resolve_work_deadline(per_call: Option<i64>, env: Option<&str>) -> Option<Duration> {
    if let Some(n) = per_call.filter(|n| *n > 0) {
        return Some(Duration::from_secs(n as u64));
    }
    env.and_then(|raw| raw.trim().parse::<i64>().ok())
        .filter(|n| *n > 0)
        .map(|n| Duration::from_secs(n as u64))
}

/// Compute the effective inline timeout in seconds (u64::MAX = no timeout).
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

fn describe(command: &str) -> String {
    let line = command.lines().map(str::trim).find(|l| !l.is_empty()).unwrap_or("");
    let mut text: String = line.chars().take(MAX_DESCRIPTION_CHARS).collect();
    if line.chars().count() > MAX_DESCRIPTION_CHARS || command.trim().lines().count() > 1 {
        text.push('…');
    }
    text
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

fn shell_command(command: &str, working_dir: &str) -> tokio::process::Command {
    let mut cmd = tokio::process::Command::new(shell_program());
    cmd.args(shell_args(command))
        .current_dir(working_dir)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true); // ensures the child is killed if its owner is dropped

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
    cmd
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

/// Synchronous, non-blocking variant for the task's kill delegate, which may
/// be invoked from any thread (shutdown, `task/stop`).
fn kill_tree_detached(pid: u32) {
    #[cfg(windows)]
    {
        std::thread::spawn(move || {
            let _ = std::process::Command::new("taskkill")
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .args(["/F", "/T", "/PID", &pid.to_string()])
                .status();
        });
    }
    #[cfg(not(windows))]
    unsafe {
        libc::kill(-(pid as libc::pid_t), libc::SIGKILL);
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

/// Like [`drain_capped`], and additionally streams every chunk into the
/// task's output ring and redacted log as it arrives. A multi-byte UTF-8
/// sequence split across reads is carried over rather than mangled.
async fn drain_into_task(
    mut reader: impl tokio::io::AsyncRead + Unpin,
    cap: usize,
    manager: &TaskManager,
    task_id: &str,
    channel: TaskOutputChannel,
) -> Vec<u8> {
    let mut buf: Vec<u8> = Vec::with_capacity(cap.min(65_536));
    let mut tmp = [0u8; 8_192];
    let mut carry: Vec<u8> = Vec::new();
    loop {
        match reader.read(&mut tmp).await {
            Ok(0) | Err(_) => break,
            Ok(n) => {
                let chunk = &tmp[..n];
                let stored = buf.len();
                if stored < cap {
                    buf.extend_from_slice(&chunk[..n.min(cap - stored)]);
                }
                carry.extend_from_slice(chunk);
                let text = take_complete_utf8(&mut carry);
                manager.append_output_channel(task_id, &text, channel);
            }
        }
    }
    if !carry.is_empty() {
        manager.append_output_channel(task_id, &String::from_utf8_lossy(&carry), channel);
    }
    buf
}

/// Decodes the longest valid prefix of `bytes`, leaving an incomplete trailing
/// sequence (at most 3 bytes) behind for the next read. Invalid bytes are
/// replaced, as `from_utf8_lossy` would.
fn take_complete_utf8(bytes: &mut Vec<u8>) -> String {
    match std::str::from_utf8(bytes) {
        Ok(text) => {
            let text = text.to_owned();
            bytes.clear();
            text
        }
        Err(error) if error.error_len().is_none() => {
            let valid = error.valid_up_to();
            let text = String::from_utf8_lossy(&bytes[..valid]).into_owned();
            bytes.drain(..valid);
            text
        }
        Err(_) => {
            let text = String::from_utf8_lossy(bytes).into_owned();
            bytes.clear();
            text
        }
    }
}

/// The result text the model has always received for a finished command.
fn format_result(exit_code: i32, out_bytes: &[u8], err_bytes: &[u8]) -> ToolResult {
    let stdout = String::from_utf8_lossy(out_bytes);
    let stderr = String::from_utf8_lossy(err_bytes);
    let text = cap_chars(format!("{stdout}{stderr}"));
    let content = format!("exit code: {exit_code}\n{text}").trim_end().to_owned();
    if exit_code != 0 {
        ToolResult::error(content)
    } else {
        ToolResult::ok(content)
    }
}

// ── Task-backed executor ──────────────────────────────────────────────────────

/// Registers a shell task, spawns the process and the task runner, and
/// returns the running task. Fails only when the task cannot be registered
/// or the process cannot be started (the task is then marked failed).
fn start_shell_task(
    manager: Arc<TaskManager>,
    command: &str,
    working_dir: &str,
    owner_task_id: Option<&str>,
    deadline: Option<Duration>,
) -> Result<Arc<ManagedTask>, String> {
    let task = manager
        .register(TaskKind::Shell, describe(command), owner_task_id, TaskExecutionMode::Foreground)
        .map_err(|e| format!("Failed to start process: {e}"))?;

    let mut child = match shell_command(command, working_dir).spawn() {
        Ok(child) => child,
        Err(e) => {
            let message = format!("Failed to start process: {e}");
            manager.fail(&task.id, Some(message.clone()));
            return Err(message);
        }
    };
    if let Some(pid) = child.id() {
        task.attach_shell_kill(move || kill_tree_detached(pid));
    }
    let stdout = child.stdout.take().expect("stdout is piped");
    let stderr = child.stderr.take().expect("stderr is piped");

    tokio::spawn(run_shell_task(manager, Arc::clone(&task), child, stdout, stderr, deadline));
    Ok(task)
}

/// Owns one shell process until it ends, then performs the task's single
/// terminal transition. Never tied to the turn that started it: only the
/// task's own token (task stop, shutdown, an interrupted foreground wait)
/// or the work deadline kills it.
async fn run_shell_task(
    manager: Arc<TaskManager>,
    task: Arc<ManagedTask>,
    mut child: tokio::process::Child,
    stdout: tokio::process::ChildStdout,
    stderr: tokio::process::ChildStderr,
    deadline: Option<Duration>,
) {
    let pid = child.id();
    let id = task.id.clone();
    let stream_cap = MAX_CHARS * 4; // 4 bytes/char worst-case

    let work = async {
        tokio::join!(
            child.wait(),
            drain_into_task(stdout, stream_cap, &manager, &id, TaskOutputChannel::Stdout),
            drain_into_task(stderr, stream_cap, &manager, &id, TaskOutputChannel::Stderr),
        )
    };
    let deadline_sleep = async {
        match deadline {
            Some(d) => tokio::time::sleep(d).await,
            None => std::future::pending().await,
        }
    };

    tokio::select! {
        // A stop that lands as the process dies (the kill itself ends it)
        // must read as a stop, not as a failed exit.
        biased;
        _ = task.cancel.cancelled() => {
            if let Some(p) = pid { kill_tree(p).await; }
            manager.stop(&id);
        }
        (exit_res, out_bytes, err_bytes) = work => {
            let exit_code = exit_res.map(|s| s.code().unwrap_or(-1)).unwrap_or(-1);
            let result = format_result(exit_code, &out_bytes, &err_bytes);
            task.set_tool_output(result.content);
            let summary = format!("exit code: {exit_code}");
            if exit_code == 0 {
                manager.complete(&id, Some(summary));
            } else {
                manager.fail(&id, Some(summary));
            }
        }
        _ = deadline_sleep => {
            if let Some(p) = pid { kill_tree(p).await; }
            let secs = deadline.map(|d| d.as_secs()).unwrap_or_default();
            let message = format!(
                "Command exceeded its {secs}s work deadline (timeoutSeconds) and was terminated."
            );
            task.set_tool_output(message.clone());
            manager.fail(&id, Some(message));
        }
    }
    task.detach_shell_kill();
    // Nobody will collect the inline result of a task that had already been
    // handed to the background; its completion went out as a push instead.
    if task.mode() == TaskExecutionMode::Background {
        task.take_tool_output();
    }
}

// ── Inline executor (no task manager) ─────────────────────────────────────────

async fn run_command(
    command: &str,
    working_dir: &str,
    timeout: Duration,
    cancel: CancellationToken,
) -> ToolOutcome {
    let mut child = match shell_command(command, working_dir).spawn() {
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
            format_result(exit_code, &out_bytes, &err_bytes)
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

/// Truncate a string to at most `MAX_CHARS` characters, appending a notice when
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
    format!("{}\n{}", &s[..cutoff], super::OUTPUT_TRUNCATED)
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

    // ── non-blocking runtime ──────────────────────────────────────────────────

    #[test]
    fn wait_seconds_are_clamped_to_the_yield_bounds() {
        let secs = |requested, yields| resolve_wait(requested, yields).map(|d| d.as_secs());
        assert_eq!(secs(None, true), Some(DEFAULT_WAIT_SECS));
        assert_eq!(secs(Some(0), true), Some(0), "0 returns the handle immediately");
        assert_eq!(secs(Some(12), true), Some(12));
        assert_eq!(secs(Some(600), true), Some(MAX_WAIT_SECS), "never above the hard bound");
        assert_eq!(secs(Some(-5), true), Some(0));
        // A context that cannot receive a pushed completion waits for the
        // command unless it asks for a budget itself.
        assert_eq!(secs(None, false), None);
        assert_eq!(secs(Some(45), false), Some(MAX_WAIT_SECS));
        assert_eq!(parse_seconds(&serde_json::json!(7.9)), Some(7));
    }

    #[test]
    fn yielding_can_be_disabled_for_headless_runs() {
        assert!(yield_enabled(None));
        assert!(yield_enabled(Some("1")));
        for off in ["0", "false", "OFF", " no "] {
            assert!(!yield_enabled(Some(off)), "{off}");
        }
    }

    #[test]
    fn the_work_deadline_is_optional_and_distinct_from_the_wait() {
        assert_eq!(resolve_work_deadline(None, None), None, "no default kill");
        assert_eq!(resolve_work_deadline(Some(90), Some("5")), Some(Duration::from_secs(90)));
        assert_eq!(resolve_work_deadline(None, Some("42")), Some(Duration::from_secs(42)));
        assert_eq!(resolve_work_deadline(Some(0), Some("0")), None);
    }

    #[test]
    fn split_utf8_sequences_are_carried_not_mangled() {
        let bytes = "aé".as_bytes();
        let mut carry = bytes[..2].to_vec(); // 'a' + first byte of 'é'
        assert_eq!(take_complete_utf8(&mut carry), "a");
        assert_eq!(carry.len(), 1);
        carry.extend_from_slice(&bytes[2..]);
        assert_eq!(take_complete_utf8(&mut carry), "é");
        assert!(carry.is_empty());
    }

    fn task_ctx() -> (ToolContext, Arc<TaskManager>) {
        let manager = TaskManager::new(
            "run-command-tests",
            Some(std::env::temp_dir().join("coda-run-command-tests")),
            64 * 1024,
            32,
        );
        let ctx = ctx().with_main_context().with_task_manager(Arc::clone(&manager));
        (ctx, manager)
    }

    #[tokio::test]
    async fn with_a_task_manager_the_command_runs_as_a_shell_task() {
        let (ctx, manager) = task_ctx();
        let result = RunCommandTool
            .execute(
                &serde_json::json!({"command": ECHO_CMD, "waitSeconds": 99}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        let (task_id, wait) = result.running_task_handle().expect("a running-task hand-off");
        assert_eq!(wait, Some(Duration::from_secs(MAX_WAIT_SECS)));

        let task = manager.find_task(task_id).expect("registered");
        assert_eq!(task.kind, TaskKind::Shell);
        tokio::time::timeout(Duration::from_secs(30), task.wait_for_completion())
            .await
            .expect("the echo finishes");
        let output = task.take_tool_output().expect("the inline result is handed over");
        assert!(output.starts_with("exit code: 0"), "{output}");
        assert!(output.contains("hello world"), "{output}");
        assert!(task.redacted_peek(4000).contains("hello world"), "output is streamed to the task");
        assert_eq!(manager.get(task_id).unwrap().result.as_deref(), Some("exit code: 0"),
            "the registry keeps only the summary, never the raw output");
    }

    #[tokio::test]
    async fn the_work_deadline_kills_a_task_backed_command() {
        let (ctx, manager) = task_ctx();
        let result = RunCommandTool
            .execute(
                &serde_json::json!({"command": SLEEP_LONG_CMD, "timeoutSeconds": 1}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        let (task_id, _) = result.running_task_handle().unwrap();
        let task = manager.find_task(task_id).unwrap();
        tokio::time::timeout(Duration::from_secs(20), task.wait_for_completion())
            .await
            .expect("the deadline must end the command");
        let snapshot = manager.get(task_id).unwrap();
        assert_eq!(snapshot.status, crate::tasks::TaskRunStatus::Failed);
        assert!(snapshot.error.unwrap().contains("work deadline"));
    }

    #[tokio::test]
    async fn stopping_the_task_kills_the_command() {
        let (ctx, manager) = task_ctx();
        let result = RunCommandTool
            .execute(&serde_json::json!({"command": SLEEP_LONG_CMD}), &ctx, CancellationToken::new())
            .await;
        let (task_id, _) = result.running_task_handle().unwrap();
        let task = manager.find_task(task_id).unwrap();
        assert_eq!(manager.stop_task(task_id), crate::tasks::TaskActionResult::Ok);
        tokio::time::timeout(Duration::from_secs(20), task.wait_for_completion())
            .await
            .expect("stop must end the command");
        assert_eq!(task.status(), crate::tasks::TaskRunStatus::Stopped);
    }

    #[test]
    fn the_description_names_the_shell_it_will_use() {
        // The description is all the model has to go on. Left saying only
        // "a shell command", it reaches for bash -- and on Windows
        // `for i in $(seq 1 100); do ...` dies with "Missing opening '('
        // after keyword 'for'", burning a turn before it retries in
        // PowerShell. The C# original always named the shell; the port
        // dropped it.
        let description = RunCommandTool.description();
        let shell = shell_program();
        let named = if shell == "powershell.exe" { "PowerShell" } else { "sh" };
        assert!(
            description.contains(named),
            "description does not say which shell it runs ({named}): {description:?}"
        );
    }
}