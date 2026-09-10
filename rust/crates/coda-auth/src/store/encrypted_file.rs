//! AES-256-GCM encrypted-file credential store.
//!
//! **Purpose**: the credential store for every host that is not Windows. The
//! .NET build uses `LlmAuth.FileTokenStore` there, and this implementation is
//! deliberately format-compatible with it so that swapping engines does not
//! log the user out:
//!
//! * same directory (`~/.coda/credentials`),
//! * same file layout (`nonce(12) || tag(16) || ciphertext`),
//! * same `key.bin` (a raw 256-bit key — .NET only DPAPI-wraps it *on
//!   Windows*, where it uses the DPAPI store instead),
//! * same file names (`Path.GetInvalidFileNameChars()` per platform, so a
//!   colon survives on Unix and becomes `_` on Windows).
//!
//! **Security**: confidentiality comes from filesystem permissions (0700 on
//! the directory, 0600 on each file) plus the key file. Anything it cannot
//! read — a foreign `key.bin`, credentials written by another backend, a
//! rotated key — is reported as an error and left untouched. Rewriting it
//! would destroy credentials that are still recoverable.

use std::path::{Path, PathBuf};

use aes_gcm::aead::{Aead, KeyInit, OsRng};
use aes_gcm::{Aes256Gcm, Key, Nonce};
use async_trait::async_trait;
use rand::RngCore;

use crate::error::AuthError;
use crate::store::atomic::Publication;
use crate::store::CredentialStore;

const KEY_FILE: &str = "key.bin";
const KEY_LEN: usize = 32; // AES-256
const NONCE_LEN: usize = 12; // GCM standard nonce
const TAG_LEN: usize = 16; // GCM authentication tag
const CRED_EXT: &str = "cred";

/// AES-256-GCM encrypted file store under `~/.coda/credentials`.
pub struct EncryptedFileStore {
    directory: PathBuf,
    key: [u8; KEY_LEN],
}

/// Never prints the key: a `{:?}` of a store must be safe in any log line.
impl std::fmt::Debug for EncryptedFileStore {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("EncryptedFileStore")
            .field("directory", &self.directory)
            .field("key", &"[REDACTED]")
            .finish()
    }
}

impl EncryptedFileStore {
    /// Opens (or creates) the store in `directory`.
    ///
    /// Creates `directory` with restrictive permissions if it does not exist,
    /// then loads the 256-bit key — generating one only when the directory is
    /// empty of credentials. A directory whose key or credential files were
    /// written by something else is refused here, *before* any write.
    pub fn new(directory: impl Into<PathBuf>) -> Result<Self, AuthError> {
        let directory = directory.into();
        std::fs::create_dir_all(&directory)?;
        Self::restrict_directory(&directory)?;

        let key = load_or_create_key(&directory)?;
        Ok(Self { directory, key })
    }

