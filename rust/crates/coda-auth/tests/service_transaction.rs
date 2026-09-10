//! The login commit transaction: one section, one coherent outcome.
//!
//! Every case here is a state the user can be left in. A commit that fails
//! half-way and says nothing is how a machine ends up with no credential at
//! all, or with a credential for one account and a `defaultProvider` naming
//! another.

use std::sync::Arc;

use async_trait::async_trait;
use coda_auth::coordination::{CommitCoordinator, LocalCoordinator, AUTH_COMMIT_KEY};
use coda_auth::credential::{Credential, CredentialKind};
use coda_auth::error::AuthError;
use coda_auth::secret::Secret;
use coda_auth::service::transaction::{commit, CommitRequest, CommitStep};
use coda_auth::service::{
    AuthSettings, AuthSettingsPatch, CommitOutcome, InMemoryAuthSettings, ProviderIdentity,
};
use coda_auth::store::{CredentialStore, InMemoryStore, ProfileCredentialStore};

// ── Fixtures ─────────────────────────────────────────────────────────────────

/// A primary store that can be told to fail one specific write.
struct FlakyPrimary {
    inner: InMemoryStore,
    fail_set: std::sync::Mutex<Option<String>>,
    fail_delete: std::sync::Mutex<Option<String>>,
}

impl FlakyPrimary {
    fn new() -> Arc<Self> {
        Arc::new(Self {
            inner: InMemoryStore::new(),
            fail_set: std::sync::Mutex::new(None),
            fail_delete: std::sync::Mutex::new(None),
        })
    }

    fn fail_set_of(&self, key: &str) {
        *self.fail_set.lock().unwrap() = Some(key.to_owned());
    }

    fn fail_delete_of(&self, key: &str) {
        *self.fail_delete.lock().unwrap() = Some(key.to_owned());
    }

    fn stop_failing(&self) {
        *self.fail_set.lock().unwrap() = None;
        *self.fail_delete.lock().unwrap() = None;
    }

    async fn keys(&self) -> Vec<String> {
        let mut keys = self.inner.keys().await;
        keys.sort();
        keys
    }
}

#[async_trait]
impl CredentialStore for FlakyPrimary {
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
        self.inner.get(key).await
    }

    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
        if self.fail_set.lock().unwrap().as_deref() == Some(key) {
            return Err(AuthError::Store("injected write failure".into()));
        }
        self.inner.set(key, value).await
    }

    async fn delete(&self, key: &str) -> Result<(), AuthError> {
        if self.fail_delete.lock().unwrap().as_deref() == Some(key) {
            return Err(AuthError::Store("injected delete failure".into()));
        }
        self.inner.delete(key).await
    }
}

struct Fixture {
    primary: Arc<FlakyPrimary>,
    store: Arc<ProfileCredentialStore>,
    coordinator: Arc<dyn CommitCoordinator>,
    settings: Arc<InMemoryAuthSettings>,
}

fn fixture() -> Fixture {
    let primary = FlakyPrimary::new();
    let coordinator: Arc<dyn CommitCoordinator> = Arc::new(LocalCoordinator::new());
    let store = Arc::new(ProfileCredentialStore::with_primary(
        Arc::clone(&primary) as Arc<dyn CredentialStore>,
        Arc::clone(&coordinator),
    ));
    Fixture {
        primary,
        store,
        coordinator,
        settings: Arc::new(InMemoryAuthSettings::new()),
    }
}

fn oauth(provider: &str, token: &str) -> Credential {
    Credential {
        provider_id: provider.into(),
        kind: CredentialKind::OAuth,
        access_token: Some(Secret::new(token.into())),
        refresh_token: Some(Secret::new("refresh".into())),
        api_key: None,
        expires_at: None,
        scopes: Vec::new(),
        account: None,
    }
}

fn api_key(key: &str) -> Credential {
    Credential {
        provider_id: "anthropic-api-key".into(),
        kind: CredentialKind::ApiKey,
        access_token: None,
        refresh_token: None,
        api_key: Some(Secret::new(key.into())),
        expires_at: None,
        scopes: Vec::new(),
        account: None,
    }
}

