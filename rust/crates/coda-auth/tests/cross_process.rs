//! Task 2 — coordination that spans processes.
//!
//! The TUI, a headless run, and any spawned engine all share one profile
//! directory. A mutex inside one manager — or inside one process — cannot see
//! the others, so the guarantee has to live on the profile itself.
//!
//! The "other process" here is this same test binary, re-executed to run the
//! ignored helper test below against the same isolated profile. The parent
//! blocks its refresh on a barrier until the child has *exited*, so the race
//! is decided by ordering, not by timing.

use std::sync::Arc;

use coda_auth::provider::AuthProvider;
use coda_auth::store::{open_profile_storage_with, Profile};
use coda_auth::CredentialManager;

mod common;
use common::{expired, seed, BarrierProvider, PROVIDER};

/// Environment variable naming the profile the child process should act on.
const CHILD_PROFILE_ENV: &str = "CODA_TEST_CHILD_PROFILE";

/// A logout performed by another process must not be undone by a refresh this
/// process had already started.
#[tokio::test]
async fn a_logout_in_another_process_survives_a_refresh_in_this_one() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = Profile::isolated(dir.path());
    let storage = open_profile_storage_with(&profile, None).expect("open profile");

    let provider = BarrierProvider::new(PROVIDER, "late-refresh-token");
    let manager = Arc::new(CredentialManager::from_storage(
        &storage,
        [Arc::clone(&provider) as Arc<dyn AuthProvider>],
    ));

    seed(&storage.store, &expired(PROVIDER, "old-token")).await;

    let refreshing = {
        let manager = Arc::clone(&manager);
        tokio::spawn(async move { manager.get_credential(PROVIDER).await })
    };
    provider.wait_until_refresh_started().await;

    // The other process logs out while our refresh is in flight; waiting for
    // it to exit is the barrier.
    let output = std::process::Command::new(std::env::current_exe().expect("test binary"))
        .arg("--exact")
        .arg("the_child_process_logs_out")
        .arg("--ignored")
        .arg("--nocapture")
        .env(CHILD_PROFILE_ENV, dir.path())
        .output()
        .expect("run the child process");
    assert!(
        output.status.success(),
        "the child logout process must succeed: {}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );

    provider.let_the_refresh_finish();
    let result = refreshing.await.expect("join").expect("refresh must not fail");
    assert!(
        result.is_none(),
        "a refresh landing after another process' logout must not restore the credential"
    );

    let reopened = open_profile_storage_with(&profile, None).expect("reopen");
    assert!(
        reopened
            .store
            .get(&format!("llmauth:{PROVIDER}"))
            .await
            .expect("read")
            .is_none(),
        "the credential must stay logged out on disk"
    );
}

/// A credential another process stored for a *different* account must not be
/// replaced by a refresh of the account it superseded.
#[tokio::test]
async fn a_login_in_another_process_survives_a_refresh_in_this_one() {
    let dir = tempfile::tempdir().expect("tempdir");
    let profile = Profile::isolated(dir.path());
    let storage = open_profile_storage_with(&profile, None).expect("open profile");

    let provider = BarrierProvider::new(PROVIDER, "late-refresh-token");
    let manager = Arc::new(CredentialManager::from_storage(
        &storage,
        [Arc::clone(&provider) as Arc<dyn AuthProvider>],
    ));

    seed(&storage.store, &expired(PROVIDER, "old-token")).await;

    let refreshing = {
        let manager = Arc::clone(&manager);
        tokio::spawn(async move { manager.get_credential(PROVIDER).await })
    };
    provider.wait_until_refresh_started().await;

    let output = std::process::Command::new(std::env::current_exe().expect("test binary"))
        .arg("--exact")
        .arg("the_child_process_signs_a_new_account_in")
        .arg("--ignored")
        .arg("--nocapture")
        .env(CHILD_PROFILE_ENV, dir.path())
        .output()
        .expect("run the child process");
    assert!(
        output.status.success(),
        "the child login process must succeed: {}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );

    provider.let_the_refresh_finish();
    let result = refreshing
        .await
        .expect("join")
        .expect("refresh must not fail")
        .expect("the child stored a credential");
    assert_eq!(
        result.access_token.as_ref().expect("token").expose(),
        "child-account-token",
        "the caller must see the credential the other process stored"
    );

    let reopened = open_profile_storage_with(&profile, None).expect("reopen");
    let stored = reopened
        .store
        .get(&format!("llmauth:{PROVIDER}"))
        .await
        .expect("read")
        .expect("stored");
    assert!(
        stored.contains("child-account-token"),
        "the other process' credential must survive the late refresh; got {stored}"
    );
}

// ── Child-process halves ─────────────────────────────────────────────────────

/// Logs out of the profile named by `CODA_TEST_CHILD_PROFILE`.
/// Ignored so it only runs when the parent invokes it explicitly.
#[ignore]
#[tokio::test]
async fn the_child_process_logs_out() {
    let manager = child_manager();
    manager.logout(PROVIDER).await.expect("logout");
}

/// Stores a fresh credential for the profile named by
/// `CODA_TEST_CHILD_PROFILE`, as a login in another process would.
#[ignore]
#[tokio::test]
async fn the_child_process_signs_a_new_account_in() {
    let manager = child_manager();
    manager
        .store_credential(PROVIDER, &common::fresh(PROVIDER, "child-account-token"))
        .await
        .expect("store");
}

fn child_manager() -> CredentialManager {
    let root = std::env::var(CHILD_PROFILE_ENV).expect("profile root");
    let profile = Profile::isolated(&root);
    let storage = open_profile_storage_with(&profile, None).expect("open profile");
    let provider = BarrierProvider::new(PROVIDER, "unused");
    CredentialManager::from_storage(&storage, [provider as Arc<dyn AuthProvider>])
}
