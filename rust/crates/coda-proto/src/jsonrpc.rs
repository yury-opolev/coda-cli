//! JSON-RPC 2.0 envelope types.
//!
//! The Coda `serve` connection is bidirectional: either peer may send requests
//! and notifications. These types therefore model an *incoming* message as an
//! untagged union rather than assuming a client or server role.

use serde::{Deserialize, Serialize};
use serde_json::Value;

/// JSON-RPC request/response correlation id.
///
/// The spec permits strings and numbers; we preserve whichever the peer used so
/// responses echo the exact id we were given.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(untagged)]
pub enum RequestId {
    Number(i64),
    String(String),
}

impl std::fmt::Display for RequestId {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            RequestId::Number(n) => write!(f, "{n}"),
            RequestId::String(s) => write!(f, "{s}"),
        }
    }
}

impl From<i64> for RequestId {
    fn from(value: i64) -> Self {
        RequestId::Number(value)
    }
}

impl From<String> for RequestId {
    fn from(value: String) -> Self {
        RequestId::String(value)
    }
}

/// An outgoing or incoming method call that expects a response.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Request {
    pub jsonrpc: Version,
    pub id: RequestId,
    pub method: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub params: Option<Value>,
}

/// A method call that expects no response.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Notification {
    pub jsonrpc: Version,
    pub method: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub params: Option<Value>,
}

/// A reply to a [`Request`]. Exactly one of `result`/`error` is populated.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Response {
    pub jsonrpc: Version,
    pub id: RequestId,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub result: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<ResponseError>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ResponseError {
    /// Per JSON-RPC 2.0 spec `code` is required, but some servers omit it.
    /// Default to -1 (unknown) so a malformed error still faults the caller
    /// rather than being silently dropped as an unparsable frame.
    #[serde(default = "default_error_code")]
    pub code: i64,
    /// Per spec `message` is required; default to empty string for the same
    /// reason as `code`.
    #[serde(default)]
    pub message: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub data: Option<Value>,
}

fn default_error_code() -> i64 {
    -1
}

impl std::fmt::Display for ResponseError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "JSON-RPC error {}: {}", self.code, self.message)
    }
}

impl std::error::Error for ResponseError {}

/// Standard JSON-RPC 2.0 error codes.
pub mod error_codes {
    pub const PARSE_ERROR: i64 = -32700;
    pub const INVALID_REQUEST: i64 = -32600;
    pub const METHOD_NOT_FOUND: i64 = -32601;
    pub const INVALID_PARAMS: i64 = -32602;
    pub const INTERNAL_ERROR: i64 = -32603;
    /// Reserved range for implementation-defined server errors.
    pub const SERVER_ERROR_START: i64 = -32099;
    pub const SERVER_ERROR_END: i64 = -32000;
    /// Sent when the client declines a server-initiated request.
    pub const REQUEST_CANCELLED: i64 = -32800;
}

/// The literal `"2.0"` version tag, enforced at (de)serialisation time.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Version;

impl Serialize for Version {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str("2.0")
    }
}

impl<'de> Deserialize<'de> for Version {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let raw = String::deserialize(deserializer)?;
        if raw == "2.0" {
            Ok(Version)
        } else {
            Err(serde::de::Error::invalid_value(
                serde::de::Unexpected::Str(&raw),
                &"\"2.0\"",
            ))
        }
    }
}

/// Any message that can arrive on the wire.
///
/// Variant order matters: `serde(untagged)` tries them in sequence, and a
/// response is distinguished from a request only by the absence of `method`.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(untagged)]
pub enum Message {
    Request(Request),
    Response(Response),
    Notification(Notification),
}

impl Message {
    pub fn method(&self) -> Option<&str> {
        match self {
            Message::Request(r) => Some(&r.method),
            Message::Notification(n) => Some(&n.method),
            Message::Response(_) => None,
        }
    }

    pub fn id(&self) -> Option<&RequestId> {
        match self {
            Message::Request(r) => Some(&r.id),
            Message::Response(r) => Some(&r.id),
            Message::Notification(_) => None,
        }
    }
}

impl Request {
    pub fn new(id: impl Into<RequestId>, method: impl Into<String>, params: Option<Value>) -> Self {
        Self {
            jsonrpc: Version,
            id: id.into(),
            method: method.into(),
            params,
        }
    }
}

impl Notification {
    pub fn new(method: impl Into<String>, params: Option<Value>) -> Self {
        Self {
            jsonrpc: Version,
            method: method.into(),
            params,
        }
    }
}

impl Response {
    pub fn success(id: RequestId, result: Value) -> Self {
        Self {
            jsonrpc: Version,
            id,
            result: Some(result),
            error: None,
        }
    }

