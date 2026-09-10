//! Task 2 — refresh and logout must agree on what is stored.
//!
//! The dangerous window is the one where a token refresh is in flight. While
//! the network call is running, anything can happen to the stored credential:
//! the user logs out from the TUI, the CLI signs a different account in, a
//! second engine process writes its own refresh. The refresh that comes back
//! late must not undo any of that.
//!
//! These tests use barriers, never sleeps: each one blocks the refresh until
//! the competing operation has actually completed, so a failure means broken
//! coordination rather than an unlucky machine.
//!
//! No real credential, keyring entry, or network call is involved — the
//! "network" is a channel the test controls.

use std::sync::Arc;
use std::time::Duration;

use coda_auth::error::AuthError;
use coda_auth::provider::AuthProvider;
use coda_auth::store::{CredentialStore, InMemoryStore};
use coda_auth::CredentialManager;

mod common;
use common::{access_token, expired, fresh, seed, BarrierProvider, OTHER_PROVIDER, PROVIDER};

/// The coordination a profile hands to every manager built from it. Separate
/// managers only agree with each other because they share it, so the tests
/// wire them exactly the way the engine does.
fn shared_coordination() -> Arc<dyn coda_auth::CommitCoordinator> {
    Arc::new(coda_auth::LocalCoordinator::new())
}
// ── Logout during a refresh ──────────────────────────────────────────────────

/// The commit section must cover the *whole* profile, not one provider's key.
///
/// A provider switch writes its own credential and removes the others. If a
/// refresh of the provider being replaced is sitting inside its own commit
/// section — past the read it based the write on, before the write — a
/// per-key gate lets the switch run right through it, and the account the
/// user just replaced comes back.
#[tokio::test]
async fn a_provider_switch_cannot_interleave_with_a_refresh_that_is_mid_commit() {
    let store = common::PausingStore::new("llmauth:claude-ai");
    let shared: Arc<dyn coda_auth::CommitCoordinator> = Arc::new(coda_auth::LocalCoordinator::new());
    let store_handle: Arc<dyn CredentialStore> = Arc::clone(&store) as Arc<dyn CredentialStore>;

    let refreshing_provider = BarrierProvider::new(PROVIDER, "refreshed-old-token");
    // Each manager registers only its own provider, exactly as the engine
    // builds them; they share the profile's coordinator, and nothing else.
    let reader = Arc::new(CredentialManager::with_coordinator(
        Arc::clone(&store_handle),
        [Arc::clone(&refreshing_provider) as Arc<dyn AuthProvider>],
        Arc::clone(&shared),
    ));
    let switcher = Arc::new(CredentialManager::with_coordinator(
        Arc::clone(&store_handle),
        [BarrierProvider::new(OTHER_PROVIDER, "unused") as Arc<dyn AuthProvider>],
        Arc::clone(&shared),
    ));

    seed(&store_handle, &expired(PROVIDER, "old-token")).await;
    store.arm();

    let refreshing = {
        let reader = Arc::clone(&reader);
        tokio::spawn(async move { reader.get_credential(PROVIDER).await })
    };
    refreshing_provider.wait_until_refresh_started().await;
    refreshing_provider.let_the_refresh_finish();

    // The refresh is now inside its commit section, about to persist.
    store.wait_until_paused_in_a_write().await;

    let switching = {
        let switcher = Arc::clone(&switcher);
        tokio::spawn(async move {
            switcher
                .store_credential(OTHER_PROVIDER, &fresh(OTHER_PROVIDER, "new-account-token"))
                .await
        })
    };

    // The switch must not be able to start while the refresh holds the section.
    let too_early = tokio::time::timeout(Duration::from_millis(200), async {
        loop {
            if store_handle
                .get(&format!("llmauth:{OTHER_PROVIDER}"))
                .await
                .expect("read")
                .is_some()
            {
                return;
            }
            tokio::task::yield_now().await;
        }
    })
    .await;
    assert!(
        too_early.is_err(),
        "the switch ran while a refresh was inside the commit section"
    );

    store.let_the_write_finish();
    refreshing.await.expect("join").expect("refresh must not fail");
    switching.await.expect("join").expect("switch must succeed");

    assert!(
        store_handle.get(&format!("llmauth:{PROVIDER}")).await.expect("read").is_none(),
        "the replaced account must be gone once the switch completes"
    );
    assert!(
        store_handle
            .get(&format!("llmauth:{OTHER_PROVIDER}"))
            .await
            .expect("read")
            .is_some(),
        "the new account must be the one that is stored"
    );
}

