//! [`CredentialManager`] — the façade consumers use.
//!
//! Registers providers, loads/persists credentials, and auto-refreshes on
//! every read.
//!
//! # Single-flight
//!
//! Per-provider refreshes are coalesced via a `tokio::sync::Mutex` gate so a
//! burst of N concurrent 401s triggers exactly ONE token refresh:
//!
//! 1. All N tasks race to acquire the gate.
//! 2. The winner re-reads the stored credential and, if still expired, calls
//!    `provider.refresh` and stores the result.
//! 3. Each subsequent waiter re-reads inside the lock, finds a fresh token,
//!    and returns it immediately without another network call.
//!
//! # Committing a refresh
//!
//! That gate is process-local, and a refresh takes as long as the network
//! does. Meanwhile the user may log out, or sign a different account in, from
//! the TUI or from another engine process sharing the profile. So the result
//! of a refresh is not simply written: it is committed through a
//! [`CommitCoordinator`], which serializes the commit sections of everyone
//! sharing the profile, and the write only happens if the stored value is
//! still the one the refresh started from. If it was deleted the refresh is
//! abandoned (a logout is never undone); if it was replaced the newer value is
//! returned to the caller (an older account never overwrites a newer one).

use std::collections::{BTreeSet, HashMap};
use std::sync::Arc;

use crate::coordination::{CommitCoordinator, LocalCoordinator, AUTH_COMMIT_KEY};
use crate::credential::Credential;
use crate::error::{sanitize_key, AuthError};
use crate::provider::AuthProvider;
use crate::store::{AuthStorage, CredentialStore};

/// Key used to store a credential in the backing store.
fn store_key(provider_id: &str) -> String {
    format!("llmauth:{provider_id}")
}

/// The provider ids this product stores credentials for.
///
/// The engine builds one manager per provider, so a manager cannot rely on its
/// own registrations to know what else might be stored: signing in through a
/// Copilot-only manager still has to evict a Claude credential, or the profile
/// ends up with two accounts and no rule about which one wins.
pub const STORED_PROVIDER_IDS: [&str; 3] = ["claude-ai", "github-copilot", "anthropic-api-key"];

/// Per-provider single-flight gate: exactly one refresh runs at a time.
///
/// Stored as an `Arc<tokio::sync::Mutex<()>>` so `CredentialManager` itself
/// can be wrapped in an `Arc` and shared across threads without requiring the
/// mutex to be borrowed for the full lifetime of the manager.
type RefreshGate = Arc<tokio::sync::Mutex<()>>;

/// The façade consumers use: registers providers, loads/persists credentials,
/// and drives auto-refresh with single-flight coalescing.
pub struct CredentialManager {
    store: Arc<dyn CredentialStore>,
    providers: HashMap<String, Arc<dyn AuthProvider>>,
    /// One gate per registered provider; initialized at construction time.
    refresh_gates: HashMap<String, RefreshGate>,
    /// Serializes commit sections with every other holder of this profile.
    coordinator: Arc<dyn CommitCoordinator>,
}

impl CredentialManager {
    /// Create a manager with the given store and providers.
    ///
    /// Coordination is **private to this manager**: it serializes nothing but
    /// its own commits. That is correct for a store only this manager can
    /// reach (an in-memory store, a test fixture). For a profile on disk —
    /// which the TUI, other managers, and other processes also write — use
    /// [`CredentialManager::from_storage`], or pass the profile's coordinator
    /// to [`CredentialManager::with_coordinator`].
    pub fn new(
        store: Arc<dyn CredentialStore>,
        providers: impl IntoIterator<Item = Arc<dyn AuthProvider>>,
    ) -> Self {
        Self::with_coordinator(store, providers, Arc::new(LocalCoordinator::new()))
    }

    /// Create a manager for a resolved profile, coordinating with every other
    /// process that shares it.
    pub fn from_storage(
        storage: &AuthStorage,
        providers: impl IntoIterator<Item = Arc<dyn AuthProvider>>,
    ) -> Self {
        Self::with_coordinator(
            Arc::clone(&storage.store),
            providers,
            Arc::clone(&storage.coordinator),
        )
    }

