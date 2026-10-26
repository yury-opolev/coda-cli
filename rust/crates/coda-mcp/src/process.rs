//! Spawns and supervises an MCP server child process.
//!
//! The child's stderr is drained to `tracing` so a server that floods stderr
//! cannot OOM the host. The ring is kept internal to this module and is not
//! exposed — crash diagnostics should come from the server's error messages,
//! not the stderr tail (which is rarely useful on Windows).

use std::ffi::OsString;
use std::process::Stdio;

use tokio::io::{AsyncBufReadExt, BufReader};
use tokio::process::{Child, ChildStdin, ChildStdout};

use crate::config::McpConnectable;
use crate::error::McpError;

/// Holds the child handle after stdin/stdout have been handed to the transport.
/// Callers receive stdin/stdout separately from `spawn` so they can move them
/// into `McpClient::connect` without a partial-move problem (tokio `Child`
/// implements `Drop`).
pub(crate) struct McpProcess {
    pub child: Child,
    tree: ProcessTree,
}

impl McpProcess {
    /// Spawns the MCP server process described by `config`.
    ///
    /// Returns `(process, stdin, stdout)`. The caller passes `stdin`/`stdout`
    /// into `McpClient::connect` and keeps `process` for lifecycle management.
    pub fn spawn(config: &McpConnectable) -> Result<(Self, ChildStdin, ChildStdout), McpError> {
        Self::spawn_inner(&config.command, config.args.iter(), |b| {
            for (k, v) in &config.env {
                b.env(k, v);
            }
        })
        .map_err(|source| McpError::Spawn {
            program: config.command.clone(),
            source,
        })
    }

    /// Spawn with an explicit command and args, bypassing `McpConnectable`.
    ///
    /// Only available in tests — production code always uses `spawn`.
    #[cfg(test)]
    pub fn spawn_raw(
        program: impl Into<OsString>,
        args: impl IntoIterator<Item = impl Into<OsString>>,
    ) -> Result<(Self, ChildStdin, ChildStdout), McpError> {
        let program = program.into();
        let program_str = program.to_string_lossy().to_string();
        Self::spawn_inner(program, args, |_| {}).map_err(|source| McpError::Spawn {
            program: program_str,
            source,
        })
    }

    fn spawn_inner(
        program: impl AsRef<std::ffi::OsStr>,
        args: impl IntoIterator<Item = impl Into<OsString>>,
        configure: impl FnOnce(&mut tokio::process::Command),
    ) -> std::io::Result<(Self, ChildStdin, ChildStdout)> {
        let mut builder = tokio::process::Command::new(program);
        builder
            .args(args.into_iter().map(|a| a.into()))
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true);
        #[cfg(unix)]
        builder.process_group(0);
        configure(&mut builder);

        let mut child = builder.spawn()?;
        #[cfg(windows)]
        let tree = ProcessTree::attach(child.id(), child.raw_handle());
        #[cfg(unix)]
        let tree = ProcessTree::attach(child.id());
        let stdin = child.stdin.take().expect("stdin piped");
        let stdout = child.stdout.take().expect("stdout piped");
        let stderr = child.stderr.take().expect("stderr piped");

        // Drain stderr to tracing; a server that floods stderr must not OOM.
        tokio::spawn(drain_stderr(stderr));

