//! Task 1 — a credential store must never quietly lose or damage what it
//! cannot read.
//!
//! These tests exercise the storage backends directly, with no factory
//! involved, and they are the ones that catch the failure mode the product
//! actually suffers from: a store that answers "no credential" (or silently
//! rewrites a key) when the truth is "I could not read this".
//!
//! No real credential, keyring entry, or network call is involved.

use coda_auth::error::AuthError;
use coda_auth::store::{CredentialStore, EncryptedFileStore};

/// A key file that is not a 32-byte raw key belongs to something else — the
/// .NET Windows file store DPAPI-wraps its key, and a corrupt key may still be
/// recoverable from a backup. Regenerating over it destroys every credential
/// in that directory, so the store must refuse and leave it byte-identical.
#[test]
fn an_invalid_key_file_is_rejected_and_never_overwritten() {
    let dir = tempfile::tempdir().expect("tempdir");
    let key_path = dir.path().join("key.bin");
    let original = vec![7u8; 48];
    std::fs::write(&key_path, &original).expect("write key");

    let err = EncryptedFileStore::new(dir.path()).expect_err("a foreign key must be rejected");
    assert!(
        matches!(err, AuthError::StoreIncompatible { .. }),
        "expected an incompatible-storage error, got {err:?}"
    );
    assert_eq!(
        std::fs::read(&key_path).expect("key still readable"),
        original,
        "the existing key file must be byte-identical after a refused open"
    );
}

/// A directory holding credential files but no key of our own is in a format
/// this build did not write — DPAPI files, for instance. Opening the encrypted
/// store there must be refused *before* any write, and must not drop a key
/// into a directory it does not own.
#[test]
fn a_credential_directory_in_an_unknown_format_is_refused_before_writing() {
    let dir = tempfile::tempdir().expect("tempdir");
    let foreign = dir.path().join("llmauth_claude-ai.cred");
    let bytes = b"\x01\x00\x00\x00\xd0\x8c\x9d\xdf-foreign-blob".to_vec();
    std::fs::write(&foreign, &bytes).expect("write foreign credential");

    let err = EncryptedFileStore::new(dir.path())
        .expect_err("an unknown-format directory must be refused");
    assert!(
        matches!(err, AuthError::StoreIncompatible { .. }),
        "expected an incompatible-storage error, got {err:?}"
    );
    assert_eq!(
        std::fs::read(&foreign).expect("credential still readable"),
        bytes,
        "the foreign credential must be byte-identical after a refused open"
    );
    assert!(
        !dir.path().join("key.bin").exists(),
        "a refused open must not create a key in a directory it does not own"
    );
}

/// A credential that exists but does not decrypt is *not* a missing
/// credential. Reporting it as missing hides key rotation and cross-user
/// copies behind a "please log in again", and the next login overwrites the
/// file that was still recoverable.
#[tokio::test]
async fn an_undecryptable_credential_is_an_error_not_a_missing_credential() {
    let dir = tempfile::tempdir().expect("tempdir");
    let store = EncryptedFileStore::new(dir.path()).expect("open");
    store.set("llmauth:claude-ai", "token-blob").await.expect("set");

    let cred_path = credential_path(dir.path(), "llmauth:claude-ai");
    let before = std::fs::read(&cred_path).expect("credential file exists");

    // Rotate the key underneath the credential, as a restored backup or a
    // second machine would.
    let mut rotated = [0u8; 32];
    rotated[0] = 1;
    std::fs::write(dir.path().join("key.bin"), rotated).expect("rotate key");

    let reopened = EncryptedFileStore::new(dir.path()).expect("reopen");
    let err = reopened
        .get("llmauth:claude-ai")
        .await
        .expect_err("an undecryptable credential must not read as missing");
    assert!(
        matches!(err, AuthError::StoreUndecryptable { .. }),
        "expected an undecryptable-credential error, got {err:?}"
    );
    assert_eq!(
        std::fs::read(&cred_path).expect("credential file still there"),
        before,
        "a failed read must leave the credential byte-identical"
    );
}

/// Truncated or garbage ciphertext is corruption, not absence.
#[tokio::test]
async fn a_corrupt_credential_file_is_an_error_not_a_missing_credential() {
    let dir = tempfile::tempdir().expect("tempdir");
    let store = EncryptedFileStore::new(dir.path()).expect("open");
    store.set("llmauth:claude-ai", "token-blob").await.expect("set");

    let cred_path = credential_path(dir.path(), "llmauth:claude-ai");
    std::fs::write(&cred_path, b"garbage").expect("corrupt the file");

    let err = store
        .get("llmauth:claude-ai")
        .await
        .expect_err("a corrupt credential must not read as missing");
    assert!(
        matches!(err, AuthError::StoreUndecryptable { .. }),
        "expected an undecryptable-credential error, got {err:?}"
    );
    assert_eq!(
        std::fs::read(&cred_path).expect("file still there"),
        b"garbage",
        "a failed read must not rewrite the file"
    );
}