    /// The directory this store reads and writes.
    pub fn directory(&self) -> &Path {
        &self.directory
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

    /// The file the .NET `FileTokenStore` would use for `key` on this platform.
    fn path_for(&self, key: &str) -> PathBuf {
        self.directory.join(format!("{}.{CRED_EXT}", dotnet_file_name(key)))
    }

    /// The file an earlier Rust build wrote for `key`.
    ///
    /// That build replaced every character outside `[A-Za-z0-9-_.]`, so
    /// `llmauth:claude-ai` landed in `llmauth_claude-ai.cred` — a different
    /// file from the .NET name on any platform where a colon is legal. Reading
    /// it keeps such a user signed in without rewriting the profile. Explicit
    /// writes use the canonical name; deletion removes both names.
    fn legacy_path_for(&self, key: &str) -> Option<PathBuf> {
        let safe: String = key
            .chars()
            .map(|c| if c.is_alphanumeric() || matches!(c, '-' | '_' | '.') { c } else { '_' })
            .collect();
        let path = self.directory.join(format!("{safe}.{CRED_EXT}"));
        (path != self.path_for(key)).then_some(path)
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

    /// Reads and decrypts `path`, or `Ok(None)` when the file is not there.
    ///
    /// Anything else — an unreadable file, a rotated key, a truncated file —
    /// is an error, never a silent "no credential".
    async fn read_at(&self, key: &str, path: &Path) -> Result<Option<String>, AuthError> {
        let bytes = match tokio::fs::read(path).await {
            Ok(bytes) => bytes,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(e) => {
                return Err(AuthError::undecryptable(
                    key,
                    format!("{} could not be read: {e}", path.display()),
                ))
            }
        };

        let plaintext = self.decrypt(&bytes).ok_or_else(|| {
            AuthError::undecryptable(
                key,
                "the file does not decrypt with this profile's key (rotated key, \
                 a copy from another machine, or a corrupt file)",
            )
        })?;

        String::from_utf8(plaintext)
            .map(Some)
            .map_err(|e| AuthError::undecryptable(key, format!("not valid UTF-8: {e}")))
    }
}

/// The file name .NET's `Path.GetInvalidFileNameChars()` produces for `key`.
///
/// Windows rejects the full punctuation set plus control characters; Unix only
/// rejects `/` and NUL, so `llmauth:claude-ai` keeps its colon there — exactly
/// as `FileTokenStore` writes it.
fn dotnet_file_name(key: &str) -> String {
    #[cfg(windows)]
    fn is_invalid(c: char) -> bool {
        matches!(c, '"' | '<' | '>' | '|' | ':' | '*' | '?' | '\\' | '/' | '\0')
            || (c as u32) < 0x20
    }
    #[cfg(not(windows))]
    fn is_invalid(c: char) -> bool {
        matches!(c, '/' | '\0')
    }

    key.chars().map(|c| if is_invalid(c) { '_' } else { c }).collect()
}

#[async_trait]
impl CredentialStore for EncryptedFileStore {
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
        if let Some(value) = self.read_at(key, &self.path_for(key)).await? {
            return Ok(Some(value));
        }

        // Reads run outside the profile's write coordination. Rewriting a
        // legacy value here could overwrite a concurrent replacement or
        // resurrect it after logout; compatibility must remain read-only.
        let Some(legacy) = self.legacy_path_for(key) else {
            return Ok(None);
        };
        self.read_at(key, &legacy).await
    }

    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
        let ciphertext = self.encrypt(value.as_bytes())?;
        let path = self.path_for(key);
        // Atomic replace: a reader must see the old credential or the new one,
        // never a truncated file, and a failure must not destroy the old one.
        tokio::task::spawn_blocking(move || crate::store::atomic::write_atomic(&path, &ciphertext))
            .await
            .map_err(|e| AuthError::store(format!("credential write task failed: {e}")))?
    }

    async fn delete(&self, key: &str) -> Result<(), AuthError> {
        // Both names must go: leaving the earlier Rust file behind would let a
        // logged-out credential reappear at the next read.
        let mut paths = vec![self.path_for(key)];
        paths.extend(self.legacy_path_for(key));
        for path in paths {
            match tokio::fs::remove_file(&path).await {
                Ok(()) => {}
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                Err(e) => return Err(AuthError::Io(e)),
            }
        }
        Ok(())
    }
}

// ── Key management ───────────────────────────────────────────────────────────

/// Loads the store key, creating one only for a directory this build owns.
///
/// A `key.bin` that is not a raw 256-bit key belongs to something else — the
/// .NET store DPAPI-wraps its key on Windows — and credential files without a
/// key were written by another backend. Both are refused: regenerating a key
/// or writing next to foreign files silently destroys credentials that are
/// still recoverable.
fn load_or_create_key(directory: &Path) -> Result<[u8; KEY_LEN], AuthError> {
    let key_path = directory.join(KEY_FILE);

    if key_path.exists() {
        return read_existing_key(&key_path);
    }

    create_or_adopt_key(directory, &key_path)
}

fn create_or_adopt_key(directory: &Path, key_path: &Path) -> Result<[u8; KEY_LEN], AuthError> {
    if let Some(existing) = first_credential_file(directory)? {
        // A cooperating creator may have published its key and first
        // credential since our absence check. Only a current missing-key
        // result justifies diagnosing a foreign credential directory.
        return match read_existing_key(key_path) {
            Ok(key) => Ok(key),
            Err(AuthError::Io(error)) if error.kind() == std::io::ErrorKind::NotFound => {
                Err(AuthError::incompatible(
                    directory,
                    format!(
                        "it holds credential files ({}) but no key of ours, so they were written \
                         by another backend. Refusing to write here",
                        existing.display()
                    ),
                ))
            }
            Err(error) => Err(error),
        };
    }

    let mut key = [0u8; KEY_LEN];
    OsRng.fill_bytes(&mut key);

    // Publish the key whole or not at all. Creating the file and filling it
    // afterwards leaves a window in which `key.bin` exists and is empty, and a
    // racing first run reads those zero bytes and — correctly, given what it
    // sees — refuses to use the profile at all.
    match crate::store::atomic::write_new_atomic(key_path, &key)? {
        Publication::Created => {
            set_owner_only_file(key_path)?;
            Ok(key)
        }
        // Someone else published first. Theirs is the key for this directory:
        // ours was never visible, and replacing theirs would make every
        // credential they are about to write unreadable.
        Publication::AlreadyExisted => read_existing_key(key_path),
    }
}

