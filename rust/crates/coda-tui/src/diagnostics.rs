//! Re-exports of `coda-boot`'s diagnostics wrapper.
//!
//! This module moved to `coda-boot` (a core-only crate, shared with the
//! standalone `coda-engine` binary) because none of it — process-role/verbosity
//! resolution, opening the log file, forwarding correlation env vars to a
//! spawned engine — needs a TUI, a renderer, or a terminal. Re-exported here
//! under the same path so every existing `coda_tui::diagnostics::...` call
//! site (this crate's own `coda-tui` binary, `coda`'s `main.rs`, and the
//! integration tests) keeps compiling unchanged.
pub use coda_boot::diagnostics::{init, forward_env, record_engine_log_path, status_report};