    /// Create a manager with an explicit coordination mechanism.
    pub fn with_coordinator(
        store: Arc<dyn CredentialStore>,
        providers: impl IntoIterator<Item = Arc<dyn AuthProvider>>,
        coordinator: Arc<dyn CommitCoordinator>,
    ) -> Self {
        let providers: HashMap<_, _> = providers
            .into_iter()
            .map(|p| (p.provider_id().to_string(), p))
            .collect();

        let refresh_gates = providers
            .keys()
            .map(|id| (id.clone(), Arc::new(tokio::sync::Mutex::new(()))))
            .collect();

        Self {
            store,
            providers,
            refresh_gates,
            coordinator,
        }
    }

    /// Registered provider ids.
    pub fn provider_ids(&self) -> impl Iterator<Item = &str> {
        self.providers.keys().map(String::as_str)
    }

    /// Load the stored credential, refreshing it first if the provider reports
    /// it is near expiry.  Refreshes are coalesced per provider: N concurrent
    /// calls for the same expired credential trigger exactly one refresh.
    ///
    /// Returns `Ok(None)` when there is no credential — including when a
    /// logout removed it while a refresh was in flight.
    pub async fn get_credential(
        &self,
        provider_id: &str,
    ) -> Result<Option<Credential>, AuthError> {
        let provider = Arc::clone(self.provider(provider_id)?);

        let Some((_, credential)) = self.load(provider_id).await? else {
            return Ok(None);
        };

        // Fast path: token is fresh, or there is nothing to refresh with.
        if !provider.needs_refresh(&credential) || credential.refresh_token.is_none() {
            return Ok(Some(credential));
        }

        // Slow path: acquire the per-provider gate so at most one refresh runs.
        let gate = self
            .refresh_gates
            .get(provider_id)
            .expect("gate initialized for every provider");

        let _guard = gate.lock().await;

        // Re-read inside the gate, without migrating anything (a migration
        // would want the commit section this refresh is about to take): a
        // waiter may already have refreshed, or the credential may be gone. A
        // stale in-memory copy must never be used as a fallback here — that is
        // how a logged-out credential comes back to life.
        let Some((raw, credential)) = self.load_pinned(provider_id).await? else {
            return Ok(None);
        };

        if !provider.needs_refresh(&credential) {
            return Ok(Some(credential));
        }

        // The network call runs outside every commit section, so a hung
        // provider cannot block a logout.
        let refreshed = provider.refresh(&credential).await?;
        self.commit_refresh(provider_id, &raw, refreshed).await
    }

    /// The auth headers for the provider (refreshing the credential if needed).
    pub async fn get_auth_headers(
        &self,
        provider_id: &str,
    ) -> Result<Vec<(String, String)>, AuthError> {
        let provider = self.provider(provider_id)?;
        let credential = self
            .get_credential(provider_id)
            .await?
            .ok_or_else(|| AuthError::NotFound(sanitize_key(provider_id)))?;
        provider.auth_headers(&credential)
    }

    /// Persist a credential obtained externally (e.g. after completing a login
    /// flow outside the manager).  Also removes the credential stored for
    /// every other provider, to preserve the single-credential invariant.
    ///
    /// The credential must belong to `provider_id`: filing one provider's
    /// tokens under another's key would hand them to the wrong API.
    ///
    /// Both writes happen inside the profile's commit section, so a refresh of
    /// the provider being replaced cannot slip its own write in between them.
    pub async fn store_credential(
        &self,
        provider_id: &str,
        credential: &Credential,
    ) -> Result<(), AuthError> {
        let _ = self.provider(provider_id)?;
        check_provider_match(provider_id, credential)?;

        let _commit = self.coordinator.begin(AUTH_COMMIT_KEY).await?;
        self.persist(provider_id, credential).await?;
        self.remove_other_credentials(Some(provider_id)).await?;
        Ok(())
    }