        Ok((Self { child, tree }, stdin, stdout))
    }

    /// Stdin has already been closed by the client. Waits until `deadline` for
    /// the direct child, then force-kills the whole tree; always kills any
    /// leftover tree members afterwards. Never fails: the outcome is reported.
    pub async fn terminate(mut self, deadline: tokio::time::Instant) -> TerminationReport {
        let started = tokio::time::Instant::now();
        let grace = deadline.saturating_duration_since(started);
        let mut early_kills = 0;
        let exit = match tokio::time::timeout_at(deadline, self.child.wait()).await {
            Ok(Ok(status)) if status.success() => ProcessExit::Graceful,
            Ok(Ok(status)) => ProcessExit::Exited(status.to_string()),
            Ok(Err(error)) => ProcessExit::Exited(error.to_string()),
            Err(_) => {
                self.tree.kill_tree_gracefully();
                let polite = tokio::time::Instant::now() + TERM_GRACE;
                let exited = tokio::time::timeout_at(polite.min(deadline + FORCE_WAIT), self.child.wait())
                    .await
                    .is_ok();
                if !exited {
                    // The count includes the direct child, which is not a leftover.
                    early_kills = self.tree.kill_tree(true).map_or(0, |n| n.saturating_sub(1));
                    let _ = self.child.start_kill();
                }
                match tokio::time::timeout_at(deadline + FORCE_WAIT, self.child.wait()).await {
                    Ok(Ok(_)) => ProcessExit::ForceKilled,
                    Ok(Err(error)) => ProcessExit::Unconfirmed(error.to_string()),
                    Err(_) => ProcessExit::Unconfirmed("exit not observed after kill".to_string()),
                }
            }
        };
        let swept = self.tree.kill_tree(false);
        self.tree.disarm();
        TerminationReport {
            exit,
            grace,
            leftovers_killed: swept.map(|n| n + early_kills),
        }
    }

    /// Waits for stdin EOF to take effect before bounded forced termination.
    pub async fn shutdown(self, deadline: tokio::time::Instant) -> std::io::Result<()> {
        match self.terminate(deadline).await.exit {
            ProcessExit::Graceful => Ok(()),
            ProcessExit::Exited(status) => Err(std::io::Error::other(format!(
                "MCP server exited with {status}"
            ))),
            ProcessExit::ForceKilled => Err(std::io::Error::new(
                std::io::ErrorKind::TimedOut,
                "MCP server required forced termination",
            )),
            ProcessExit::Unconfirmed(_) => Err(std::io::Error::new(
                std::io::ErrorKind::TimedOut,
                "MCP server termination could not be confirmed",
            )),
        }
    }
}

/// How long a SIGTERM to the process group is given before SIGKILL (unix only).
const TERM_GRACE: std::time::Duration = std::time::Duration::from_millis(250);
/// Extra time after the deadline to observe the exit of a force-killed child.
const FORCE_WAIT: std::time::Duration = std::time::Duration::from_millis(500);

