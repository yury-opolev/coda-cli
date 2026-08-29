//! Wire protocol for the Coda `serve` engine.
//!
//! This crate is transport-agnostic and I/O free: it defines the framing codec,
//! the JSON-RPC 2.0 envelopes, and the Coda-specific method and event payloads.
//! Driving an actual engine process lives in `coda-client`.

pub mod events;
pub mod framing;
pub mod jsonrpc;
pub mod messages;

pub use events::{Event, ToolCallStatus};
pub use framing::{encode_frame, FrameDecoder, FramingError};
pub use jsonrpc::{
    error_codes, Message, Notification, Request, RequestId, Response, ResponseError, Version,
};
pub use messages::{Correlation, PROTOCOL_VERSION};
