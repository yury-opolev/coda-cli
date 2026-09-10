//! Coordination for credential writes.
//!
//! # The window that needs it
//!
//! Refreshing a token takes a network round trip. During it, the credential
//! the refresh is based on can be deleted (the user logs out) or replaced (a
//! different account signs in, another engine process refreshes first). A
//! refresh that persists its result blindly afterwards signs the user back in
//! with a revoked token, or rolls a newer account back to an older one.
//!
//! Two things prevent that, and both are needed:
//!
//! * **A commit section.** Read-check-write happens while holding the key's
//!   commit lock, so two writers cannot interleave inside it.
//! * **Compare-and-set.** Entering the section is not enough: the refresh must
//!   still verify that what is stored is the value it started from, because
//!   the competing write may have completed *before* it got there.
//!
//! # Scope
//!
//! Coordination is cooperative. It covers every party that goes through this
//! crate for the same profile — several managers in one process, and several
//! processes sharing one profile directory ([`FileCoordinator`]). It does not
//! cover a process that writes the credential files directly, an older build
//! that predates this coordination, or a token revoked at the provider. No
//! process is ever killed or fenced out.
//!
//! # No network under a lock
//!
//! A commit section is only ever held around local reads and writes. The
//! network refresh happens outside it, so a slow or hung provider cannot block
//! a logout, and a nested store operation cannot deadlock against its own
//! commit lock.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use async_trait::async_trait;

use crate::error::AuthError;

/// The single commit section every credential mutation takes.
///
/// One gate for the whole profile, not one per provider. A provider switch
/// writes one key and deletes the others, so per-key locking lets a refresh of
/// the provider being replaced sit between its own read and write while the
/// switch runs — and the replaced account comes back. Different managers
/// register different providers, so nothing per-provider could be shared
/// between them anyway; the profile is the thing they have in common.
///
/// Contention is not a concern: no network call ever happens inside a commit
/// section, only local reads and writes.
pub const AUTH_COMMIT_KEY: &str = "llmauth";

/// Exclusive access to one key's commit section; released when dropped —
/// including when the awaiting task is cancelled.
pub struct CommitGuard {
    _release: Box<dyn Send>,
}

impl std::fmt::Debug for CommitGuard {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("CommitGuard")
    }
}

/// Serializes the commit sections of everyone sharing a profile.
#[async_trait]
pub trait CommitCoordinator: Send + Sync + std::fmt::Debug {
    /// Waits until the commit section for `key` is exclusively held.
    async fn begin(&self, key: &str) -> Result<CommitGuard, AuthError>;
}

/// Coordination within this process only.
///
/// Correct when the store itself is process-local (in-memory stores, tests).
/// It knows nothing about other processes, and must not be used to guard a
/// store that another process can also write.
#[derive(Debug, Default)]
pub struct LocalCoordinator {
    gates: Mutex<HashMap<String, Arc<tokio::sync::Mutex<()>>>>,
}

impl LocalCoordinator {
    pub fn new() -> Self {
        Self::default()
    }

    fn gate(&self, key: &str) -> Arc<tokio::sync::Mutex<()>> {
        let mut gates = self.gates.lock().expect("coordination map is never poisoned");
        Arc::clone(gates.entry(key.to_owned()).or_default())
    }
}

#[async_trait]
impl CommitCoordinator for LocalCoordinator {
    async fn begin(&self, key: &str) -> Result<CommitGuard, AuthError> {
        let guard = self.gate(key).lock_owned().await;
        Ok(CommitGuard { _release: Box::new(guard) })
    }
}

/// Coordination across every process sharing a profile directory.
///
/// Uses one advisory lock file per credential key next to the credentials
/// themselves, so two engine processes — or the TUI and a headless run —
/// serialize their commit sections. An in-process gate is taken first so that
/// tasks in this process queue in the runtime rather than piling up blocking
/// threads on the same file.
#[derive(Debug)]
pub struct FileCoordinator {
    directory: PathBuf,
    local: LocalCoordinator,
}

impl FileCoordinator {
    /// Coordinates on lock files kept in `directory` (the profile's credential
    /// directory).
    pub fn new(directory: impl Into<PathBuf>) -> Self {
        Self { directory: directory.into(), local: LocalCoordinator::new() }
    }

