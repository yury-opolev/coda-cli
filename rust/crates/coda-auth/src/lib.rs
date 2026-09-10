//! Authentication and credential storage for the Coda engine.
//!
//! ## What this crate provides
//!
//! - **[`CredentialStore`][store::CredentialStore]** — a trait implemented by
//!   the Windows DPAPI store ([`DpapiStore`][store::DpapiStore]) and the
//!   AES-256-GCM file store ([`EncryptedFileStore`][store::EncryptedFileStore]),
//!   both format-compatible with the .NET build, plus the OS keyring
//!   ([`KeyringStore`][store::KeyringStore]) kept as an adoption source for
//!   credentials an earlier Rust build wrote. An in-memory implementation
//!   ([`InMemoryStore`][store::InMemoryStore]) is provided for tests.
//!   [`open_profile_storage`][store::open_profile_storage] is the one way to
//!   choose between them for a profile.
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
//! - Failures shown to a user go through [`AuthFailure`][failure::AuthFailure],
//!   a closed classification that retains no server- or user-supplied text.
//! - PKCE uses S256 with a 256-bit verifier, validated against the RFC 7636
//!   Appendix B known vector in tests.
//! - The OAuth `state` parameter is verified on the callback; a mismatch
//!   returns [`AuthError::StateMismatch`][error::AuthError::StateMismatch].
//! - The loopback listener binds to `127.0.0.1` only (never `0.0.0.0`).
//! - Token refresh is single-flight per provider.

pub mod credential;
pub mod credential_source;
pub mod coordination;
pub mod error;
pub mod failure;
pub mod home;
pub mod loopback;
pub mod manager;
pub mod pkce;
pub mod provider;
pub mod secret;
pub mod service;
pub mod store;

// Re-export the most commonly used types at the crate root.
pub use coordination::{CommitCoordinator, CommitGuard, FileCoordinator, LocalCoordinator};
pub use credential::{AccountInfo, Credential, CredentialKind};
pub use home::{coda_dir, coda_home};
pub use credential_source::{
    credential_to_auth_headers, CredentialManagerSource, EnvironmentApiKeySource,
};
pub use error::AuthError;
pub use failure::{safe_message, AuthFailure, StoreBackend};
pub use manager::CredentialManager;
pub use provider::{AuthProvider, DeviceCodePrompt};
pub use secret::Secret;
pub use store::{
    open_profile_storage, open_profile_storage_with, AuthStorage, BackendKind, CredentialStore,
    DpapiStore, EncryptedFileStore, InMemoryStore, KeyringStore, Profile,
};
