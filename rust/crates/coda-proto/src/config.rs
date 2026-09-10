//! `config/describe` and `config/set` DTOs.
//!
//! Two rules bind every field here.
//!
//! 1. **Secret deny-list.** A config entry never carries a provider API key,
//!    an OAuth or keyring token, a `coda-secret:` target, an MCP `env` value
//!    or a custom header value. Where such a setting exists, the entry
//!    reports its *name* and whether it resolved — never the value.
//! 2. **`appliesAt` is derived from what the engine really does**, not from
//!    what would be convenient. There is no pending-change scheduler: the
//!    engine reads these values at turn-build time / at the next permission
//!    check, so the scope stated here is the scope the code implements.

use serde::{Deserialize, Serialize};

/// When a change to a config key actually takes hold.
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum AppliesWhen {
    /// Observable on the very next read, with no turn boundary involved.
    Immediately,
    /// The running turn keeps its captured mode; the next permission
    /// decision reads the new one.
    NextPermissionCheck,
    /// The running turn keeps its captured value; the next turn is built
    /// with the new one.
    NextTurn,
    /// Only a new engine process picks this up.
    NewEngineInstance,
    /// The engine does not own this at all — it is the client's own setting.
    ClientLocal,
}

impl AppliesWhen {
    pub fn as_str(self) -> &'static str {
        match self {
            AppliesWhen::Immediately => "immediately",
            AppliesWhen::NextPermissionCheck => "nextPermissionCheck",
            AppliesWhen::NextTurn => "nextTurn",
            AppliesWhen::NewEngineInstance => "newEngineInstance",
            AppliesWhen::ClientLocal => "clientLocal",
        }
    }
}

/// Who owns a config key.
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum ConfigOwner {
    /// The running session in this engine process. Mutable through
    /// `config/set`, which delegates to the existing validated RPC.
    Session,
    /// Fixed for the life of this engine process (chosen at startup).
    EngineStartup,
    /// The client's own setting; the engine never applies it and never
    /// writes it. Reported so a client can enumerate it without linking
    /// engine crates, but `config/set` refuses it.
    ClientLocal,
    /// Owned by a file on the machine the client is running on. Coda never
    /// writes a remote client's filesystem, so this is read-only here.
    LocalFile,
}

/// One described configuration key.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ConfigEntryDto {
    pub key: String,
    pub owner: ConfigOwner,
    pub applies_at: AppliesWhen,
    /// `true` only when `config/set` for this key is genuinely wired to a
    /// validated engine method. Never `true` "in principle".
    pub mutable: bool,
    /// Why it is not mutable. Present exactly when `mutable` is false.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    /// The value the *next* turn / next check will use. Omitted when the
    /// engine genuinely does not know it — never a fabricated default.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub value: Option<serde_json::Value>,
    /// The value captured into the currently running turn, when one is
    /// running and it differs in kind from `value`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub active_value: Option<serde_json::Value>,
    /// The closed set of accepted values, when there is one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub allowed_values: Option<Vec<AllowedValue>>,
    /// The RPC method that owns the *live* set of accepted values, for keys
    /// whose set is provider- or model-dependent and therefore cannot be
    /// stated here without going stale.
    ///
    /// Present exactly when `allowedValues` is not authoritative on its own,
    /// so an absent or partial `allowedValues` always has a discoverable
    /// authority instead of leaving a client to guess.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub allowed_values_from: Option<String>,
    pub description: String,
}

/// One accepted value for a key with a closed set.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct AllowedValue {
    pub value: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
}

impl AllowedValue {
    pub fn new(value: impl Into<String>) -> Self {
        Self { value: value.into(), description: None }
    }
    pub fn described(value: impl Into<String>, description: impl Into<String>) -> Self {
        Self { value: value.into(), description: Some(description.into()) }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ConfigDescribeResult {
    pub entries: Vec<ConfigEntryDto>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ConfigSetResult {
    pub ok: bool,
    pub key: String,
    pub applied_at: AppliesWhen,
    /// What the engine actually holds now, read back after the delegated
    /// method ran. Never an echo of the request.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub effective: Option<serde_json::Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn applies_when_serialises_as_lower_camel() {
        let v = serde_json::to_value(AppliesWhen::NextPermissionCheck).unwrap();
        assert_eq!(v, "nextPermissionCheck");
        assert_eq!(AppliesWhen::NextPermissionCheck.as_str(), "nextPermissionCheck");
    }

    #[test]
    fn an_immutable_entry_round_trips_with_its_reason_and_omits_absent_fields() {
        let entry = ConfigEntryDto {
            key: "provider".into(),
            owner: ConfigOwner::EngineStartup,
            applies_at: AppliesWhen::NewEngineInstance,
            mutable: false,
            reason: Some("the provider is chosen when the engine process starts".into()),
            value: Some(serde_json::json!("anthropic")),
            active_value: None,
            allowed_values: None,
            allowed_values_from: None,
            description: "The connected LLM provider.".into(),
        };
        let v = serde_json::to_value(&entry).unwrap();
        assert_eq!(v["appliesAt"], "newEngineInstance");
        assert!(v.get("activeValue").is_none(), "absent optionals are omitted, never null");
        assert!(v.get("allowedValues").is_none());
        assert!(v.get("allowedValuesFrom").is_none());
        let back: ConfigEntryDto = serde_json::from_value(v).unwrap();
        assert_eq!(back, entry);
    }
}
