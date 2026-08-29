use coda_proto::{FramingError, ResponseError};

#[derive(Debug, thiserror::Error)]
pub enum ClientError {
    #[error("the engine connection is closed")]
    ConnectionClosed,

    #[error(transparent)]
    Rpc(#[from] ResponseError),

    #[error("protocol framing error: {0}")]
    Framing(#[from] FramingError),

    #[error("i/o error: {0}")]
    Io(#[from] std::io::Error),

    #[error("serialisation error: {0}")]
    Serde(#[from] serde_json::Error),

    #[error("failed to launch the engine ({program}): {source}")]
    Spawn {
        program: String,
        #[source]
        source: std::io::Error,
    },

    #[error("the engine did not expose {0}")]
    MissingStdio(&'static str),

    #[error("unexpected response shape for {method}: {detail}")]
    UnexpectedResponse { method: String, detail: String },
}
