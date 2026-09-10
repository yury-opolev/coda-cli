//! Who may write the settings, and which settings those are.
//!
//! Two different files' worth of meaning live in one `settings.json`:
//!
//! - **Engine-owned values** — the default provider, the model per provider,
//!   the per-model reasoning effort, the permission mode, custom HTTP headers,
//!   telemetry. The *engine* reads these, at its own startup, on its own host.
//! - **Client-local values** — this front-end's own presentation, which no
//!   engine ever reads.
//!
//! When this client started the engine itself the distinction is invisible:
//! one machine, one file. When it did not — an explicit `--engine`, a proxy,
//! an engine on another host — writing an engine-owned value here changes a
//! file the engine will never read, while reporting success. That is the
//! failure this module exists to prevent, and it prevents it **before** the
//! filesystem is touched rather than by tidying up afterwards.
//!
//! The session itself is a different matter: `session/setModel`,
//! `session/setPermissionMode` and `model/setEffort` are ordinary RPCs and
//! work perfectly well against a remote engine. They are never gated. What is
//! gated is the claim that the change will still be there next time.

use super::App;
use crate::config::{ConfigError, Paths, Settings};
use crate::transcript::NoticeLevel;

/// What a settings write actually did.
#[derive(Debug, PartialEq, Eq)]
pub(super) enum Saved {
    /// Written to the file the reader of this setting really reads.
    Ok,
    /// Not attempted: this client does not own the engine's settings.
    Refused,
    /// Attempted, and failed.
    Failed(String),
}

/// Loads, edits and writes the settings file. Blocking, hence the caller's
/// `spawn_blocking`.
fn write(paths: &Paths, edit: impl FnOnce(&mut Settings)) -> Result<(), ConfigError> {
    let mut settings = Settings::load(paths)?;
    edit(&mut settings);
    settings.save()
}

impl App {
    /// Whether the engine-owned settings on *this* machine are the ones the
    /// connected engine reads.
    ///
    /// Answered from how this client launched, never from anything the engine
    /// says: an engine cannot know whether its client is this terminal or a
    /// browser elsewhere, so a server-advertised answer would be a claim it is
    /// not entitled to make.
    pub(super) fn owns_engine_settings(&self) -> bool {
        self.access_mode.allows_local_maintenance()
    }

    /// Refuses an operation whose *whole point* is to change what the engine
    /// reads at startup — `/provider`, `/headers`, telemetry.
    ///
    /// Returns `false` and says why, before any filesystem access.
    pub(super) fn allow_engine_settings(&mut self, what: &str) -> bool {
        if self.owns_engine_settings() {
            return true;
        }
        self.notice(crate::local::unsupported_remotely(what), NoticeLevel::Warning);
        false
    }

    /// Persists a value the engine reads at startup, when this client owns
    /// the file it reads.
    ///
    /// Used by the paths whose *session* change already succeeded over the
    /// API — the model, the effort, the permission mode. The session change
    /// stands either way; this only decides whether the next start inherits
    /// it, and the caller reports exactly what happened rather than claiming
    /// a save it never made.
    pub(super) async fn persist_engine_default(
        &mut self,
        edit: impl FnOnce(&mut Settings) + Send + 'static,
    ) -> Saved {
        if !self.owns_engine_settings() {
            return Saved::Refused;
        }
        self.write_settings(edit).await
    }

    /// Persists a value only this client reads.
    ///
    /// Not gated: appearance and other client-local settings are this
    /// process's own concern in every mode, and refusing them would break
    /// working functionality for no safety gain.
    pub(super) async fn persist_client_local(
        &mut self,
        edit: impl FnOnce(&mut Settings) + Send + 'static,
    ) -> Saved {
        self.write_settings(edit).await
    }

    async fn write_settings(
        &mut self,
        edit: impl FnOnce(&mut Settings) + Send + 'static,
    ) -> Saved {
        let paths = self.paths.clone();
        match tokio::task::spawn_blocking(move || write(&paths, edit)).await {
            Ok(Ok(())) => Saved::Ok,
            Ok(Err(error)) => Saved::Failed(error.to_string()),
            Err(error) => Saved::Failed(error.to_string()),
        }
    }

    /// The sentence a session-scoped change appends when the same change
    /// could not be made durable.
    pub(super) fn not_saved_remotely(&self) -> &'static str {
        " It is not saved for the next start: this engine's defaults live on \
         its own host."
    }
}
