//! The bootstrap order every Coda front-end shares.
//!
//! Before this stage the interactive TUI and `coda run` resolved
//! `--resume`/`--continue`/`--fork` by reading `.coda/sessions` themselves
//! (`SessionTranscriptStore::list`, `coda_agent::session::fork`) and only then
//! started an engine. That made the front-end a privileged component: it knew
//! where the engine keeps its files and what format they are in, and an
//! external orchestrator driving the same API could not do what it did.
//!
//! The order is now the one the contract documents (§2.8):
//!
//! 1. **spawn the core** — an ordinary `coda serve` child, the same process an
//!    external orchestrator would launch;
//! 2. **`session/listSessions`** — read-only and valid *before* `initialize`,
//!    which is what makes "pick a session before a session exists" possible
//!    without a filesystem read;
//! 3. **`initialize { sessionId }`** — resuming is part of the handshake
//!    because the engine seeds its history while initialising;
//! 4. **`session/fork`** — only when forking was asked for.
//!
//! No step reads a transcript, constructs a `.coda/sessions` path, or links
//! the agent runtime. `tests/conventions.rs` enforces that, including the
//! laundering variant — moving the reads into `coda-boot` and calling *that*
//! would satisfy a naive "no `coda_agent` in `coda-tui`" grep while changing
//! nothing.

use coda_boot::SessionIntent;
use coda_client::{ClientError, Connection, Engine, EngineCommand, Inbound};
use coda_proto::history::SessionSummaryDto;
use coda_proto::messages::{self, method, InitializeResult};
use tokio::sync::mpsc;

use super::view::client_capabilities;

/// Why a launch could not open the session it was asked for.
#[derive(Debug)]
pub enum BootError {
    /// The core would not start.
    Spawn(ClientError),
    /// An explicit id was given and the engine has no such session.
    NotFound(String),
    /// `--resume`/`--fork` was asked for and this workspace has no sessions.
    ///
    /// Deliberately *not* raised for `--continue`: "carry on from where I
    /// was" in a fresh directory means starting, not being told off.
    NoSessions,
    /// The handshake or a bootstrap RPC failed.
    Rpc(ClientError),
}

impl std::fmt::Display for BootError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            BootError::Spawn(error) => write!(f, "failed to start the engine: {error}"),
            // Sanitised: the id came from the command line and is about to be
            // printed to a terminal that interprets escape sequences.
            BootError::NotFound(id) => write!(
                f,
                "No session '{}' in this directory.",
                coda_render::text::sanitize(id)
            ),
            BootError::NoSessions => write!(f, "No sessions in this directory yet."),
            BootError::Rpc(error) => write!(f, "the engine rejected the handshake: {error}"),
        }
    }
}

impl std::error::Error for BootError {}

/// A started, initialised engine and everything the caller needs to drive it.
pub struct Booted {
    pub engine: Engine,
    pub inbound: mpsc::UnboundedReceiver<Inbound>,
    pub connection: Connection,
    pub initialize: InitializeResult,
    /// The session the engine is actually running — after a fork, the *new*
    /// one.
    pub session_id: String,
    /// The session a fork was taken from, when one was.
    pub forked_from: Option<String>,
    /// Things worth telling the user about how the launch resolved.
    pub notices: Vec<String>,
}

/// Runs the bootstrap for `intent`.
///
/// `client_info` is the front-end's own name (`coda-tui`, `coda-run`), which
/// the engine records; it is not used to grant anything.
pub async fn boot(
    command: EngineCommand,
    intent: &SessionIntent,
    client_info: &'static str,
) -> Result<Booted, BootError> {
    let (engine, inbound) = Engine::spawn(command).map_err(BootError::Spawn)?;
    let connection = engine.connection();

    match resolve_and_initialize(&connection, intent, client_info).await {
        Ok(opened) => Ok(Booted {
            engine,
            inbound,
            connection,
            initialize: opened.initialize,
            session_id: opened.session_id,
            forked_from: opened.forked_from,
            notices: opened.notices,
        }),
        // A child left running behind a failed launch is a stray process
        // holding the workspace open, and on Windows it also holds file
        // handles.
        Err(error) => {
            let _ = engine.shutdown(std::time::Duration::from_secs(5)).await;
            Err(error)
        }
    }
}

/// What the bootstrap RPCs resolved to.
#[derive(Debug)]
pub struct Opened {
    pub initialize: InitializeResult,
    pub session_id: String,
    pub forked_from: Option<String>,
    pub notices: Vec<String>,
}