    /// Delete the stored credential for a provider.
    ///
    /// Taken inside the profile's commit section so a refresh — of this
    /// provider or of the one being replaced — cannot slip a write in between
    /// this delete and its own check.
    pub async fn logout(&self, provider_id: &str) -> Result<(), AuthError> {
        let _commit = self.coordinator.begin(AUTH_COMMIT_KEY).await?;
        self.store.delete(&store_key(provider_id)).await
    }

    /// The single provider id that currently has a stored credential.
    ///
    /// Exactly one is the invariant. If several are stored — an interrupted
    /// switch, an older build, a hand-edited profile — that is reported, not
    /// resolved by picking one: the choice would decide which account the user
    /// is signed in as.
    pub async fn connected_provider_id(&self) -> Result<Option<String>, AuthError> {
        let mut connected = BTreeSet::new();
        for id in self.candidate_provider_ids() {
            if self.store.read_only(&store_key(&id)).await?.is_some() {
                connected.insert(id);
            }
        }

        let mut found = connected.into_iter();
        match (found.next(), found.next()) {
            (None, _) => Ok(None),
            (Some(only), None) => Ok(Some(only)),
            (Some(first), Some(second)) => {
                let rest: Vec<String> = std::iter::once(first)
                    .chain(std::iter::once(second))
                    .chain(found)
                    .collect();
                Err(AuthError::AmbiguousCredentials { providers: rest.join(", ") })
            }
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    fn provider(&self, provider_id: &str) -> Result<&Arc<dyn AuthProvider>, AuthError> {
        self.providers
            .get(provider_id)
            .ok_or_else(|| AuthError::UnknownProvider(sanitize_key(provider_id)))
    }

    /// Writes a refreshed credential only if the stored value is still the one
    /// the refresh was based on.
    ///
    /// * gone → the user logged out while the refresh was running; the refresh
    ///   is discarded and the caller is told there is no credential;
    /// * changed → someone stored a newer credential (a re-login, another
    ///   process' refresh); theirs stands and the caller gets it;
    /// * unchanged → the refresh is persisted.
    async fn commit_refresh(
        &self,
        provider_id: &str,
        expected: &str,
        refreshed: Credential,
    ) -> Result<Option<Credential>, AuthError> {
        // A refresh result is still a credential being filed under a key: if
        // the provider handed back someone else's, refuse before taking any
        // lock or writing anything.
        check_provider_match(provider_id, &refreshed)?;

        let _commit = self.coordinator.begin(AUTH_COMMIT_KEY).await?;

        match self.load_pinned(provider_id).await? {
            None => Ok(None),
            Some((raw, current)) if raw != expected => Ok(Some(current)),
            Some(_) => {
                self.persist(provider_id, &refreshed).await?;
                Ok(Some(refreshed))
            }
        }
    }

    /// The stored credential and the exact bytes it was parsed from — the
    /// bytes are what a commit compares against.
    ///
    /// Uses the migrating read, so an ordinary read still moves a credential
    /// an earlier build left behind into the primary backend. Never call it
    /// while holding the commit section; use [`Self::load_pinned`] there.
    async fn load(&self, provider_id: &str) -> Result<Option<(String, Credential)>, AuthError> {
        Self::parse(provider_id, self.store.get(&store_key(provider_id)).await?)
    }

    /// [`Self::load`] without any migration, for use inside a commit section.
    async fn load_pinned(
        &self,
        provider_id: &str,
    ) -> Result<Option<(String, Credential)>, AuthError> {
        Self::parse(provider_id, self.store.read_only(&store_key(provider_id)).await?)
    }

    fn parse(provider_id: &str, raw: Option<String>) -> Result<Option<(String, Credential)>, AuthError> {
        match raw {
            Some(json) => {
                let credential = serde_json::from_str(&json)?;
                check_provider_match(provider_id, &credential)?;
                Ok(Some((json, credential)))
            }
            None => Ok(None),
        }
    }

    async fn persist(&self, provider_id: &str, credential: &Credential) -> Result<(), AuthError> {
        check_provider_match(provider_id, credential)?;
        let json = serde_json::to_string(credential)?;
        self.store.set(&store_key(provider_id), &json).await
    }

    /// Every provider id whose credential this manager is responsible for:
    /// the ones it registered plus the ones the product stores, so a
    /// single-provider manager still enforces the single-credential rule.
    fn candidate_provider_ids(&self) -> BTreeSet<String> {
        self.providers
            .keys()
            .cloned()
            .chain(STORED_PROVIDER_IDS.iter().map(|id| (*id).to_owned()))
            .collect()
    }

    async fn remove_other_credentials(&self, keep: Option<&str>) -> Result<(), AuthError> {
        for id in self.candidate_provider_ids() {
            if keep.map(|k| k != id).unwrap_or(true) {
                self.store.delete(&store_key(&id)).await?;
            }
        }
        Ok(())
    }
}

/// Refuses a credential that does not belong to `provider_id`.
///
/// Used on load before a provider sees token material, and at the write
/// boundary for external credentials and refresh results.
fn check_provider_match(provider_id: &str, credential: &Credential) -> Result<(), AuthError> {
    if credential.provider_id == provider_id {
        return Ok(());
    }
    Err(AuthError::CredentialProviderMismatch {
        expected: sanitize_key(provider_id),
        actual: sanitize_key(&credential.provider_id),
    })
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Arc;
    use std::time::Duration;

    use async_trait::async_trait;

    use super::*;
    use crate::credential::{Credential, CredentialKind};
    use crate::secret::Secret;
    use crate::store::InMemoryStore;

    async fn assert_wrong_provider_blob_is_refused(refresh: bool) {
        let store = Arc::new(InMemoryStore::new());
        let credential = Credential {
            provider_id: "github-copilot".into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("test-access".into())),
            refresh_token: Some(Secret::new("test-refresh".into())),
            api_key: None,
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        };
        store.set("llmauth:claude-ai", &serde_json::to_string(&credential).unwrap()).await.unwrap();
        let calls = Arc::new(AtomicUsize::new(0));
        let provider = MockProvider {
            id: "claude-ai", refresh_count: calls.clone(),
            always_needs_refresh: refresh, refresh_delay: Duration::ZERO,
        };
        let manager = CredentialManager::new(
            store, [Arc::new(provider) as Arc<dyn AuthProvider>],
        );
        let result = manager.get_credential("claude-ai").await;
        assert_eq!(calls.load(Ordering::SeqCst), 0, "a foreign token must not reach the refresh provider");
        assert!(matches!(result, Err(AuthError::CredentialProviderMismatch { .. })));
    }

    #[tokio::test]
    async fn a_loaded_foreign_provider_credential_is_never_returned() {
        assert_wrong_provider_blob_is_refused(false).await;
    }

    #[tokio::test]
    async fn a_loaded_foreign_provider_credential_is_never_refreshed() {
        assert_wrong_provider_blob_is_refused(true).await;
    }

    // ── Mock provider ─────────────────────────────────────────────────────────

    struct MockProvider {
        id: &'static str,
        refresh_count: Arc<AtomicUsize>,
        /// When `true`, `needs_refresh` always returns `true` (simulates an expired token).
        always_needs_refresh: bool,
        /// Optional delay to make concurrent refreshes actually overlap.
        refresh_delay: Duration,
    }

    impl MockProvider {
        fn new(id: &'static str, refresh_count: Arc<AtomicUsize>) -> Self {
            Self {
                id,
                refresh_count,
                always_needs_refresh: false,
                refresh_delay: Duration::ZERO,
            }
        }
    }

    #[async_trait]
    impl AuthProvider for MockProvider {
        fn provider_id(&self) -> &str {
            self.id
        }

        fn needs_refresh(&self, _: &Credential) -> bool {
            self.always_needs_refresh
        }

        async fn refresh(&self, credential: &Credential) -> Result<Credential, AuthError> {
            if !self.refresh_delay.is_zero() {
                tokio::time::sleep(self.refresh_delay).await;
            }
            self.refresh_count.fetch_add(1, Ordering::SeqCst);
            // Return a credential with `always_needs_refresh = false` so further
            // callers inside the lock see a "fresh" token and skip the refresh.
            // We simulate this by returning a credential whose expiry is in the future;
            // since MockProvider.always_needs_refresh is per-instance and we cannot
            // change it here, the test must set `always_needs_refresh = false` OR
            // accept a re-read-inside-the-lock approach.
            //
            // The manager re-reads the credential from the store inside the lock after
            // persisting.  For the coalescing to work correctly, the stored credential
            // must have `needs_refresh()==false` after the refresh.  Since MockProvider
            // ignores the credential's fields and uses `always_needs_refresh`, this
            // works correctly only when the test uses a separate "fresh" provider for
            // the coalescing assertion.  The concurrent test sets needs_refresh to
            // always return true on the first call but false on subsequent calls via
            // the atomic counter trick — see `concurrent_refresh_fires_exactly_once`.
            Ok(Credential {
                provider_id: self.id.into(),
                kind: CredentialKind::OAuth,
                access_token: Some(Secret::new("refreshed_token".into())),
                refresh_token: credential.refresh_token.clone(),
                api_key: None,
                // Store an expiry far in the future so re-reads see a fresh credential.
                expires_at: Some(chrono::Utc::now() + chrono::Duration::hours(1)),
                scopes: Vec::new(),
                account: None,
            })
        }

        fn auth_headers(&self, credential: &Credential) -> Result<Vec<(String, String)>, AuthError> {
            let token = credential
                .access_token
                .as_ref()
                .map(|s| s.expose().clone())
                .unwrap_or_default();
            Ok(vec![("authorization".into(), format!("Bearer {token}"))])
        }
    }

    /// A variant of `MockProvider` that calls `needs_refresh = true` exactly
    /// N times (based on how many times it has been called so far), then false.
    /// This models the before/after state of a real token.
    struct CountingExpiryProvider {
        id: &'static str,
        refresh_count: Arc<AtomicUsize>,
        refresh_delay: Duration,
    }

    impl CountingExpiryProvider {
        fn new(
            id: &'static str,
            refresh_count: Arc<AtomicUsize>,
            _expire_for_first_n: usize,
            refresh_delay: Duration,
        ) -> Self {
            Self {
                id,
                refresh_count,
                refresh_delay,
            }
        }
    }

    #[async_trait]
    impl AuthProvider for CountingExpiryProvider {
        fn provider_id(&self) -> &str {
            self.id
        }

        fn needs_refresh(&self, credential: &Credential) -> bool {
            // Use the credential's expiry field as the source of truth so that
            // after the stored credential is updated (to one with far-future expiry),
            // concurrent waiters inside the lock see it as fresh.
            credential
                .expires_at
                .map(|exp| exp <= chrono::Utc::now() + chrono::Duration::minutes(10))
                .unwrap_or(false)
        }

        async fn refresh(&self, credential: &Credential) -> Result<Credential, AuthError> {
            if !self.refresh_delay.is_zero() {
                tokio::time::sleep(self.refresh_delay).await;
            }
            self.refresh_count.fetch_add(1, Ordering::SeqCst);
            Ok(Credential {
                expires_at: Some(chrono::Utc::now() + chrono::Duration::hours(1)),
                access_token: Some(Secret::new("new_token".into())),
                refresh_token: credential.refresh_token.clone(),
                ..credential.clone()
            })
        }

        fn auth_headers(&self, _credential: &Credential) -> Result<Vec<(String, String)>, AuthError> {
            Ok(vec![("authorization".into(), "Bearer tok".into())])
        }
    }

    fn expired_credential(provider_id: &str) -> Credential {
        Credential {
            provider_id: provider_id.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("old_token".into())),
            // Expiry in the past, plus within the 5-minute refresh buffer.
            expires_at: Some(chrono::Utc::now() - chrono::Duration::minutes(1)),
            refresh_token: Some(Secret::new("ref_tok".into())),
            api_key: None,
            scopes: Vec::new(),
            account: None,
        }
    }