/// The .NET file store keeps the key-file layout we share, so the credential
/// file name must follow the same platform rule it does
/// (`Path.GetInvalidFileNameChars`): a colon is illegal on Windows but
/// perfectly legal on Unix, where .NET keeps it.
#[tokio::test]
async fn the_credential_file_name_matches_the_dotnet_file_store() {
    let dir = tempfile::tempdir().expect("tempdir");
    let store = EncryptedFileStore::new(dir.path()).expect("open");
    store.set("llmauth:claude-ai", "token-blob").await.expect("set");

    let expected = credential_path(dir.path(), "llmauth:claude-ai");
    assert!(
        expected.is_file(),
        "expected the .NET-compatible file name at {}",
        expected.display()
    );
}

/// A read that fails because the file cannot be opened (a sharing violation, a
/// permission change, a broken mount) must surface as an error. Mapping it to
/// "no credential" makes a working install look logged out and invites the
/// caller to overwrite it.
#[cfg(windows)]
#[tokio::test]
async fn an_unreadable_credential_file_is_an_error_not_a_missing_credential() {
    use std::os::windows::fs::OpenOptionsExt;

    let dir = tempfile::tempdir().expect("tempdir");
    let store = coda_auth::DpapiStore::with_directory(dir.path());
    store.set("llmauth:claude-ai", "value").await.expect("set");

    let path = dir.path().join("llmauth_claude-ai.cred");
    let before = std::fs::read(&path).expect("credential written");

    // Hold the file open with no sharing so any other read fails with an I/O
    // error rather than "not found".
    let exclusive = std::fs::OpenOptions::new()
        .read(true)
        .share_mode(0)
        .open(&path)
        .expect("exclusive open");

    let result = store.get("llmauth:claude-ai").await;

    drop(exclusive);
    let err = result.expect_err("an unreadable credential must not read as missing");
    assert!(
        !matches!(err, AuthError::NotFound(_)),
        "an I/O failure must not be reported as a missing credential: {err:?}"
    );
    assert_eq!(
        std::fs::read(&path).expect("credential still readable"),
        before,
        "a failed read must leave the credential byte-identical"
    );
}

// ── The store key is published whole or not at all ───────────────────────────

/// Several first runs starting at once — two engine processes, a TUI and an
/// engine, a test harness — must all end up with the *same* usable key, and
/// none of them may fail.
///
/// Creating the file and then filling it leaves a moment where `key.bin`
/// exists and is empty. A racing factory reads those zero bytes, correctly
/// refuses to treat them as a key, and the loser of the race is told its
/// profile is unusable — on a brand-new profile.
#[test]
fn concurrent_first_runs_all_get_the_same_usable_key() {
    const THREADS: usize = 8;
    const ROUNDS: usize = 40;

    for round in 0..ROUNDS {
        let dir = tempfile::tempdir().expect("tempdir");
        let barrier = std::sync::Arc::new(std::sync::Barrier::new(THREADS));

        let opened: Vec<_> = (0..THREADS)
            .map(|thread| {
                let path = dir.path().to_path_buf();
                let barrier = std::sync::Arc::clone(&barrier);
                std::thread::spawn(move || {
                    // Every thread arrives at `new` at the same moment.
                    barrier.wait();
                    let store = EncryptedFileStore::new(&path)
                        .unwrap_or_else(|e| panic!("round {round}: opening a fresh profile must not fail: {e}"));
                    // Prove the key works, from this thread's own store.
                    let key = format!("llmauth:claude-ai-{thread}");
                    let value = format!("value-{thread}");
                    futures_lite_block_on(store.set(&key, &value)).expect("set");
                    store
                })
            })
            .collect();

        let stores: Vec<_> = opened.into_iter().map(|t| t.join().expect("thread")).collect();

        // One key, shared by all of them: every store must read what every
        // other store wrote.
        for (thread, store) in stores.iter().enumerate() {
            for other in 0..THREADS {
                let got = futures_lite_block_on(store.get(&format!("llmauth:claude-ai-{other}")))
                    .unwrap_or_else(|e| {
                        panic!("round {round}: store {thread} could not read store {other}: {e}")
                    });
                assert_eq!(
                    got.as_deref(),
                    Some(format!("value-{other}").as_str()),
                    "round {round}: every store must share one key"
                );
            }
        }

        let key_bytes = std::fs::read(dir.path().join("key.bin")).expect("the key must exist");
        assert_eq!(key_bytes.len(), 32, "round {round}: the published key must be complete");
        assert!(
            leftover_scratch_files(dir.path()).is_empty(),
            "round {round}: a lost race must not leave scratch files: {:?}",
            leftover_scratch_files(dir.path())
        );
    }
}

