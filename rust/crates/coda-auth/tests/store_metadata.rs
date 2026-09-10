//! The narrow primary-only operations a compound auth transaction needs to put
//! a profile back exactly as it found it.
//!
//! An ordinary `delete` deliberately publishes a hashed *retirement marker*
//! before removing anything, so an unavailable legacy store cannot resurrect a
//! logged-out account later. That makes `delete` the wrong tool for a rollback:
//! clearing a marker with it would file a marker *about the marker*, and
//! restoring a credential that was previously absent would invent retirement
//! state that never existed.

use std::sync::Arc;

use coda_auth::coordination::LocalCoordinator;
use coda_auth::store::{CredentialStore, InMemoryStore, ProfileCredentialStore};

fn profile_store() -> (Arc<InMemoryStore>, Arc<ProfileCredentialStore>) {
    let primary = Arc::new(InMemoryStore::new());
    let store = Arc::new(ProfileCredentialStore::with_primary(
        Arc::clone(&primary) as Arc<dyn CredentialStore>,
        Arc::new(LocalCoordinator::new()),
    ));
    (primary, store)
}

/// Every key the primary holds, so a test can prove no extra metadata was
/// manufactured.
async fn keys(primary: &InMemoryStore) -> Vec<String> {
    let mut keys = primary.keys().await;
    keys.sort();
    keys
}

#[tokio::test]
async fn an_ordinary_delete_still_records_retirement_intent() {
    let (primary, store) = profile_store();
    store.set("llmauth:claude-ai", "{}").await.unwrap();
    store.delete("llmauth:claude-ai").await.unwrap();

    assert!(store.is_retired("llmauth:claude-ai").await.unwrap());
    assert_eq!(keys(&primary).await.len(), 1, "the marker is the only thing left");
}

#[tokio::test]
async fn clearing_a_marker_does_not_manufacture_another_one() {
    let (primary, store) = profile_store();
    store.set("llmauth:claude-ai", "{}").await.unwrap();
    store.delete("llmauth:claude-ai").await.unwrap();

    store.set_retired("llmauth:claude-ai", false).await.unwrap();

    assert!(!store.is_retired("llmauth:claude-ai").await.unwrap());
    assert_eq!(
        keys(&primary).await,
        Vec::<String>::new(),
        "clearing a marker through the ordinary delete path would leave a marker about the marker"
    );
}

#[tokio::test]
async fn restoring_a_credential_writes_no_retirement_state() {
    let (primary, store) = profile_store();

    store.restore_primary("llmauth:claude-ai", Some("{\"restored\":true}")).await.unwrap();

    assert_eq!(store.read_only("llmauth:claude-ai").await.unwrap().as_deref(), Some("{\"restored\":true}"));
    assert_eq!(keys(&primary).await, vec!["llmauth:claude-ai".to_owned()]);
}

#[tokio::test]
async fn restoring_an_absent_credential_removes_it_without_retiring_it() {
    let (primary, store) = profile_store();
    store.set("llmauth:claude-ai", "{}").await.unwrap();

    // Rolling back to "there was nothing here" must not invent the retirement
    // metadata a real logout would have written.
    store.restore_primary("llmauth:claude-ai", None).await.unwrap();

    assert_eq!(store.read_only("llmauth:claude-ai").await.unwrap(), None);
    assert!(!store.is_retired("llmauth:claude-ai").await.unwrap());
    assert_eq!(keys(&primary).await, Vec::<String>::new());
}

#[tokio::test]
async fn a_marker_can_be_restored_exactly_as_it_was() {
    let (_, store) = profile_store();

    store.set_retired("llmauth:github-copilot", true).await.unwrap();
    assert!(store.is_retired("llmauth:github-copilot").await.unwrap());

    store.set_retired("llmauth:github-copilot", false).await.unwrap();
    assert!(!store.is_retired("llmauth:github-copilot").await.unwrap());

    // Idempotent in both directions: a rollback runs whatever the state was.
    store.set_retired("llmauth:github-copilot", false).await.unwrap();
    assert!(!store.is_retired("llmauth:github-copilot").await.unwrap());
}

#[tokio::test]
async fn the_marker_key_is_not_the_credential_key() {
    let (primary, store) = profile_store();
    store.set_retired("llmauth:claude-ai", true).await.unwrap();

    let stored = keys(&primary).await;
    assert_eq!(stored.len(), 1);
    assert_ne!(stored[0], "llmauth:claude-ai");
    assert!(
        !stored[0].contains("claude-ai"),
        "the marker must not spell out which account was retired: {}",
        stored[0]
    );
}