    fn manager_with(
        provider: Arc<dyn AuthProvider>,
    ) -> (Arc<CredentialManager>, Arc<InMemoryStore>) {
        let store = Arc::new(InMemoryStore::new());
        let manager = Arc::new(CredentialManager::new(store.clone(), [provider]));
        (manager, store)
    }

    #[tokio::test]
    async fn get_credential_returns_none_when_nothing_stored() {
        let (mgr, _) = manager_with(Arc::new(MockProvider::new(
            "mock",
            Arc::new(AtomicUsize::new(0)),
        )));
        assert!(mgr.get_credential("mock").await.unwrap().is_none());
    }

    #[tokio::test]
    async fn get_credential_returns_stored_credential() {
        let (mgr, store) = manager_with(Arc::new(MockProvider::new(
            "mock",
            Arc::new(AtomicUsize::new(0)),
        )));
        let cred = expired_credential("mock");
        // Persist directly so needs_refresh returns true but there IS something stored.
        store
            .set(
                "llmauth:mock",
                &serde_json::to_string(&cred).unwrap(),
            )
            .await
            .unwrap();

        // With always_needs_refresh=false but the credential has an expiry that
        // satisfies CountingExpiryProvider's check — use MockProvider which ignores expiry.
        let result = mgr.get_credential("mock").await.unwrap();
        assert!(result.is_some());
    }