async fn seed(fixture: &Fixture, credential: &Credential) {
    fixture
        .store
        .set(
            &format!("llmauth:{}", credential.provider_id),
            &serde_json::to_string(credential).unwrap(),
        )
        .await
        .unwrap();
}

async fn run(fixture: &Fixture, request: CommitRequest) -> CommitOutcome {
    tokio::time::timeout(
        std::time::Duration::from_secs(10),
        commit(
            &fixture.store,
            &fixture.coordinator,
            fixture.settings.as_ref(),
            request,
        ),
    )
    .await
    .expect("a commit must not deadlock against its own commit section")
}

async fn request_for(fixture: &Fixture, credential: Credential) -> CommitRequest {
    let identity =
        ProviderIdentity::from_stored_id(&credential.provider_id).expect("a known identity");
    CommitRequest::new(identity, credential)
        .with_settings(
            AuthSettingsPatch::empty().with_default_provider(Some(identity.engine_id().into())),
        )
        .with_baseline(
            coda_auth::service::transaction::Baseline::capture(&fixture.store)
                .await
                .expect("baseline"),
        )
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn a_successful_commit_leaves_one_credential_and_a_matching_choice() {
    let fixture = fixture();
    seed(&fixture, &oauth("github-copilot", "old-copilot")).await;
    fixture
        .store
        .set("coda-mcp:some-server", "unrelated secret")
        .await
        .unwrap();

    let request = request_for(&fixture, oauth("claude-ai", "new-claude")).await;
    let outcome = run(&fixture, request).await;

    assert!(matches!(outcome, CommitOutcome::Committed { .. }), "{outcome:?}");
    assert!(outcome.engine_may_start());
    assert!(fixture.store.read_only("llmauth:claude-ai").await.unwrap().is_some());
    assert!(fixture.store.read_only("llmauth:github-copilot").await.unwrap().is_none());
    assert_eq!(fixture.settings.current().default_provider.as_deref(), Some("claude-ai"));
    assert_eq!(
        fixture.store.read_only("coda-mcp:some-server").await.unwrap().as_deref(),
        Some("unrelated secret"),
        "a login must not touch unrelated secrets"
    );
}

#[tokio::test]
async fn a_failed_credential_write_changes_nothing() {
    let fixture = fixture();
    seed(&fixture, &oauth("github-copilot", "old-copilot")).await;
    let before = fixture.primary.keys().await;

    fixture.primary.fail_set_of("llmauth:claude-ai");
    let request = request_for(&fixture, oauth("claude-ai", "new-claude")).await;
    let outcome = run(&fixture, request).await;

    match outcome {
        CommitOutcome::Failed { step, rolled_back, .. } => {
            assert_eq!(step, CommitStep::StoreCredential);
            assert!(!rolled_back, "nothing had been written yet");
        }
        other => panic!("expected a failed commit, got {other:?}"),
    }
    assert!(fixture.store.read_only("llmauth:github-copilot").await.unwrap().is_some());
    assert_eq!(fixture.primary.keys().await, before, "no key may be added or removed");
    assert_eq!(fixture.settings.current(), AuthSettings::default());
}

#[tokio::test]
async fn a_failed_settings_write_restores_the_previous_account() {
    let fixture = fixture();
    let previous = oauth("github-copilot", "old-copilot");
    seed(&fixture, &previous).await;
    let previous_blob = fixture.store.read_only("llmauth:github-copilot").await.unwrap();

    fixture.settings.fail_next("injected settings failure");
    let request = request_for(&fixture, oauth("claude-ai", "new-claude")).await;
    let outcome = run(&fixture, request).await;

    match outcome {
        CommitOutcome::Failed { step, rolled_back, .. } => {
            assert_eq!(step, CommitStep::ApplySettings);
            assert!(rolled_back, "the credential write must have been undone");
        }
        other => panic!("expected a failed commit, got {other:?}"),
    }
    assert_eq!(
        fixture.store.read_only("llmauth:github-copilot").await.unwrap(),
        previous_blob,
        "the account that was working must still be readable, with the same credential value"
    );
    assert!(fixture.store.read_only("llmauth:claude-ai").await.unwrap().is_none());
    assert_eq!(fixture.settings.current(), AuthSettings::default());
}

#[tokio::test]
async fn rollback_restores_retirement_metadata_exactly_and_makes_no_meta_markers() {
    let fixture = fixture();
    // Copilot was logged out earlier, so its retirement marker exists; Claude
    // has never been retired.
    seed(&fixture, &oauth("github-copilot", "old-copilot")).await;
    fixture.store.delete("llmauth:github-copilot").await.unwrap();
    seed(&fixture, &api_key("sk-existing")).await;
    let keys_before = fixture.primary.keys().await;
    assert!(fixture.store.is_retired("llmauth:github-copilot").await.unwrap());
    assert!(!fixture.store.is_retired("llmauth:claude-ai").await.unwrap());

    fixture.settings.fail_next("injected settings failure");
    let request = request_for(&fixture, oauth("claude-ai", "new-claude")).await;
    let outcome = run(&fixture, request).await;
    assert!(matches!(outcome, CommitOutcome::Failed { rolled_back: true, .. }), "{outcome:?}");

    assert!(
        fixture.store.is_retired("llmauth:github-copilot").await.unwrap(),
        "a marker that existed before the commit must still exist after the rollback"
    );
    assert!(
        !fixture.store.is_retired("llmauth:claude-ai").await.unwrap(),
        "a rollback must not invent retirement state for a credential that was simply absent"
    );
    assert_eq!(
        fixture.primary.keys().await,
        keys_before,
        "the profile must hold exactly the keys it held before, and no meta-markers"
    );
    assert!(
        fixture.store.read_only("llmauth:anthropic-api-key").await.unwrap().is_some(),
        "the displaced credential must be back"
    );
}

#[tokio::test]
async fn a_legitimate_login_after_a_logout_still_works() {
    let fixture = fixture();
    seed(&fixture, &oauth("claude-ai", "first")).await;
    fixture.store.delete("llmauth:claude-ai").await.unwrap();
    assert!(fixture.store.is_retired("llmauth:claude-ai").await.unwrap());

    let request = request_for(&fixture, oauth("claude-ai", "second")).await;
    let outcome = run(&fixture, request).await;

    assert!(matches!(outcome, CommitOutcome::Committed { .. }), "{outcome:?}");
    assert!(
        fixture.store.read_only("llmauth:claude-ai").await.unwrap().is_some(),
        "a retirement marker must not block signing back in"
    );
}

#[tokio::test]
async fn a_failed_rollback_is_reported_and_never_authorizes_a_restart() {
    let fixture = fixture();
    seed(&fixture, &oauth("github-copilot", "old-copilot")).await;

    // The settings write fails, and so does the write that would put the old
    // credential back.
    fixture.settings.fail_next("injected settings failure");
    let request = request_for(&fixture, oauth("claude-ai", "new-claude")).await;
    fixture.primary.fail_set_of("llmauth:github-copilot");
    let outcome = run(&fixture, request).await;

    match &outcome {
        CommitOutcome::RestorationFailed { failed_step, restore_step, .. } => {
            assert_eq!(*failed_step, CommitStep::ApplySettings);
            assert_eq!(
                *restore_step,
                CommitStep::RestoreCredential(ProviderIdentity::GithubCopilot)
            );
        }
        other => panic!("expected a restoration failure, got {other:?}"),
    }
    assert!(
        !outcome.engine_may_start(),
        "an engine must not be started against a profile we could not put back"
    );
    let rendered = outcome.to_string();
    assert!(!rendered.contains("injected"), "internal detail must not leak: {rendered}");
    fixture.primary.stop_failing();
}

#[tokio::test]
async fn a_commit_refuses_when_another_account_was_signed_in_meanwhile() {
    let fixture = fixture();
    seed(&fixture, &oauth("github-copilot", "old-copilot")).await;
    let request = request_for(&fixture, oauth("claude-ai", "new-claude")).await;

    // Another process signs a different account in while the login was being
    // prepared.
    seed(&fixture, &api_key("sk-someone-elses")).await;
    fixture.store.delete("llmauth:github-copilot").await.unwrap();

    let outcome = run(&fixture, request).await;
    assert!(matches!(outcome, CommitOutcome::Superseded { .. }), "{outcome:?}");
    assert!(
        fixture.store.read_only("llmauth:claude-ai").await.unwrap().is_none(),
        "a superseded commit writes nothing"
    );
    assert_eq!(fixture.settings.current(), AuthSettings::default());
}

#[tokio::test]
async fn a_token_refresh_during_preparation_is_not_an_account_switch() {
    let fixture = fixture();
    let existing = oauth("github-copilot", "old-copilot");
    seed(&fixture, &existing).await;
    let request = request_for(&fixture, oauth("claude-ai", "new-claude")).await;

    // The engine refreshes the credential that is about to be replaced: same
    // account, new access token.
    let mut refreshed = existing.clone();
    refreshed.access_token = Some(Secret::new("rotated-copilot".into()));
    refreshed.expires_at = Some(chrono::Utc::now() + chrono::Duration::hours(1));
    seed(&fixture, &refreshed).await;

    let outcome = run(&fixture, request).await;
    assert!(
        matches!(outcome, CommitOutcome::Committed { .. }),
        "a rotated access token is not a different account: {outcome:?}"
    );
}

#[tokio::test]
async fn a_commit_holds_the_section_and_still_completes() {
    // The transaction uses raw store operations only: calling a manager method
    // that takes the same section from inside it would deadlock, and this is
    // the test that would hang if that regressed.
    let fixture = fixture();
    let request = request_for(&fixture, api_key("sk-fresh")).await;
    let outcome = run(&fixture, request).await;
    assert!(matches!(outcome, CommitOutcome::Committed { .. }), "{outcome:?}");

    // The section must be free afterwards.
    tokio::time::timeout(
        std::time::Duration::from_secs(5),
        fixture.coordinator.begin(AUTH_COMMIT_KEY),
    )
    .await
    .expect("the commit section must have been released")
    .expect("begin");
}

#[tokio::test]
async fn a_failed_removal_of_the_displaced_account_restores_both_credentials() {
    let fixture = fixture();
    let displaced = oauth("github-copilot", "old-copilot");
    seed(&fixture, &displaced).await;
    let displaced_blob = fixture.store.read_only("llmauth:github-copilot").await.unwrap();
    let keys_before = fixture.primary.keys().await;

    fixture.primary.fail_delete_of("llmauth:github-copilot");
    let request = request_for(&fixture, oauth("claude-ai", "new-claude")).await;
    let outcome = run(&fixture, request).await;

    match outcome {
        CommitOutcome::Failed { step, rolled_back, .. } => {
            assert_eq!(step, CommitStep::RemoveDisplaced(ProviderIdentity::GithubCopilot));
            assert!(rolled_back);
        }
        other => panic!("expected a failed commit, got {other:?}"),
    }
    assert_eq!(
        fixture.store.read_only("llmauth:github-copilot").await.unwrap(),
        displaced_blob
    );
    assert!(fixture.store.read_only("llmauth:claude-ai").await.unwrap().is_none());
    assert_eq!(
        fixture.primary.keys().await,
        keys_before,
        "the interrupted removal must leave no retirement marker behind"
    );
    assert_eq!(fixture.settings.current(), AuthSettings::default());
    fixture.primary.stop_failing();
}

#[tokio::test]
async fn a_copilot_commit_saves_the_deployment_it_signed_in_to() {
    let fixture = fixture();
    let identity = ProviderIdentity::GithubCopilot;
    let request = CommitRequest::new(identity, oauth("github-copilot", "tenant-token"))
        .with_settings(
            AuthSettingsPatch::empty()
                .with_default_provider(Some(identity.engine_id().into()))
                .with_github_enterprise_domain(Some("octocorp.ghe.com".into())),
        )
        .with_baseline(
            coda_auth::service::transaction::Baseline::capture(&fixture.store).await.unwrap(),
        );

    let outcome = run(&fixture, request).await;
    assert!(matches!(outcome, CommitOutcome::Committed { .. }), "{outcome:?}");
    assert_eq!(
        fixture.settings.current(),
        AuthSettings {
            default_provider: Some("github-copilot".into()),
            github_enterprise_domain: Some("octocorp.ghe.com".into()),
        }
    );
}

// ── The environment selection: a commit that stores no credential ────────────
//
// `CommitRequest::environment_api_key` records the choice and removes what it
// displaces. Every guarantee of an ordinary commit still applies: the same
// section, the same baseline, the same rollback to the exact prior bytes.

fn environment_request() -> CommitRequest {
    CommitRequest::environment_api_key().with_settings(
        AuthSettingsPatch::empty()
            .with_default_provider(Some(ProviderIdentity::AnthropicApiKey.engine_id().into())),
    )
}

async fn environment_request_for(fixture: &Fixture) -> CommitRequest {
    environment_request().with_baseline(
        coda_auth::service::transaction::Baseline::capture(&fixture.store).await.expect("baseline"),
    )
}

#[tokio::test]
async fn an_environment_commit_records_the_choice_and_writes_no_key() {
    let fixture = fixture();
    seed(&fixture, &api_key("sk-ant-previously-stored")).await;
    seed(&fixture, &oauth("github-copilot", "old-copilot")).await;

    let outcome = run(&fixture, environment_request_for(&fixture).await).await;

    match &outcome {
        CommitOutcome::Committed { identity, replaced, settings_changed } => {
            assert_eq!(*identity, ProviderIdentity::AnthropicApiKey);
            assert!(
                replaced.contains(&ProviderIdentity::AnthropicApiKey),
                "the stored key of the same identity is removed too: {replaced:?}"
            );
            assert!(replaced.contains(&ProviderIdentity::GithubCopilot), "{replaced:?}");
            assert!(settings_changed);
        }
        other => panic!("expected a committed environment selection, got {other:?}"),
    }
    assert!(
        fixture.store.read_only("llmauth:anthropic-api-key").await.unwrap().is_none(),
        "no credential blob may be written for a key that stays in the environment"
    );
    for key in fixture.primary.keys().await {
        let value = fixture.primary.get(&key).await.unwrap().unwrap_or_default();
        assert!(!value.contains("sk-ant-previously-stored"), "leaked under {key}");
    }
    assert_eq!(fixture.settings.current().default_provider.as_deref(), Some("anthropic"));
}

#[tokio::test]
async fn an_environment_commit_retires_the_key_it_replaces_even_when_none_is_readable() {
    // Nothing is stored *here*, but a legacy source that is unavailable at
    // this moment may still hold a key. Publishing the removal is what stops
    // it answering in place of the environment once it comes back.
    let fixture = fixture();
    let outcome = run(&fixture, environment_request_for(&fixture).await).await;

    match &outcome {
        CommitOutcome::Committed { replaced, .. } => assert!(
            replaced.is_empty(),
            "nothing readable was removed, so nothing may be reported as removed: {replaced:?}"
        ),
        other => panic!("expected a committed environment selection, got {other:?}"),
    }
    assert!(
        fixture.store.is_retired("llmauth:anthropic-api-key").await.unwrap(),
        "the removal must be published, not merely attempted against what is visible"
    );
}

#[tokio::test]
async fn an_environment_commit_that_cannot_remove_the_stored_key_puts_it_back_exactly() {
    let fixture = fixture();
    seed(&fixture, &api_key("sk-ant-previously-stored")).await;
    let blob_before = fixture.store.read_only("llmauth:anthropic-api-key").await.unwrap();
    let keys_before = fixture.primary.keys().await;

    fixture.primary.fail_delete_of("llmauth:anthropic-api-key");
    let outcome = run(&fixture, environment_request_for(&fixture).await).await;

    match outcome {
        CommitOutcome::Failed { step, rolled_back, .. } => {
            assert_eq!(step, CommitStep::RemoveDisplaced(ProviderIdentity::AnthropicApiKey));
            assert!(rolled_back);
        }
        other => panic!("expected a failed commit, got {other:?}"),
    }
    assert_eq!(
        fixture.store.read_only("llmauth:anthropic-api-key").await.unwrap(),
        blob_before,
        "the key that was working must be readable again, byte for byte"
    );
    assert!(!fixture.store.is_retired("llmauth:anthropic-api-key").await.unwrap());
    assert_eq!(
        fixture.primary.keys().await,
        keys_before,
        "the interrupted removal must leave no retirement marker behind"
    );
    assert_eq!(fixture.settings.current(), AuthSettings::default());
    fixture.primary.stop_failing();
}

#[tokio::test]
async fn an_environment_commit_whose_settings_write_fails_restores_the_removed_key() {
    let fixture = fixture();
    seed(&fixture, &api_key("sk-ant-previously-stored")).await;
    let blob_before = fixture.store.read_only("llmauth:anthropic-api-key").await.unwrap();
    let keys_before = fixture.primary.keys().await;

    fixture.settings.fail_next("injected settings failure");
    let outcome = run(&fixture, environment_request_for(&fixture).await).await;

    match outcome {
        CommitOutcome::Failed { step, rolled_back, .. } => {
            assert_eq!(step, CommitStep::ApplySettings);
            assert!(rolled_back, "the removal must have been undone");
        }
        other => panic!("expected a failed commit, got {other:?}"),
    }
    assert_eq!(
        fixture.store.read_only("llmauth:anthropic-api-key").await.unwrap(),
        blob_before,
        "a selection that never landed must not have cost the user their stored key"
    );
    assert!(!fixture.store.is_retired("llmauth:anthropic-api-key").await.unwrap());
    assert_eq!(fixture.primary.keys().await, keys_before);
    assert_eq!(fixture.settings.current(), AuthSettings::default());
}

#[tokio::test]
async fn an_environment_commit_refuses_when_another_account_was_signed_in_meanwhile() {
    let fixture = fixture();
    let request = environment_request_for(&fixture).await;

    seed(&fixture, &oauth("claude-ai", "someone-elses-login")).await;

    let outcome = run(&fixture, request).await;
    assert!(matches!(outcome, CommitOutcome::Superseded { .. }), "{outcome:?}");
    assert!(
        fixture.store.read_only("llmauth:claude-ai").await.unwrap().is_some(),
        "a superseded commit removes nothing"
    );
    assert!(!fixture.store.is_retired("llmauth:anthropic-api-key").await.unwrap());
    assert_eq!(fixture.settings.current(), AuthSettings::default());
}

#[tokio::test]
async fn an_environment_commit_carrying_a_copilot_context_is_refused_before_any_write() {
    // A context binds endpoints to a credential. This commit writes none, so a
    // caller attaching one is asking for a binding nothing can satisfy: it is
    // refused, and neither the profile nor the published context moves.
    let fixture = fixture();
    seed(&fixture, &oauth("github-copilot", "old-copilot")).await;
    let keys_before = fixture.primary.keys().await;

    let cell = coda_auth::service::CopilotContextCell::initial(
        coda_auth::provider::copilot::CopilotConfig::default_public(),
        coda_auth::provider::copilot::CopilotDeployment::Public,
        None,
    );
    let generation_before = cell.current().generation;
    let request = environment_request_for(&fixture).await.with_context(
        coda_auth::service::PendingCopilotContext::new(
            Arc::clone(&cell),
            coda_auth::provider::copilot::CopilotConfig::default_public(),
            coda_auth::provider::copilot::CopilotDeployment::Enterprise {
                domain: "octocorp.ghe.com".into(),
            },
        ),
    );

    let outcome = run(&fixture, request).await;
    match outcome {
        CommitOutcome::Failed { step, failure, rolled_back } => {
            assert_eq!(step, CommitStep::StoreCredential);
            assert_eq!(failure, coda_auth::AuthFailure::ProviderMismatch);
            assert!(!rolled_back, "nothing was written, so nothing was undone");
        }
        other => panic!("expected a refused commit, got {other:?}"),
    }
    assert_eq!(cell.current().generation, generation_before, "no context may be published");
    assert_eq!(fixture.primary.keys().await, keys_before);
    assert_eq!(fixture.settings.current(), AuthSettings::default());
}
