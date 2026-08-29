//! Client for driving a Coda engine over the `serve` JSON-RPC protocol.
//!
//! The TUI does not link the engine; it spawns `coda serve` and talks to it.
//! That keeps the front-end decoupled from the engine implementation, which is
//! what lets the Rust UI ship ahead of a full engine port.

pub mod error;
pub mod process;
pub mod transport;

pub use error::ClientError;
pub use process::{Engine, EngineCommand};
pub use transport::{connect, Connection, ConnectionTasks, Inbound, Responder};