    #[tokio::test]
    async fn refresh_is_triggered_once_for_expired_credential() {
        let refresh_count = Arc::new(AtomicUsize::new(0));
        let provider = Arc::new(CountingExpiryProvider::new(
            "mock",
            refresh_count.clone(),
            usize::MAX,
            Duration::ZERO,
        ));
        let (mgr, store) = manager_with(provider);

        let cred = expired_credential("mock");
        store
            .set("llmauth:mock", &serde_json::to_string(&cred).unwrap())
            .await
            .unwrap();

        let result = mgr.get_credential("mock").await.unwrap().unwrap();
        assert_eq!(result.access_token.as_ref().map(|s| s.expose().as_str()), Some("new_token"));
        assert_eq!(refresh_count.load(Ordering::SeqCst), 1);
    }

    /// N concurrent callers all see an expired token and race to refresh it.
    /// Exactly ONE refresh must happen; all others must coalesce behind the gate.
    #[tokio::test]
    async fn concurrent_refresh_fires_exactly_once() {
        const CONCURRENCY: usize = 20;

        let refresh_count = Arc::new(AtomicUsize::new(0));
        let provider = Arc::new(CountingExpiryProvider::new(
            "concurrent",
            refresh_count.clone(),
            CONCURRENCY,
            // A small delay makes the concurrent race actually overlap.
            Duration::from_millis(20),
        ));
        let store = Arc::new(InMemoryStore::new());
        let manager = Arc::new(CredentialManager::new(
            store.clone(),
            [provider as Arc<dyn AuthProvider>],
        ));

        // Pre-populate with an expired credential (has a refresh token so the
        // manager will attempt a refresh).
        let cred = expired_credential("concurrent");
        store
            .set("llmauth:concurrent", &serde_json::to_string(&cred).unwrap())
            .await
            .unwrap();

        // Spawn N concurrent tasks.
        let handles: Vec<_> = (0..CONCURRENCY)
            .map(|_| {
                let m = manager.clone();
                tokio::spawn(async move { m.get_credential("concurrent").await })
            })
            .collect();

        for handle in handles {
            handle.await.unwrap().unwrap();
        }

        let actual = refresh_count.load(Ordering::SeqCst);
        assert_eq!(
            actual, 1,
            "expected exactly 1 refresh for {CONCURRENCY} concurrent callers; got {actual}"
        );
    }