/// The same race seen through the factory, which inspects the directory
/// *before* opening the store: a key that is momentarily empty would be
/// reported as an unusable profile there too, so serialising the open alone
/// would not be enough.
#[test]
fn concurrent_factory_opens_never_see_a_half_written_key() {
    const THREADS: usize = 8;
    const ROUNDS: usize = 40;
    // The AES store is the platform default off Windows; on Windows it is the
    // separated backend, which is where a key file lives there.
    let backend = if cfg!(windows) { Some("file") } else { None };

    for round in 0..ROUNDS {
        let dir = tempfile::tempdir().expect("tempdir");
        let barrier = std::sync::Arc::new(std::sync::Barrier::new(THREADS));

        let threads: Vec<_> = (0..THREADS)
            .map(|_| {
                let path = dir.path().to_path_buf();
                let barrier = std::sync::Arc::clone(&barrier);
                std::thread::spawn(move || {
                    barrier.wait();
                    coda_auth::store::open_profile_storage_with(
                        &coda_auth::store::Profile::isolated(&path),
                        backend,
                    )
                    .map(|storage| storage.primary_dir)
                })
            })
            .collect();

        for thread in threads {
            thread
                .join()
                .expect("thread")
                .unwrap_or_else(|e| panic!("round {round}: a fresh profile must open: {e}"));
        }
    }
}

/// Runs a future to completion on the current thread.
///
/// The store API is async but its file work is not; these race tests need real
/// OS threads, so each one drives its own future rather than sharing a runtime.
fn futures_lite_block_on<F: std::future::Future>(future: F) -> F::Output {
    tokio::runtime::Builder::new_current_thread()
        .build()
        .expect("runtime")
        .block_on(future)
}

/// The path the .NET `FileTokenStore` would use for `key`, per
/// `Path.GetInvalidFileNameChars()` on this platform.
fn credential_path(dir: &std::path::Path, key: &str) -> std::path::PathBuf {
    let name = if cfg!(windows) {
        key.replace(':', "_")
    } else {
        key.to_owned()
    };
    dir.join(format!("{name}.cred"))
}

// ── Known-answer compatibility with the .NET FileTokenStore ──────────────────

/// The fixture vectors, produced by .NET's `AesGcm` with the primitives
/// `LlmAuth.FileTokenStore` uses. Reading the file the .NET build would have
/// written is the only way to know the two are actually interoperable —
/// a Rust-writes-Rust-reads round trip proves nothing about .NET.
fn dotnet_fixture() -> serde_json::Value {
    let raw = include_str!("fixtures/dotnet_file_token_store.json");
    serde_json::from_str(raw).expect("the fixture must be valid JSON")
}

fn from_hex(hex: &str) -> Vec<u8> {
    (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).expect("hex"))
        .collect()
}

/// A credential file written by the .NET `FileTokenStore` must decrypt in the
/// Rust store, byte for byte, including non-ASCII secrets.
#[tokio::test]
async fn a_dotnet_written_credential_file_decrypts_in_the_rust_store() {
    let fixture = dotnet_fixture();
    let dir = tempfile::tempdir().expect("tempdir");
    std::fs::write(dir.path().join("key.bin"), from_hex(fixture["key"].as_str().unwrap()))
        .expect("write the .NET key");

    let store = EncryptedFileStore::new(dir.path()).expect("open over a .NET store");

    for sample in fixture["samples"].as_array().expect("samples") {
        let name = sample["name"].as_str().unwrap();
        let key = format!("llmauth:{name}");
        std::fs::write(
            credential_path(dir.path(), &key),
            from_hex(sample["file"].as_str().unwrap()),
        )
        .expect("write the .NET credential file");

        let got = store.get(&key).await.expect("read the .NET credential");
        assert_eq!(
            got.as_deref(),
            Some(sample["plaintext"].as_str().unwrap()),
            "sample '{name}' must decrypt to the .NET plaintext"
        );
    }
}

/// The file-name rule must follow `Path.GetInvalidFileNameChars()` for every
/// character, not just the colon: .NET keeps Unicode and spaces verbatim on
/// both platforms, and only Windows rejects the punctuation set.
#[tokio::test]
async fn the_file_name_policy_matches_dotnet_for_unicode_and_punctuation() {
    let dir = tempfile::tempdir().expect("tempdir");
    let store = EncryptedFileStore::new(dir.path()).expect("open");

    let key = "llmauth:clé-你好 server";
    store.set(key, "value").await.expect("set");

    let expected = credential_path(dir.path(), key);
    assert!(
        expected.is_file(),
        "expected the .NET-compatible file name at {}",
        expected.display()
    );
    assert_eq!(store.get(key).await.expect("get").as_deref(), Some("value"));
}

