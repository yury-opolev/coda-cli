//! [`CredentialManager`] — the façade consumers use.
//!
//! Registers providers, loads/persists credentials, and auto-refreshes on
//! every read.  Per-provider refreshes are coalesced via a `tokio::sync::Mutex`
//! gate so a burst of N concurrent 401s triggers exactly ONE token refresh:
//!
//! 1. All N tasks race to acquire the gate.
//! 2. The winner re-reads the stored credential and, if still expired, calls
//!    `provider.refresh` and stores the result.
//! 3. Each subsequent waiter re-reads inside the lock, finds a fresh token,
//!    and returns it immediately without another network call.

use std::collections::HashMap;
use std::sync::Arc;

use crate::credential::Credential;
use crate::error::AuthError;
use crate::provider::AuthProvider;
use crate::store::CredentialStore;

/// Key used to store a credential in the backing store.
fn store_key(provider_id: &str) -> String {
    format!("llmauth:{provider_id}")
}

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
}

impl CredentialManager {
    /// Create a manager with the given store and providers.
    pub fn new(
        store: Arc<dyn CredentialStore>,
        providers: impl IntoIterator<Item = Arc<dyn AuthProvider>>,
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
        }
    }

    /// Registered provider ids.
    pub fn provider_ids(&self) -> impl Iterator<Item = &str> {
        self.providers.keys().map(String::as_str)
    }

    /// Load the stored credential, refreshing it first if the provider reports
    /// it is near expiry.  Refreshes are coalesced per provider: N concurrent
    /// calls for the same expired credential trigger exactly one refresh.
    pub async fn get_credential(
        &self,
        provider_id: &str,
    ) -> Result<Option<Credential>, AuthError> {
        let provider = self.provider(provider_id)?;

        let credential = match self.load(provider_id).await? {
            Some(c) => c,
            None => return Ok(None),
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

        // Re-read inside the lock: another waiter may have already refreshed.
        let credential = self.load(provider_id).await?.unwrap_or(credential);

        if !provider.needs_refresh(&credential) {
            return Ok(Some(credential));
        }

        let refreshed = provider.refresh(&credential).await?;
        self.persist(provider_id, &refreshed).await?;
        Ok(Some(refreshed))
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
            .ok_or_else(|| AuthError::NotFound(provider_id.into()))?;
        provider.auth_headers(&credential)
    }

    /// Persist a credential obtained externally (e.g. after completing a login
    /// flow outside the manager).  Also removes any credential stored for other
    /// providers to preserve the single-credential invariant.
    pub async fn store_credential(
        &self,
        provider_id: &str,
        credential: &Credential,
    ) -> Result<(), AuthError> {
        let _ = self.provider(provider_id)?;
        self.persist(provider_id, credential).await?;
        self.remove_other_credentials(Some(provider_id)).await?;
        Ok(())
    }

    /// Delete the stored credential for a provider.
    pub async fn logout(&self, provider_id: &str) -> Result<(), AuthError> {
        self.store.delete(&store_key(provider_id)).await
    }

    /// The single provider id that currently has a stored credential, or `None`.
    pub async fn connected_provider_id(&self) -> Result<Option<String>, AuthError> {
        for id in self.providers.keys() {
            if self.store.get(&store_key(id)).await?.is_some() {
                return Ok(Some(id.clone()));
            }
        }
        Ok(None)
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    fn provider(&self, provider_id: &str) -> Result<&Arc<dyn AuthProvider>, AuthError> {
        self.providers
            .get(provider_id)
            .ok_or_else(|| AuthError::UnknownProvider(provider_id.into()))
    }

    async fn load(&self, provider_id: &str) -> Result<Option<Credential>, AuthError> {
        match self.store.get(&store_key(provider_id)).await? {
            Some(json) => Ok(Some(serde_json::from_str(&json)?)),
            None => Ok(None),
        }
    }

    async fn persist(&self, provider_id: &str, credential: &Credential) -> Result<(), AuthError> {
        let json = serde_json::to_string(credential)?;
        self.store.set(&store_key(provider_id), &json).await
    }

    async fn remove_other_credentials(&self, keep: Option<&str>) -> Result<(), AuthError> {
        for id in self.providers.keys() {
            if keep.map(|k| k != id).unwrap_or(true) {
                self.store.delete(&store_key(id)).await?;
            }
        }
        Ok(())
    }
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