    #[tokio::test]
    async fn logout_removes_credential() {
        let (mgr, store) = manager_with(Arc::new(MockProvider::new(
            "mock",
            Arc::new(AtomicUsize::new(0)),
        )));
        let cred = expired_credential("mock");
        store
            .set("llmauth:mock", &serde_json::to_string(&cred).unwrap())
            .await
            .unwrap();

        mgr.logout("mock").await.unwrap();
        assert!(store.get("llmauth:mock").await.unwrap().is_none());
    }

    #[tokio::test]
    async fn store_credential_evicts_other_providers() {
        let store = Arc::new(InMemoryStore::new());
        let manager = Arc::new(CredentialManager::new(
            store.clone(),
            [
                Arc::new(MockProvider::new("a", Arc::new(AtomicUsize::new(0))))
                    as Arc<dyn AuthProvider>,
                Arc::new(MockProvider::new("b", Arc::new(AtomicUsize::new(0)))),
            ],
        ));

        // Store something for "a".
        let cred_a = expired_credential("a");
        store
            .set("llmauth:a", &serde_json::to_string(&cred_a).unwrap())
            .await
            .unwrap();

        // Storing for "b" must evict "a".
        let cred_b = expired_credential("b");
        manager.store_credential("b", &cred_b).await.unwrap();

        assert!(store.get("llmauth:a").await.unwrap().is_none(), "a should be evicted");
        assert!(store.get("llmauth:b").await.unwrap().is_some(), "b should be present");
    }

