//! Authentication and credential storage for the Coda engine.
//!
//! ## What this crate provides
//!
//! - **[`CredentialStore`][store::CredentialStore]** — a trait backed by
//!   the OS keyring ([`KeyringStore`][store::KeyringStore]) on interactive
//!   systems or AES-256-GCM encrypted files
//!   ([`EncryptedFileStore`][store::EncryptedFileStore]) on headless Linux.
//!   An in-memory implementation ([`InMemoryStore`][store::InMemoryStore])
//!   is provided for tests.
//!
//! - **[`AuthProvider`][provider::AuthProvider]** — a trait for pluggable
//!   auth strategies:
//!   - [`ApiKeyProvider`][provider::ApiKeyProvider] — Anthropic API key.
//!   - [`ClaudeAiProvider`][provider::ClaudeAiProvider] — Claude.ai OAuth
//!     (Authorization Code + PKCE over a loopback redirect).
//!   - [`CopilotProvider`][provider::CopilotProvider] — GitHub Copilot
//!     device-code flow with Copilot token exchange.
//!
//! - **[`CredentialManager`][manager::CredentialManager]** — the façade:
//!   registers providers, auto-refreshes on read, and coalesces N concurrent
//!   refresh requests into exactly one network call.
//!
//! ## Security properties
//!
//! - No secret (token, key, refresh token) ever appears in a `Debug` or
//!   `Display` output; all are wrapped in [`Secret`][secret::Secret].
//! - PKCE uses S256 with a 256-bit verifier, validated against the RFC 7636
//!   Appendix B known vector in tests.
//! - The OAuth `state` parameter is verified on the callback; a mismatch
//!   returns [`AuthError::StateMismatch`][error::AuthError::StateMismatch].
//! - The loopback listener binds to `127.0.0.1` only (never `0.0.0.0`).
//! - Token refresh is single-flight per provider.

pub mod credential;
pub mod credential_source;
pub mod error;
pub mod loopback;
pub mod manager;
pub mod pkce;
pub mod provider;
pub mod secret;
pub mod store;

// Re-export the most commonly used types at the crate root.
pub use credential::{AccountInfo, Credential, CredentialKind};
pub use credential_source::{CredentialManagerSource, credential_to_auth_headers};
pub use error::AuthError;
pub use manager::CredentialManager;
pub use provider::{AuthProvider, DeviceCodePrompt};
pub use secret::Secret;
pub use store::{CredentialStore, DpapiStore, EncryptedFileStore, InMemoryStore, KeyringStore};
