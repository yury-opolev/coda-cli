//! MCP OAuth / bearer-token authentication.
//!
//! This module provides the auth provider trait and its implementations for
//! the HTTP MCP transport:
//! - [`StaticBearerAuthProvider`] — attaches a static bearer token.
//! - [`McpOAuthProvider`] — runs the full OAuth 2.1 + PKCE discovery flow.
//!
//! The [`McpAuthMetadataClient`] fetches the OAuth discovery documents (RFC 9728
//! protected-resource metadata and RFC 8414 / OIDC authorization-server
//! metadata) and drives Dynamic Client Registration (RFC 7591).

pub mod metadata_client;
pub mod oauth;
pub mod provider;
pub mod types;

pub use metadata_client::McpAuthMetadataClient;
pub use oauth::{McpOAuthProvider, McpOAuthReauthenticator};
pub use provider::{McpAuthProvider, StaticBearerAuthProvider};
pub use types::{
    AuthorizationServerMetadata, McpAuthConfig, McpAuthMode, McpAuthResult,
    McpClientRegistration, McpStoredToken, ProtectedResourceMetadata,
    WwwAuthenticateChallenge, canonical_resource_uri,
};
