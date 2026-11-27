//! MCP OAuth / bearer-token authentication.
//!
//! This module provides the auth provider trait and its implementations for
//! the HTTP MCP transport:
//! - [`StaticBearerAuthProvider`] — attaches a static bearer token.
//! - [`McpOAuthProvider`] — runs the full OAuth 2.1 + PKCE discovery flow.
//! - [`EntraBrokerProvider`] — Microsoft Entra tokens from the OS account
//!   broker ([`TokenBroker`]), falling back to the browser flow.
//!
//! The [`McpAuthMetadataClient`] fetches the OAuth discovery documents (RFC 9728
//! protected-resource metadata and RFC 8414 / OIDC authorization-server
//! metadata) and drives Dynamic Client Registration (RFC 7591).

pub mod broker;
pub mod entra;
pub mod entra_broker;
pub mod metadata_client;
pub mod oauth;
pub mod provider;
#[cfg(test)]
pub(crate) mod test_http;
pub mod types;

pub use broker::{
    BrokerAccount, BrokerError, BrokerRequest, BrokerToken, BrokerTokenCache, TokenBroker,
    UnavailableBroker, default_broker,
};
pub use entra_broker::{EntraBrokerOptions, EntraBrokerProvider};
pub use metadata_client::McpAuthMetadataClient;
pub use oauth::{McpOAuthProvider, McpOAuthReauthenticator};
pub use provider::{McpAuthProvider, StaticBearerAuthProvider};
pub use types::{
    AuthorizationServerMetadata, McpAuthConfig, McpAuthMode, McpAuthResult, McpAuthStatus,
    McpClientRegistration, McpStoredToken, ProtectedResourceMetadata,
    WwwAuthenticateChallenge, canonical_resource_uri,
};
