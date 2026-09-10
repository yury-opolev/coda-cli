//! Keyring-backed [`CredentialStore`].
//!
//! Delegates to the OS-native secure storage:
//! - **Windows**: Credential Manager
//! - **macOS**: Keychain
//! - **Linux desktop**: Secret Service (GNOME Keyring / KWallet)
//!
//! This is no longer a primary backend: the profile factory uses the file
//! stores, which are the ones the .NET build reads and writes. The keyring is
//! kept as an *adoption source* for credentials an earlier Rust build left
//! there, which makes the difference between "this host has no keyring" and
//! "the keyring is there but will not open" matter:
//!
//! * **not available** (no Secret Service on a headless box) — nothing of ours
//!   can be reached there, so a caller may skip it;
//! * **anything else** (locked, access denied, unreadable entry) — a
//!   credential may well be sitting there. Reporting "nothing found" would
//!   send the user through a login that overwrites it, so it is an error.

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
    /// error describing why it is not.
    ///
    /// Not used to choose a backend — that decision is made by the profile
    /// factory without touching anything — but useful for diagnostics.
    pub fn probe() -> Result<(), AuthError> {
        let entry = keyring::Entry::new(SERVICE, "__coda_probe__")
            .map_err(|e| classify("open", "__coda_probe__", e))?;

        entry
            .set_password("probe")
            .map_err(|e| classify("write", "__coda_probe__", e))?;

        // Best-effort cleanup; ignore deletion errors.
        let _ = entry.delete_password();

        Ok(())
    }
}

/// Turns a keyring failure into the auth error that describes it honestly.
///
/// Only a platform that cannot host the store at all becomes
/// [`AuthError::StoreUnavailable`], which callers are allowed to skip. A
/// locked or unreadable store is a plain store error and must be surfaced.
fn classify(operation: &str, key: &str, error: keyring::Error) -> AuthError {
    let key = crate::error::sanitize_key(key);
    match error {
        // The platform has no working secret service (headless Linux, no DBus,
        // an OS without the API). Nothing of ours can be stored there.
        keyring::Error::PlatformFailure(e) => AuthError::StoreUnavailable {
            backend: "keyring".into(),
            detail: format!("{e}"),
        },
        // The store exists but will not let us in: it may hold the credential.
        keyring::Error::NoStorageAccess(e) => AuthError::store(format!(
            "the OS credential store refused access while trying to {operation} '{key}': {e}. \
             A credential may still be stored there; nothing was changed"
        )),
        keyring::Error::NoEntry => AuthError::store(format!(
            "the OS credential store reports no entry for '{key}' during {operation}"
        )),
        other => AuthError::store(format!(
            "the OS credential store failed to {operation} '{key}': {other}"
        )),
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
        let entry = keyring::Entry::new(SERVICE, key).map_err(|e| classify("open", key, e))?;

        match entry.get_password() {
            Ok(value) => Ok(Some(value)),
            Err(keyring::Error::NoEntry) => Ok(None),
            Err(e) => Err(classify("read", key, e)),
        }
    }

    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
        let entry = keyring::Entry::new(SERVICE, key).map_err(|e| classify("open", key, e))?;

        entry.set_password(value).map_err(|e| classify("write", key, e))
    }

    async fn delete(&self, key: &str) -> Result<(), AuthError> {
        let entry = keyring::Entry::new(SERVICE, key).map_err(|e| classify("open", key, e))?;

        match entry.delete_password() {
            Ok(()) => Ok(()),
            // Deleting a non-existent entry is not an error.
            Err(keyring::Error::NoEntry) => Ok(()),
            Err(e) => Err(classify("delete", key, e)),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// These tests classify errors only. They never touch the machine's real
    /// credential store.
    fn boxed(message: &str) -> Box<dyn std::error::Error + Send + Sync> {
        Box::new(std::io::Error::other(message.to_owned()))
    }

    /// A host without a working secret service is a skippable condition: no
    /// credential of ours can be there.
    #[test]
    fn a_host_without_a_secret_service_is_reported_as_unavailable() {
        let err = classify(
            "read",
            "llmauth:claude-ai",
            keyring::Error::PlatformFailure(boxed("no dbus session")),
        );
        assert!(
            matches!(err, AuthError::StoreUnavailable { .. }),
            "a missing platform store must be skippable, got {err:?}"
        );
    }

    /// A locked store may hold the credential, so it must not be skippable —
    /// skipping it would report "no credentials" and invite an overwrite.
    #[test]
    fn a_locked_store_is_not_reported_as_unavailable() {
        let err = classify(
            "read",
            "llmauth:claude-ai",
            keyring::Error::NoStorageAccess(boxed("the keyring is locked")),
        );
        assert!(
            !matches!(err, AuthError::StoreUnavailable { .. }),
            "a locked store must be surfaced, got {err:?}"
        );
    }

    /// An entry we cannot decode is also not a skippable condition.
    #[test]
    fn an_unreadable_entry_is_not_reported_as_unavailable() {
        let err = classify("read", "llmauth:claude-ai", keyring::Error::BadEncoding(vec![0xff]));
        assert!(!matches!(err, AuthError::StoreUnavailable { .. }), "got {err:?}");
    }

    /// Untrusted key text must not reach the message verbatim.
    #[test]
    fn the_key_is_sanitized_in_the_message() {
        let err =
            classify("read", "evil\u{7}\nkey", keyring::Error::NoStorageAccess(boxed("locked")));
        let rendered = err.to_string();
        assert!(!rendered.contains('\n') && !rendered.contains('\u{7}'), "got {rendered:?}");
    }
}
