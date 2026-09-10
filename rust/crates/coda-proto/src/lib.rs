//! Wire protocol for the Coda `serve` engine.
//!
//! This crate is transport-agnostic and I/O free: it defines the framing codec,
//! the JSON-RPC 2.0 envelopes, and the Coda-specific method and event payloads.
//! Driving an actual engine process lives in `coda-client`.

pub mod config;
pub mod events;
pub mod framing;
pub mod history;
pub mod jsonrpc;
pub mod mcp;
pub mod messages;
pub mod requests;
pub mod responses;
#[cfg(feature = "schema")]
pub mod schema;
pub mod state;
pub mod state_events;

pub use events::{Event, ToolCallStatus};
pub use framing::{encode_frame, FrameDecoder, FramingError};
pub use jsonrpc::{
    error_codes, Message, Notification, Request, RequestId, Response, ResponseError, Version,
};
pub use messages::{Correlation, PROTOCOL_VERSION};
