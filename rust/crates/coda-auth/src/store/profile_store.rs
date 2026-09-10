//! The store every reader of a profile shares: one primary, plus the sources
//! an earlier build may have left behind.
//!
//! Adoption exists so that upgrading the engine never presents an empty store
//! to a user who is signed in. Three rules make it safe:
//!
//! * it runs inside the profile's commit section and **rechecks** the primary
//!   after taking it, so a logout or a replacement that happened while the old
//!   copy was being read wins;
//! * the primary is written *before* the old copy is retired, so a crash in
//!   between leaves the credential readable from both, never from neither;
//! * a delete records persistent intent before removing credentials, so an
//!   unavailable legacy store cannot later resurrect a logged-out account.
//!
//! [`CredentialStore::read_only`] is the read that never migrates anything; it
//! is what a caller already holding the commit section must use, and it cannot
//! deadlock against that section.
//!
//! Legacy sources are *optional*. One that is simply not available on this
//! host - no Secret Service on a headless Linux box - is skipped with a
//! warning. One that exists but refuses access may be holding the user's
//! credential, so it is reported as an error rather than as "no credentials":
//! the alternative sends the user through a login that overwrites it.

use std::sync::Arc;

use async_trait::async_trait;
use base64::Engine;
use sha2::{Digest, Sha256};

use crate::coordination::{CommitCoordinator, AUTH_COMMIT_KEY};
use crate::error::AuthError;
use crate::store::CredentialStore;

/// Internal metadata, not a provider credential. Compound auth transactions
/// must preserve this key too if they roll back a provider replacement.
pub(crate) fn retirement_key(key: &str) -> String {
    format!(
        "coda-auth-retired:{}",
        base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(Sha256::digest(key.as_bytes()))
    )
}

/// A credential source an earlier build wrote.
pub(crate) struct LegacyEntry {
    pub(crate) store: Arc<dyn CredentialStore>,
    /// `true` when this source keeps its credentials at the very paths the
    /// primary writes (earlier Rust AES files inside the DPAPI directory).
    /// Writing the primary then already replaced them; deleting afterwards
    /// would delete what was just adopted.
    pub(crate) shares_primary_path: bool,
}

/// A profile's credential store: the primary backend, plus verified adoption
/// sources consulted only when the primary has nothing.
pub struct ProfileCredentialStore {
    primary: Arc<dyn CredentialStore>,
    legacy: Vec<LegacyEntry>,
    coordinator: Arc<dyn CommitCoordinator>,
}

impl ProfileCredentialStore {
    pub(crate) fn new(
        primary: Arc<dyn CredentialStore>,
        legacy: Vec<LegacyEntry>,
        coordinator: Arc<dyn CommitCoordinator>,
    ) -> Self {
        Self { primary, legacy, coordinator }
    }

    /// A profile store with a single primary backend and no adoption sources.
    ///
    /// This is what an isolated profile resolves to once there is nothing left
    /// to adopt, and it is the shape tests use so they exercise the real
    /// retirement and commit behaviour over an injected primary rather than a
    /// stand-in that has neither.
    pub fn with_primary(
        primary: Arc<dyn CredentialStore>,
        coordinator: Arc<dyn CommitCoordinator>,
    ) -> Self {
        Self::new(primary, Vec::new(), coordinator)
    }

    // ── Primary-only operations for compound transactions ────────────────────
    //
    // A login is one transaction over several keys plus the settings file. If
    // any step fails, the profile has to go back to exactly what it was —
    // including which credentials were *absent* and which had a retirement
    // marker. The ordinary `set`/`delete` pair cannot express that: `delete`
    // publishes a marker (correct for a logout, wrong for a rollback), and
    // nothing clears one. These three do, and they touch the primary only,
    // because a rollback restores what this transaction changed and must not
    // start writing into an adoption source it never wrote to.
    //
    // None of them takes the commit section: the transaction that calls them
    // is already inside it, and taking it again would deadlock.

    /// Put `key` back to `value` (or back to absent), writing no retirement
    /// metadata.
    ///
    /// The write goes to the **primary** backend, which is where every reader
    /// of this profile looks. That is the restoration a rollback needs: the
    /// same credential, readable again, with no retirement state invented. It
    /// deliberately does not try to put a value back into a legacy source it
    /// may have been adopted from — the transaction never wrote there, and an
    /// adoption source can be read-only or unavailable.
    pub async fn restore_primary(
        &self,
        key: &str,
        value: Option<&str>,
    ) -> Result<(), AuthError> {
        match value {
            Some(value) => self.primary.set(key, value).await,
            None => self.primary.delete(key).await,
        }
    }

