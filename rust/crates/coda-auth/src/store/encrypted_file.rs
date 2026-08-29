//! AES-256-GCM encrypted-file credential store.
//!
//! **Purpose**: headless Linux environments (CI servers, containers, SSH
//! sessions) typically run without a Secret Service daemon, so the keyring
//! backend fails.  This implementation falls back to storing credentials as
//! AES-256-GCM encrypted files under `~/.coda/credentials/` using a
//! per-installation key kept in `key.bin`.
//!
//! **Security**: confidentiality comes from filesystem permissions (0700 on
//! the directory, 0600 on each file) rather than from OS-level key protection.
//! This is acceptable for headless servers that have no better option; it is
//! not the right choice for interactive desktops where the keyring is available.
//!
//! **Format**: each `.cred` file contains `nonce(12) || tag(16) || ciphertext`,
//! matching the C# `FileTokenStore` layout so that credentials written by one
//! implementation are readable by neither (the encryption keys differ), but the
//! layout is consistent.

use std::path::{Path, PathBuf};

use aes_gcm::aead::{Aead, KeyInit, OsRng};
use aes_gcm::{Aes256Gcm, Key, Nonce};
use async_trait::async_trait;
use rand::RngCore;

use crate::error::AuthError;
use crate::store::CredentialStore;

const KEY_FILE: &str = "key.bin";
const KEY_LEN: usize = 32; // AES-256
const NONCE_LEN: usize = 12; // GCM standard nonce
const TAG_LEN: usize = 16; // GCM authentication tag

/// AES-256-GCM encrypted file store under `~/.coda/credentials`.
pub struct EncryptedFileStore {
    directory: PathBuf,
    key: [u8; KEY_LEN],
}

impl EncryptedFileStore {
    /// Opens (or creates) the store in `directory`.
    ///
    /// Creates `directory` with restrictive permissions if it does not exist,
    /// then loads or generates the 256-bit encryption key.
    pub fn new(directory: impl Into<PathBuf>) -> Result<Self, AuthError> {
        let directory = directory.into();
        std::fs::create_dir_all(&directory)?;
        Self::restrict_directory(&directory)?;

        let key = load_or_create_key(&directory)?;
        Ok(Self { directory, key })
    }

    fn restrict_directory(dir: &Path) -> Result<(), AuthError> {
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(dir, std::fs::Permissions::from_mode(0o700))?;
        }
        let _ = dir; // silence unused-variable warning on non-unix
        Ok(())
    }

    fn path_for(&self, key: &str) -> PathBuf {
        // Make the key safe for use as a filename by replacing any character
        // that is not alphanumeric, hyphen, underscore, or dot.
        let safe: String = key
            .chars()
            .map(|c| {
                if c.is_alphanumeric() || matches!(c, '-' | '_' | '.') {
                    c
                } else {
                    '_'
                }
            })
            .collect();
        self.directory.join(format!("{safe}.cred"))
    }

    fn encrypt(&self, plaintext: &[u8]) -> Result<Vec<u8>, AuthError> {
        let cipher_key = Key::<Aes256Gcm>::from_slice(&self.key);
        let cipher = Aes256Gcm::new(cipher_key);

        // Fresh random nonce for every write.
        let mut nonce_bytes = [0u8; NONCE_LEN];
        OsRng.fill_bytes(&mut nonce_bytes);
        let nonce = Nonce::from_slice(&nonce_bytes);

        let ciphertext = cipher
            .encrypt(nonce, plaintext)
            .map_err(|e| AuthError::store(format!("encryption failed: {e}")))?;

        // Layout: nonce(12) || tag(16) || ciphertext
        // aes_gcm appends the 16-byte tag to the ciphertext; split it back out
        // to match the explicit layout.
        let tag_start = ciphertext.len() - TAG_LEN;
        let tag = &ciphertext[tag_start..];
        let ct = &ciphertext[..tag_start];

        let mut out = Vec::with_capacity(NONCE_LEN + TAG_LEN + ct.len());
        out.extend_from_slice(&nonce_bytes);
        out.extend_from_slice(tag);
        out.extend_from_slice(ct);
        Ok(out)
    }

    fn decrypt(&self, data: &[u8]) -> Option<Vec<u8>> {
        if data.len() < NONCE_LEN + TAG_LEN {
            return None;
        }
        let nonce = Nonce::from_slice(&data[..NONCE_LEN]);
        let tag = &data[NONCE_LEN..NONCE_LEN + TAG_LEN];
        let ct = &data[NONCE_LEN + TAG_LEN..];

        let cipher_key = Key::<Aes256Gcm>::from_slice(&self.key);
        let cipher = Aes256Gcm::new(cipher_key);

        // Reassemble in the form aes_gcm expects: ciphertext || tag.
        let mut combined = ct.to_vec();
        combined.extend_from_slice(tag);

        cipher.decrypt(nonce, combined.as_slice()).ok()
    }
}