/// Steps 2-4 of the bootstrap, over an already-connected engine.
///
/// Split out so the *actual* sequence the runtime runs can be driven against
/// a recording server in a test — proving which methods a launch really calls,
/// and therefore that it reads no engine-private files. A test against a
/// re-implementation of these steps would prove only that the test agrees
/// with itself.
pub async fn resolve_and_initialize(
    connection: &Connection,
    intent: &SessionIntent,
    client_info: &str,
) -> Result<Opened, BootError> {
    let mut notices = Vec::new();

    // Step 2: read-only discovery, only when the intent actually needs it.
    // An explicit `--resume <id>` does not: the engine validates the id
    // during `initialize` and answers with a typed "no such session", so
    // listing first would be a second round-trip that adds nothing.
    let resolved = match intent {
        SessionIntent::New => None,
        SessionIntent::Resume(id) => Some(id.clone()),
        SessionIntent::Latest => match latest(connection).await.map_err(BootError::Rpc)? {
            Some(id) => Some(id),
            None => {
                // "Carry on from where I was" in a directory with no history
                // means starting, not being told off. `--resume`/`--fork`,
                // which name a thing that should exist, still fail loudly.
                notices
                    .push("No previous session in this directory; starting a new one.".to_string());
                None
            }
        },
        SessionIntent::Fork(source) => {
            let sessions = super::list_sessions(connection, None)
                .await
                .map_err(BootError::Rpc)?
                .sessions;
            match source {
                Some(id) if sessions.iter().any(|s| &s.session_id == id) => Some(id.clone()),
                Some(id) => return Err(BootError::NotFound(id.clone())),
                None => match newest(&sessions) {
                    Some(id) => Some(id),
                    None => return Err(BootError::NoSessions),
                },
            }
        }
    };

    // Step 3: the handshake, resuming as part of it.
    let mut params = messages::InitializeParams::new(client_info)
        .with_client_capabilities(client_capabilities());
    params.session_id = resolved.clone();
    let params = serde_json::to_value(params).unwrap_or_default();
    let initialize: InitializeResult =
        match connection.request(method::INITIALIZE, Some(params)).await {
            Ok(value) => serde_json::from_value(value)
                .map_err(|error| BootError::Rpc(ClientError::Serde(error)))?,
            Err(error) => {
                return Err(match (&error, &resolved) {
                    (ClientError::Rpc(rpc), Some(id))
                        if rpc.code == messages::error_code::SESSION_NOT_FOUND =>
                    {
                        BootError::NotFound(id.clone())
                    }
                    _ => BootError::Rpc(error),
                })
            }
        };

    let mut session_id = initialize.session_id.clone();
    let mut forked_from = None;

    // Step 4: fork, through the engine's own RPC. The original stays frozen
    // and the engine — not this client — writes the copy.
    if matches!(intent, SessionIntent::Fork(_)) {
        let value = connection
            .request(method::FORK, Some(serde_json::json!({})))
            .await
            .map_err(BootError::Rpc)?;
        match value.get("newSessionId").and_then(|v| v.as_str()) {
            Some(new_id) => {
                forked_from = Some(session_id.clone());
                session_id = new_id.to_string();
            }
            None => notices.push("Forked, but the engine did not report the new id.".to_string()),
        }
    }

    Ok(Opened { initialize, session_id, forked_from, notices })
}

/// The most recent saved session, or `None` when there are none.
async fn latest(connection: &Connection) -> Result<Option<String>, ClientError> {
    let result = super::list_sessions(connection, None).await?;
    Ok(newest(&result.sessions))
}

/// Picks the newest summary explicitly rather than trusting document order.
///
/// The engine does sort newest-first today, but "most recent" is the property
/// that matters and the DTO carries the timestamp to decide it. `createdUtc`
/// is RFC 3339 in UTC, so a lexicographic maximum is the same ordering — the
/// comparison is on the value the engine published, not on a re-derived one.
fn newest(sessions: &[SessionSummaryDto]) -> Option<String> {
    sessions
        .iter()
        .max_by(|a, b| a.created_utc.cmp(&b.created_utc))
        .map(|s| s.session_id.clone())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn summary(id: &str, created: &str) -> SessionSummaryDto {
        SessionSummaryDto {
            session_id: id.into(),
            created_utc: created.into(),
            message_count: 2,
            preview: "hello".into(),
            preview_truncated: false,
            is_current: false,
        }
    }

    #[test]
    fn the_newest_session_is_chosen_by_its_timestamp_not_by_list_order() {
        let sessions = vec![
            summary("older", "2026-09-01T10:00:00+00:00"),
            summary("newest", "2026-09-08T09:00:00+00:00"),
            summary("middle", "2026-09-04T11:00:00+00:00"),
        ];
        assert_eq!(newest(&sessions).as_deref(), Some("newest"));
    }

    #[test]
    fn an_empty_workspace_has_no_newest_session() {
        assert_eq!(newest(&[]), None);
    }

    #[test]
    fn boot_errors_sanitise_an_id_that_came_from_the_command_line() {
        let error = BootError::NotFound("a\u{1b}[31mb".into());
        let text = error.to_string();
        assert!(!text.contains('\u{1b}'), "an escape sequence reached the terminal: {text:?}");
    }
}