// ── Writes must never be observable half-done ────────────────────────────────

/// A reader running alongside a writer must always see a whole credential —
/// the previous one or the new one. A truncate-in-place write exposes an empty
/// or half-written file, which a strict reader correctly reports as corrupt,
/// so a background refresh would make an unrelated read fail.
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn a_concurrent_reader_never_sees_a_partial_credential() {
    let dir = tempfile::tempdir().expect("tempdir");
    let store = std::sync::Arc::new(EncryptedFileStore::new(dir.path()).expect("open"));
    let key = "llmauth:claude-ai";

    let short = "{\"providerId\":\"claude-ai\"}";
    let long = format!("{{\"providerId\":\"claude-ai\",\"accessToken\":\"{}\"}}", "x".repeat(4096));
    store.set(key, short).await.expect("seed");

    let writer = {
        let store = std::sync::Arc::clone(&store);
        let long = long.clone();
        tokio::spawn(async move {
            for i in 0..300 {
                let value = if i % 2 == 0 { long.clone() } else { short.to_owned() };
                store.set(key, &value).await.expect("write");
                tokio::task::yield_now().await;
            }
        })
    };

    let reader = {
        let store = std::sync::Arc::clone(&store);
        tokio::spawn(async move {
            for _ in 0..3000 {
                match store.get(key).await {
                    Ok(Some(value)) => assert!(
                        value.starts_with("{\"providerId\":\"claude-ai\""),
                        "a reader must never see a partial credential; got {} bytes",
                        value.len()
                    ),
                    Ok(None) => panic!("the credential must never disappear during a write"),
                    Err(e) => panic!("a concurrent read must not fail: {e}"),
                }
                tokio::task::yield_now().await;
            }
        })
    };

    writer.await.expect("writer");
    reader.await.expect("reader");
}

/// A write that cannot complete must leave the previous credential exactly as
/// it was — losing it would sign the user out with nothing to fall back on —
/// and must not leave scratch files behind.
#[tokio::test]
async fn a_failed_write_leaves_the_previous_credential_byte_identical() {
    let dir = tempfile::tempdir().expect("tempdir");
    let store = EncryptedFileStore::new(dir.path()).expect("open");
    let key = "llmauth:claude-ai";
    store.set(key, "the-original-value").await.expect("seed");

    let path = credential_path(dir.path(), key);
    let before = std::fs::read(&path).expect("read the original");
    let guard = deny_writes(&path);

    let result = store.set(key, "the-replacement-value").await;

    drop(guard);
    assert!(result.is_err(), "a write that cannot be applied must report failure");
    assert_eq!(
        std::fs::read(&path).expect("the original must still be there"),
        before,
        "a failed write must leave the previous credential byte-identical"
    );
    assert_eq!(
        store.get(key).await.expect("read").as_deref(),
        Some("the-original-value"),
        "the previous credential must still be readable after a failed write"
    );
    assert!(
        leftover_scratch_files(dir.path()).is_empty(),
        "a failed write must not leave scratch files: {:?}",
        leftover_scratch_files(dir.path())
    );
}

/// A successful write must not leave scratch files either.
#[tokio::test]
async fn a_successful_write_leaves_no_scratch_files() {
    let dir = tempfile::tempdir().expect("tempdir");
    let store = EncryptedFileStore::new(dir.path()).expect("open");
    store.set("llmauth:claude-ai", "value").await.expect("set");
    store.set("llmauth:claude-ai", "value-2").await.expect("set again");

    assert!(
        leftover_scratch_files(dir.path()).is_empty(),
        "no scratch files may survive a write: {:?}",
        leftover_scratch_files(dir.path())
    );
}

/// Files in `dir` that are neither the key nor a credential.
fn leftover_scratch_files(dir: &std::path::Path) -> Vec<String> {
    std::fs::read_dir(dir)
        .expect("read dir")
        .filter_map(|e| e.ok())
        .map(|e| e.file_name().to_string_lossy().to_string())
        .filter(|name| name != "key.bin" && !name.ends_with(".cred"))
        .collect()
}

/// Makes `path` unwritable for the duration of the returned guard, so a write
/// through it fails the way a permission change or a locked file would.
fn deny_writes(path: &std::path::Path) -> WriteDenial {
    let previous = std::fs::metadata(path).expect("metadata").permissions();
    let mut denied = previous.clone();
    denied.set_readonly(true);
    std::fs::set_permissions(path, denied).expect("deny writes");
    WriteDenial { path: path.to_path_buf(), previous }
}

struct WriteDenial {
    path: std::path::PathBuf,
    previous: std::fs::Permissions,
}

impl Drop for WriteDenial {
    fn drop(&mut self) {
        let _ = std::fs::set_permissions(&self.path, self.previous.clone());
    }
}

