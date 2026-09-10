//! Spawns and supervises a `coda serve` engine process.
//!
//! The engine talks JSON-RPC on stdin/stdout, so stdout is reserved for the
//! protocol and stderr is drained separately into a bounded ring that we can
//! surface in diagnostics when the engine dies unexpectedly.

use std::collections::VecDeque;
use std::ffi::OsString;
use std::path::PathBuf;
use std::process::Stdio;
use std::sync::{Arc, Mutex};

use tokio::io::{AsyncBufReadExt, BufReader};
use tokio::process::{Child, Command};
use tokio::sync::mpsc;

use crate::error::ClientError;
use crate::transport::{connect, Connection, ConnectionTasks, Inbound};

/// Number of stderr lines retained for crash diagnostics.
const STDERR_RING_LINES: usize = 200;

/// How to launch the engine.
#[derive(Debug, Clone)]
pub struct EngineCommand {
    /// Executable to run. Defaults to `coda` resolved from `PATH`.
    pub program: OsString,
    /// Arguments. Defaults to `["serve"]`.
    pub args: Vec<OsString>,
    /// Working directory for the session.
    pub working_dir: Option<PathBuf>,
    /// Extra environment variables.
    pub env: Vec<(OsString, OsString)>,
    /// Environment variables to remove from the child's inherited environment.
    ///
    /// Adding a variable cannot express "make sure this inherited secret is
    /// gone" — an empty value is still a present, empty variable, which some
    /// readers treat differently from absence. Removal is required to isolate a
    /// child from inherited credentials (`ANTHROPIC_API_KEY`) and configuration
    /// (`CODA_SERVE_*`) so a test environment cannot leak into it.
    pub env_remove: Vec<OsString>,
}

impl Default for EngineCommand {
    fn default() -> Self {
        Self {
            program: OsString::from("coda"),
            args: vec![OsString::from("serve")],
            working_dir: None,
            env: Vec::new(),
            env_remove: Vec::new(),
        }
    }
}

impl EngineCommand {
    /// A command with no arguments preset. Use [`EngineCommand::default`] for
    /// the usual `coda serve` invocation.
    pub fn new(program: impl Into<OsString>) -> Self {
        Self {
            program: program.into(),
            args: Vec::new(),
            working_dir: None,
            env: Vec::new(),
            env_remove: Vec::new(),
        }
    }

    pub fn arg(mut self, arg: impl Into<OsString>) -> Self {
        self.args.push(arg.into());
        self
    }

    pub fn args<I, S>(mut self, args: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: Into<OsString>,
    {
        self.args.extend(args.into_iter().map(Into::into));
        self
    }

    pub fn working_dir(mut self, dir: impl Into<PathBuf>) -> Self {
        self.working_dir = Some(dir.into());
        self
    }

    pub fn env(mut self, key: impl Into<OsString>, value: impl Into<OsString>) -> Self {
        self.env.push((key.into(), value.into()));
        self
    }

    /// Removes an inherited environment variable from the child.
    ///
    /// Use this to strip credentials and configuration the child must not see
    /// (e.g. `ANTHROPIC_API_KEY`, `CODA_SERVE_API_KEY`, other `CODA_SERVE_*`),
    /// which setting an empty value cannot guarantee.
    pub fn env_remove(mut self, key: impl Into<OsString>) -> Self {
        self.env_remove.push(key.into());
        self
    }
}

/// A running engine process and its protocol connection.
#[derive(Debug)]
pub struct Engine {
    child: Child,
    connection: Connection,
    tasks: ConnectionTasks,
    stderr: Arc<Mutex<VecDeque<String>>>,
}

impl Engine {
    /// Launches the engine and wires up its protocol streams.
    pub fn spawn(
        command: EngineCommand,
    ) -> Result<(Self, mpsc::UnboundedReceiver<Inbound>), ClientError> {
        let program = command.program.to_string_lossy().into_owned();

        let mut builder = Command::new(&command.program);
        builder
            .args(&command.args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true);

        if let Some(dir) = &command.working_dir {
            builder.current_dir(dir);
        }
        for (key, value) in &command.env {
            builder.env(key, value);
        }
        for key in &command.env_remove {
            builder.env_remove(key);
        }

        let mut child = builder
            .spawn()
            .map_err(|source| ClientError::Spawn { program, source })?;

        if let Some(ctx) = coda_diagnostics::current() {
            ctx.record(coda_diagnostics::Event::EngineStart { pid: child.id() });
        }

        let stdin = child.stdin.take().ok_or(ClientError::MissingStdio("stdin"))?;
        let stdout = child
            .stdout
            .take()
            .ok_or(ClientError::MissingStdio("stdout"))?;
        let stderr = child
            .stderr
            .take()
            .ok_or(ClientError::MissingStdio("stderr"))?;

        let ring = Arc::new(Mutex::new(VecDeque::with_capacity(STDERR_RING_LINES)));
        tokio::spawn(drain_stderr(stderr, Arc::clone(&ring)));

        let (connection, inbound, tasks) = connect(stdout, stdin);

        Ok((
            Self {
                child,
                connection,
                tasks,
                stderr: ring,
            },
            inbound,
        ))
    }

