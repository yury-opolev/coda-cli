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
    pub async fn shutdown(mut self, grace: std::time::Duration) -> std::io::Result<()> {
        // Dropping the connection closes the outgoing channel, which ends the
        // writer task and in turn closes the engine's stdin.
        drop(self.connection);
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
}