impl Default for EncryptedFileStore {
    /// Creates the store under the default `~/.coda/credentials` directory.
    fn default() -> Self {
        let dir = default_credentials_dir();
        // Panic is appropriate at startup if the home directory cannot be resolved
        // — subsequent operations would fail anyway.
        Self::new(dir).expect("failed to initialize encrypted credential store")
    }
}

#[async_trait]
impl CredentialStore for EncryptedFileStore {
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
        let path = self.path_for(key);
        if !path.exists() {
            return Ok(None);
        }

        let bytes = tokio::fs::read(&path).await?;
        let plaintext = match self.decrypt(&bytes) {
            Some(p) => p,
            // Treat a corrupt file or a rotated key as missing rather than
            // failing hard: the user is prompted to re-login instead of being
            // locked out of every credential read.
            None => {
                tracing::warn!(
                    path = %path.display(),
                    "failed to decrypt credential; treating as missing (corrupt file or rotated key)"
                );
                return Ok(None);
            }
        };

        Ok(Some(
            String::from_utf8(plaintext)
                .map_err(|e| AuthError::store(format!("credential is not valid UTF-8: {e}")))?,
        ))
    }

    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
        let ciphertext = self.encrypt(value.as_bytes())?;
        let path = self.path_for(key);
        tokio::fs::write(&path, &ciphertext).await?;
        set_owner_only_file(&path)?;
        Ok(())
    }

    async fn delete(&self, key: &str) -> Result<(), AuthError> {
        let path = self.path_for(key);
        if path.exists() {
            tokio::fs::remove_file(&path).await?;
        }
        Ok(())
    }
}

// ── Key management ───────────────────────────────────────────────────────────

fn load_or_create_key(directory: &Path) -> Result<[u8; KEY_LEN], AuthError> {
    let key_path = directory.join(KEY_FILE);

    if key_path.exists() {
        let bytes = std::fs::read(&key_path)?;
        if bytes.len() == KEY_LEN {
            let mut key = [0u8; KEY_LEN];
            key.copy_from_slice(&bytes);
            return Ok(key);
        }
        // Corrupt / wrong length — regenerate below.
    }

    let mut key = [0u8; KEY_LEN];
    OsRng.fill_bytes(&mut key);
    std::fs::write(&key_path, &key)?;
    set_owner_only_file(&key_path)?;
    Ok(key)
}

fn set_owner_only_file(path: &Path) -> Result<(), AuthError> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600))?;
    }
    let _ = path; // silence unused-variable warning on non-unix
    Ok(())
}