    /// Whether `key` carries a retirement marker.
    pub async fn is_retired(&self, key: &str) -> Result<bool, AuthError> {
        self.legacy_retired(key).await
    }

    /// Set or clear `key`'s retirement marker.
    ///
    /// Clearing goes straight to the primary: routing it through
    /// [`CredentialStore::delete`] would publish a marker *about the marker*,
    /// and that meta-marker would then be the thing nothing could clear.
    pub async fn set_retired(&self, key: &str, retired: bool) -> Result<(), AuthError> {
        let marker = retirement_key(key);
        if retired {
            self.primary.set(&marker, "1").await
        } else {
            self.primary.delete(&marker).await
        }
    }

    /// The coordination this store commits under - the same one a
    /// [`CredentialManager`][crate::manager::CredentialManager] built from the
    /// profile uses, so a migration and a logout cannot interleave.
    pub fn coordinator(&self) -> &Arc<dyn CommitCoordinator> {
        &self.coordinator
    }

    /// Whether a primary read failure should be answered by the legacy
    /// sources rather than propagated.
    ///
    /// Only an *undecryptable* primary entry qualifies, and only when there is
    /// somewhere else to look: on Windows the earlier Rust AES files occupy
    /// the same paths as DPAPI credentials, so "this is not DPAPI ciphertext"
    /// is exactly the signal that the file belongs to the older backend.
    /// Without a legacy source the error is the answer.
    fn may_fall_back(&self, error: &AuthError) -> bool {
        !self.legacy.is_empty() && matches!(error, AuthError::StoreUndecryptable { .. })
    }

    async fn legacy_retired(&self, key: &str) -> Result<bool, AuthError> {
        self.primary.get(&retirement_key(key)).await.map(|value| value.is_some())
    }

    /// Reads `key` from one legacy source, applying the availability rule.
    ///
    /// A source that is not available on this host is skipped with a warning
    /// and reads as "nothing here"; every other failure is reported, because a
    /// source that exists but will not answer may be holding the credential.
    async fn read_entry(
        &self,
        entry: &LegacyEntry,
        key: &str,
    ) -> Result<Option<String>, AuthError> {
        match entry.store.get(key).await {
            Ok(value) => Ok(value),
            Err(AuthError::StoreUnavailable { backend, detail }) => {
                tracing::warn!(
                    backend = %backend,
                    detail = %detail,
                    "an older credential store is not available on this host and was \
                     skipped; credentials saved there cannot be migrated until it can \
                     be read"
                );
                Ok(None)
            }
            Err(e) => Err(e),
        }
    }

    /// Reads `key` from the first legacy source that has it.
    async fn read_legacy(&self, key: &str) -> Result<Option<(&LegacyEntry, String)>, AuthError> {
        for entry in &self.legacy {
            if let Some(value) = self.read_entry(entry, key).await? {
                return Ok(Some((entry, value)));
            }
        }
        Ok(None)
    }
}

#[async_trait]
impl CredentialStore for ProfileCredentialStore {
    /// Reads `key`, migrating a credential an earlier build left behind.
    ///
    /// Must not be called while holding the profile's commit section - use
    /// [`CredentialStore::read_only`] there.
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
        let primary_error = match self.primary.get(key).await {
            Ok(Some(value)) => return Ok(Some(value)),
            Ok(None) => None,
            Err(e) if self.may_fall_back(&e) => Some(e),
            Err(e) => return Err(e),
        };
        if self.legacy_retired(key).await? {
            return primary_error.map_or(Ok(None), Err);
        }

        // The primary has nothing readable. Look for an older copy - this read
        // happens outside the commit section, so its result is only a
        // candidate.
        let Some((entry, _candidate)) = self.read_legacy(key).await? else {
            return match primary_error {
                // Nothing anywhere else, and the primary could not read what
                // it has: say so instead of reporting a credential that exists
                // as missing.
                Some(e) => Err(e),
                None => Ok(None),
            };
        };

        // Migrate inside the section a logout takes, and recheck: while the
        // old copy was being read the credential may have been replaced (that
        // value wins) or logged out (nothing may come back).
        let _commit = self.coordinator.begin(AUTH_COMMIT_KEY).await?;

