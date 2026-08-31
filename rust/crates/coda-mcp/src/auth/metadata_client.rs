//! Fetches OAuth discovery documents and performs Dynamic Client Registration.
//!
//! Mirrors `McpAuthMetadataClient.cs`.

use reqwest::Client;

use super::types::{
    AuthorizationServerMetadata, McpClientRegistration, ProtectedResourceMetadata,
};

/// Fetches OAuth discovery documents and performs Dynamic Client Registration
/// (DCR) for the MCP authorization flow.
///
/// - RFC 9728 protected-resource metadata
/// - RFC 8414 / OpenID Connect authorization-server metadata
/// - RFC 7591 Dynamic Client Registration
pub struct McpAuthMetadataClient {
    http: Client,
}

impl McpAuthMetadataClient {
    pub fn new(http: Client) -> Self {
        Self { http }
    }

    /// Fetch the RFC 9728 Protected Resource Metadata document.
    ///
    /// Returns `None` on any network, parse, or non-2xx error.
    pub async fn get_protected_resource_metadata(
        &self,
        metadata_url: &str,
    ) -> Option<ProtectedResourceMetadata> {
        let doc = self.get_json(metadata_url).await?;
        Some(ProtectedResourceMetadata::parse(&doc))
    }

    /// Fetch authorization-server metadata, trying RFC 8414 then OIDC Discovery.
    ///
    /// Returns `None` when neither well-known endpoint returns a usable document.
    pub async fn get_authorization_server_metadata(
        &self,
        issuer: &str,
    ) -> Option<AuthorizationServerMetadata> {
        let base = issuer.trim_end_matches('/');
        let candidates = [
            format!("{base}/.well-known/oauth-authorization-server"),
            format!("{base}/.well-known/openid-configuration"),
        ];

        for candidate in &candidates {
            if let Some(doc) = self.get_json(candidate).await {
                if let Some(meta) = AuthorizationServerMetadata::parse(&doc) {
                    return Some(meta);
                }
            }
        }
        None
    }

    /// Register a public native client via RFC 7591 Dynamic Client Registration.
    ///
    /// Returns `None` when registration fails (non-2xx, malformed response,
    /// network error, or non-caller timeout).
    pub async fn register_client(
        &self,
        registration_endpoint: &str,
        redirect_uri: &str,
        grant_types: &[&str],
    ) -> Option<McpClientRegistration> {
        use serde_json::json;

        let body = json!({
            "client_name": "Coda CLI",
            "redirect_uris": [redirect_uri],
            "grant_types": grant_types,
            "response_types": ["code"],
            "token_endpoint_auth_method": "none",
            "application_type": "native",
        });

        let response = match self
            .http
            .post(registration_endpoint)
            .json(&body)
            .send()
            .await
        {
            Ok(r) => r,
            Err(_) => return None,
        };

        if !response.status().is_success() {
            return None;
        }

        let text = match response.text().await {
            Ok(t) => t,
            Err(_) => return None,
        };
        let doc: serde_json::Value = match serde_json::from_str(&text) {
            Ok(v) => v,
            Err(_) => return None,
        };
        let reg = McpClientRegistration::parse(&doc)?;
        if reg.client_id.is_empty() {
            return None;
        }
        Some(reg)
    }

    async fn get_json(&self, url: &str) -> Option<serde_json::Value> {
        let response = self.http.get(url).send().await.ok()?;
        if !response.status().is_success() {
            return None;
        }
        let text = response.text().await.ok()?;
        serde_json::from_str(&text).ok()
    }
}