/// Reads a key that is already published.
///
/// Anything that is not a 32-byte key is refused outright — no retry, no
/// regeneration. The bytes are left exactly as they are: they may be the only
/// way back to the credentials beside them.
fn read_existing_key(key_path: &Path) -> Result<[u8; KEY_LEN], AuthError> {
    let bytes = std::fs::read(key_path)?;
    if bytes.len() != KEY_LEN {
        return Err(AuthError::incompatible(
            key_path,
            format!(
                "expected a {KEY_LEN}-byte key but found {} bytes; this key was not \
                 written by this build (the .NET store wraps its key with DPAPI on \
                 Windows). Nothing was changed; keep this file — it may be needed to \
                 recover the credentials beside it",
                bytes.len()
            ),
        ));
    }
    let mut key = [0u8; KEY_LEN];
    key.copy_from_slice(&bytes);
    Ok(key)
}

/// The first `*.cred` file in `directory`, if any.
fn first_credential_file(directory: &Path) -> Result<Option<PathBuf>, AuthError> {
    let entries = match std::fs::read_dir(directory) {
        Ok(entries) => entries,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(AuthError::Io(e)),
    };
    for entry in entries {
        let path = entry?.path();
        if path.extension().and_then(|e| e.to_str()) == Some(CRED_EXT) {
            return Ok(Some(path));
        }
    }
    Ok(None)
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

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn a_racing_creator_adopts_a_key_even_after_the_winner_writes_a_credential() {
        let dir = tempfile::tempdir().unwrap();
        let winner = EncryptedFileStore::new(dir.path()).unwrap();
        winner.set("llmauth:claude-ai", "test credential").await.unwrap();
        // The other opener observed absence before the winner ran, so it is
        // already in the create/adopt path rather than the initial fast read.
        let key = create_or_adopt_key(dir.path(), &dir.path().join(KEY_FILE)).unwrap();
        assert_eq!(key.as_slice(), std::fs::read(dir.path().join(KEY_FILE)).unwrap());
        assert_eq!(winner.get("llmauth:claude-ai").await.unwrap().as_deref(), Some("test credential"));
    }

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

    /// A rotated or replaced key means the credential is unreadable, not
    /// absent: answering `None` would hand the caller a "log in again" that
    /// overwrites a file the real key still opens.
    #[tokio::test]
    async fn wrong_key_is_reported_as_undecryptable() {
        let dir = tempdir();
        let store1 = EncryptedFileStore::new(dir.clone()).expect("store1");
        store1.set("k", "v").await.expect("set");

        // Overwrite the key file to simulate a rotated key.
        let key_path = dir.join(KEY_FILE);
        let mut new_key = [0u8; KEY_LEN];
        OsRng.fill_bytes(&mut new_key);
        std::fs::write(&key_path, new_key).expect("overwrite key");

        let store2 = EncryptedFileStore::new(dir).expect("store2");
        let err = store2.get("k").await.expect_err("must not read as missing");
        assert!(matches!(err, AuthError::StoreUndecryptable { .. }), "got {err:?}");
    }

    #[tokio::test]
    async fn corrupt_file_is_reported_as_undecryptable() {
        let store = temp_store();
        let path = store.path_for("corrupt");
        // Write random garbage that is too short to even parse as nonce||tag||ct.
        tokio::fs::write(&path, b"this is garbage").await.expect("write");
        let err = store.get("corrupt").await.expect_err("must not read as missing");
        assert!(matches!(err, AuthError::StoreUndecryptable { .. }), "got {err:?}");
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

    /// Keys contain colons (`llmauth:<provider>`). The file name must follow
    /// the same rule .NET's `Path.GetInvalidFileNameChars()` gives on this
    /// platform, so the two builds address the same file.
    #[tokio::test]
    async fn a_key_containing_a_colon_uses_the_dotnet_file_name() {
        let store = temp_store();
        store
            .set("llmauth:claude-ai", "token-value-for-claude")
            .await
            .expect("set");

        let path = store.path_for("llmauth:claude-ai");
        assert!(path.exists(), "credential file must be created");
        let filename = path.file_name().unwrap().to_string_lossy().to_string();
        if cfg!(windows) {
            assert_eq!(filename, "llmauth_claude-ai.cred");
        } else {
            assert_eq!(filename, "llmauth:claude-ai.cred");
        }

        let got = store.get("llmauth:claude-ai").await.expect("get");
        assert_eq!(got.as_deref(), Some("token-value-for-claude"));
    }

    /// Compatibility reads cannot race a coordinated write by publishing an
    /// old credential from outside that write's critical section.
    #[tokio::test]
    async fn earlier_filename_reads_never_mutate_credentials() {
        let dir = tempdir();
        let store = EncryptedFileStore::new(dir.clone()).expect("store");
        let legacy = store.legacy_path_for("llmauth:claude ai").expect("distinct legacy name");

        // Write the value under the earlier name only.
        let cipher = store.encrypt(b"earlier-value").expect("encrypt");
        std::fs::write(&legacy, &cipher).expect("write legacy file");

        let read_only = store.read_only("llmauth:claude ai").await.expect("read only");
        assert_eq!(read_only.as_deref(), Some("earlier-value"));
        assert!(!store.path_for("llmauth:claude ai").exists(), "read_only wrote a credential");
        let got = store.get("llmauth:claude ai").await.expect("get");
        assert_eq!(got.as_deref(), Some("earlier-value"));
        assert!(!store.path_for("llmauth:claude ai").exists(), "get wrote a credential");
        assert_eq!(std::fs::read(&legacy).unwrap(), cipher);
    }

    #[tokio::test]
    async fn concurrent_earlier_filename_reads_succeed_and_cannot_resurrect_after_delete() {
        let directory = tempfile::tempdir().unwrap();
        let store = EncryptedFileStore::new(directory.path()).unwrap();
        let key = "llmauth:claude ai";
        let legacy = store.legacy_path_for(key).expect("distinct legacy name");
        let bytes = store.encrypt(b"earlier-value").unwrap();
        std::fs::write(&legacy, &bytes).unwrap();
        let start = std::sync::Arc::new(tokio::sync::Barrier::new(17));
        let mut reads = tokio::task::JoinSet::new();
        for _ in 0..16 {
            let reader = EncryptedFileStore::new(directory.path()).unwrap();
            let start = start.clone();
            reads.spawn(async move {
                start.wait().await;
                reader.get(key).await
            });
        }
        start.wait().await;
        while let Some(read) = reads.join_next().await {
            assert_eq!(read.unwrap().unwrap().as_deref(), Some("earlier-value"));
        }
        assert_eq!(std::fs::read(&legacy).unwrap(), bytes);
        assert!(!store.path_for(key).exists());
        store.set(key, "replacement").await.unwrap();
        assert_eq!(store.get(key).await.unwrap().as_deref(), Some("replacement"));
        store.delete(key).await.unwrap();
        assert!(store.get(key).await.unwrap().is_none());
        assert!(!legacy.exists());
    }

    /// After a logout no copy may remain under the earlier name, or the next
    /// read resurrects the credential the user just removed.
    #[tokio::test]
    async fn delete_removes_the_earlier_rust_file_too() {
        let dir = tempdir();
        let store = EncryptedFileStore::new(dir).expect("store");
        let Some(legacy) = store.legacy_path_for("llmauth:claude ai") else {
            return;
        };
        let cipher = store.encrypt(b"earlier-value").expect("encrypt");
        std::fs::write(&legacy, cipher).expect("write legacy file");

        store.delete("llmauth:claude ai").await.expect("delete");
        assert!(!legacy.exists(), "logout must remove the earlier copy");
        assert!(store.get("llmauth:claude ai").await.expect("get").is_none());
    }

    /// A `key.bin` this build did not write (the .NET store DPAPI-wraps its
    /// key on Windows) must be refused, not regenerated: regenerating it makes
    /// every credential in the directory permanently unreadable.
    #[tokio::test]
    async fn a_foreign_key_file_is_refused_and_left_untouched() {
        let dir = tempdir();
        let key_path = dir.join(KEY_FILE);
        let original = vec![1u8, 2, 3, 4, 5];
        std::fs::write(&key_path, &original).expect("write foreign key");

        let err = EncryptedFileStore::new(dir).expect_err("must refuse a foreign key");
        assert!(matches!(err, AuthError::StoreIncompatible { .. }), "got {err:?}");
        assert_eq!(
            std::fs::read(&key_path).expect("key still there"),
            original,
            "the foreign key must be byte-identical"
        );
    }
}
