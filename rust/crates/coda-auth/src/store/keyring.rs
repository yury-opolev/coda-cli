//! Keyring-backed [`CredentialStore`].
//!
//! Delegates to the OS-native secure storage:
//! - **Windows**: Credential Manager
//! - **macOS**: Keychain
//! - **Linux desktop**: Secret Service (GNOME Keyring / KWallet)
//!
//! On headless Linux where no Secret Service daemon is running, every
//! keyring operation fails with a platform error.  `probe()` surfaces this
//! early so `default_store()` can select the encrypted-file fallback before
//! any real credential is attempted.

use async_trait::async_trait;

use crate::error::AuthError;
use crate::store::CredentialStore;

const SERVICE: &str = "coda-auth";

/// OS-native credential store backed by the `keyring` crate.
pub struct KeyringStore;

impl KeyringStore {
    pub fn new() -> Self {
        Self
    }

    /// Probes the OS credential store by writing and immediately deleting a
    /// test entry.  Returns `Ok(())` when the store is operational, or an
    /// [`AuthError::Store`] when it is not (e.g. no Secret Service on
    /// headless Linux).
    pub fn probe() -> Result<(), AuthError> {
        let entry = keyring::Entry::new(SERVICE, "__coda_probe__")
            .map_err(|e| AuthError::store(format!("keyring probe failed: {e}")))?;

        entry
            .set_password("probe")
            .map_err(|e| AuthError::store(format!("keyring probe write failed: {e}")))?;

        // Best-effort cleanup; ignore deletion errors.
        let _ = entry.delete_password();

        Ok(())
    }
}

impl Default for KeyringStore {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait]
impl CredentialStore for KeyringStore {
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
        let entry = keyring::Entry::new(SERVICE, key)
            .map_err(|e| AuthError::store(e.to_string()))?;

        match entry.get_password() {
            Ok(value) => Ok(Some(value)),
            Err(keyring::Error::NoEntry) => Ok(None),
            Err(e) => Err(AuthError::store(e.to_string())),
        }
    }

    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
        let entry = keyring::Entry::new(SERVICE, key)
            .map_err(|e| AuthError::store(e.to_string()))?;

        entry
            .set_password(value)
            .map_err(|e| AuthError::store(e.to_string()))
    }

    async fn delete(&self, key: &str) -> Result<(), AuthError> {
        let entry = keyring::Entry::new(SERVICE, key)
            .map_err(|e| AuthError::store(e.to_string()))?;

        match entry.delete_password() {
            Ok(()) => Ok(()),
            // Deleting a non-existent entry is not an error.
            Err(keyring::Error::NoEntry) => Ok(()),
            Err(e) => Err(AuthError::store(e.to_string())),
        }
    }
}