/// How the old process ended.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum ProcessExit {
    /// Exited with success within the grace period.
    Graceful,
    /// Exited on its own with a failure status (crash / non-zero exit), or had already exited.
    Exited(String),
    /// Did not exit within the grace period; the tree was force-killed and the exit confirmed.
    ForceKilled,
    /// Force-kill was attempted but the exit could not be confirmed.
    Unconfirmed(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct TerminationReport {
    pub exit: ProcessExit,
    pub grace: std::time::Duration,
    /// Other processes of the tree that were still alive and got killed.
    /// `None` when the platform cannot count them.
    pub leftovers_killed: Option<usize>,
}

#[cfg(windows)]
mod tree {
    use std::os::windows::io::RawHandle;

    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE};
    use windows_sys::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
        JobObjectBasicAccountingInformation, JobObjectExtendedLimitInformation,
        QueryInformationJobObject, SetInformationJobObject, TerminateJobObject,
    };

    struct Job(HANDLE);

    // SAFETY: a job handle is just a kernel object reference, usable from any thread.
    unsafe impl Send for Job {}
    unsafe impl Sync for Job {}

    impl Drop for Job {
        fn drop(&mut self) {
            // KILL_ON_JOB_CLOSE terminates whatever is still in the job.
            // SAFETY: the handle is owned by this value and closed once.
            unsafe { CloseHandle(self.0) };
        }
    }

    /// A Job Object owning the server and everything it spawns. Without a job
    /// (creation/assignment failed, e.g. a restrictive parent job) it falls
    /// back to `taskkill /T`.
    pub(super) struct ProcessTree {
        job: Option<Job>,
        pid: Option<u32>,
    }

    impl ProcessTree {
        /// Must be called right after spawn. `tokio::process::Command` cannot
        /// spawn suspended, so a grandchild started before the assignment
        /// escapes the job; the window is microseconds, far shorter than a
        /// wrapper needs to start its own child.
        pub(super) fn attach(pid: Option<u32>, handle: Option<RawHandle>) -> Self {
            let job = handle.and_then(|handle| match create_job(handle as HANDLE) {
                Ok(job) => Some(job),
                Err(error) => {
                    tracing::warn!("MCP process tree not job-managed, using taskkill: {error}");
                    None
                }
            });
            Self { job, pid }
        }

        pub(super) fn kill_tree_gracefully(&self) {}

        /// Kills the whole tree; returns how many processes were active, when known.
        /// `root_alive` tells whether the direct child may still be running, so
        /// the taskkill fallback never targets a possibly reused pid.
        pub(super) fn kill_tree(&self, root_alive: bool) -> Option<usize> {
            match &self.job {
                Some(job) => {
                    let active = active_processes(job);
                    // SAFETY: valid job handle.
                    unsafe { TerminateJobObject(job.0, 1) };
                    active
                }
                None => {
                    if let (true, Some(pid)) = (root_alive, self.pid) {
                        let _ = std::process::Command::new("taskkill")
                            .args(["/T", "/F", "/PID", &pid.to_string()])
                            .stdout(std::process::Stdio::null())
                            .stderr(std::process::Stdio::null())
                            .status();
                    }
                    None
                }
            }
        }

        pub(super) fn disarm(&mut self) {}
    }

    fn create_job(process: HANDLE) -> Result<Job, String> {
        // SAFETY: plain Win32 calls with correctly sized, initialised arguments.
        unsafe {
            let handle = CreateJobObjectW(std::ptr::null(), std::ptr::null());
            if handle.is_null() {
                return Err(format!("CreateJobObjectW: {}", std::io::Error::last_os_error()));
            }
            let job = Job(handle);
            let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if SetInformationJobObject(
                job.0,
                JobObjectExtendedLimitInformation,
                &limits as *const _ as *const _,
                std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            ) == 0
            {
                return Err(format!(
                    "SetInformationJobObject: {}",
                    std::io::Error::last_os_error()
                ));
            }
            if AssignProcessToJobObject(job.0, process) == 0 {
                return Err(format!(
                    "AssignProcessToJobObject: {}",
                    std::io::Error::last_os_error()
                ));
            }
            Ok(job)
        }
    }

    fn active_processes(job: &Job) -> Option<usize> {
        // SAFETY: the buffer matches the requested information class.
        unsafe {
            let mut info: JOBOBJECT_BASIC_ACCOUNTING_INFORMATION = std::mem::zeroed();
            let ok = QueryInformationJobObject(
                job.0,
                JobObjectBasicAccountingInformation,
                &mut info as *mut _ as *mut _,
                std::mem::size_of::<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>() as u32,
                std::ptr::null_mut(),
            );
            (ok != 0).then_some(info.ActiveProcesses as usize)
        }
    }
}

#[cfg(unix)]
mod tree {
    /// The server runs in its own process group (`process_group(0)`), so the
    /// group id equals the child's pid and signals reach every descendant
    /// that did not start a group of its own.
    pub(super) struct ProcessTree {
        pgid: libc::pid_t,
        armed: bool,
    }

    impl ProcessTree {
        pub(super) fn attach(pid: Option<u32>) -> Self {
            Self {
                pgid: pid.map_or(0, |p| p as libc::pid_t),
                armed: pid.is_some(),
            }
        }

        fn signal(&self, signal: libc::c_int) {
            if self.armed {
                // SAFETY: killpg on our own group; ESRCH (already gone) is fine.
                unsafe { libc::killpg(self.pgid, signal) };
            }
        }

        pub(super) fn kill_tree_gracefully(&self) {
            self.signal(libc::SIGTERM);
        }

        /// Cannot count the members of a process group, so always `None`.
        pub(super) fn kill_tree(&self, _root_alive: bool) -> Option<usize> {
            self.signal(libc::SIGKILL);
            None
        }

        /// The group was reaped by `terminate`; the pgid must not be signalled again.
        pub(super) fn disarm(&mut self) {
            self.armed = false;
        }
    }

    impl Drop for ProcessTree {
        fn drop(&mut self) {
            self.signal(libc::SIGKILL);
        }
    }
}

