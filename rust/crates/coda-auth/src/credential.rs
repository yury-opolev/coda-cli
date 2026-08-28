//! The core credential types persisted by the auth crate.
//!
//! `Credential` is serialized to JSON and stored (encrypted) in the credential
//! store.  All secret fields are wrapped in [`Secret`] so that a
//! `format!("{:?}", credential)` can never print a token, key, or password
//! into a log line or error message.

use std::fmt;

use serde::{Deserialize, Serialize};

use crate::secret::Secret;

/// How a credential authenticates with its provider.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CredentialKind {
    /// OAuth access/refresh token pair (e.g. Claude.ai subscriber).
    OAuth,
    /// A static API key sent as `x-api-key`.
    ApiKey,
}

/// Account / org metadata returned alongside an OAuth token, if the
/// provider supplies it.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AccountInfo {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub account_uuid: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub email_address: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub organization_uuid: Option<String>,
}

/// A resolved credential for a single provider.
///
/// Serialized to the [`CredentialStore`][crate::store::CredentialStore] — the
/// store is responsible for encrypting the blob at rest.  All secret fields use
/// [`Secret`] so that `Debug` output never reveals token material.
#[derive(Clone, Serialize, Deserialize)]
pub struct Credential {
    /// Stable provider id (e.g. `"claude-ai"` or `"github-copilot"`).
    pub provider_id: String,

    pub kind: CredentialKind,

    /// OAuth access token (OAuth credentials only).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub access_token: Option<Secret<String>>,

    /// OAuth refresh token, kept so the short-lived access token can be renewed
    /// without re-authenticating the user.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub refresh_token: Option<Secret<String>>,

    /// Static API key (`ApiKey` credentials only).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub api_key: Option<Secret<String>>,

    /// Absolute UTC expiry of `access_token`, if known.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub expires_at: Option<chrono::DateTime<chrono::Utc>>,

    /// Granted OAuth scopes.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub scopes: Vec<String>,

    /// Account/org info returned by the token endpoint, if any.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub account: Option<AccountInfo>,
}

/// Manual `Debug` implementation: secret fields print `[REDACTED]` verbatim.
/// Non-secret metadata (provider id, kind, expiry, scopes) are printed so
/// that logs are still actionable.
impl fmt::Debug for Credential {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let mut s = f.debug_struct("Credential");
        s.field("provider_id", &self.provider_id);
        s.field("kind", &self.kind);
        // Token-bearing fields: always redacted.
        s.field("access_token", &self.access_token.as_ref().map(|_| "[REDACTED]"));
        s.field("refresh_token", &self.refresh_token.as_ref().map(|_| "[REDACTED]"));
        s.field("api_key", &self.api_key.as_ref().map(|_| "[REDACTED]"));
        // Non-secret metadata: safe to log.
        s.field("expires_at", &self.expires_at);
        s.field("scopes", &self.scopes);
        s.field("account", &self.account);
        s.finish()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn secret_credential() -> Credential {
        Credential {
            provider_id: "claude-ai".into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("acc_secret_value_xyz".into())),
            refresh_token: Some(Secret::new("ref_secret_value_xyz".into())),
            api_key: None,
            expires_at: None,
            scopes: vec!["user:inference".into()],
            account: None,
        }
    }

    #[test]
    fn debug_does_not_reveal_access_token() {
        let cred = secret_credential();
        let debug = format!("{:?}", cred);
        assert!(
            !debug.contains("acc_secret_value_xyz"),
            "access_token must not appear in Debug output; got: {debug}"
        );
    }

    #[test]
    fn debug_does_not_reveal_refresh_token() {
        let cred = secret_credential();
        let debug = format!("{:?}", cred);
        assert!(
            !debug.contains("ref_secret_value_xyz"),
            "refresh_token must not appear in Debug output; got: {debug}"
        );
    }

    #[test]
    fn debug_preserves_non_secret_metadata() {
        let cred = secret_credential();
        let debug = format!("{:?}", cred);
        assert!(debug.contains("claude-ai"), "provider_id should appear");
        assert!(debug.contains("user:inference"), "scopes should appear");
    }

    #[test]
    fn api_key_credential_does_not_reveal_key_in_debug() {
        let cred = Credential {
            provider_id: "anthropic-api-key".into(),
            kind: CredentialKind::ApiKey,
            access_token: None,
            refresh_token: None,
            api_key: Some(Secret::new("sk-ant-supersecret123".into())),
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        };
        let debug = format!("{:?}", cred);
        assert!(
            !debug.contains("sk-ant-supersecret123"),
            "api_key must not appear in Debug output; got: {debug}"
        );
    }

    #[test]
    fn credential_round_trips_through_json() {
        let cred = secret_credential();
        let json = serde_json::to_string(&cred).unwrap();
        let restored: Credential = serde_json::from_str(&json).unwrap();
        assert_eq!(
            restored.access_token.as_ref().map(|s| s.expose().as_str()),
            Some("acc_secret_value_xyz")
        );
        assert_eq!(restored.provider_id, "claude-ai");
    }
}
