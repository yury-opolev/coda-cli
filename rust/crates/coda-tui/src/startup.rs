//! What the launch flags ask for.
//!
//! Only the re-export now. `SessionIntent`/`from_flags` live in `coda-boot`
//! because they are pure: no disk access, no terminal, no running engine.
//!
//! **`resolve()` is gone.** It read `SessionTranscriptStore` and called
//! `coda_agent::session::fork` to turn an intent into a session id *before*
//! an engine existed, which made this front-end a privileged component: it
//! knew where the engine keeps its transcripts and what format they are in,
//! and an external orchestrator driving the same public API could not do what
//! it did. The resolution now happens through the engine, in
//! [`crate::api::boot`]: spawn the core, `session/listSessions` (read-only and
//! valid before `initialize`), `initialize { sessionId }`, then `session/fork`
//! when forking.
//!
//! Moving it into `coda-boot` instead would have satisfied a naive "the TUI
//! must not depend on `coda-agent`" check while changing nothing about who
//! reads the files, so it was deleted rather than relocated.

pub use coda_boot::SessionIntent;

use anyhow::Result;
use coda_client::{Engine, EngineCommand, Inbound};
use coda_render::Theme;

/// The launch this CLI invocation describes, assembled but not started.
///
/// Separated from [`connect`] because the *preflight* runs between the two: a
/// first run, or a launch whose explicitly-selected provider has no
/// credential, has to be offered a sign-in **before** a child is spawned, and
/// the account it connects is applied to this command. A function that
/// assembled and spawned in one step left no seam for that at all.
pub fn engine_command(
    cli: &crate::cli::Cli,
    prepare: impl FnOnce(EngineCommand) -> EngineCommand,
) -> Result<EngineCommand> {
    let working_dir = cli.resolved_directory()?;
    let mut command = EngineCommand::new(cli.engine_program().as_os_str())
        .arg("serve")
        .working_dir(&working_dir);
    command = prepare(command);
    for arg in &cli.engine_args {
        command = command.arg(std::ffi::OsString::from(arg));
    }
    Ok(command)
}

/// Launches the engine this CLI invocation asked for and becomes its client.
///
/// The whole launch — which binary, which working directory, and the
/// local-maintenance contract that follows from those — lives here rather
/// than in `main`, so the shipped path and the path a test exercises are the
/// same code. A binary that assembled this itself was how the standalone
/// front-end came to claim `TrustedLocal` for an engine the user had
/// explicitly pointed elsewhere.
pub async fn connect(
    command: EngineCommand,
    theme: Theme,
    access_mode: crate::local::AccessMode,
) -> Result<(
    crate::app::App,
    Engine,
    tokio::sync::mpsc::UnboundedReceiver<Inbound>,
)> {
    crate::app::App::connect(command, theme, access_mode).await
}

/// [`engine_command`] then [`connect`], for a caller with no preflight to run.
pub async fn connect_from_cli(
    cli: &crate::cli::Cli,
    theme: Theme,
    prepare: impl FnOnce(EngineCommand) -> EngineCommand,
) -> Result<(
    crate::app::App,
    Engine,
    tokio::sync::mpsc::UnboundedReceiver<Inbound>,
)> {
    let command = engine_command(cli, prepare)?;
    connect(command, theme, cli.access_mode()).await
}