        let current_error = match self.primary.get(key).await {
            Ok(Some(current)) => return Ok(Some(current)),
            Ok(None) => None,
            Err(e) if self.may_fall_back(&e) => Some(e),
            Err(e) => return Err(e),
        };
        if self.legacy_retired(key).await? {
            return current_error.map_or(Ok(None), Err);
        }

        let Some(value) = self.read_entry(entry, key).await? else {
            // Logged out — or the source went away — while the old copy was
            // being read: nothing may be written back on the strength of a
            // value we can no longer confirm.
            return current_error.map_or(Ok(None), Err);
        };

        // Write the primary first: until it succeeds the old copy is the only
        // one there is.
        self.primary.set(key, &value).await?;
        if !entry.shares_primary_path {
            entry.store.delete(key).await?;
        }
        Ok(Some(value))
    }

    /// Reads `key` from the primary, falling back to the legacy sources,
    /// without migrating anything between backends.
    ///
    /// Safe to call while holding the profile's commit section: neither the
    /// primary read nor the legacy fallback writes anything or acquires that
    /// section, so this cannot migrate credentials or deadlock.
    async fn read_only(&self, key: &str) -> Result<Option<String>, AuthError> {
        let primary_error = match self.primary.get(key).await {
            Ok(Some(value)) => return Ok(Some(value)),
            Ok(None) => None,
            Err(e) if self.may_fall_back(&e) => Some(e),
            Err(e) => return Err(e),
        };
        if self.legacy_retired(key).await? {
            return primary_error.map_or(Ok(None), Err);
        }

        match self.read_legacy(key).await? {
            Some((_, value)) => Ok(Some(value)),
            None => match primary_error {
                Some(e) => Err(e),
                None => Ok(None),
            },
        }
    }

    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
        self.primary.set(key, value).await
    }

    async fn delete(&self, key: &str) -> Result<(), AuthError> {
        // Publish intent first. A temporarily unavailable legacy source may
        // still hold a token; deleting the primary alone is not a lasting logout.
        self.primary.set(&retirement_key(key), "1").await?;
        // Every source is attempted even if one fails, so a partial failure
        // cannot leave a live copy behind unnoticed; the first error is
        // reported once all of them have been tried.
        let mut first_error = self.primary.delete(key).await.err();
        for entry in &self.legacy {
            if entry.shares_primary_path {
                continue;
            }
            match entry.store.delete(key).await {
                Ok(()) => {}
                // The persistent marker prevents later adoption if this
                // source recovers with an old copy still present.
                Err(AuthError::StoreUnavailable { backend, detail }) => {
                    tracing::warn!(
                        backend = %backend,
                        detail = %detail,
                        "an older credential store is not available on this host; nothing \
                         there could be removed"
                    );
                }
                Err(e) => {
                    first_error.get_or_insert(e);
                }
            }
        }
        match first_error {
            Some(e) => Err(e),
            None => Ok(()),
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use crate::coordination::LocalCoordinator;
    use crate::store::InMemoryStore;

    fn coordinator() -> Arc<dyn CommitCoordinator> {
        Arc::new(LocalCoordinator::new())
    }

    fn store_with_legacy() -> (Arc<InMemoryStore>, Arc<InMemoryStore>, Arc<ProfileCredentialStore>)
    {
        let primary = Arc::new(InMemoryStore::new());
        let legacy = Arc::new(InMemoryStore::new());
        let profile = Arc::new(ProfileCredentialStore::new(
            primary.clone(),
            vec![LegacyEntry { store: legacy.clone(), shares_primary_path: false }],
            coordinator(),
        ));
        (primary, legacy, profile)
    }

    struct RecoveringLegacy {
        inner: InMemoryStore,
        offline: std::sync::atomic::AtomicBool,
    }

    impl RecoveringLegacy {
        fn check(&self) -> Result<(), AuthError> {
            if self.offline.load(std::sync::atomic::Ordering::SeqCst) {
                Err(AuthError::StoreUnavailable {
                    backend: "test legacy".into(), detail: "temporarily offline".into(),
                })
            } else {
                Ok(())
            }
        }
    }

    #[async_trait]
    impl CredentialStore for RecoveringLegacy {
        async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
            self.check()?;
            self.inner.get(key).await
        }
        async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
            self.check()?;
            self.inner.set(key, value).await
        }
        async fn delete(&self, key: &str) -> Result<(), AuthError> {
            self.check()?;
            self.inner.delete(key).await
        }
    }

    #[tokio::test]
    async fn logout_does_not_revive_a_legacy_credential_when_its_store_recovers() {
        const KEY: &str = "llmauth:github-copilot";
        for primary_present in [false, true] {
            let dir = tempfile::tempdir().unwrap();
            let primary = Arc::new(crate::store::EncryptedFileStore::new(dir.path()).unwrap());
            if primary_present {
                primary.set(KEY, "current credential").await.unwrap();
            }
            let legacy = Arc::new(RecoveringLegacy {
                inner: InMemoryStore::new(),
                offline: std::sync::atomic::AtomicBool::new(true),
            });
            legacy.inner.set(KEY, "stale legacy credential").await.unwrap();
            let profile = ProfileCredentialStore::new(
                primary,
                vec![LegacyEntry { store: legacy.clone(), shares_primary_path: false }],
                coordinator(),
            );
            profile.delete(KEY).await.unwrap();
            drop(profile);

            legacy.offline.store(false, std::sync::atomic::Ordering::SeqCst);
            let reopened = ProfileCredentialStore::new(
                Arc::new(crate::store::EncryptedFileStore::new(dir.path()).unwrap()),
                vec![LegacyEntry { store: legacy.clone(), shares_primary_path: false }],
                coordinator(),
            );
            assert!(reopened.read_only(KEY).await.unwrap().is_none(), "primary_present={primary_present}");
            assert!(reopened.get(KEY).await.unwrap().is_none());
            assert!(legacy.inner.get(KEY).await.unwrap().is_some(), "the unavailable copy really remained");

            reopened.set(KEY, "newly authorized credential").await.unwrap();
            assert_eq!(
                reopened.get(KEY).await.unwrap().as_deref(), Some("newly authorized credential"),
                "suppression applies to legacy fallback, not a new explicit login"
            );
        }
    }

    /// A legacy source that always fails the same way.
    struct FailingStore {
        error: fn() -> AuthError,
        reads: std::sync::atomic::AtomicUsize,
    }

    impl FailingStore {
        fn new(error: fn() -> AuthError) -> Arc<Self> {
            Arc::new(Self { error, reads: std::sync::atomic::AtomicUsize::new(0) })
        }
        fn reads(&self) -> usize {
            self.reads.load(std::sync::atomic::Ordering::SeqCst)
        }
    }

    #[async_trait]
    impl CredentialStore for FailingStore {
        async fn get(&self, _key: &str) -> Result<Option<String>, AuthError> {
            self.reads.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            Err((self.error)())
        }
        async fn set(&self, _: &str, _: &str) -> Result<(), AuthError> {
            Err((self.error)())
        }
        async fn delete(&self, _: &str) -> Result<(), AuthError> {
            Err((self.error)())
        }
    }

    /// A legacy source whose *first* read blocks until the test releases it.
    struct BlockingStore {
        inner: Arc<InMemoryStore>,
        entered: tokio::sync::Semaphore,
        release: tokio::sync::Semaphore,
        blocked_once: std::sync::atomic::AtomicBool,
        delete_unavailable: std::sync::atomic::AtomicBool,
    }

    impl BlockingStore {
        fn new(inner: Arc<InMemoryStore>) -> Arc<Self> {
            Arc::new(Self {
                inner,
                entered: tokio::sync::Semaphore::new(0),
                release: tokio::sync::Semaphore::new(0),
                blocked_once: std::sync::atomic::AtomicBool::new(false),
                delete_unavailable: std::sync::atomic::AtomicBool::new(false),
            })
        }
        async fn wait_until_read(&self) {
            let permit = tokio::time::timeout(
                std::time::Duration::from_secs(10),
                self.entered.acquire(),
            )
            .await
            .expect("a legacy read should have started")
            .expect("open");
            permit.forget();
        }
        fn let_the_read_finish(&self) {
            self.release.add_permits(1);
        }
    }

    #[async_trait]
    impl CredentialStore for BlockingStore {
        async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
            let value = self.inner.get(key).await?;
            // Only the candidate read is held: the recheck inside the commit
            // section must be allowed to run, or the test would deadlock
            // instead of exercising the race.
            if !self.blocked_once.swap(true, std::sync::atomic::Ordering::SeqCst) {
                self.entered.add_permits(1);
                self.release.acquire().await.expect("open").forget();
            }
            Ok(value)
        }
        async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
            self.inner.set(key, value).await
        }
        async fn delete(&self, key: &str) -> Result<(), AuthError> {
            if self.delete_unavailable.load(std::sync::atomic::Ordering::SeqCst) {
                return Err(AuthError::StoreUnavailable {
                    backend: "test legacy".into(), detail: "temporarily offline".into(),
                });
            }
            self.inner.delete(key).await
        }
    }

    #[tokio::test]
    async fn an_in_flight_adoption_cannot_bypass_a_persisted_logout() {
        const KEY: &str = "llmauth:claude-ai";
        let primary = Arc::new(InMemoryStore::new());
        let legacy_inner = Arc::new(InMemoryStore::new());
        legacy_inner.set(KEY, "old credential").await.unwrap();
        let legacy = BlockingStore::new(legacy_inner);
        let profile = Arc::new(ProfileCredentialStore::new(
            primary.clone(),
            vec![LegacyEntry { store: legacy.clone(), shares_primary_path: false }],
            coordinator(),
        ));
        let reading = {
            let profile = profile.clone();
            tokio::spawn(async move { profile.get(KEY).await })
        };
        legacy.wait_until_read().await;
        legacy.delete_unavailable.store(true, std::sync::atomic::Ordering::SeqCst);
        profile.delete(KEY).await.unwrap();
        legacy.delete_unavailable.store(false, std::sync::atomic::Ordering::SeqCst);
        legacy.let_the_read_finish();
        assert!(reading.await.unwrap().unwrap().is_none());
        assert!(primary.get(KEY).await.unwrap().is_none());
    }

    #[tokio::test]
    async fn a_legacy_credential_is_adopted_into_the_primary() {
        let (primary, legacy, profile) = store_with_legacy();
        legacy.set("llmauth:claude-ai", "value").await.unwrap();

        assert_eq!(profile.get("llmauth:claude-ai").await.unwrap().as_deref(), Some("value"));
        assert_eq!(
            primary.get("llmauth:claude-ai").await.unwrap().as_deref(),
            Some("value"),
            "adoption must write the primary"
        );
        assert!(
            legacy.get("llmauth:claude-ai").await.unwrap().is_none(),
            "the adopted copy must be retired"
        );
    }

    #[tokio::test]
    async fn a_delete_reaches_every_source() {
        let (primary, legacy, profile) = store_with_legacy();
        primary.set("llmauth:claude-ai", "new").await.unwrap();
        legacy.set("llmauth:claude-ai", "old").await.unwrap();

        profile.delete("llmauth:claude-ai").await.unwrap();

        assert!(profile.get("llmauth:claude-ai").await.unwrap().is_none());
        assert!(legacy.get("llmauth:claude-ai").await.unwrap().is_none());
    }

    #[tokio::test]
    async fn the_primary_wins_over_a_stale_legacy_copy() {
        let (primary, legacy, profile) = store_with_legacy();
        primary.set("llmauth:claude-ai", "current").await.unwrap();
        legacy.set("llmauth:claude-ai", "stale").await.unwrap();

        assert_eq!(profile.get("llmauth:claude-ai").await.unwrap().as_deref(), Some("current"));
    }

    // ── Availability of optional legacy sources (finding 2) ──────────────────

    /// A machine with no usable keyring — headless Linux, a container, a
    /// locked-out service — must still hand out a working store. The legacy
    /// source is optional; the primary is authoritative.
    #[tokio::test]
    async fn an_unavailable_legacy_source_is_skipped_not_fatal() {
        let primary = Arc::new(InMemoryStore::new());
        let legacy = FailingStore::new(|| AuthError::StoreUnavailable {
            backend: "keyring".into(),
            detail: "no secret service on this host".into(),
        });
        let profile = ProfileCredentialStore::new(
            primary.clone(),
            vec![LegacyEntry { store: legacy.clone(), shares_primary_path: false }],
            coordinator(),
        );

        assert!(
            profile.get("llmauth:claude-ai").await.expect("an absent credential is not an error").is_none(),
            "an unavailable optional source must not turn a fresh profile into a failure"
        );

        profile.set("llmauth:claude-ai", "value").await.expect("set");
        assert_eq!(
            profile.get("llmauth:claude-ai").await.expect("get").as_deref(),
            Some("value"),
            "the primary must keep working"
        );
        assert!(legacy.reads() > 0, "the source was consulted before being skipped");
    }

    /// A logout must not fail because an optional source is unavailable —
    /// there is nothing readable there to resurrect.
    #[tokio::test]
    async fn a_delete_tolerates_an_unavailable_legacy_source() {
        let primary = Arc::new(InMemoryStore::new());
        let legacy = FailingStore::new(|| AuthError::StoreUnavailable {
            backend: "keyring".into(),
            detail: "no secret service on this host".into(),
        });
        let profile = ProfileCredentialStore::new(
            primary.clone(),
            vec![LegacyEntry { store: legacy, shares_primary_path: false }],
            coordinator(),
        );
        primary.set("llmauth:claude-ai", "value").await.unwrap();

        profile.delete("llmauth:claude-ai").await.expect("logout must succeed");
        assert!(primary.get("llmauth:claude-ai").await.unwrap().is_none());
    }

    /// A source that *is* there but refuses access — a locked keyring, a
    /// permission change — may be holding the user's credential. Reporting
    /// "no credentials" would send them through a fresh login that overwrites
    /// it. This must be an error.
    #[tokio::test]
    async fn a_locked_legacy_source_is_not_reported_as_no_credentials() {
        let primary = Arc::new(InMemoryStore::new());
        let legacy = FailingStore::new(|| {
            AuthError::store("the OS credential store refused access (locked)")
        });
        let profile = ProfileCredentialStore::new(
            primary,
            vec![LegacyEntry { store: legacy, shares_primary_path: false }],
            coordinator(),
        );

        let err = profile
            .get("llmauth:claude-ai")
            .await
            .expect_err("a locked source must not read as absent");
        assert!(!matches!(err, AuthError::StoreUnavailable { .. }), "got {err:?}");
    }

    /// A legacy source that becomes unavailable *between* the candidate read
    /// and the migration must be treated the same way as one that was
    /// unavailable all along: skipped, not turned into a failure of the read.
    #[tokio::test]
    async fn a_legacy_source_that_goes_unavailable_mid_adoption_is_still_skipped() {
        let primary = Arc::new(InMemoryStore::new());
        let legacy = FlakyStore::new(
            "old-value",
            || AuthError::StoreUnavailable {
                backend: "keyring".into(),
                detail: "the session bus went away".into(),
            },
        );
        let profile = ProfileCredentialStore::new(
            primary.clone(),
            vec![LegacyEntry { store: legacy, shares_primary_path: false }],
            coordinator(),
        );

        let got = profile
            .get("llmauth:claude-ai")
            .await
            .expect("an unavailable source must not fail the read");
        assert!(got.is_none(), "nothing may be reported when the source went away");
        assert!(
            primary.get("llmauth:claude-ai").await.unwrap().is_none(),
            "nothing may be migrated from a source that cannot be re-read"
        );
    }

    /// The same point for a source that locks up mid-adoption: that one may be
    /// holding the credential, so it must surface.
    #[tokio::test]
    async fn a_legacy_source_that_locks_mid_adoption_is_reported() {
        let primary = Arc::new(InMemoryStore::new());
        let legacy = FlakyStore::new("old-value", || {
            AuthError::store("the OS credential store refused access (locked)")
        });
        let profile = ProfileCredentialStore::new(
            primary,
            vec![LegacyEntry { store: legacy, shares_primary_path: false }],
            coordinator(),
        );

        let err = profile
            .get("llmauth:claude-ai")
            .await
            .expect_err("a source that locks up must not read as absent");
        assert!(!matches!(err, AuthError::StoreUnavailable { .. }), "got {err:?}");
    }

    /// A legacy source that answers the first read and then fails.
    struct FlakyStore {
        value: &'static str,
        error: fn() -> AuthError,
        read_once: std::sync::atomic::AtomicBool,
    }

    impl FlakyStore {
        fn new(value: &'static str, error: fn() -> AuthError) -> Arc<Self> {
            Arc::new(Self {
                value,
                error,
                read_once: std::sync::atomic::AtomicBool::new(false),
            })
        }
    }

    #[async_trait]
    impl CredentialStore for FlakyStore {
        async fn get(&self, _key: &str) -> Result<Option<String>, AuthError> {
            if self.read_once.swap(true, std::sync::atomic::Ordering::SeqCst) {
                return Err((self.error)());
            }
            Ok(Some(self.value.to_owned()))
        }
        async fn set(&self, _: &str, _: &str) -> Result<(), AuthError> {
            Err((self.error)())
        }
        async fn delete(&self, _: &str) -> Result<(), AuthError> {
            Err((self.error)())
        }
    }

    // ── Adoption is coordinated (finding 3) ──────────────────────────────────

    /// A logout that lands while an adoption is in flight must win: the
    /// credential must not be written back into the primary by the migration.
    #[tokio::test]
    async fn an_adoption_that_lands_after_a_logout_does_not_restore_the_credential() {
        let primary = Arc::new(InMemoryStore::new());
        let legacy_inner = Arc::new(InMemoryStore::new());
        legacy_inner.set("llmauth:claude-ai", "old-value").await.unwrap();
        let legacy = BlockingStore::new(legacy_inner.clone());

        let coordinator = coordinator();
        let profile = Arc::new(ProfileCredentialStore::new(
            primary.clone(),
            vec![LegacyEntry { store: legacy.clone(), shares_primary_path: false }],
            Arc::clone(&coordinator),
        ));

        let adopting = {
            let profile = Arc::clone(&profile);
            tokio::spawn(async move { profile.get("llmauth:claude-ai").await })
        };
        legacy.wait_until_read().await;

        // The logout runs to completion while the adoption is mid-flight.
        profile.delete("llmauth:claude-ai").await.expect("logout");
        legacy.let_the_read_finish();

        let adopted = adopting.await.expect("join").expect("adoption must not fail");
        assert!(
            adopted.is_none(),
            "an adoption completing after a logout must not hand back the credential"
        );
        assert!(
            primary.get("llmauth:claude-ai").await.unwrap().is_none(),
            "an adoption must never write back a credential that was logged out"
        );
        assert!(legacy_inner.get("llmauth:claude-ai").await.unwrap().is_none());
    }

    /// The same race against a replacement: the credential someone else stored
    /// must not be overwritten by the older copy being migrated.
    #[tokio::test]
    async fn an_adoption_that_lands_after_a_replacement_does_not_overwrite_it() {
        let primary = Arc::new(InMemoryStore::new());
        let legacy_inner = Arc::new(InMemoryStore::new());
        legacy_inner.set("llmauth:claude-ai", "old-value").await.unwrap();
        let legacy = BlockingStore::new(legacy_inner);

        let profile = Arc::new(ProfileCredentialStore::new(
            primary.clone(),
            vec![LegacyEntry { store: legacy.clone(), shares_primary_path: false }],
            coordinator(),
        ));

        let adopting = {
            let profile = Arc::clone(&profile);
            tokio::spawn(async move { profile.get("llmauth:claude-ai").await })
        };
        legacy.wait_until_read().await;

        profile.set("llmauth:claude-ai", "new-value").await.expect("replacement");
        legacy.let_the_read_finish();

        let adopted = adopting.await.expect("join").expect("adoption must not fail");
        assert_eq!(
            adopted.as_deref(),
            Some("new-value"),
            "the caller must see the credential that is actually stored"
        );
        assert_eq!(
            primary.get("llmauth:claude-ai").await.unwrap().as_deref(),
            Some("new-value"),
            "an adoption must never roll back a newer credential"
        );
    }

    /// A read taken while a commit section is held must not try to adopt —
    /// that would deadlock against the section's own lock.
    #[tokio::test]
    async fn a_read_only_read_does_not_adopt_and_never_waits_for_the_commit_gate() {
        let (primary, legacy, profile) = store_with_legacy();
        legacy.set("llmauth:claude-ai", "old-value").await.unwrap();

        let coordinator = Arc::clone(profile.coordinator());
        let _held = coordinator.begin(crate::coordination::AUTH_COMMIT_KEY).await.unwrap();

        let value = tokio::time::timeout(
            std::time::Duration::from_secs(5),
            profile.read_only("llmauth:claude-ai"),
        )
        .await
        .expect("a read inside a commit section must not wait for the gate")
        .expect("read");

        assert_eq!(value.as_deref(), Some("old-value"), "the value must still be visible");
        assert!(
            primary.get("llmauth:claude-ai").await.unwrap().is_none(),
            "a read taken inside a commit section must not migrate anything"
        );
    }
}

