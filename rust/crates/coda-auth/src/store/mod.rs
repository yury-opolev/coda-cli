//! [`CredentialStore`] trait and its built-in implementations.
//!
//! Implementations:
//! - [`KeyringStore`] — OS-native storage via the `keyring` crate
//!   (Windows Credential Manager, macOS Keychain, Linux Secret Service).
//! - [`EncryptedFileStore`] — AES-256-GCM encrypted files under
//!   `~/.coda/credentials`, used as a fallback on headless Linux where no
//!   Secret Service is running.
//! - [`InMemoryStore`] — ephemeral in-process storage for tests.

use async_trait::async_trait;

use crate::error::AuthError;

pub use self::encrypted_file::EncryptedFileStore;
pub use self::keyring::KeyringStore;
pub use self::memory::InMemoryStore;
pub use self::dpapi::DpapiStore;

pub mod dpapi;
pub mod encrypted_file;
pub mod keyring;
pub mod memory;

/// Persists serialized credentials keyed by an opaque string.
///
/// Implementations are responsible for encryption at rest (the keyring backend
/// delegates to the OS; the file backend uses AES-256-GCM).  The JSON blob
/// stored here already contains all credential fields, so a caller should
/// treat it opaquely.
#[async_trait]
pub trait CredentialStore: Send + Sync {
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError>;
    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError>;
    async fn delete(&self, key: &str) -> Result<(), AuthError>;
}

/// Returns the best available credential store for the current platform.
///
/// On Windows and macOS, and on Linux desktops where a Secret Service is
/// running, [`KeyringStore`] is returned.  On headless Linux (CI, servers)
/// where no Secret Service is available, the keyring probe fails and
/// [`EncryptedFileStore`] is returned instead.
pub fn default_store() -> Box<dyn CredentialStore> {
    // Probe the keyring by writing and deleting a test entry.  If it succeeds,
    // the OS store is available; otherwise fall back to encrypted files.
    match KeyringStore::probe() {
        Ok(()) => Box::new(KeyringStore::new()),
        Err(_) => Box::new(EncryptedFileStore::default()),
    }
}