fn default_credentials_dir() -> PathBuf {
    directories::BaseDirs::new()
        .map(|dirs| dirs.home_dir().join(".coda").join("credentials"))
        .unwrap_or_else(|| PathBuf::from(".coda/credentials"))
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_store() -> EncryptedFileStore {
        let dir = tempdir();
        EncryptedFileStore::new(dir).expect("store")
    }

    /// Creates a unique temporary directory that is cleaned up when returned
    /// `PathBuf` goes out of scope — we keep it simple here by returning a path
    /// that will be cleaned up by the OS later (tests run in CI).
    fn tempdir() -> PathBuf {
        let mut dir = std::env::temp_dir();
        dir.push(format!("coda_auth_test_{}", rand::random::<u64>()));
        std::fs::create_dir_all(&dir).expect("tempdir");
        dir
    }

    #[tokio::test]
    async fn round_trips_a_credential_blob() {
        let store = temp_store();
        store.set("key1", "hello_world").await.expect("set");
        let got = store.get("key1").await.expect("get");
        assert_eq!(got.as_deref(), Some("hello_world"));
    }

    #[tokio::test]
    async fn missing_key_returns_none() {
        let store = temp_store();
        let got = store.get("absent").await.expect("get");
        assert!(got.is_none());
    }

    #[tokio::test]
    async fn delete_makes_entry_invisible() {
        let store = temp_store();
        store.set("to_delete", "value").await.expect("set");
        store.delete("to_delete").await.expect("delete");
        assert!(store.get("to_delete").await.expect("get").is_none());
    }

    #[tokio::test]
    async fn delete_of_absent_key_is_ok() {
        let store = temp_store();
        store.delete("never_existed").await.expect("delete");
    }

    #[tokio::test]
    async fn stored_file_is_not_plaintext() {
        let store = temp_store();
        store.set("secret_key", "super_secret_value").await.expect("set");
        let path = store.path_for("secret_key");
        let raw = std::fs::read(&path).expect("read file");
        // The raw bytes must not contain the plaintext.
        let raw_str = String::from_utf8_lossy(&raw);
        assert!(
            !raw_str.contains("super_secret_value"),
            "credential file must not contain plaintext"
        );
    }

    #[tokio::test]
    async fn wrong_key_returns_none_instead_of_erroring() {
        // MINOR 9: a corrupt file or rotated key must degrade to Ok(None) so
        // the caller is prompted to re-login rather than failing hard.
        let dir = tempdir();
        let store1 = EncryptedFileStore::new(dir.clone()).expect("store1");
        store1.set("k", "v").await.expect("set");

        // Overwrite the key file to simulate a rotated key.
        let key_path = dir.join(KEY_FILE);
        let mut new_key = [0u8; KEY_LEN];
        OsRng.fill_bytes(&mut new_key);
        std::fs::write(&key_path, &new_key).expect("overwrite key");

        let store2 = EncryptedFileStore::new(dir).expect("store2");
        // Must return Ok(None) — treating the corrupt credential as missing —
        // not an Err that would propagate to the user as a hard failure.
        assert_eq!(store2.get("k").await.expect("get"), None);
    }

    #[tokio::test]
    async fn corrupt_file_returns_none() {
        // Any file short enough to fail the nonce+tag length check or that
        // contains random bytes must degrade to Ok(None), not a hard Err.
        let store = temp_store();
        let path = store.path_for("corrupt");
        // Write random garbage that is too short to even parse as nonce||tag||ct.
        tokio::fs::write(&path, b"this is garbage").await.expect("write");
        assert_eq!(store.get("corrupt").await.expect("get"), None);
    }

    // ── additional behaviours from the C# FileTokenStoreTests spec ────────────

    /// A second `EncryptedFileStore` opened over the same directory must reload
    /// the persisted AES key and successfully decrypt what the first instance
    /// wrote.  This is the core "process restart" scenario.
    #[tokio::test]
    async fn second_instance_reads_data_written_by_first_instance() {
        let dir = tempdir();

        let store1 = EncryptedFileStore::new(dir.clone()).expect("store1");
        store1.set("llmauth:persist-check", "persisted-value").await.expect("set");
        drop(store1); // make the first instance go away

        let store2 = EncryptedFileStore::new(dir).expect("store2");
        let result = store2.get("llmauth:persist-check").await.expect("get");
        assert_eq!(result.as_deref(), Some("persisted-value"));
    }

    /// A second write to the same key must silently replace the previous value.
    #[tokio::test]
    async fn overwrite_returns_latest_value() {
        let store = temp_store();
        store.set("llmauth:overwrite", "a").await.expect("set a");
        store.set("llmauth:overwrite", "b").await.expect("set b");
        let result = store.get("llmauth:overwrite").await.expect("get");
        assert_eq!(result.as_deref(), Some("b"));
    }

    /// Keys must be isolated from each other: writing one key must not clobber
    /// a different key, and both must be independently readable.
    #[tokio::test]
    async fn multiple_keys_are_isolated() {
        let store = temp_store();
        store.set("llmauth:key1", "value-one").await.expect("set1");
        store.set("llmauth:key2", "value-two").await.expect("set2");
        assert_eq!(
            store.get("llmauth:key1").await.expect("get1").as_deref(),
            Some("value-one")
        );
        assert_eq!(
            store.get("llmauth:key2").await.expect("get2").as_deref(),
            Some("value-two")
        );
    }

    /// Keys containing colons (our standard `"llmauth:<provider>"` format) and
    /// other characters that would be invalid in file names must be sanitised to
    /// a safe filename without losing uniqueness.
    #[tokio::test]
    async fn key_containing_colon_maps_to_a_safe_filename() {
        let store = temp_store();
        store
            .set("llmauth:claude-ai", "token-value-for-claude")
            .await
            .expect("set");

        // The credential file must exist but must not contain a colon in its name.
        let path = store.path_for("llmauth:claude-ai");
        assert!(
            path.exists(),
            "credential file must be created for a colon-containing key"
        );
        let filename = path.file_name().unwrap().to_string_lossy();
        assert!(
            !filename.contains(':'),
            "filename must not contain a colon; got '{filename}'"
        );

        // And the value must still round-trip correctly.
        let got = store.get("llmauth:claude-ai").await.expect("get");
        assert_eq!(got.as_deref(), Some("token-value-for-claude"));
    }

    /// A `key.bin` that is corrupt (wrong length or invalid content) must be
    /// silently regenerated so the store is usable after construction.  Any
    /// previously encrypted credentials will be unreadable (wrong key), but the
    /// store itself must not panic or return an error during `new()`.
    #[tokio::test]
    async fn corrupt_key_file_is_regenerated_and_store_still_works() {
        let dir = tempdir();
        let key_path = dir.join(KEY_FILE);

        // Write a key.bin that is neither a valid 32-byte raw key nor a valid
        // DPAPI-wrapped blob: just five garbage bytes.
        std::fs::write(&key_path, &[1u8, 2, 3, 4, 5]).expect("write corrupt key");

        // Construction must not panic — the corrupt key must be regenerated.
        let store = EncryptedFileStore::new(dir.clone()).expect("store must not fail on corrupt key");

        // The regenerated store must be fully functional: set and get must work.
        store
            .set("llmauth:copilot", "secret-value")
            .await
            .expect("set after key regen");

        // A second instance over the same directory reads the key that was
        // regenerated above (not the original corrupt bytes).
        let store2 = EncryptedFileStore::new(dir).expect("store2");
        let got = store2.get("llmauth:copilot").await.expect("get");
        assert_eq!(got.as_deref(), Some("secret-value"));
    }
}