/// The single-credential rule belongs to the profile, not to whichever
/// providers a manager happens to have registered: the engine builds one
/// manager per provider, so a switch made through one of them must still
/// evict the account stored by another.
#[tokio::test]
async fn a_manager_that_registers_one_provider_still_evicts_the_others() {
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    seed(&store, &fresh(PROVIDER, "old-account-token")).await;

    let copilot_only = CredentialManager::new(
        Arc::clone(&store),
        [BarrierProvider::new(OTHER_PROVIDER, "unused") as Arc<dyn AuthProvider>],
    );
    copilot_only
        .store_credential(OTHER_PROVIDER, &fresh(OTHER_PROVIDER, "new-account-token"))
        .await
        .expect("store");

    assert!(
        store.get(&format!("llmauth:{PROVIDER}")).await.expect("read").is_none(),
        "signing a new provider in must leave exactly one stored credential"
    );
}

/// A refresh that comes back with a credential belonging to a different
/// provider must not be filed under this provider's key.
#[tokio::test]
async fn a_refresh_that_returns_another_providers_credential_is_rejected() {
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    let provider = common::WrongProviderRefresh::new(PROVIDER, OTHER_PROVIDER);
    let manager = CredentialManager::new(
        Arc::clone(&store),
        [Arc::clone(&provider) as Arc<dyn AuthProvider>],
    );

    seed(&store, &expired(PROVIDER, "old-token")).await;
    let before = store.get(&format!("llmauth:{PROVIDER}")).await.expect("read");

    let err = manager
        .get_credential(PROVIDER)
        .await
        .expect_err("a mismatched refresh result must be rejected");
    assert!(
        matches!(err, AuthError::CredentialProviderMismatch { .. }),
        "expected a provider-mismatch error, got {err:?}"
    );
    assert_eq!(
        store.get(&format!("llmauth:{PROVIDER}")).await.expect("read"),
        before,
        "a rejected refresh must not change what is stored"
    );
}

/// More than one stored provider credential is a broken invariant, not a
/// menu to pick from at random.
#[tokio::test]
async fn several_stored_credentials_are_reported_rather_than_chosen_between() {
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    seed(&store, &fresh(PROVIDER, "one")).await;
    seed(&store, &fresh(OTHER_PROVIDER, "two")).await;

    let manager = CredentialManager::new(
        Arc::clone(&store),
        [
            BarrierProvider::new(PROVIDER, "unused") as Arc<dyn AuthProvider>,
            BarrierProvider::new(OTHER_PROVIDER, "unused") as Arc<dyn AuthProvider>,
        ],
    );

    let err = manager
        .connected_provider_id()
        .await
        .expect_err("an ambiguous store must not resolve to one provider");
    assert!(
        matches!(err, AuthError::AmbiguousCredentials { .. }),
        "expected an ambiguity error, got {err:?}"
    );
}

/// A refresh that lands after a logout must not restore the credential.
#[tokio::test]
async fn a_refresh_that_lands_after_a_logout_does_not_restore_the_credential() {
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    let provider = BarrierProvider::new(PROVIDER, "refreshed-token");
    let coordination = shared_coordination();
    let reader = Arc::new(CredentialManager::with_coordinator(
        Arc::clone(&store),
        [Arc::clone(&provider) as Arc<dyn AuthProvider>],
        Arc::clone(&coordination),
    ));
    // A *separate* manager, as the TUI and the engine each have, sharing the
    // profile's coordination exactly as production wires it.
    let logout_side = CredentialManager::with_coordinator(
        Arc::clone(&store),
        [Arc::clone(&provider) as Arc<dyn AuthProvider>],
        Arc::clone(&coordination),
    );

    seed(&store, &expired(PROVIDER, "old-token")).await;

    let refreshing = {
        let reader = Arc::clone(&reader);
        tokio::spawn(async move { reader.get_credential(PROVIDER).await })
    };

    provider.wait_until_refresh_started().await;
    logout_side.logout(PROVIDER).await.expect("logout");
    provider.let_the_refresh_finish();

    let result = refreshing.await.expect("join").expect("refresh must not fail");
    assert!(
        result.is_none(),
        "a refresh completing after a logout must not hand back a credential"
    );
    assert!(
        store.get(&format!("llmauth:{PROVIDER}")).await.expect("read").is_none(),
        "the logged-out credential must stay gone"
    );
}