    /// The lock file for `key`. Named `.lock`, never `.cred`, so it is never
    /// mistaken for a credential by anything that scans the directory.
    fn lock_path(&self, key: &str) -> PathBuf {
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
        self.directory.join(format!("{safe}.lock"))
    }
}

#[async_trait]
impl CommitCoordinator for FileCoordinator {
    async fn begin(&self, key: &str) -> Result<CommitGuard, AuthError> {
        let local = self.local.begin(key).await?;

        std::fs::create_dir_all(&self.directory).map_err(|e| {
            AuthError::store(format!(
                "cannot create the credential directory {}: {e}",
                self.directory.display()
            ))
        })?;
        let path = self.lock_path(key);

        // The blocking wait runs off-runtime. If the caller is cancelled while
        // waiting, this task still finishes and drops the file, which releases
        // the lock — and the caller never reaches its write.
        let file = tokio::task::spawn_blocking(move || lock_exclusive(&path))
            .await
            .map_err(|e| AuthError::store(format!("credential lock task failed: {e}")))??;

        Ok(CommitGuard { _release: Box::new((local, file)) })
    }
}

/// Opens `path` and takes the exclusive advisory lock, blocking until it is
/// available. Dropping the returned file releases it.
fn lock_exclusive(path: &Path) -> Result<std::fs::File, AuthError> {
    use fs2::FileExt;

    let file = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(path)
        .map_err(|e| {
            AuthError::store(format!("cannot open the credential lock {}: {e}", path.display()))
        })?;
    file.lock_exclusive().map_err(|e| {
        AuthError::store(format!("cannot lock {} for writing: {e}", path.display()))
    })?;
    Ok(file)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    /// Two holders of the same key must not be inside the section together.
    #[tokio::test]
    async fn a_local_section_is_exclusive_per_key() {
        let coordinator = Arc::new(LocalCoordinator::new());
        let inside = Arc::new(AtomicUsize::new(0));
        let peak = Arc::new(AtomicUsize::new(0));

        let tasks: Vec<_> = (0..8)
            .map(|_| {
                let coordinator = Arc::clone(&coordinator);
                let inside = Arc::clone(&inside);
                let peak = Arc::clone(&peak);
                tokio::spawn(async move {
                    let _guard = coordinator.begin("llmauth:claude-ai").await.unwrap();
                    let now = inside.fetch_add(1, Ordering::SeqCst) + 1;
                    peak.fetch_max(now, Ordering::SeqCst);
                    tokio::task::yield_now().await;
                    inside.fetch_sub(1, Ordering::SeqCst);
                })
            })
            .collect();
        for task in tasks {
            task.await.unwrap();
        }
        assert_eq!(peak.load(Ordering::SeqCst), 1, "the section must be exclusive");
    }

    /// Different keys must not queue behind each other.
    #[tokio::test]
    async fn different_keys_do_not_block_each_other() {
        let coordinator = LocalCoordinator::new();
        let _first = coordinator.begin("llmauth:claude-ai").await.unwrap();
        tokio::time::timeout(
            std::time::Duration::from_secs(5),
            coordinator.begin("llmauth:github-copilot"),
        )
        .await
        .expect("a different key must not wait")
        .expect("begin");
    }

    #[tokio::test]
    async fn a_file_section_is_released_when_the_guard_is_dropped() {
        let dir = std::env::temp_dir().join(format!("coda-lock-{}", std::process::id()));
        let coordinator = FileCoordinator::new(&dir);

        let guard = coordinator.begin("llmauth:claude-ai").await.expect("first");
        drop(guard);

        tokio::time::timeout(
            std::time::Duration::from_secs(5),
            coordinator.begin("llmauth:claude-ai"),
        )
        .await
        .expect("the released section must be available")
        .expect("second");

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// The lock file must not look like a credential to anything scanning the
    /// credential directory.
    #[test]
    fn the_lock_file_is_not_a_credential_file() {
        let coordinator = FileCoordinator::new("/creds");
        let path = coordinator.lock_path("llmauth:claude-ai");
        assert_eq!(path.extension().and_then(|e| e.to_str()), Some("lock"));
    }
}
