//! [`CredentialStore`] trait and its built-in implementations.
//!
//! Implementations:
//! - [`DpapiStore`] — Windows DPAPI files, the format the .NET build writes.
//! - [`EncryptedFileStore`] — AES-256-GCM files under `~/.coda/credentials`,
//!   the format the .NET `FileTokenStore` writes on every other host.
//! - [`KeyringStore`] — OS-native storage via the `keyring` crate, kept as an
//!   adoption source for credentials an earlier Rust build wrote there.
//! - [`InMemoryStore`] — ephemeral in-process storage for tests.
//!
//! Callers do not choose between them: [`open_profile_storage`] resolves the
//! backend for a profile deterministically. See [`factory`] for the rules.

use async_trait::async_trait;

use crate::error::AuthError;

pub use self::dpapi::DpapiStore;
pub use self::encrypted_file::EncryptedFileStore;
pub use self::factory::{
    open_layout, open_profile_storage, open_profile_storage_with, primary_dir_for,
    resolve_backend, resolve_store_layout, AuthStorage, BackendKind, DirectoryFacts, LegacySource,
    Profile, ProfileFacts, StoreLayout, CODA_CREDENTIAL_BACKEND_ENV,
};
pub use self::keyring::KeyringStore;
pub use self::memory::InMemoryStore;
pub use self::profile_store::ProfileCredentialStore;

pub(crate) mod atomic;
pub mod dpapi;
pub mod encrypted_file;
pub mod factory;
pub mod keyring;
pub mod memory;
pub mod profile_store;

/// Persists serialized credentials keyed by an opaque string.
///
/// Implementations are responsible for encryption at rest (the keyring backend
/// delegates to the OS; the file backends use DPAPI or AES-256-GCM).  The JSON
/// blob stored here already contains all credential fields, so a caller should
/// treat it opaquely.
///
/// `Ok(None)` means **there is no credential**. A credential that exists but
/// cannot be read — wrong key, another user, a locked file — is an error
/// ([`AuthError::StoreUndecryptable`]), never `None`: a caller told "nothing
/// here" logs the user out and overwrites what was still recoverable.
#[async_trait]
pub trait CredentialStore: Send + Sync {
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError>;
    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError>;
    async fn delete(&self, key: &str) -> Result<(), AuthError>;

    /// Reads `key` without any migration side effect.
    ///
    /// [`get`][CredentialStore::get] may move a credential an earlier build
    /// left behind into the primary backend, which it does inside the
    /// profile's commit section. A caller that already holds that section must
    /// use this method instead, or it would wait on a lock it is holding.
    ///
    /// File stores also read older file names without rewriting them.
    /// Migration belongs to the coordinating profile store, never to a
    /// compatibility read that could race a replacement or logout.
    ///
    /// The default is `get`, which is correct for stores whose reads have no
    /// migration side effects.
    async fn read_only(&self, key: &str) -> Result<Option<String>, AuthError> {
        self.get(key).await
    }
}
