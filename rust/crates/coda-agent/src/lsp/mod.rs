//! LSP client, server manager, diagnostic registry, and the `lsp_diagnostics` tool.
//!
//! ## Phase 4 — LSP client
//!
//! - **`config`**: `LspServerConfig` (parsed from `settings.json`).
//! - **`client`**: `LspClient` — Content-Length-framed JSON-RPC connection to
//!   an LSP server, reusing `coda-client`'s transport. Handles
//!   `initialize`/`initialized`, notification dispatch, server-initiated
//!   request dispatch, and bounded `shutdown`/`exit`.
//! - **`diagnostic`**: `LspDiagnostic`, `LspDiagnosticRegistry` — thread-safe
//!   collection and deduplication of `publishDiagnostics` notifications with
//!   per-turn volume limits.
//! - **`manager`**: `LspServerManager` — routes by extension, manages
//!   lifecycle, tracks open files for `didOpen`/`didChange`/`didClose`.
//! - **`tool`**: `LspDiagnosticsTool` — the `lsp_diagnostics` tool wired into
//!   the agent's tool registry.

pub mod client;
pub mod config;
pub mod diagnostic;
pub mod manager;
pub mod map_builder;
pub mod plugin_loader;
pub mod tool;

pub use client::{LspClient, LspError};
pub use config::LspServerConfig;
pub use diagnostic::{
    DiagnosticFile, LspDiagnostic, LspDiagnosticRegistry, LspDiagnosticSeverity, LspPosition,
    LspRange,
};
pub use manager::{LspServerManager, LspServerSnapshot, LspServerState};
pub use map_builder::LspServerMapBuilder;
pub use plugin_loader::PluginLspServerLoader;
pub use tool::LspDiagnosticsTool;