    /// A cloneable handle for sending requests and notifications.
    pub fn connection(&self) -> Connection {
        self.connection.clone()
    }

    /// The most recent engine stderr lines, oldest first.
    pub fn recent_stderr(&self) -> Vec<String> {
        self.stderr
            .lock()
            .expect("stderr ring poisoned")
            .iter()
            .cloned()
            .collect()
    }

    /// Returns the exit status if the engine has already terminated.
    pub fn try_exit_status(&mut self) -> Option<std::process::ExitStatus> {
        self.child.try_wait().ok().flatten()
    }

    /// Closes stdin and waits for a graceful exit, killing the process if it
    /// outlives the grace period.
    ///
    /// The connection is *closed*, not merely dropped. Dropping only ends the
    /// connection when the last clone goes, and the application keeps one:
    /// after a sign-out the writer therefore stayed alive on somebody else's
    /// sender, so a command issued afterwards was queued for a process that no
    /// longer exists and its caller waited for ever. Closing fails every
    /// outstanding request now, refuses new ones, and lets the writer drain
    /// what was already queued — the reply to a reverse request the engine is
    /// blocked on is still written — before it closes the engine's stdin,
    /// which is what a healthy engine exits on.
    pub async fn shutdown(mut self, grace: std::time::Duration) -> std::io::Result<()> {
        self.connection.close();
        // Bounded by the same grace: the writer is draining into a pipe whose
        // reader may already be gone, and a stop that hangs here is the very
        // thing this is preventing elsewhere.
        let _ = tokio::time::timeout(grace, &mut self.tasks.writer).await;
        self.tasks.reader.abort();

        let result = match tokio::time::timeout(grace, self.child.wait()).await {
            Ok(result) => result.map(|_| ()),
            Err(_) => {
                tracing::warn!("engine did not exit within the grace period; killing it");
                self.child.kill().await
            }
        };

        if let Some(ctx) = coda_diagnostics::current() {
            // Exit code and correlation only — never the stderr ring, however
            // plausible its contents look; that stays a narrowly-scoped,
            // explicit crash-diagnostic feature, not routine logging.
            let exit_code = self.child.try_wait().ok().flatten().and_then(|s| s.code());
            ctx.record(coda_diagnostics::Event::EngineEnd { exit_code });
        }

        result
    }
}

async fn drain_stderr(
    stderr: tokio::process::ChildStderr,
    ring: Arc<Mutex<VecDeque<String>>>,
) {
    let mut lines = BufReader::new(stderr).lines();
    while let Ok(Some(line)) = lines.next_line().await {
        // Scrubbed: no raw stderr content in tracing — it can contain
        // anything the engine process wrote, including provider error text
        // or a credential-bearing URL. The ring buffer below still retains
        // the actual lines for on-demand crash diagnostics (an explicit,
        // narrowly-scoped feature, not routine logging).
        tracing::debug!(target: "coda::engine::stderr", bytes = line.len(), "engine stderr line received");
        let mut ring = ring.lock().expect("stderr ring poisoned");
        if ring.len() == STDERR_RING_LINES {
            ring.pop_front();
        }
        ring.push_back(line);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_to_the_coda_serve_command() {
        let command = EngineCommand::default();
        assert_eq!(command.program, OsString::from("coda"));
        assert_eq!(command.args, vec![OsString::from("serve")]);
    }

    #[test]
    fn builder_appends_arguments_in_order() {
        let command = EngineCommand::new("coda.exe")
            .arg("serve")
            .args(["--api-key", "secret"]);
        assert_eq!(
            command.args,
            vec![
                OsString::from("serve"),
                OsString::from("--api-key"),
                OsString::from("secret")
            ]
        );
    }

    #[test]
    fn new_does_not_preset_arguments() {
        assert!(EngineCommand::new("coda.exe").args.is_empty());
    }

    #[tokio::test]
    async fn reports_a_spawn_failure_with_the_program_name() {
        let result = Engine::spawn(EngineCommand::new(
            "definitely-not-a-real-executable-xyz",
        ));
        match result {
            Err(ClientError::Spawn { program, .. }) => {
                assert_eq!(program, "definitely-not-a-real-executable-xyz");
            }
            other => panic!("expected a spawn error, got {other:?}"),
        }
    }

    // ── Stopping an engine other handles still hold ──────────────────────────

    /// The bound these tests await under. The failure they cover is a caller
    /// that waits for ever, so a hang has to be a failed assertion rather than
    /// a run that never ends.
    const NO_HANG: std::time::Duration = std::time::Duration::from_secs(10);

    /// A child that stays alive and never speaks the protocol — exactly what
    /// an engine looks like between "stop this" and "it is gone".
    fn idle_child() -> EngineCommand {
        if cfg!(windows) {
            EngineCommand::new("powershell.exe")
                .arg("-NoProfile")
                .arg("-NonInteractive")
                .arg("-Command")
                .arg("Start-Sleep -Seconds 30")
        } else {
            EngineCommand::new("sleep").arg("30")
        }
    }

    #[tokio::test]
    async fn a_request_made_after_a_shutdown_fails_rather_than_waiting_for_a_dead_engine() {
        // The hang this closes. A sign-out stops the engine but the
        // application keeps its own clone of the connection, so the writer
        // task survives on that clone's sender. A command issued afterwards
        // registered a waiter, the frame was written into a pipe whose reader
        // is a killed process, and nothing ever failed the waiter: the UI
        // awaited it inline and the terminal was lost.
        let (engine, _inbound) = Engine::spawn(idle_child()).expect("the child starts");
        let connection = engine.connection();
        let _ = engine.shutdown(std::time::Duration::from_millis(250)).await;

        let error = tokio::time::timeout(NO_HANG, connection.request("session/fork", None))
            .await
            .expect("a request made after a shutdown never returned")
            .expect_err("a stopped engine cannot answer");
        assert!(
            matches!(
                error,
                ClientError::ConnectionClosed | ClientError::Rpc(_) | ClientError::Io(_)
            ),
            "got {error:?}"
        );
        assert!(connection.is_closed(), "the stopped engine's connection still reports open");
    }

    #[tokio::test]
    async fn shutting_down_fails_the_requests_already_in_flight() {
        let (engine, _inbound) = Engine::spawn(idle_child()).expect("the child starts");
        let connection = engine.connection();
        let waiter = connection.send_request("session/getState", None).expect("send");

        let _ = engine.shutdown(std::time::Duration::from_millis(250)).await;

        let outcome = tokio::time::timeout(NO_HANG, waiter)
            .await
            .expect("an in-flight request outlived the engine that was to answer it")
            .expect("a stopped engine must fail its waiters, not drop them");
        assert!(outcome.is_err(), "a stopped engine answered a request");
    }

    #[tokio::test]
    async fn a_graceful_stop_answers_over_the_connection_before_the_child_goes_away() {
        // The other side of the fail-fast tests, and the one they must not
        // break. A stop is a *conversation*: the client asks the engine to
        // shut down over the live connection, the engine answers, and only
        // then is the process stopped — which it does by seeing its stdin
        // close, not by being killed. Closing the connection when that
        // request is sent would refuse it before it reached the wire; killing
        // instead of closing stdin would deny the child the chance to flush.
        // Both are visible here: the answer must come back, and the marker is
        // only written on a clean end-of-input.
        let dir = tempfile::tempdir().expect("temp dir");
        let marker = dir.path().join("stopped-after-answering.marker");
        let (engine, _inbound) =
            Engine::spawn(answering_child(&marker)).expect("the child starts");
        let connection = engine.connection();

        let answer = tokio::time::timeout(NO_HANG, connection.request("shutdown", None))
            .await
            .expect("the engine never answered the stop request")
            .expect("a live engine must answer over an open connection");
        assert_eq!(answer["ok"], true, "{answer}");

        let _ = engine.shutdown(std::time::Duration::from_secs(5)).await;

        assert!(
            marker.exists(),
            "the child was killed rather than allowed to finish on end-of-input"
        );
        assert!(connection.is_closed(), "the stopped engine's connection still reports open");
    }

    /// A child that answers the first framed request it is sent, then exits
    /// when its stdin closes — the minimum that makes "asked, answered, then
    /// stopped" observable against a real process.
    fn answering_child(marker: &std::path::Path) -> EngineCommand {
        // `Content-Length: N\r\n\r\n<body>`, the same framing the transport
        // writes: the reply is built for request id 1, which is the first id a
        // fresh connection mints.
        let marker = marker.display().to_string();
        if cfg!(windows) {
            EngineCommand::new("powershell.exe")
                .arg("-NoProfile")
                .arg("-NonInteractive")
                .arg("-Command")
                .arg(format!(
                    "$null = [Console]::In.ReadLine(); \
                     $body = '{{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{{\"ok\":true}}}}'; \
                     $out = [Console]::OpenStandardOutput(); \
                     $bytes = [Text.Encoding]::ASCII.GetBytes(\
                     \"Content-Length: $($body.Length)`r`n`r`n$body\"); \
                     $out.Write($bytes, 0, $bytes.Length); $out.Flush(); \
                     $null = [Console]::In.ReadToEnd(); \
                     New-Item -ItemType File -Force -Path '{marker}' | Out-Null"
                ))
        } else {
            EngineCommand::new("sh").arg("-c").arg(format!(
                "read _header; \
                 body='{{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{{\"ok\":true}}}}'; \
                 printf 'Content-Length: %s\\r\\n\\r\\n%s' \"${{#body}}\" \"$body\"; \
                 cat >/dev/null; touch '{marker}'"
            ))
        }
    }
}