    #[tokio::test]
    async fn unknown_provider_returns_error() {
        let (mgr, _) = manager_with(Arc::new(MockProvider::new(
            "mock",
            Arc::new(AtomicUsize::new(0)),
        )));
        let err = mgr.get_credential("nonexistent").await.unwrap_err();
        assert!(matches!(err, AuthError::UnknownProvider(_)));
    }

    #[tokio::test]
    async fn connected_provider_id_returns_none_when_empty() {
        let (mgr, _) = manager_with(Arc::new(MockProvider::new(
            "mock",
            Arc::new(AtomicUsize::new(0)),
        )));
        assert!(mgr.connected_provider_id().await.unwrap().is_none());
    }

    #[tokio::test]
    async fn connected_provider_id_returns_the_stored_provider() {
        let (mgr, store) = manager_with(Arc::new(MockProvider::new(
            "mock",
            Arc::new(AtomicUsize::new(0)),
        )));
        let cred = expired_credential("mock");
        store
            .set("llmauth:mock", &serde_json::to_string(&cred).unwrap())
            .await
            .unwrap();
        assert_eq!(
            mgr.connected_provider_id().await.unwrap(),
            Some("mock".into())
        );
    }

    // ── additional behaviours from the C# CredentialManagerTests spec ─────────

    /// `provider_ids()` must enumerate exactly the providers that were passed to
    /// the constructor — no more, no less.
    #[test]
    fn provider_ids_reflects_all_registered_providers() {
        let store = Arc::new(InMemoryStore::new());
        let manager = CredentialManager::new(
            store,
            [
                Arc::new(MockProvider::new("alpha", Arc::new(AtomicUsize::new(0))))
                    as Arc<dyn AuthProvider>,
                Arc::new(MockProvider::new("beta", Arc::new(AtomicUsize::new(0)))),
            ],
        );
        let mut ids: Vec<_> = manager.provider_ids().collect();
        ids.sort(); // HashMap ordering is non-deterministic
        assert_eq!(ids, vec!["alpha", "beta"]);
    }

    /// `get_auth_headers` must return `AuthError::NotFound` when no credential
    /// has been stored for the requested provider.  Callers must not receive an
    /// empty-header result that would silently make unauthenticated API calls.
    #[tokio::test]
    async fn get_auth_headers_returns_error_when_nothing_stored() {
        let (mgr, _) = manager_with(Arc::new(MockProvider::new(
            "mock",
            Arc::new(AtomicUsize::new(0)),
        )));
        let err = mgr.get_auth_headers("mock").await.unwrap_err();
        assert!(
            matches!(err, AuthError::NotFound(_)),
            "expected AuthError::NotFound when no credential stored, got {err:?}"
        );
    }
}
