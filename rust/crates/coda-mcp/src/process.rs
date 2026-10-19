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
        configure(&mut builder);

        let mut child = builder.spawn()?;
        let stdin = child.stdin.take().expect("stdin piped");
        let stdout = child.stdout.take().expect("stdout piped");
        let stderr = child.stderr.take().expect("stderr piped");

        // Drain stderr to tracing; a server that floods stderr must not OOM.
        tokio::spawn(drain_stderr(stderr));

        Ok((Self { child }, stdin, stdout))
    }

    /// Waits for stdin EOF to take effect before bounded forced termination.
    pub async fn shutdown(mut self, deadline: tokio::time::Instant) -> std::io::Result<()> {
        match tokio::time::timeout_at(deadline, self.child.wait()).await {
            Ok(Ok(status)) if status.success() => Ok(()),
            Ok(Ok(status)) => Err(std::io::Error::other(format!(
                "MCP server exited with {status}"
            ))),
            Ok(Err(error)) => Err(error),
            Err(_) => {
                self.child.start_kill()?;
                match tokio::time::timeout_at(
                    deadline + std::time::Duration::from_millis(500),
                    self.child.wait(),
                )
                .await
                {
                    Ok(Ok(_)) => Err(std::io::Error::new(
                        std::io::ErrorKind::TimedOut,
                        "MCP server required forced termination",
                    )),
                    Ok(Err(error)) => Err(error),
                    Err(_) => Err(std::io::Error::new(
                        std::io::ErrorKind::TimedOut,
                        "MCP server termination could not be confirmed",
                    )),
                }
            }
        }
    }
}

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
}
