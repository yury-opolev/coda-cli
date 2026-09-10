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
//! - `history`    — UI-safe projection of `coda_llm::Message` (Stage D)
//! - `config_api` — `config/describe` / `config/set` (Stage D)
//! - `mcp_list`   — read-only, secret-free `mcp/list` (Stage D)
//! - `turn_scope` — which API turn a unit of execution belongs to
//! - `transport`  — stdio read/write loop
//! - `session`    — per-session state

pub mod catalog;
pub mod bus;
pub mod capabilities;
pub mod config_api;
pub mod dispatch;
pub mod history;
pub mod host;
mod mcp;
pub mod mcp_list;
pub mod prompts;
pub mod session;
pub mod settings;
pub mod sink;
pub mod skills;
pub mod state;
pub mod state_sink;
pub mod transport;
pub mod turn_scope;

pub use dispatch::{dispatch, RpcError, ServeBackend};
pub use transport::serve_stdio;