    pub fn failure(id: RequestId, code: i64, message: impl Into<String>) -> Self {
        Self {
            jsonrpc: Version,
            id,
            result: None,
            error: Some(ResponseError {
                code,
                message: message.into(),
                data: None,
            }),
        }
    }

    /// Splits the response into the `Result` the caller actually wants.
    pub fn into_result(self) -> Result<Value, ResponseError> {
        match self.error {
            Some(error) => Err(error),
            // A successful response with no `result` member is treated as null,
            // which is what the C# host emits for void methods.
            None => Ok(self.result.unwrap_or(Value::Null)),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn parse(value: serde_json::Value) -> Message {
        serde_json::from_value(value).expect("parse message")
    }

    #[test]
    fn serialises_a_request_with_the_version_tag() {
        let request = Request::new(1, "session/prompt", Some(json!({ "text": "hi" })));
        assert_eq!(
            serde_json::to_value(&request).unwrap(),
            json!({
                "jsonrpc": "2.0",
                "id": 1,
                "method": "session/prompt",
                "params": { "text": "hi" }
            })
        );
    }

    #[test]
    fn omits_absent_params() {
        let notification = Notification::new("event/turnComplete", None);
        assert_eq!(
            serde_json::to_value(&notification).unwrap(),
            json!({ "jsonrpc": "2.0", "method": "event/turnComplete" })
        );
    }

    #[test]
    fn classifies_a_request() {
        let message = parse(json!({ "jsonrpc": "2.0", "id": 7, "method": "request/permission" }));
        assert!(matches!(message, Message::Request(_)));
        assert_eq!(message.method(), Some("request/permission"));
        assert_eq!(message.id(), Some(&RequestId::Number(7)));
    }

    #[test]
    fn classifies_a_notification_by_its_missing_id() {
        let message = parse(json!({ "jsonrpc": "2.0", "method": "event/assistantText" }));
        assert!(matches!(message, Message::Notification(_)));
        assert_eq!(message.id(), None);
    }

    #[test]
    fn classifies_a_response_by_its_missing_method() {
        let message = parse(json!({ "jsonrpc": "2.0", "id": 3, "result": { "ok": true } }));
        assert!(matches!(message, Message::Response(_)));
        assert_eq!(message.method(), None);
    }

    #[test]
    fn classifies_an_error_response() {
        let message = parse(json!({
            "jsonrpc": "2.0",
            "id": 3,
            "error": { "code": -32601, "message": "Method not found" }
        }));
        let Message::Response(response) = message else {
            panic!("expected a response");
        };
        let error = response.into_result().expect_err("expected an error");
        assert_eq!(error.code, error_codes::METHOD_NOT_FOUND);
    }

    #[test]
    fn preserves_string_ids() {
        let message = parse(json!({ "jsonrpc": "2.0", "id": "abc", "method": "x" }));
        assert_eq!(message.id(), Some(&RequestId::String("abc".into())));
    }

    #[test]
    fn treats_a_missing_result_as_null() {
        let response = Response {
            jsonrpc: Version,
            id: RequestId::Number(1),
            result: None,
            error: None,
        };
        assert_eq!(response.into_result().unwrap(), Value::Null);
    }

    #[test]
    fn rejects_a_wrong_version_tag() {
        let err = serde_json::from_value::<Request>(json!({
            "jsonrpc": "1.0", "id": 1, "method": "x"
        }));
        assert!(err.is_err());
    }

    /// A non-conformant server that sends `"error": {}` (missing `code` and
    /// `message`) must still fault the caller rather than being dropped as an
    /// unparsable frame.  Callers get code=-1 and an empty message rather than
    /// waiting forever for a response that will never arrive.
    #[test]
    fn error_response_with_missing_code_and_message_uses_defaults() {
        let message = parse(json!({
            "jsonrpc": "2.0",
            "id": 42,
            "error": {}
        }));
        let Message::Response(response) = message else {
            panic!("expected a response");
        };
        let error = response.into_result().expect_err("expected an error");
        assert_eq!(error.code, -1, "missing code should default to -1");
        assert_eq!(error.message, "", "missing message should default to empty string");
    }

    /// Same as above but only `code` is present — `message` defaults to empty.
    #[test]
    fn error_response_with_missing_message_uses_empty_string() {
        let message = parse(json!({
            "jsonrpc": "2.0",
            "id": 7,
            "error": { "code": -32601 }
        }));
        let Message::Response(response) = message else {
            panic!("expected a response");
        };
        let error = response.into_result().expect_err("expected an error");
        assert_eq!(error.code, error_codes::METHOD_NOT_FOUND);
        assert_eq!(error.message, "");
    }
}


