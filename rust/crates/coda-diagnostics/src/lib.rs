//! Bounded, privacy-safe operational diagnostics for the Coda binaries.
//!
//! This crate is deliberately a leaf: no dependency on `coda-auth` or
//! `coda-agent`. Callers resolve `CODA_HOME`/the default directory
//! themselves (in the executable, not here) and pass it in via [`Options`].
//!
//! Three pieces:
//! - [`event::Event`]: the fixed, allowlisted set of records — no free-text
//!   `message` field anywhere, so nothing unsafe can be logged by accident.
//! - [`writer::Logger`]: one bounded, rotating JSONL file per process.
//! - [`context`]: a [`tokio::task_local!`]-backed [`context::DiagnosticContext`]
//!   that follows one request/turn through async work without a
//!   process-global "current session".

pub mod context;
pub mod detail;
pub mod event;
pub mod writer;

pub use context::{
    current, env_dir, env_run_id, env_verbosity, record, resolve_run_id, scope, DiagnosticContext,
    ProcessRole, Verbosity, DIR_ENV, RUN_ID_ENV, VERBOSITY_ENV,
};
pub use event::Event;
pub use writer::{Health, Limits, Logger, Options, Status};
