//! Local maintenance: the things Coda edits on *this* machine, and the gate.
//!
//! Some of what this front-end does has no engine API and deliberately will
//! not get one: installing a plugin, adding a marketplace, editing
//! `.mcp.json`. Coda must never write a remote client's filesystem on its
//! behalf, so those operations are the *client's* own, performed knowingly by
//! the person sitting at the terminal.
//!
//! # The gate is the client's launch mode, not a server claim
//!
//! [`AccessMode`] is chosen by this process from how it was started. It is
//! emphatically **not** read from anything the engine says. An engine cannot
//! know whether its client is a local terminal or a browser on another
//! continent, so an engine-advertised `isLocal` flag would be a claim it is
//! not entitled to make — and a compromised or merely misconfigured engine
//! could then talk a client into writing files it should not touch. A server
//! claim can only ever *reduce* what this client offers, never enable a write.
//!
//! # What is still local, and unaffected
//!
//! Appearance, keybindings, clipboard access and image files the user picks
//! are the client's own concerns in every mode. They are not the engine's
//! private files and are not gated here.
//!
//! # What is gated
//!
//! Editing the MCP server file, installing/enabling plugins and managing
//! marketplaces. In [`AccessMode::ApiOnly`] those surfaces render read-only
//! with an explicit reason, and every write path answers
//! [`unsupported_remotely`] *before* touching the filesystem — never a silent
//! fallback to editing local files that the remote engine will never read.

pub mod auth;
pub(crate) mod login;
pub mod mcp;

/// How this client reaches its engine, and therefore whether the files the
/// engine reads are files this client owns.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AccessMode {
    /// This process started the core itself, on this machine, in this
    /// workspace. The MCP/plugin/marketplace files the engine reads are the
    /// ones this client would edit, so local maintenance is meaningful.
    TrustedLocal,
    /// The engine is somebody else's process: an explicitly supplied
    /// `--engine`/`CODA_ENGINE` binary or a proxy. Its files are not
    /// necessarily these files, so local maintenance is refused rather than
    /// silently applied to the wrong machine.
    ApiOnly,
}

impl AccessMode {
    /// The mode for a launch, from the client's own knowledge of it.
    ///
    /// `custom_engine` is true when the user pointed this front-end at an
    /// engine binary or proxy of their own. The default — `coda` launching
    /// `<current_exe> serve` — is the local case, which is what keeps every
    /// shipping editor working exactly as before.
    ///
    /// Conservative on purpose: a custom engine *might* still be local, but
    /// this client cannot tell, and the failure mode of guessing "local" is
    /// editing files that have no effect on the engine actually running.
    pub fn for_launch(custom_engine: bool) -> Self {
        if custom_engine {
            AccessMode::ApiOnly
        } else {
            AccessMode::TrustedLocal
        }
    }

    /// Whether this client may maintain the engine-adjacent files itself.
    pub fn allows_local_maintenance(self) -> bool {
        matches!(self, AccessMode::TrustedLocal)
    }
}

/// The message shown — and returned *before* any filesystem write — when a
/// maintenance operation is attempted against an engine this client does not
/// own.
pub fn unsupported_remotely(what: &str) -> String {
    format!(
        "{what} is managed on the engine host. This session is connected to an engine \
         this client did not start, so editing local files here would change nothing \
         the engine reads."
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_default_launch_keeps_local_maintenance_working() {
        // The shipping behaviour: `coda` starts its own core, so the MCP,
        // plugin and marketplace editors keep working exactly as before.
        assert_eq!(AccessMode::for_launch(false), AccessMode::TrustedLocal);
        assert!(AccessMode::for_launch(false).allows_local_maintenance());
    }

    #[test]
    fn a_custom_engine_is_treated_as_someone_elses_machine() {
        assert_eq!(AccessMode::for_launch(true), AccessMode::ApiOnly);
        assert!(!AccessMode::for_launch(true).allows_local_maintenance());
    }

    #[test]
    fn the_refusal_names_what_was_refused_and_why() {
        let message = unsupported_remotely("MCP server configuration");
        assert!(message.starts_with("MCP server configuration"));
        assert!(message.contains("engine host"));
    }
}
