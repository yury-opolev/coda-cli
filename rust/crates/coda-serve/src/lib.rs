//! `coda-serve` — the Rust engine host.
//!
//! Exposes a `serve_stdio` entry point that speaks JSON-RPC 2.0 over
//! `Content-Length`-framed stdio, wire-compatible with the C# `coda serve`
//! engine.
//!
//! Module layout
//! - `dispatch`   — pure: routes `(method, params) -> Result<Value, RpcError>`
//! - `host`       — `ServeHost` implements `ServeBackend`
//! - `sink`       — `ServeSink` bridges `AgentSink` to outbound notifications
//! - `prompts`    — server-initiated `request/*` round-trips (fail-closed)
//! - `transport`  — stdio read/write loop
//! - `session`    — per-session state

pub mod catalog;
pub mod dispatch;
pub mod host;
mod mcp;
pub mod prompts;
pub mod session;
pub mod settings;
pub mod sink;
pub mod skills;
pub mod transport;

pub use dispatch::{dispatch, RpcError, ServeBackend};
pub use transport::serve_stdio;