use tree::ProcessTree;
async fn drain_stderr(stderr: tokio::process::ChildStderr) {
    let mut lines = BufReader::new(stderr).lines();
    while let Ok(Some(line)) = lines.next_line().await {
        tracing::debug!(target: "coda::mcp::stderr", "{line}");
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    #[tokio::test]
    async fn stdin_eof_allows_a_clean_process_exit() {
        let (process, stdin, stdout) = if cfg!(windows) {
            McpProcess::spawn_raw(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$null = [Console]::In.ReadToEnd(); exit 0",
                ],
            )
        } else {
            McpProcess::spawn_raw("cat", Vec::<String>::new())
        }
        .expect("spawn");
        drop(stdin);
        drop(stdout);
        process
            .shutdown(tokio::time::Instant::now() + Duration::from_secs(3))
            .await
            .expect("EOF must be graceful");
    }

    #[tokio::test]
    async fn a_hung_process_is_reaped_and_reported_within_the_allowance() {
        let (process, stdin, stdout) = if cfg!(windows) {
            McpProcess::spawn_raw(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "Start-Sleep -Seconds 30",
                ],
            )
        } else {
            McpProcess::spawn_raw("sleep", ["30"])
        }
        .expect("spawn");
        drop(stdin);
        drop(stdout);
        let started = tokio::time::Instant::now();
        let error = process
            .shutdown(started + Duration::from_millis(100))
            .await
            .unwrap_err();
        assert_eq!(error.kind(), std::io::ErrorKind::TimedOut);
        assert!(started.elapsed() < Duration::from_millis(650));
    }

    use crate::test_fixture::{PID_FILE_ENV, fake_server_connectable, is_process_alive, wait_until};

    /// stdout stays open: libtest's banner would otherwise hit a broken pipe and panic.
    fn spawn_fake(mode: &str, env: &[(&str, &str)]) -> (McpProcess, ChildStdout) {
        let config = fake_server_connectable("fake", mode, env);
        let (process, stdin, stdout) = McpProcess::spawn(&config).expect("spawn fake");
        drop(stdin);
        (process, stdout)
    }

    async fn spawn_with_grandchild() -> (McpProcess, u32, std::path::PathBuf) {
        let pid_file = std::env::temp_dir().join(format!("coda-mcp-{}.pid", uuid::Uuid::new_v4()));
        let config = fake_server_connectable(
            "fake",
            "grandchild",
            &[(PID_FILE_ENV, pid_file.to_str().unwrap())],
        );
        let (process, stdin, stdout) = McpProcess::spawn(&config).expect("spawn fake");
        // Keep the pipes open: closing stdin would end the wrapper too early.
        std::mem::forget(stdout);
        std::mem::forget(stdin);
        assert!(wait_until(Duration::from_secs(10), || pid_file.exists()).await);
        let pid: u32 = std::fs::read_to_string(&pid_file)
            .unwrap()
            .trim()
            .parse()
            .unwrap();
        assert!(is_process_alive(pid));
        (process, pid, pid_file)
    }

    #[tokio::test]
    async fn terminate_reports_a_forced_kill_as_force_killed_not_an_error() {
        let (process, _stdout) = spawn_fake("hang", &[]);
        let pid = process.child.id().expect("pid");
        let report = process
            .terminate(tokio::time::Instant::now() + Duration::from_millis(300))
            .await;
        assert_eq!(report.exit, ProcessExit::ForceKilled);
        assert!(wait_until(Duration::from_secs(2), || !is_process_alive(pid)).await);
    }

    #[tokio::test]
    async fn terminate_reports_a_crash_as_exited() {
        let (process, _stdout) = spawn_fake("crash", &[]);
        let report = process
            .terminate(tokio::time::Instant::now() + Duration::from_secs(5))
            .await;
        assert!(matches!(report.exit, ProcessExit::Exited(_)), "{report:?}");
    }

    #[tokio::test]
    async fn terminate_kills_a_grandchild_left_behind_by_a_wrapper() {
        let (process, grandchild, pid_file) = spawn_with_grandchild().await;
        let report = process
            .terminate(tokio::time::Instant::now() + Duration::from_secs(3))
            .await;
        let gone = wait_until(Duration::from_secs(2), || !is_process_alive(grandchild)).await;
        let _ = std::fs::remove_file(&pid_file);
        assert!(gone, "grandchild survived: {report:?}");
    }

    #[tokio::test]
    async fn dropping_the_process_kills_its_tree() {
        let (process, grandchild, pid_file) = spawn_with_grandchild().await;
        drop(process);
        let gone = wait_until(Duration::from_secs(2), || !is_process_alive(grandchild)).await;
        let _ = std::fs::remove_file(&pid_file);
        assert!(gone, "grandchild survived drop");
    }
}
