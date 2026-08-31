//! Session persistence subsystem.
//!
//! Ports the C# session-persistence layer (transcript store, audit store,
//! session bundles, fork and rewind) to Rust, keeping the same on-disk
//! format so the two engines remain interoperable.
//!
//! ## Module overview
//! - **`ids`** — session-id validation (`is_valid`) and minting (`new_id`).
//! - **`store`** — `SessionTranscriptStore`: save/load/list the conversation
//!   transcript at `<working_dir>/.coda/sessions/<id>.json`.
//! - **`audit`** — `SessionAuditStore`: append-only per-turn audit trail at
//!   `<working_dir>/.coda/sessions/<id>.audit.jsonl`.
//! - **`bundle`** — `SessionBundleService`: export to / import from a portable
//!   `*.coda-session.json` bundle.
//! - **`forking`** — `fork` (clone to a new id) and `rewind` (remove exchanges).
//! - **`message_json`** — JSON (de)serialization of `coda_llm::Message`.

pub mod audit;
pub mod bundle;
pub mod forking;
pub mod ids;
pub mod message_json;
pub mod store;

// Convenience re-exports.
pub use audit::{AuditToolCall, AuditTurn, SessionAuditStore};
pub use bundle::{BundleTurn, ImportError, SessionBundle, SessionBundleService};
pub use forking::{fork, rewind};
pub use ids::{is_valid as session_id_is_valid, new_id as new_session_id};
pub use store::{SessionSummary, SessionTranscriptStore, StoredSession};
