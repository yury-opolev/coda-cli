//! Headless bootstrap shared by `coda serve` and the standalone `coda-engine`
//! binary.
//!
//! `coda-boot` exists so the *only* things two independent frontends
//! (the unified `coda` binary's `serve` subcommand, and the TUI-free
//! `coda-engine` binary) need in common — diagnostics initialization,
//! product version, `ServeArgs` parsing, startup env-var translation, and
//! pure session-intent resolution and host-local settings/browser helpers —
//! live in one place, in a crate
//! that cannot pull in a terminal renderer, clipboard, or the agent runtime
//! by accident. `crates/coda-engine/tests/independence.rs` guards that with
//! a `cargo tree` check; this crate's own dependency list
//! is the other half of that guarantee.
//!
//! What is deliberately *not* here, and why:
//! - Disk-backed session lookup belongs to the engine. Frontends discover
//!   and hydrate sessions through the public serve API, never through a
//!   private transcript reader in this crate.
//! - Anything requiring a `Tui` renderer (banners, exit summaries, the
//!   `status_report` text a slash command prints) stays in `coda-tui`.
//! - `coda_serve::serve_stdio()` itself is not called from here: this crate
//!   does not depend on `coda-serve`, so a binary calls
//!   [`serve::prepare`] and then `coda_serve::serve_stdio().await` itself.

pub mod diagnostics;
pub mod browser;
pub mod console;
pub mod secret_input;
pub mod auth_args;
pub mod auth_cli;
pub mod serve;
pub mod session_intent;
pub mod settings_store;

mod prompt;
mod version;

pub use prompt::resolve_system_prompt;
pub use serve::{parse_diagnostic_verbosity, parse_effort_level, ServeArgs};
pub use session_intent::SessionIntent;
pub use settings_store::{SettingsError, SettingsFile};
pub use version::version;