/// The same race, but the user signed a *different* provider in while the
/// refresh was running. The late refresh must not resurrect the account that
/// was replaced.
#[tokio::test]
async fn a_refresh_that_lands_after_a_provider_switch_does_not_overwrite_the_new_account() {
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    let old = BarrierProvider::new(PROVIDER, "refreshed-old-token");
    let new = BarrierProvider::new(OTHER_PROVIDER, "unused");
    let providers: Vec<Arc<dyn AuthProvider>> =
        vec![Arc::clone(&old) as Arc<dyn AuthProvider>, Arc::clone(&new) as Arc<dyn AuthProvider>];

    let coordination = shared_coordination();
    let reader = Arc::new(CredentialManager::with_coordinator(
        Arc::clone(&store),
        providers.clone(),
        Arc::clone(&coordination),
    ));
    let login_side =
        CredentialManager::with_coordinator(Arc::clone(&store), providers, Arc::clone(&coordination));

    seed(&store, &expired(PROVIDER, "old-token")).await;

    let refreshing = {
        let reader = Arc::clone(&reader);
        tokio::spawn(async move { reader.get_credential(PROVIDER).await })
    };

    old.wait_until_refresh_started().await;
    login_side
        .store_credential(OTHER_PROVIDER, &fresh(OTHER_PROVIDER, "new-account-token"))
        .await
        .expect("sign the new account in");
    old.let_the_refresh_finish();

    let result = refreshing.await.expect("join").expect("refresh must not fail");
    assert!(
        result.is_none(),
        "the replaced account must not be handed back after the switch"
    );
    assert!(
        store.get(&format!("llmauth:{PROVIDER}")).await.expect("read").is_none(),
        "the replaced account must not be written back"
    );
    let survivor = store
        .get(&format!("llmauth:{OTHER_PROVIDER}"))
        .await
        .expect("read")
        .expect("the new account must still be stored");
    assert!(survivor.contains("new-account-token"), "the new account must be intact");
}

/// A credential that another writer replaced *for the same provider* (a second
/// login on the same account, or another process' refresh) must not be rolled
/// back by a refresh that started earlier.
#[tokio::test]
async fn a_refresh_that_lands_after_a_replacement_does_not_roll_it_back() {
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    let provider = BarrierProvider::new(PROVIDER, "stale-refresh-token");
    let coordination = shared_coordination();
    let reader = Arc::new(CredentialManager::with_coordinator(
        Arc::clone(&store),
        [Arc::clone(&provider) as Arc<dyn AuthProvider>],
        Arc::clone(&coordination),
    ));
    let login_side = CredentialManager::with_coordinator(
        Arc::clone(&store),
        [Arc::clone(&provider) as Arc<dyn AuthProvider>],
        Arc::clone(&coordination),
    );

    seed(&store, &expired(PROVIDER, "old-token")).await;

    let refreshing = {
        let reader = Arc::clone(&reader);
        tokio::spawn(async move { reader.get_credential(PROVIDER).await })
    };

    provider.wait_until_refresh_started().await;
    login_side
        .store_credential(PROVIDER, &fresh(PROVIDER, "re-login-token"))
        .await
        .expect("re-login");
    provider.let_the_refresh_finish();

    let result = refreshing
        .await
        .expect("join")
        .expect("refresh must not fail")
        .expect("a credential is stored");
    assert_eq!(
        access_token(&result),
        "re-login-token",
        "the caller must see the credential that is actually stored"
    );

    let stored = store
        .get(&format!("llmauth:{PROVIDER}"))
        .await
        .expect("read")
        .expect("stored");
    assert!(
        stored.contains("re-login-token"),
        "the newer credential must survive the late refresh; got {stored}"
    );
}

/// Cancelling the caller (the user hits Esc, the session ends) must release
/// the coordination and must never produce a write afterwards.
#[tokio::test]
async fn a_cancelled_refresh_releases_coordination_and_never_writes() {
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    let provider = BarrierProvider::new(PROVIDER, "cancelled-token");
    let manager = Arc::new(CredentialManager::new(
        Arc::clone(&store),
        [Arc::clone(&provider) as Arc<dyn AuthProvider>],
    ));

    seed(&store, &expired(PROVIDER, "old-token")).await;

    let refreshing = {
        let manager = Arc::clone(&manager);
        tokio::spawn(async move { manager.get_credential(PROVIDER).await })
    };
    provider.wait_until_refresh_started().await;
    refreshing.abort();
    let _ = refreshing.await;
    provider.let_the_refresh_finish();

    // The coordination must be free again: a logout through the same manager
    // completes instead of deadlocking behind the abandoned refresh.
    tokio::time::timeout(Duration::from_secs(10), manager.logout(PROVIDER))
        .await
        .expect("a cancelled refresh must not hold the coordination")
        .expect("logout");

    // And nothing may appear afterwards.
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert!(
        store.get(&format!("llmauth:{PROVIDER}")).await.expect("read").is_none(),
        "a cancelled refresh must not write"
    );
}

/// The existing single-flight behaviour must survive: N concurrent readers of
/// one expired credential still trigger exactly one refresh.
#[tokio::test]
async fn concurrent_readers_still_trigger_exactly_one_refresh() {
    const READERS: usize = 16;

    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    let provider = BarrierProvider::new(PROVIDER, "single-flight-token");
    let manager = Arc::new(CredentialManager::new(
        Arc::clone(&store),
        [Arc::clone(&provider) as Arc<dyn AuthProvider>],
    ));

    seed(&store, &expired(PROVIDER, "old-token")).await;

    let readers: Vec<_> = (0..READERS)
        .map(|_| {
            let manager = Arc::clone(&manager);
            tokio::spawn(async move { manager.get_credential(PROVIDER).await })
        })
        .collect();

    provider.wait_until_refresh_started().await;
    provider.let_the_refresh_finish();

    for reader in readers {
        let credential = reader
            .await
            .expect("join")
            .expect("read")
            .expect("a credential is stored");
        assert_eq!(access_token(&credential), "single-flight-token");
    }
    assert_eq!(provider.refresh_count(), 1, "one refresh for {READERS} readers");
}

// ── Writing under the right key ──────────────────────────────────────────────

/// A credential belonging to another provider must never be filed under this
/// provider's key: the next read would hand a Claude token to Copilot.
#[tokio::test]
async fn a_credential_for_another_provider_is_not_written_under_this_key() {
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    let provider = BarrierProvider::new(PROVIDER, "unused");
    let manager = CredentialManager::new(
        Arc::clone(&store),
        [Arc::clone(&provider) as Arc<dyn AuthProvider>],
    );

    let err = manager
        .store_credential(PROVIDER, &fresh(OTHER_PROVIDER, "foreign-token"))
        .await
        .expect_err("a mismatched credential must be rejected");
    assert!(
        matches!(err, AuthError::CredentialProviderMismatch { .. }),
        "expected a provider-mismatch error, got {err:?}"
    );
    assert!(
        store.get(&format!("llmauth:{PROVIDER}")).await.expect("read").is_none(),
        "nothing may be written under the wrong key"
    );
}

/// An unknown provider id is untrusted text. It must be reported without
/// echoing control characters or unbounded content into logs.
#[tokio::test]
async fn an_unknown_provider_id_is_reported_without_echoing_raw_text() {
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    let provider = BarrierProvider::new(PROVIDER, "unused");
    let manager = CredentialManager::new(store, [Arc::clone(&provider) as Arc<dyn AuthProvider>]);

    let hostile = format!("evil\u{0007}\n{}", "A".repeat(500));
    let err = manager.get_credential(&hostile).await.expect_err("unknown provider");
    let rendered = err.to_string();
    assert!(
        !rendered.contains('\n') && !rendered.contains('\u{0007}'),
        "control characters must not reach the message: {rendered:?}"
    );
    assert!(
        rendered.len() < 200,
        "an unbounded provider id must not flood the message: {} chars",
        rendered.len()
    );
}

// ── MCP secrets ──────────────────────────────────────────────────────────────

/// Storing a provider credential evicts the *other providers*, and nothing
/// else. An MCP server secret sharing the store must survive.
#[tokio::test]
async fn storing_a_credential_leaves_unrelated_mcp_secrets_alone() {
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    store.set("mcp:github/token", "mcp-secret").await.expect("seed mcp");

    let claude = BarrierProvider::new(PROVIDER, "unused");
    let copilot = BarrierProvider::new(OTHER_PROVIDER, "unused");
    let manager = CredentialManager::new(
        Arc::clone(&store),
        [
            Arc::clone(&claude) as Arc<dyn AuthProvider>,
            Arc::clone(&copilot) as Arc<dyn AuthProvider>,
        ],
    );

    manager
        .store_credential(PROVIDER, &fresh(PROVIDER, "token"))
        .await
        .expect("store");
    manager.logout(PROVIDER).await.expect("logout");

    assert_eq!(
        store.get("mcp:github/token").await.expect("read").as_deref(),
        Some("mcp-secret"),
        "an MCP secret must not be touched by provider operations"
    );
}

/// Sanity: an error raised inside a coordinated section must not poison the
/// coordination for later callers.
#[tokio::test]
async fn a_failed_operation_leaves_the_coordination_usable() {
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    let provider = BarrierProvider::new(PROVIDER, "unused");
    let manager = CredentialManager::new(
        Arc::clone(&store),
        [Arc::clone(&provider) as Arc<dyn AuthProvider>],
    );

    let _ = manager
        .store_credential(PROVIDER, &fresh(OTHER_PROVIDER, "foreign"))
        .await
        .expect_err("mismatch");

    tokio::time::timeout(
        Duration::from_secs(5),
        manager.store_credential(PROVIDER, &fresh(PROVIDER, "good")),
    )
    .await
    .expect("the coordination must still be usable")
    .expect("store");

    let stored = store.get(&format!("llmauth:{PROVIDER}")).await.expect("read");
    assert!(stored.expect("stored").contains("good"));
}
