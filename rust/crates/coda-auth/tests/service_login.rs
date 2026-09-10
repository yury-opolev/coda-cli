//! The shared auth service: prepare, commit, status, logout and verification.
//!
//! Every fixture here is local: an in-memory profile, an in-memory settings
//! port, an explicit environment map, and loopback HTTP sockets. Nothing reads
//! the developer's real credentials, keyring, settings file or environment.

mod common;

use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use coda_auth::coordination::{CommitCoordinator, LocalCoordinator};
use coda_auth::credential::{Credential, CredentialKind};
use coda_auth::error::AuthError;
use coda_auth::provider::claude_ai::{ClaudeAiConfig, ALL_OAUTH_SCOPES};
use coda_auth::provider::DeviceCodePrompt;
use coda_auth::secret::Secret;
use coda_auth::service::{
    ApiKeySource, AuthEnvironment, AuthService, AuthSettings, CommitInterruption, CommitOutcome,
    CommitStep, InMemoryAuthSettings, LoginRequest, LoginUi, LogoutFailure, MapEnvironment,
    PrepareFailure, ProviderIdentity, StoredState, UnverifiedPolicy, VerificationOutcome,
};
use coda_auth::store::{CredentialStore, InMemoryStore, ProfileCredentialStore};
use coda_llm::{LlmClient, LlmError, ModelInfo};

use common::http::{http_get, FakeHttp};

// ── Fixture ──────────────────────────────────────────────────────────────────

struct Harness {
    primary: Arc<InMemoryStore>,
    store: Arc<ProfileCredentialStore>,
    settings: Arc<InMemoryAuthSettings>,
    environment: Arc<MapEnvironment>,
    service: AuthService,
}

async fn harness_with(env: &[(&str, &str)], claude: ClaudeAiConfig) -> Harness {
    harness_over(Arc::new(InMemoryStore::new()), env, claude).await
}

/// A harness over an explicit primary store, so a test can make a specific
/// write fail — or panic — inside the transaction.
async fn harness_over(
    primary: Arc<InMemoryStore>,
    env: &[(&str, &str)],
    claude: ClaudeAiConfig,
) -> Harness {
    harness_over_store(Arc::clone(&primary) as Arc<dyn CredentialStore>, primary, env, claude).await
}

async fn harness_over_store(
    store_impl: Arc<dyn CredentialStore>,
    primary: Arc<InMemoryStore>,
    env: &[(&str, &str)],
    claude: ClaudeAiConfig,
) -> Harness {
    let coordinator: Arc<dyn CommitCoordinator> = Arc::new(LocalCoordinator::new());
    let store = Arc::new(ProfileCredentialStore::with_primary(
        store_impl,
        Arc::clone(&coordinator),
    ));
    let settings = Arc::new(InMemoryAuthSettings::new());
    let environment = Arc::new(MapEnvironment::new(env));
    let service = AuthService::builder(Arc::clone(&store), Arc::clone(&coordinator))
        .with_settings(Arc::clone(&settings) as Arc<dyn coda_auth::service::AuthSettingsPort>)
        .with_environment(Arc::clone(&environment) as Arc<dyn AuthEnvironment>)
        .with_claude_config(claude)
        .with_login_timeout(Duration::from_secs(5))
        .build()
        .await
        .expect("the provider context resolves");
    Harness { primary, store, settings, environment, service }
}

async fn harness() -> Harness {
    harness_with(&[], ClaudeAiConfig::production("test-client")).await
}

fn oauth(provider: &str, token: &str) -> Credential {
    Credential {
        provider_id: provider.into(),
        kind: CredentialKind::OAuth,
        access_token: Some(Secret::new(token.into())),
        refresh_token: Some(Secret::new("refresh".into())),
        api_key: None,
        expires_at: Some(chrono::Utc::now() + chrono::Duration::hours(2)),
        scopes: vec!["user:inference".into()],
        account: None,
    }
}

async fn seed(harness: &Harness, credential: &Credential) {
    harness
        .store
        .set(
            &format!("llmauth:{}", credential.provider_id),
            &serde_json::to_string(credential).unwrap(),
        )
        .await
        .unwrap();
}

async fn keys(harness: &Harness) -> Vec<String> {
    let mut keys = harness.primary.keys().await;
    keys.sort();
    keys
}

// ── Login UI doubles ─────────────────────────────────────────────────────────

/// A UI that answers everything, and records what it was asked.
#[derive(Default)]
struct ScriptedUi {
    api_key: Option<String>,
    authorization_urls: std::sync::Mutex<Vec<String>>,
    device_prompts: std::sync::Mutex<Vec<DeviceCodePrompt>>,
    /// When set, the callback URL is fetched as a browser would.
    complete_browser_login: bool,
    cancel: bool,
}

impl ScriptedUi {
    fn with_key(key: &str) -> Arc<Self> {
        Arc::new(Self { api_key: Some(key.into()), ..Self::default() })
    }

    fn browser() -> Arc<Self> {
        Arc::new(Self { complete_browser_login: true, ..Self::default() })
    }

    fn cancelling() -> Arc<Self> {
        Arc::new(Self { cancel: true, ..Self::default() })
    }

    fn urls(&self) -> Vec<String> {
        self.authorization_urls.lock().unwrap().clone()
    }

    fn prompts(&self) -> Vec<DeviceCodePrompt> {
        self.device_prompts.lock().unwrap().clone()
    }
}

#[async_trait]
impl LoginUi for ScriptedUi {
    async fn api_key(&self) -> Result<Secret<String>, AuthError> {
        if self.cancel {
            return Err(AuthError::LoginCancelled("the user pressed escape".into()));
        }
        Ok(Secret::new(self.api_key.clone().unwrap_or_default()))
    }

    async fn authorization_url(&self, url: &str) -> Result<(), AuthError> {
        self.authorization_urls.lock().unwrap().push(url.to_owned());
        if self.cancel {
            return Err(AuthError::LoginCancelled("the user closed the browser prompt".into()));
        }
        if self.complete_browser_login {
            // Act as the browser: follow the redirect back to the loopback
            // listener the flow owns.
            let redirect = redirect_from(url);
            tokio::spawn(async move {
                let _ = http_get(&redirect).await;
            });
        }
        Ok(())
    }

    async fn device_code(&self, prompt: DeviceCodePrompt) -> Result<(), AuthError> {
        self.device_prompts.lock().unwrap().push(prompt);
        if self.cancel {
            return Err(AuthError::LoginCancelled("the user cancelled the device login".into()));
        }
        Ok(())
    }
}

/// Builds the loopback callback URL from an authorize URL, the way a browser
/// would after the user approves.
fn redirect_from(authorize_url: &str) -> String {
    let query = authorize_url.split_once('?').map(|(_, q)| q).unwrap_or("");
    let mut redirect = String::new();
    let mut state = String::new();
    for pair in query.split('&') {
        let (key, value) = pair.split_once('=').unwrap_or((pair, ""));
        match key {
            "redirect_uri" => redirect = percent_decode(value),
            "state" => state = value.to_owned(),
            _ => {}
        }
    }
    format!("{redirect}?code=auth-code-from-browser&state={state}")
}

fn percent_decode(value: &str) -> String {
    let bytes = value.as_bytes();
    let mut out = String::new();
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'%' && index + 2 < bytes.len() {
            if let Ok(byte) = u8::from_str_radix(&value[index + 1..index + 3], 16) {
                out.push(byte as char);
                index += 3;
                continue;
            }
        }
        out.push(bytes[index] as char);
        index += 1;
    }
    out
}

// ── LLM client doubles ───────────────────────────────────────────────────────

/// A store that can be told to fail one specific write or delete.
struct FlakyStore {
    inner: Arc<InMemoryStore>,
    fail_set: std::sync::Mutex<Option<String>>,
    fail_delete: std::sync::Mutex<Option<String>>,
}

impl FlakyStore {
    fn over(inner: Arc<InMemoryStore>) -> Arc<Self> {
        Arc::new(Self {
            inner,
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
}

#[async_trait]
impl CredentialStore for FlakyStore {
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

/// A store whose delete panics, standing in for any bug or abort that kills a
/// commit task half-way through.
struct PanicOnDelete {
    inner: Arc<InMemoryStore>,
    panic_on: &'static str,
}

#[async_trait]
impl CredentialStore for PanicOnDelete {
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
        self.inner.get(key).await
    }

    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
        self.inner.set(key, value).await
    }

    async fn delete(&self, key: &str) -> Result<(), AuthError> {
        assert_ne!(key, self.panic_on, "injected commit-task failure");
        self.inner.delete(key).await
    }
}

/// A client whose `list_models` is a warm cache and whose `refresh_models`
/// really asks the provider.
struct CachingClient {
    provider_id: &'static str,
    cached: Vec<ModelInfo>,
    refresh: Result<Vec<ModelInfo>, LlmError>,
    refreshes: std::sync::atomic::AtomicUsize,
}

impl CachingClient {
    fn new(refresh: Result<Vec<ModelInfo>, LlmError>) -> Self {
        Self {
            provider_id: "anthropic",
            cached: vec![model("cached-model")],
            refresh,
            refreshes: std::sync::atomic::AtomicUsize::new(0),
        }
    }

    fn refresh_count(&self) -> usize {
        self.refreshes.load(std::sync::atomic::Ordering::SeqCst)
    }
}

fn model(id: &str) -> ModelInfo {
    ModelInfo::new(id)
}

/// A probe that answers as a working credential does.
fn working_probe() -> Arc<dyn LlmClient> {
    Arc::new(CachingClient::new(Ok(vec![model("claude-opus-5")]))) as Arc<dyn LlmClient>
}

#[async_trait]
impl LlmClient for CachingClient {
    fn provider_id(&self) -> &str {
        self.provider_id
    }

    async fn stream(
        &self,
        _request: coda_llm::ChatRequest,
    ) -> Result<coda_llm::ResponseStream, LlmError> {
        panic!("verification must never start a completion");
    }

    async fn list_models(&self) -> Result<Vec<ModelInfo>, LlmError> {
        Ok(self.cached.clone())
    }

    async fn refresh_models(&self) -> Result<Vec<ModelInfo>, LlmError> {
        self.refreshes.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        match &self.refresh {
            Ok(models) => Ok(models.clone()),
            Err(error) => Err(clone_error(error)),
        }
    }
}

fn clone_error(error: &LlmError) -> LlmError {    match error {
        LlmError::Unauthorized(message) => LlmError::Unauthorized(message.clone()),
        LlmError::Transport(message) => LlmError::Transport(message.clone()),
        LlmError::Api { status, message, kind, retry_after, body } => LlmError::Api {
            status: *status,
            message: message.clone(),
            kind: *kind,
            retry_after: *retry_after,
            body: body.clone(),
        },
        other => LlmError::Transport(other.to_string()),
    }
}

// ── 1. Preparation writes nothing ────────────────────────────────────────────

#[tokio::test]
async fn preparing_an_api_key_login_writes_nothing() {
    let harness = harness().await;
    seed(&harness, &oauth("github-copilot", "existing")).await;
    let before = keys(&harness).await;

    let prepared = harness
        .service
        .prepare_login_with_probe(
            LoginRequest::api_key(ApiKeySource::Prompt),
            ScriptedUi::with_key("sk-ant-new-key").as_ref(),
            Some(working_probe()),
        )
        .await
        .expect("preparation succeeds");

    assert_eq!(prepared.identity(), ProviderIdentity::AnthropicApiKey);
    assert_eq!(keys(&harness).await, before, "preparation must not write a credential");
    assert_eq!(harness.settings.applied_count(), 0, "preparation must not write settings");
    assert_eq!(harness.settings.current(), AuthSettings::default());
}

#[tokio::test]
async fn a_convenience_flag_cannot_trade_a_working_account_for_an_unchecked_key() {
    // `without_validation` is for a first sign-in. With an account already
    // connected, the key is checked regardless — and a rejection keeps the
    // account that works.
    let harness = harness().await;
    seed(&harness, &oauth("github-copilot", "existing")).await;
    let before = keys(&harness).await;

    let probe = Arc::new(CachingClient::new(Err(LlmError::Unauthorized("invalid x-api-key".into()))));
    let failure = harness
        .service
        .prepare_login_with_probe(
            LoginRequest::api_key(ApiKeySource::Prompt).without_validation(),
            ScriptedUi::with_key("sk-ant-bad").as_ref(),
            Some(Arc::clone(&probe) as Arc<dyn LlmClient>),
        )
        .await
        .expect_err("the check must still run");
    assert!(matches!(failure, PrepareFailure::Rejected(_)), "{failure:?}");
    assert_eq!(probe.refresh_count(), 1);
    assert_eq!(keys(&harness).await, before);

    // With nothing to displace, the flag does what it says.
    let (_dir, empty) = (0, harness_with(&[], ClaudeAiConfig::production("c")).await);
    let prepared = empty
        .service
        .prepare_login(
            LoginRequest::api_key(ApiKeySource::Prompt).without_validation(),
            ScriptedUi::with_key("sk-ant-first").as_ref(),
        )
        .await
        .expect("a first sign-in needs no probe");
    assert!(!prepared.is_verified(), "and it is not recorded as verified");
}

#[tokio::test]
async fn a_prepared_login_never_prints_its_secret() {
    let harness = harness().await;
    let prepared = harness
        .service
        .prepare_login(
            LoginRequest::api_key(ApiKeySource::Prompt).without_validation(),
            ScriptedUi::with_key("sk-ant-TOPSECRET").as_ref(),
        )
        .await
        .expect("prepared");
    let rendered = format!("{prepared:?}");
    assert!(!rendered.contains("sk-ant-TOPSECRET"), "{rendered}");
    assert!(rendered.contains("REDACTED"), "{rendered}");
}

#[tokio::test]
async fn a_cancelled_login_leaves_the_previous_account_untouched() {
    let harness = harness().await;
    seed(&harness, &oauth("github-copilot", "existing")).await;
    let before = keys(&harness).await;

    let failure = harness
        .service
        .prepare_login(LoginRequest::api_key(ApiKeySource::Prompt), ScriptedUi::cancelling().as_ref())
        .await
        .expect_err("a cancelled login has no prepared result");

    assert!(matches!(failure, PrepareFailure::Cancelled), "{failure:?}");
    assert_eq!(keys(&harness).await, before);
    assert_eq!(harness.settings.applied_count(), 0);
}

#[tokio::test]
async fn a_blank_api_key_is_refused_before_anything_is_prepared() {
    let harness = harness().await;
    let failure = harness
        .service
        .prepare_login(LoginRequest::api_key(ApiKeySource::Prompt), ScriptedUi::with_key("   ").as_ref())
        .await
        .expect_err("a blank key is not a credential");
    assert!(matches!(failure, PrepareFailure::Rejected(_)), "{failure:?}");
    assert!(!failure.to_string().contains("sk-"), "{failure}");
}

#[tokio::test]
async fn a_failed_browser_login_leaves_the_previous_account_untouched() {
    // The token endpoint rejects the exchange: no credential, nothing written.
    let token_endpoint = FakeHttp::start(vec![(400, r#"{"error":"invalid_grant"}"#.into())]).await;
    let harness = harness_with(
        &[],
        ClaudeAiConfig::production("test-client").with_token_url(&token_endpoint.base_url),
    )
    .await;
    seed(&harness, &oauth("github-copilot", "existing")).await;
    let before = keys(&harness).await;

    let ui = ScriptedUi::browser();
    let failure = harness
        .service
        .prepare_login(LoginRequest::claude_ai(), ui.as_ref())
        .await
        .expect_err("a rejected exchange is not a login");

    assert!(!matches!(failure, PrepareFailure::Cancelled), "{failure:?}");
    assert_eq!(keys(&harness).await, before);
    assert_eq!(harness.settings.applied_count(), 0);
    assert_eq!(ui.urls().len(), 1, "the host was asked to open exactly one URL");
}

#[tokio::test]
async fn a_successful_browser_login_prepares_a_claude_identity_without_writing() {
    let token_endpoint = FakeHttp::start(vec![(
        200,
        r#"{"access_token":"acc_live","refresh_token":"ref_live","expires_in":3600,"scope":"user:inference"}"#
            .into(),
    )])
    .await;
    let harness = harness_with(
        &[],
        ClaudeAiConfig::production("test-client").with_token_url(&token_endpoint.base_url),
    )
    .await;
    let before = keys(&harness).await;

    let ui = ScriptedUi::browser();
    let prepared = harness
        .service
        .prepare_login(LoginRequest::claude_ai(), ui.as_ref())
        .await
        .expect("the loopback callback completes the exchange");

    assert_eq!(prepared.identity(), ProviderIdentity::ClaudeAi);
    assert_eq!(keys(&harness).await, before, "preparation writes nothing");
    let url = ui.urls().first().cloned().unwrap_or_default();
    for scope in ALL_OAUTH_SCOPES {
        assert!(url.contains(&scope.replace(':', "%3A")), "{url}");
    }

    // And committing it stores exactly that identity.
    let outcome = harness.service.commit_login(prepared).await;
    assert!(matches!(outcome, CommitOutcome::Committed { .. }), "{outcome:?}");
    assert!(harness.store.read_only("llmauth:claude-ai").await.unwrap().is_some());
    assert_eq!(harness.settings.current().default_provider.as_deref(), Some("claude-ai"));
}

#[tokio::test]
async fn a_copilot_device_login_shows_the_code_and_records_the_tenant_without_writing() {
    let device = FakeHttp::start(vec![(
        200,
        r#"{"device_code":"dev","user_code":"WXYZ-1234","verification_uri":"https://github.com/login/device","expires_in":30,"interval":0}"#
            .into(),
    )])
    .await;
    let token = FakeHttp::start(vec![(200, r#"{"access_token":"gho_live_token"}"#.into())]).await;

    // The endpoints are redirected through the environment port, so no test
    // touches the real GitHub endpoints or the process environment.
    let harness = harness_with(
        &[
            ("GH_COPILOT_DEVICE_CODE_URL", &device.base_url),
            ("GH_COPILOT_TOKEN_URL", &token.base_url),
            ("GH_COPILOT_USE_EXCHANGE", "0"),
        ],
        ClaudeAiConfig::production("c"),
    )
    .await;
    seed(&harness, &oauth("claude-ai", "existing")).await;
    let before = keys(&harness).await;

    let ui = ScriptedUi::browser();
    let prepared = harness
        .service
        .prepare_login(LoginRequest::copilot(None), ui.as_ref())
        .await
        .expect("device login");

    assert_eq!(prepared.identity(), ProviderIdentity::GithubCopilot);
    assert_eq!(
        ui.prompts().first().map(|prompt| prompt.user_code.clone()),
        Some("WXYZ-1234".into()),
        "the host is handed the code to display, for this login only"
    );
    assert_eq!(keys(&harness).await, before, "preparation writes nothing");
    assert_eq!(harness.settings.applied_count(), 0);
    assert!(
        prepared.replaces().contains(&ProviderIdentity::ClaudeAi),
        "the account this login would remove must be disclosed"
    );
    // A redirected endpoint is not a tenant: nothing is recorded as one.
    assert_eq!(prepared.deployment(), None);

    let outcome = harness.service.commit_login(prepared).await;
    assert!(matches!(outcome, CommitOutcome::Committed { .. }), "{outcome:?}");
    assert!(harness.store.read_only("llmauth:github-copilot").await.unwrap().is_some());
    assert!(harness.store.read_only("llmauth:claude-ai").await.unwrap().is_none());
    assert_eq!(
        harness.settings.current().default_provider.as_deref(),
        Some("github-copilot")
    );
}

// ── Environment-key logins are validated before displacing an account ────────

#[tokio::test]
async fn an_environment_key_is_validated_before_it_displaces_a_saved_account() {
    let harness = harness_with(&[("ANTHROPIC_API_KEY", "sk-env-key")], ClaudeAiConfig::production("c")).await;
    seed(&harness, &oauth("github-copilot", "existing")).await;
    let before = keys(&harness).await;

    // The probe says the key is rejected.
    let probe = Arc::new(CachingClient::new(Err(LlmError::Unauthorized("invalid x-api-key".into()))));
    let failure = harness
        .service
        .prepare_login_with_probe(
            LoginRequest::api_key(ApiKeySource::Environment),
            ScriptedUi::with_key("unused").as_ref(),
            Some(Arc::clone(&probe) as Arc<dyn LlmClient>),
        )
        .await
        .expect_err("a rejected key must not be committed");

    assert!(matches!(failure, PrepareFailure::Rejected(_)), "{failure:?}");
    assert_eq!(probe.refresh_count(), 1, "validation must really ask the provider");
    assert_eq!(keys(&harness).await, before, "the working credential is still there");
}

#[tokio::test]
async fn an_unavailable_validation_is_not_a_rejection_and_needs_a_decision() {
    let harness = harness_with(&[("ANTHROPIC_API_KEY", "sk-env-key")], ClaudeAiConfig::production("c")).await;
    seed(&harness, &oauth("github-copilot", "existing")).await;

    let probe = Arc::new(CachingClient::new(Err(LlmError::Transport("connection reset".into()))));
    let failure = harness
        .service
        .prepare_login_with_probe(
            LoginRequest::api_key(ApiKeySource::Environment),
            ScriptedUi::with_key("unused").as_ref(),
            Some(Arc::clone(&probe) as Arc<dyn LlmClient>),
        )
        .await
        .expect_err("an unavailable check must not silently proceed");
    assert!(matches!(failure, PrepareFailure::ValidationUnavailable { .. }), "{failure:?}");

    // With an explicit host decision it proceeds, and says it is unverified.
    let prepared = harness
        .service
        .prepare_login_with_probe(
            LoginRequest::api_key(ApiKeySource::Environment)
                .with_unverified_policy(UnverifiedPolicy::AllowedByHost),
            ScriptedUi::with_key("unused").as_ref(),
            Some(Arc::clone(&probe) as Arc<dyn LlmClient>),
        )
        .await
        .expect("the host explicitly allowed proceeding unverified");
    assert!(!prepared.is_verified());
    assert!(
        prepared.replaces().contains(&ProviderIdentity::GithubCopilot),
        "the user must be told which stored account this removes"
    );
}

// ── I4: an unresolved Copilot context must never become the public default ───

/// A builder over an isolated in-memory profile with an explicit settings port.
fn service_with_settings(
    settings: Arc<InMemoryAuthSettings>,
    env: &[(&str, &str)],
) -> (Arc<ProfileCredentialStore>, coda_auth::service::AuthServiceBuilder) {
    let primary = Arc::new(InMemoryStore::new());
    let coordinator: Arc<dyn CommitCoordinator> = Arc::new(LocalCoordinator::new());
    let store = Arc::new(ProfileCredentialStore::with_primary(
        Arc::clone(&primary) as Arc<dyn CredentialStore>,
        Arc::clone(&coordinator),
    ));
    let builder = AuthService::builder(Arc::clone(&store), coordinator)
        .with_settings(settings as Arc<dyn coda_auth::service::AuthSettingsPort>)
        .with_environment(Arc::new(MapEnvironment::new(env)))
        .with_claude_config(ClaudeAiConfig::production("c"));
    (store, builder)
}

#[tokio::test]
async fn a_service_refuses_to_build_when_the_provider_context_cannot_be_read() {
    let settings = Arc::new(InMemoryAuthSettings::new());
    settings.fail_load("settings.json contains invalid JSON");
    let (_store, builder) = service_with_settings(Arc::clone(&settings), &[]);

    let error = builder.build().await.expect_err("an unreadable context must not build a service");
    let rendered = coda_auth::safe_message(&error);
    assert!(!rendered.contains("invalid JSON"), "the raw detail must not ride out: {rendered}");
}

#[tokio::test]
async fn an_invalid_saved_tenant_never_authorizes_against_public_github() {
    let device = FakeHttp::start(vec![(200, "{}".into())]).await;
    let token = FakeHttp::start(vec![(200, "{}".into())]).await;
    let settings = Arc::new(InMemoryAuthSettings::with(AuthSettings {
        default_provider: Some("github-copilot".into()),
        // A saved value this build refuses to turn into endpoints.
        github_enterprise_domain: Some("https://octocorp.ghe.com/enterprise".into()),
    }));
    let (store, builder) = service_with_settings(
        Arc::clone(&settings),
        &[
            ("GH_COPILOT_DEVICE_CODE_URL", &device.base_url),
            ("GH_COPILOT_TOKEN_URL", &token.base_url),
        ],
    );

    // The strict constructor refuses outright.
    let (store2, builder2) = service_with_settings(Arc::clone(&settings), &[]);
    assert!(builder2.build().await.is_err(), "an invalid tenant must not build a signing-in service");
    drop(store2);

    // The reporting constructor still works, but cannot sign in to Copilot and
    // cannot refresh a Copilot credential against public endpoints.
    let service = builder.build_degraded().await;
    let status = service.status().await.expect("status still renders");
    assert!(
        status.provider_context_error.is_some(),
        "the broken tenant configuration must be reported"
    );

    let failure = service
        .prepare_login(LoginRequest::copilot(None), ScriptedUi::browser().as_ref())
        .await
        .expect_err("a login must not proceed on an unresolved tenant");
    assert!(!matches!(failure, PrepareFailure::Cancelled), "{failure:?}");
    assert_eq!(device.hits(), 0, "no device authorization may be requested");
    assert_eq!(token.hits(), 0, "no token endpoint may be contacted");

    // The manager it exposes must not have a public Copilot provider wired.
    let refresh = service.manager().get_credential("github-copilot").await;
    assert!(
        matches!(refresh, Err(AuthError::UnknownProvider(_))),
        "a Copilot provider must not be registered against endpoints we could not resolve: {refresh:?}"
    );
    assert!(store.read_only("llmauth:github-copilot").await.unwrap().is_none());
}

#[tokio::test]
async fn a_copilot_login_stops_before_the_network_when_settings_become_unreadable() {
    let device = FakeHttp::start(vec![(200, "{}".into())]).await;
    let settings = Arc::new(InMemoryAuthSettings::new());
    let (store, builder) = service_with_settings(
        Arc::clone(&settings),
        &[("GH_COPILOT_DEVICE_CODE_URL", &device.base_url)],
    );
    let service = builder.build().await.expect("a clean context builds");

    // Another process corrupts settings.json after the service was built.
    settings.fail_load("settings.json cannot be read");

    let failure = service
        .prepare_login(LoginRequest::copilot(None), ScriptedUi::browser().as_ref())
        .await
        .expect_err("the saved tenant is unknown, so the login must stop");
    assert!(!matches!(failure, PrepareFailure::Cancelled), "{failure:?}");
    assert_eq!(device.hits(), 0, "nothing may be authorized against a guessed deployment");
    assert!(store.read_only("llmauth:github-copilot").await.unwrap().is_none());
}

// ── The refresh must follow the deployment that was committed ────────────────
//
// A Copilot credential is refreshed by sending its durable GitHub token to the
// tenant's exchange endpoint. If the manager keeps the configuration it was
// built with, a login that *changed* deployment leaves the new token being
// exchanged at the old host — a credential sent to the wrong server.

/// An environment whose Copilot exchange endpoint can be moved, standing in
/// for a deployment change (public ↔ enterprise resolve to different hosts).
#[derive(Debug)]
struct MovingEndpointEnv {
    device: String,
    token: String,
    exchange: std::sync::Mutex<String>,
}

impl MovingEndpointEnv {
    fn new(device: &str, token: &str, exchange: &str) -> Arc<Self> {
        Arc::new(Self {
            device: device.to_owned(),
            token: token.to_owned(),
            exchange: std::sync::Mutex::new(exchange.to_owned()),
        })
    }

    fn move_exchange_to(&self, url: &str) {
        *self.exchange.lock().unwrap() = url.to_owned();
    }
}

impl AuthEnvironment for MovingEndpointEnv {
    fn var(&self, name: &str) -> Option<String> {
        match name {
            "GH_COPILOT_DEVICE_CODE_URL" => Some(self.device.clone()),
            "GH_COPILOT_TOKEN_URL" => Some(self.token.clone()),
            "GH_COPILOT_COPILOT_TOKEN_URL" => Some(self.exchange.lock().unwrap().clone()),
            _ => None,
        }
    }
}

fn copilot_token_body(token: &str) -> String {
    // Already expired, so the very next read forces a refresh.
    let expires_at = chrono::Utc::now().timestamp() - 60;
    format!(r#"{{"token":"{token}","expires_at":{expires_at}}}"#)
}

/// Builds a service whose Copilot endpoints come from `env`.
async fn copilot_harness(env: Arc<MovingEndpointEnv>, saved_domain: Option<&str>) -> Harness {
    let primary = Arc::new(InMemoryStore::new());
    let coordinator: Arc<dyn CommitCoordinator> = Arc::new(LocalCoordinator::new());
    let store = Arc::new(ProfileCredentialStore::with_primary(
        Arc::clone(&primary) as Arc<dyn CredentialStore>,
        Arc::clone(&coordinator),
    ));
    let settings = Arc::new(InMemoryAuthSettings::with(AuthSettings {
        default_provider: Some("github-copilot".into()),
        github_enterprise_domain: saved_domain.map(str::to_owned),
    }));
    let service = AuthService::builder(Arc::clone(&store), Arc::clone(&coordinator))
        .with_settings(Arc::clone(&settings) as Arc<dyn coda_auth::service::AuthSettingsPort>)
        .with_environment(Arc::clone(&env) as Arc<dyn AuthEnvironment>)
        .with_claude_config(ClaudeAiConfig::production("c"))
        .with_login_timeout(Duration::from_secs(5))
        .build()
        .await
        .expect("a clean context builds");
    Harness {
        primary,
        store,
        settings,
        environment: Arc::new(MapEnvironment::new(&[])),
        service,
    }
}

async fn sign_in_to_copilot(
    harness: &Harness,
    choice: coda_auth::provider::copilot::CopilotDeploymentChoice,
) {
    let prepared = harness
        .service
        .prepare_login(LoginRequest::copilot(Some(choice)), ScriptedUi::browser().as_ref())
        .await
        .expect("device login");
    let outcome = harness.service.commit_login(prepared).await;
    assert!(matches!(outcome, CommitOutcome::Committed { .. }), "{outcome:?}");
}

#[tokio::test]
async fn a_refresh_follows_the_deployment_the_login_committed() {
    use coda_auth::provider::copilot::CopilotDeploymentChoice;

    let device = FakeHttp::start(vec![(
        200,
        r#"{"device_code":"dev","user_code":"CODE","verification_uri":"https://example.invalid/device","expires_in":30,"interval":0}"#
            .into(),
    )])
    .await;
    let token = FakeHttp::start(vec![(200, r#"{"access_token":"ghu_durable"}"#.into())]).await;
    let old_tenant = FakeHttp::start(vec![(200, copilot_token_body("old-tenant-token"))]).await;
    let new_tenant = FakeHttp::start(vec![(200, copilot_token_body("new-tenant-token"))]).await;

    // The service starts life resolved against the old deployment.
    let env = MovingEndpointEnv::new(&device.base_url, &token.base_url, &old_tenant.base_url);
    let harness = copilot_harness(Arc::clone(&env), Some("octocorp.ghe.com")).await;

    // The user signs in to a different deployment, which resolves elsewhere.
    env.move_exchange_to(&new_tenant.base_url);
    sign_in_to_copilot(&harness, CopilotDeploymentChoice::Public).await;
    let after_login = (old_tenant.hits(), new_tenant.hits());
    assert_eq!(after_login.0, 0, "the login itself must not contact the old tenant");
    assert!(after_login.1 >= 1, "the login exchanged its token at the chosen deployment");

    // A conflicting ambient domain appears afterwards: it must not move the
    // committed choice.
    env.move_exchange_to(&old_tenant.base_url);

    // Force the refresh the manager performs on read.
    let credential = harness
        .service
        .manager()
        .get_credential("github-copilot")
        .await
        .expect("the refresh succeeds");
    assert!(credential.is_some());

    assert_eq!(
        old_tenant.hits(),
        0,
        "the durable token must never be exchanged at the deployment the user left"
    );
    assert!(
        new_tenant.hits() > after_login.1,
        "the refresh must reach the deployment the login committed"
    );
}

#[tokio::test]
async fn the_reverse_switch_is_bound_the_same_way() {
    use coda_auth::provider::copilot::CopilotDeploymentChoice;

    let device = FakeHttp::start(vec![(
        200,
        r#"{"device_code":"dev","user_code":"CODE","verification_uri":"https://example.invalid/device","expires_in":30,"interval":0}"#
            .into(),
    )])
    .await;
    let token = FakeHttp::start(vec![(200, r#"{"access_token":"ghu_durable"}"#.into())]).await;
    let public = FakeHttp::start(vec![(200, copilot_token_body("public-token"))]).await;
    let enterprise = FakeHttp::start(vec![(200, copilot_token_body("enterprise-token"))]).await;

    // Public first, then an explicit enterprise sign-in.
    let env = MovingEndpointEnv::new(&device.base_url, &token.base_url, &public.base_url);
    let harness = copilot_harness(Arc::clone(&env), None).await;
    env.move_exchange_to(&enterprise.base_url);
    sign_in_to_copilot(
        &harness,
        CopilotDeploymentChoice::Enterprise("octocorp.ghe.com".into()),
    )
    .await;
    let after_login = enterprise.hits();

    harness
        .service
        .manager()
        .get_credential("github-copilot")
        .await
        .expect("refresh");

    assert_eq!(public.hits(), 0, "the public exchange must not see an enterprise token");
    assert!(enterprise.hits() > after_login);
}

#[tokio::test]
async fn a_credential_from_another_login_is_never_refreshed_at_the_committed_tenant() {
    use coda_auth::provider::AuthProvider;
    use coda_auth::service::{ContextBoundCopilotProvider, CopilotContextCell};
    use coda_auth::provider::copilot::{CopilotConfig, CopilotDeployment};

    // A context bound to one login must refuse a credential from another,
    // before anything is sent: that is the "old token, new tenant" pairing.
    let tenant = FakeHttp::start(vec![(200, copilot_token_body("tenant-token"))]).await;
    let config = CopilotConfig {
        copilot_token_url: Some(tenant.base_url.clone()),
        use_exchange: true,
        ..CopilotConfig::default_public()
    };
    let committed = copilot_credential("ghu_committed");
    let cell = CopilotContextCell::initial(
        config.clone(),
        CopilotDeployment::Public,
        Some(&committed),
    );
    let provider = ContextBoundCopilotProvider::new(Arc::clone(&cell));
    cell.publish(config, CopilotDeployment::Public, &committed);

    let error = provider
        .refresh(&copilot_credential("ghu_from_another_login"))
        .await
        .expect_err("a credential this context was not published for must be refused");
    assert!(matches!(error, coda_auth::AuthError::CannotRefresh(_, _)), "{error:?}");
    assert_eq!(tenant.hits(), 0, "nothing may be sent before the pairing is confirmed");

    // The credential it *was* published for still refreshes, and so does the
    // result of that refresh (the durable token survives it).
    let refreshed = provider.refresh(&committed).await.expect("the bound credential refreshes");
    assert_eq!(tenant.hits(), 1);
    provider.refresh(&refreshed).await.expect("a refreshed credential is still the same login");
}

/// A Copilot credential that is not due for a refresh, so a lookup exercises
/// the header path rather than the exchange path.
fn fresh_copilot_credential(durable: &str) -> Credential {
    Credential {
        expires_at: Some(chrono::Utc::now() + chrono::Duration::hours(2)),
        ..copilot_credential(durable)
    }
}

fn copilot_credential(durable: &str) -> Credential {
    Credential {
        provider_id: "github-copilot".into(),
        kind: CredentialKind::OAuth,
        access_token: Some(Secret::new("copilot-token".into())),
        refresh_token: Some(Secret::new(durable.into())),
        api_key: None,
        expires_at: Some(chrono::Utc::now() - chrono::Duration::minutes(1)),
        scopes: Vec::new(),
        account: None,
    }
}

// ── Stale references must fail closed, not carry a new tenant's token ────────

/// A loopback endpoint that blocks inside the request until the test releases
/// it, so an interleaving can be forced instead of raced.
struct GatedHttp {
    base_url: String,
    entered: Arc<tokio::sync::Semaphore>,
    release: Arc<tokio::sync::Semaphore>,
}

impl GatedHttp {
    async fn start(body: String) -> Self {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let port = listener.local_addr().expect("addr").port();
        let entered = Arc::new(tokio::sync::Semaphore::new(0));
        let release = Arc::new(tokio::sync::Semaphore::new(0));
        let (task_entered, task_release) = (Arc::clone(&entered), Arc::clone(&release));

        tokio::spawn(async move {
            while let Ok((mut socket, _)) = listener.accept().await {
                let mut buf = vec![0u8; 8192];
                let _ = socket.read(&mut buf).await;
                task_entered.add_permits(1);
                task_release.acquire().await.expect("open").forget();
                let response = format!(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                let _ = socket.write_all(response.as_bytes()).await;
            }
        });
        Self { base_url: format!("http://127.0.0.1:{port}"), entered, release }
    }

    async fn wait_until_in_flight(&self) {
        tokio::time::timeout(Duration::from_secs(10), self.entered.acquire())
            .await
            .expect("a request should have arrived")
            .expect("open")
            .forget();
    }

    fn let_it_answer(&self) {
        self.release.add_permits(1);
    }
}

/// A manager over an isolated in-memory store with the context-bound provider.
fn bound_manager(
    store: Arc<ProfileCredentialStore>,
    coordinator: Arc<dyn CommitCoordinator>,
    cell: Arc<coda_auth::service::CopilotContextCell>,
) -> Arc<coda_auth::CredentialManager> {
    use coda_auth::provider::AuthProvider;
    use coda_auth::service::ContextBoundCopilotProvider;

    Arc::new(coda_auth::CredentialManager::with_coordinator(
        store as Arc<dyn CredentialStore>,
        [Arc::new(ContextBoundCopilotProvider::new(cell)) as Arc<dyn AuthProvider>],
        coordinator,
    ))
}

fn isolated_store() -> (Arc<InMemoryStore>, Arc<ProfileCredentialStore>, Arc<dyn CommitCoordinator>) {
    let primary = Arc::new(InMemoryStore::new());
    let coordinator: Arc<dyn CommitCoordinator> = Arc::new(LocalCoordinator::new());
    let store = Arc::new(ProfileCredentialStore::with_primary(
        Arc::clone(&primary) as Arc<dyn CredentialStore>,
        Arc::clone(&coordinator),
    ));
    (primary, store, coordinator)
}

async fn seed_raw(store: &Arc<ProfileCredentialStore>, credential: &Credential) {
    store
        .set(
            &format!("llmauth:{}", credential.provider_id),
            &serde_json::to_string(credential).unwrap(),
        )
        .await
        .expect("seed");
}

#[tokio::test]
async fn a_bound_context_reuses_its_exchange_absence_latch_until_replaced() {
    use coda_auth::provider::AuthProvider;
    use coda_auth::service::{ContextBoundCopilotProvider, CopilotContextCell};
    use coda_auth::provider::copilot::CopilotDeployment;
    let absent = FakeHttp::start(vec![(404, "{}".into())]).await;
    let credential = copilot_credential("ghu_context_latch");
    let cell = CopilotContextCell::initial(
        copilot_config_at(&absent.base_url), CopilotDeployment::Public, Some(&credential),
    );
    let store = Arc::new(InMemoryStore::new());
    store.set("llmauth:github-copilot", &serde_json::to_string(&credential).unwrap()).await.unwrap();
    let manager = coda_auth::CredentialManager::new(
        store.clone(),
        [Arc::new(ContextBoundCopilotProvider::new(cell.clone())) as Arc<dyn AuthProvider>],
    );
    for _ in 0..2 {
        manager.get_credential("github-copilot").await.unwrap().unwrap();
    }
    assert_eq!(absent.hits(), 1, "an absent exchange endpoint must not be probed per request");

    let available = FakeHttp::start(vec![(200, copilot_token_body("new-context-token"))]).await;
    cell.publish(copilot_config_at(&available.base_url), CopilotDeployment::Public, &credential);
    store.set("llmauth:github-copilot", &serde_json::to_string(&credential).unwrap()).await.unwrap();
    manager.get_credential("github-copilot").await.unwrap().unwrap();
    assert_eq!(available.hits(), 1, "a new context must not inherit the old endpoint's absent verdict");
    assert_eq!(absent.hits(), 1);
}

fn copilot_config_at(exchange: &str) -> coda_auth::provider::copilot::CopilotConfig {
    coda_auth::provider::copilot::CopilotConfig {
        copilot_token_url: Some(exchange.to_owned()),
        use_exchange: true,
        ..coda_auth::provider::copilot::CopilotConfig::default_public()
    }
}

/// A store whose reads block until the test releases them, so a context change
/// can be forced *inside* a credential lookup that would otherwise succeed.
struct GatedStore {
    inner: Arc<InMemoryStore>,
    gate_key: &'static str,
    entered: Arc<tokio::sync::Semaphore>,
    release: Arc<tokio::sync::Semaphore>,
    armed: std::sync::atomic::AtomicBool,
}

impl GatedStore {
    fn over(inner: Arc<InMemoryStore>, gate_key: &'static str) -> Arc<Self> {
        Arc::new(Self {
            inner,
            gate_key,
            entered: Arc::new(tokio::sync::Semaphore::new(0)),
            release: Arc::new(tokio::sync::Semaphore::new(0)),
            armed: std::sync::atomic::AtomicBool::new(false),
        })
    }

    fn arm(&self) {
        self.armed.store(true, std::sync::atomic::Ordering::SeqCst);
    }

    async fn wait_until_reading(&self) {
        tokio::time::timeout(Duration::from_secs(10), self.entered.acquire())
            .await
            .expect("a read should have arrived")
            .expect("open")
            .forget();
    }

    fn let_the_read_finish(&self) {
        self.release.add_permits(1);
    }
}

#[async_trait]
impl CredentialStore for GatedStore {
    async fn get(&self, key: &str) -> Result<Option<String>, AuthError> {
        if key == self.gate_key && self.armed.load(std::sync::atomic::Ordering::SeqCst) {
            self.entered.add_permits(1);
            self.release.acquire().await.expect("open").forget();
        }
        self.inner.get(key).await
    }

    async fn set(&self, key: &str, value: &str) -> Result<(), AuthError> {
        self.inner.set(key, value).await
    }

    async fn delete(&self, key: &str) -> Result<(), AuthError> {
        self.inner.delete(key).await
    }
}

#[tokio::test]
async fn a_context_change_during_a_successful_lookup_still_refuses_the_headers() {
    use coda_auth::provider::copilot::CopilotDeployment;
    use coda_auth::service::{copilot_connection, CopilotContextCell};

    // The credential does *not* need refreshing, so the lookup would succeed
    // and hand back headers. The only thing standing between those headers and
    // a client that now points at the previous tenant is the re-check after
    // the lookup.
    let (primary, _unused, _c) = isolated_store();
    let gated = GatedStore::over(Arc::clone(&primary), "llmauth:github-copilot");
    let coordinator: Arc<dyn CommitCoordinator> = Arc::new(LocalCoordinator::new());
    let store = Arc::new(ProfileCredentialStore::with_primary(
        Arc::clone(&gated) as Arc<dyn CredentialStore>,
        Arc::clone(&coordinator),
    ));
    let mut credential = copilot_credential("ghu_durable");
    // Fresh, so no refresh happens during the lookup.
    credential.expires_at = Some(chrono::Utc::now() + chrono::Duration::hours(2));
    seed_raw(&store, &credential).await;

    let cell = CopilotContextCell::initial(
        copilot_config_at("https://api.githubcopilot.com"),
        CopilotDeployment::Public,
        Some(&credential),
    );
    let manager = bound_manager(Arc::clone(&store), Arc::clone(&coordinator), Arc::clone(&cell));
    let connection = copilot_connection(Arc::clone(&manager), Arc::clone(&cell));

    gated.arm();
    let source: Arc<dyn coda_llm::CredentialSource> = Arc::clone(&connection.source);
    let lookup = tokio::spawn(async move { source.auth_headers().await });
    gated.wait_until_reading().await;

    // A login lands while the lookup is inside the store: same account, a
    // different deployment.
    cell.publish(
        copilot_config_at("https://copilot-api.octocorp.ghe.com"),
        CopilotDeployment::Enterprise { domain: "octocorp.ghe.com".into() },
        &credential,
    );
    gated.let_the_read_finish();

    let result = lookup.await.expect("the lookup task finishes");
    assert!(
        matches!(result, Err(coda_llm::LlmError::Unauthorized(_))),
        "headers looked up under the previous context must not reach the caller: {result:?}"
    );
}

#[tokio::test]
async fn a_context_change_during_a_refresh_discards_the_result() {
    use coda_auth::provider::copilot::CopilotDeployment;
    use coda_auth::service::{copilot_connection, CopilotContextCell};

    // The exchange is held open, so the login lands *while the lookup is in
    // flight* rather than before or after it.
    let tenant = GatedHttp::start(copilot_token_body("refreshed-token")).await;
    let (_primary, store, coordinator) = isolated_store();
    let credential = copilot_credential("ghu_durable");
    seed_raw(&store, &credential).await;

    let config = copilot_config_at(&tenant.base_url);
    let cell = CopilotContextCell::initial(
        config.clone(),
        CopilotDeployment::Public,
        Some(&credential),
    );
    let manager = bound_manager(Arc::clone(&store), Arc::clone(&coordinator), Arc::clone(&cell));
    let connection = copilot_connection(Arc::clone(&manager), Arc::clone(&cell));

    let source: Arc<dyn coda_llm::CredentialSource> = Arc::clone(&connection.source);
    let lookup = tokio::spawn(async move { source.auth_headers().await });
    tenant.wait_until_in_flight().await;

    // A login commits a new context while the exchange is still open.
    cell.publish(config, CopilotDeployment::Public, &copilot_credential("ghu_other"));
    tenant.let_it_answer();

    let result = lookup.await.expect("the lookup task finishes");
    assert!(
        matches!(result, Err(coda_llm::LlmError::Unauthorized(_))),
        "headers minted under the old context must not reach the caller: {result:?}"
    );
}

#[tokio::test]
async fn headers_are_refused_when_the_stored_credential_is_not_the_bound_one() {
    use coda_auth::provider::copilot::CopilotDeployment;
    use coda_auth::service::{copilot_connection, CopilotContextCell};

    // The shape an interrupted (`Indeterminate`) commit leaves: a new context
    // is published while the credential in the store is still the old one.
    let tenant = FakeHttp::start(vec![(200, copilot_token_body("token"))]).await;
    let (_primary, store, coordinator) = isolated_store();
    let stored = fresh_copilot_credential("ghu_still_the_old_one");
    seed_raw(&store, &stored).await;

    let config = copilot_config_at(&tenant.base_url);
    let cell = CopilotContextCell::initial(config.clone(), CopilotDeployment::Public, Some(&stored));
    let manager = bound_manager(Arc::clone(&store), Arc::clone(&coordinator), Arc::clone(&cell));

    // The publication names a credential that never reached the store.
    cell.publish(config, CopilotDeployment::Public, &fresh_copilot_credential("ghu_never_stored"));

    // A *fresh* connection — current generation, so the staleness check passes
    // — must still refuse, because the credential does not match the context.
    let connection = copilot_connection(Arc::clone(&manager), Arc::clone(&cell));
    let error = connection
        .source
        .auth_headers()
        .await
        .expect_err("a mismatched credential must not produce headers");
    assert!(matches!(error, coda_llm::LlmError::Unauthorized(_)), "{error:?}");
    assert_eq!(tenant.hits(), 0, "nothing may be exchanged for a credential we refuse");
}

#[tokio::test]
async fn a_paired_connection_cannot_mix_an_old_url_with_a_current_source() {
    use coda_auth::provider::copilot::CopilotDeployment;
    use coda_auth::service::{copilot_connection, CopilotContextCell};

    let first = FakeHttp::start(vec![(200, copilot_token_body("first-token"))]).await;
    let second = FakeHttp::start(vec![(200, copilot_token_body("second-token"))]).await;
    let (_primary, store, coordinator) = isolated_store();
    let credential = copilot_credential("ghu_durable");
    seed_raw(&store, &credential).await;

    let cell = CopilotContextCell::initial(
        copilot_config_at(&first.base_url),
        CopilotDeployment::Public,
        Some(&credential),
    );
    let manager = bound_manager(Arc::clone(&store), Arc::clone(&coordinator), Arc::clone(&cell));

    // One snapshot: endpoints and source together.
    let before = copilot_connection(Arc::clone(&manager), Arc::clone(&cell));
    assert_eq!(before.context.config.copilot_token_url.as_deref(), Some(first.base_url.as_str()));

    // A login moves the deployment and the account.
    let moved = copilot_credential("ghu_moved");
    seed_raw(&store, &moved).await;
    cell.publish(copilot_config_at(&second.base_url), CopilotDeployment::Public, &moved);

    // The pair taken before the switch refuses as a unit.
    let error = before
        .source
        .auth_headers()
        .await
        .expect_err("a connection taken before the switch must fail closed");
    assert!(matches!(error, coda_llm::LlmError::Unauthorized(_)), "{error:?}");
    assert_eq!(first.hits(), 0, "the abandoned tenant sees nothing");

    // A pair taken after it is coherent, and works.
    let after = copilot_connection(Arc::clone(&manager), Arc::clone(&cell));
    assert_eq!(after.context.config.copilot_token_url.as_deref(), Some(second.base_url.as_str()));
    let headers = after
        .source
        .auth_headers()
        .await
        .expect("the current connection authenticates")
        .expect("headers");
    assert!(headers.iter().any(|(name, _)| name == "authorization"));
    assert_eq!(first.hits(), 0);
}

#[tokio::test]
async fn a_context_bound_at_construction_refuses_an_account_stored_by_someone_else() {
    use coda_auth::provider::copilot::CopilotDeployment;
    use coda_auth::service::{copilot_connection, CopilotContextCell};

    // Two holders of one profile: this one starts bound to the credential it
    // read, another process then replaces it with a different account. The
    // first must not refresh that account's token at its own endpoints.
    let mine = FakeHttp::start(vec![(200, copilot_token_body("my-token"))]).await;
    let (_primary, store, coordinator) = isolated_store();
    let original = fresh_copilot_credential("ghu_mine");
    seed_raw(&store, &original).await;

    let cell = CopilotContextCell::initial(
        copilot_config_at(&mine.base_url),
        CopilotDeployment::Public,
        Some(&original),
    );
    let manager = bound_manager(Arc::clone(&store), Arc::clone(&coordinator), Arc::clone(&cell));
    let connection = copilot_connection(Arc::clone(&manager), Arc::clone(&cell));
    connection
        .source
        .auth_headers()
        .await
        .expect("my own credential works")
        .expect("headers");
    let hits = mine.hits();

    // Out of band: a different account is signed in by another process.
    seed_raw(&store, &fresh_copilot_credential("ghu_somebody_else")).await;

    let error = connection
        .source
        .auth_headers()
        .await
        .expect_err("another account's token must not be presented at my endpoints");
    assert!(matches!(error, coda_llm::LlmError::Unauthorized(_)), "{error:?}");
    assert_eq!(mine.hits(), hits, "and nothing may be exchanged for it");
}

#[tokio::test]
async fn a_context_with_no_credential_behind_it_matches_nothing() {
    use coda_auth::provider::copilot::CopilotDeployment;
    use coda_auth::service::{copilot_connection, CopilotContextCell};

    // Nothing was stored when this context was established. A credential that
    // appears afterwards belongs to a login this process never made.
    let tenant = FakeHttp::start(vec![(200, copilot_token_body("token"))]).await;
    let (_primary, store, coordinator) = isolated_store();
    let cell = CopilotContextCell::initial(
        copilot_config_at(&tenant.base_url),
        CopilotDeployment::Public,
        None,
    );
    let manager = bound_manager(Arc::clone(&store), Arc::clone(&coordinator), Arc::clone(&cell));
    let connection = copilot_connection(Arc::clone(&manager), Arc::clone(&cell));

    seed_raw(&store, &fresh_copilot_credential("ghu_appeared_later")).await;

    let error = connection
        .source
        .auth_headers()
        .await
        .expect_err("an unbound context must match nothing, not everything");
    assert!(matches!(error, coda_llm::LlmError::Unauthorized(_)), "{error:?}");
    assert_eq!(tenant.hits(), 0);
}

#[tokio::test]
async fn a_service_binds_its_initial_context_to_the_stored_credential() {

    // The service reads the credential and resolves the endpoints as one
    // snapshot, so an existing credential keeps working...
    let tenant = FakeHttp::start(vec![(200, copilot_token_body("token"))]).await;
    let env = MovingEndpointEnv::new("http://127.0.0.1:1", "http://127.0.0.1:1", &tenant.base_url);
    let harness = copilot_harness(Arc::clone(&env), None).await;
    let stored = copilot_credential("ghu_existing");
    seed_raw(&harness.store, &stored).await;

    // ...but only for a service that read it. This one was built before the
    // credential existed, so its context is bound to nothing.
    let connection = harness.service.copilot_connection();
    assert!(!connection.context.is_bound());
    let error = connection
        .source
        .auth_headers()
        .await
        .expect_err("a context bound to nothing must refuse");
    assert!(matches!(error, coda_llm::LlmError::Unauthorized(_)), "{error:?}");

    // A service built now binds to it and works.
    let rebuilt = AuthService::builder(Arc::clone(&harness.store), Arc::new(LocalCoordinator::new()))
        .with_settings(Arc::clone(&harness.settings) as Arc<dyn coda_auth::service::AuthSettingsPort>)
        .with_environment(Arc::clone(&env) as Arc<dyn AuthEnvironment>)
        .build()
        .await
        .expect("builds");
    let connection = rebuilt.copilot_connection();
    assert!(connection.context.is_bound());
    let headers = connection
        .source
        .auth_headers()
        .await
        .expect("the credential this service bound to works")
        .expect("headers");
    assert!(headers.iter().any(|(name, _)| name == "authorization"));
}

#[tokio::test]
async fn a_source_from_before_a_tenant_change_refuses_instead_of_handing_over_the_new_token() {
    use coda_auth::provider::copilot::CopilotDeploymentChoice;

    let device = FakeHttp::start(vec![(
        200,
        r#"{"device_code":"dev","user_code":"CODE","verification_uri":"https://example.invalid/device","expires_in":30,"interval":0}"#
            .into(),
    )])
    .await;
    let token = FakeHttp::start(vec![(200, r#"{"access_token":"ghu_durable"}"#.into())]).await;
    let first = FakeHttp::start(vec![(200, copilot_token_body("first-tenant-token"))]).await;
    let second = FakeHttp::start(vec![(200, copilot_token_body("second-tenant-token"))]).await;

    let env = MovingEndpointEnv::new(&device.base_url, &token.base_url, &first.base_url);
    let harness = copilot_harness(Arc::clone(&env), Some("octocorp.ghe.com")).await;
    sign_in_to_copilot(
        &harness,
        CopilotDeploymentChoice::Enterprise("octocorp.ghe.com".into()),
    )
    .await;

    // A client built now holds this source; its inference URL is the first
    // tenant's and cannot change.
    let stale = harness.service.copilot_connection().source;
    let headers = stale
        .auth_headers()
        .await
        .expect("the source works while its context is in force")
        .expect("headers");
    assert!(headers.iter().any(|(name, _)| name == "authorization"));
    // That read refreshed at the first tenant, which is correct while it is
    // the context in force; nothing may be added to it after the switch.
    let first_hits = first.hits();

    // The user signs in to a different deployment.
    env.move_exchange_to(&second.base_url);
    let hits_before = second.hits();
    sign_in_to_copilot(&harness, CopilotDeploymentChoice::Public).await;

    // The old client asks for headers again. It must be refused: handing it
    // the new account's token would send that token to the *old* tenant's
    // inference host.
    let error = stale
        .auth_headers()
        .await
        .expect_err("a stale source must fail closed");
    assert!(matches!(error, coda_llm::LlmError::Unauthorized(_)), "{error:?}");
    let rendered = error.to_string();
    assert!(!rendered.contains("ghu_"), "no token material in the refusal: {rendered}");
    assert!(!rendered.contains("127.0.0.1"), "no endpoint in the refusal: {rendered}");
    assert_eq!(
        second.hits(),
        hits_before + 1,
        "the refusal costs no extra exchange; only the login itself contacted the new tenant"
    );

    // A source taken after the change works, and carries the new account.
    let fresh = harness.service.copilot_connection().source;
    let headers = fresh
        .auth_headers()
        .await
        .expect("a fresh source authenticates")
        .expect("headers");
    let authorization = headers
        .iter()
        .find(|(name, _)| name == "authorization")
        .map(|(_, value)| value.clone())
        .expect("authorization header");
    assert!(
        authorization.contains("second-tenant-token"),
        "the fresh source must carry the account that is signed in now"
    );
    assert_eq!(
        first.hits(),
        first_hits,
        "nothing may reach the abandoned tenant once the deployment has changed"
    );
}

// ── 5. Status ────────────────────────────────────────────────────────────────

#[tokio::test]
async fn an_unreadable_settings_file_never_selects_something_else() {
    // The saved choice cannot be read. Neither the exported key nor the one
    // credential that happens to be stored may quietly become the account.
    let harness = harness_with(&[("ANTHROPIC_API_KEY", "sk-env")], ClaudeAiConfig::production("c")).await;
    seed(&harness, &oauth("github-copilot", "stored")).await;
    let before = keys(&harness).await;
    harness.settings.fail_load("settings.json contains invalid JSON");

    let status = harness.service.status().await.expect("status still renders");

    assert!(status.settings_error.is_some(), "the fault must be reported");
    assert!(
        matches!(status.selection, Err(coda_auth::service::SelectionError::Unavailable { .. })),
        "{:?}",
        status.selection
    );
    assert!(
        status.provider(ProviderIdentity::GithubCopilot).is_present(),
        "per-entry facts survive: the credential is still reported as stored"
    );
    assert!(status.environment_api_key, "availability is still reported separately");
    assert_eq!(keys(&harness).await, before, "reading status changes nothing");

    // The same refusal on the selection path the engine uses.
    let selection = harness.service.selected_provider().await;
    assert!(
        matches!(selection, Err(coda_auth::service::SelectionError::Unavailable { .. })),
        "{selection:?}"
    );
    let explicit = harness.service.select(Some("copilot")).await;
    assert!(
        matches!(explicit, Err(coda_auth::service::SelectionError::Unavailable { .. })),
        "an unreadable settings file makes every selection unavailable: {explicit:?}"
    );
}

#[tokio::test]
async fn an_unreadable_credential_beside_a_readable_one_blocks_an_inferred_selection() {
    let harness = harness().await;
    harness.store.set("llmauth:claude-ai", "{not json").await.unwrap();
    seed(&harness, &oauth("github-copilot", "stored")).await;

    let status = harness.service.status().await.expect("status");
    assert!(
        matches!(status.selection, Err(coda_auth::service::SelectionError::Unavailable { .. })),
        "'the only stored account is Copilot' is not knowable here: {:?}",
        status.selection
    );
    assert!(status.has_inconsistency());
    assert_eq!(status.unreadable().len(), 1);

    // A named, readable account is still selectable — the name settles it.
    let named = harness
        .service
        .select(Some("copilot"))
        .await
        .expect("a named readable account is unambiguous");
    assert_eq!(named.identity, ProviderIdentity::GithubCopilot);

    // But naming the unreadable one is a storage fault, not a missing login.
    let unreadable = harness.service.select(Some("claude-ai")).await;
    assert!(
        matches!(unreadable, Err(coda_auth::service::SelectionError::Unavailable { .. })),
        "{unreadable:?}"
    );
}

#[tokio::test]
async fn status_reports_every_provider_without_refreshing_anything() {
    let harness = harness_with(&[("ANTHROPIC_API_KEY", "sk-env")], ClaudeAiConfig::production("c")).await;
    let mut expiring = oauth("claude-ai", "about-to-expire");
    expiring.expires_at = Some(chrono::Utc::now() - chrono::Duration::hours(1));
    seed(&harness, &expiring).await;

    let status = harness.service.status().await.expect("status");

    assert_eq!(status.providers.len(), 3, "all three identities are always listed");
    let claude = status.provider(ProviderIdentity::ClaudeAi);
    assert!(matches!(claude.state, StoredState::Present(_)), "{:?}", claude.state);
    assert!(matches!(
        status.provider(ProviderIdentity::GithubCopilot).state,
        StoredState::Absent
    ));
    assert!(status.environment_api_key, "an exported key is reported separately");
    assert_eq!(
        status.selection.as_ref().map(|s| s.identity).ok(),
        Some(ProviderIdentity::ClaudeAi),
        "the sole stored credential is the selection, not the ambient key"
    );
    // Nothing was refreshed: an expired token is still the stored one.
    let stored = harness.store.read_only("llmauth:claude-ai").await.unwrap().unwrap();
    assert!(stored.contains("about-to-expire"));
}

#[tokio::test]
async fn an_unreadable_credential_is_never_reported_as_no_credentials() {
    let harness = harness().await;
    harness.store.set("llmauth:claude-ai", "{not json").await.unwrap();

    let status = harness.service.status().await.expect("status still renders");
    let claude = status.provider(ProviderIdentity::ClaudeAi);
    match &claude.state {
        StoredState::Unreadable(failure) => {
            assert!(!failure.to_string().contains("not json"), "{failure}");
            assert!(!failure.to_string().contains("no credential"), "{failure}");
        }
        other => panic!("expected an unreadable credential, got {other:?}"),
    }
}

#[tokio::test]
async fn status_reports_a_saved_choice_whose_credential_is_missing_as_needing_login() {
    let harness = harness_with(&[("ANTHROPIC_API_KEY", "sk-env")], ClaudeAiConfig::production("c")).await;
    harness.settings.set(AuthSettings {
        default_provider: Some("claude-ai".into()),
        github_enterprise_domain: None,
    });

    let status = harness.service.status().await.expect("status");
    assert!(
        matches!(
            status.selection,
            Err(coda_auth::service::SelectionError::NeedsLogin { .. })
        ),
        "{:?}",
        status.selection
    );
    assert!(status.environment_api_key);
}

#[tokio::test]
async fn a_commit_whose_task_dies_is_reported_as_indeterminate_and_releases_teardown() {
    // The delete that removes the displaced account panics *after* the new
    // credential has been written. The profile is genuinely half-changed, and
    // saying "nothing was changed" or "the previous connection was restored"
    // would both be lies.
    let primary = Arc::new(InMemoryStore::new());
    let panicking: Arc<dyn CredentialStore> = Arc::new(PanicOnDelete {
        inner: Arc::clone(&primary),
        panic_on: "llmauth:github-copilot",
    });
    let harness = harness_over_store(
        panicking,
        Arc::clone(&primary),
        &[],
        ClaudeAiConfig::production("c"),
    )
    .await;
    seed(&harness, &oauth("github-copilot", "existing")).await;

    let prepared = harness
        .service
        .prepare_login_with_probe(
            LoginRequest::api_key(ApiKeySource::Prompt),
            ScriptedUi::with_key("sk-ant-new").as_ref(),
            Some(working_probe()),
        )
        .await
        .expect("prepared");

    let outcome = tokio::time::timeout(
        Duration::from_secs(10),
        harness.service.commit_login(prepared),
    )
    .await
    .expect("a dead commit task must not hang the caller");

    match &outcome {
        CommitOutcome::Indeterminate { .. } => {}
        other => panic!("expected an indeterminate outcome, got {other:?}"),
    }
    assert!(!outcome.engine_may_start(), "no engine may be started on this");
    assert!(
        !outcome.profile_is_intact(),
        "the profile was half-changed; claiming it is intact is the bug"
    );
    let rendered = outcome.to_string();
    assert!(!rendered.contains("nothing was changed"), "{rendered}");
    assert!(!rendered.contains("restored"), "{rendered}");

    // Teardown must be able to await it, and must not wait forever.
    tokio::time::timeout(Duration::from_secs(10), harness.service.await_pending_commit())
        .await
        .expect("the in-flight count must be released even when the task dies");

    // And the profile really is half-changed, which is why the outcome may not
    // claim otherwise: the new credential landed, the old one is still there.
    assert!(
        harness.store.read_only("llmauth:anthropic-api-key").await.unwrap().is_some(),
        "the new credential had already been written when the task died"
    );
    assert!(
        harness.store.read_only("llmauth:github-copilot").await.unwrap().is_some(),
        "and the one it was replacing was never removed"
    );
}

#[test]
fn a_commit_started_without_a_runtime_is_reported_rather_than_dropped() {
    // A host calling this from the wrong place must not get silence — and the
    // in-flight marker must not be left behind by a task that never existed.
    let runtime = tokio::runtime::Runtime::new().expect("runtime");
    let (harness, prepared) = runtime.block_on(async {
        let harness = harness().await;
        let prepared = harness
            .service
            .prepare_login(
                LoginRequest::api_key(ApiKeySource::Prompt).without_validation(),
                ScriptedUi::with_key("sk-ant-new").as_ref(),
            )
            .await
            .expect("prepared");
        (harness, prepared)
    });

    // Outside any runtime context: nothing can be spawned.
    let handle = harness.service.commit_login(prepared);
    let outcome = runtime.block_on(handle);
    assert!(
        matches!(
            outcome,
            CommitOutcome::Indeterminate { cause: CommitInterruption::NotStarted }
        ),
        "{outcome:?}"
    );
    assert!(!outcome.engine_may_start());

    runtime.block_on(async {
        tokio::time::timeout(Duration::from_secs(5), harness.service.await_pending_commit())
            .await
            .expect("teardown must not wait for a task that never started");
        assert!(
            harness.store.read_only("llmauth:anthropic-api-key").await.unwrap().is_none(),
            "and nothing was written"
        );
    });
}

#[tokio::test]
async fn a_dropped_commit_handle_still_releases_the_in_flight_count() {
    let harness = harness().await;
    let prepared = harness
        .service
        .prepare_login(
            LoginRequest::api_key(ApiKeySource::Prompt).without_validation(),
            ScriptedUi::with_key("sk-ant-new").as_ref(),
        )
        .await
        .expect("prepared");

    drop(harness.service.commit_login(prepared));

    tokio::time::timeout(Duration::from_secs(10), harness.service.await_pending_commit())
        .await
        .expect("teardown must not hang on a handle the host dropped");
    assert!(
        harness.store.read_only("llmauth:anthropic-api-key").await.unwrap().is_some(),
        "the commit still ran to completion"
    );
}

#[tokio::test]
async fn awaiting_a_pending_commit_returns_immediately_when_none_is_running() {
    let harness = harness().await;
    tokio::time::timeout(Duration::from_secs(5), harness.service.await_pending_commit())
        .await
        .expect("no commit is running");
}

// ── 9. Logout ────────────────────────────────────────────────────────────────

#[tokio::test]
async fn logging_out_leaves_the_host_disconnected_and_says_so() {
    let harness = harness_with(&[("ANTHROPIC_API_KEY", "sk-env")], ClaudeAiConfig::production("c")).await;
    seed(&harness, &oauth("claude-ai", "signed-in")).await;
    harness.settings.set(AuthSettings {
        default_provider: Some("claude-ai".into()),
        github_enterprise_domain: None,
    });

    let report = harness.service.logout(None).await.expect("logout");

    assert_eq!(report.removed, vec![ProviderIdentity::ClaudeAi]);
    assert!(report.environment_api_key_still_available);
    assert!(report.cleared_default_provider);
    assert!(harness.store.read_only("llmauth:claude-ai").await.unwrap().is_none());
    assert_eq!(harness.settings.current().default_provider, None);

    // No auto-fallback: the service does not now select the ambient key.
    let status = harness.service.status().await.expect("status");
    assert!(
        matches!(status.selection, Ok(selection) if selection.identity == ProviderIdentity::AnthropicApiKey)
            || matches!(status.selection, Err(_)),
        "the report describes availability; it never reconnects on its own"
    );
    assert_eq!(
        harness.service.selected_provider().await.err().is_some(),
        false,
        "an unconfigured profile with an exported key remains merely available"
    );
    let rendered = report.to_string();
    assert!(rendered.contains("ANTHROPIC_API_KEY"), "{rendered}");
    assert!(rendered.contains("other"), "the report mentions other processes: {rendered}");
}

#[tokio::test]
async fn logging_out_of_a_provider_that_is_not_connected_removes_nothing() {
    let harness = harness().await;
    seed(&harness, &oauth("claude-ai", "signed-in")).await;

    let report = harness
        .service
        .logout(Some(ProviderIdentity::GithubCopilot))
        .await
        .expect("logout");
    assert!(report.removed.is_empty());
    assert!(harness.store.read_only("llmauth:claude-ai").await.unwrap().is_some());
}

#[tokio::test]
async fn a_logout_that_cannot_finish_reports_what_it_could_not_do() {
    // The credential is deleted, then clearing the saved choice fails. A
    // profile that is signed out but still names the deleted account is not a
    // state to leave behind quietly, so the deletion is undone and reported.
    let primary = Arc::new(InMemoryStore::new());
    let flaky: Arc<FlakyStore> = FlakyStore::over(Arc::clone(&primary));
    let harness = harness_over_store(
        Arc::clone(&flaky) as Arc<dyn CredentialStore>,
        Arc::clone(&primary),
        &[],
        ClaudeAiConfig::production("c"),
    )
    .await;
    seed(&harness, &oauth("claude-ai", "signed-in")).await;
    let blob = harness.store.read_only("llmauth:claude-ai").await.unwrap();
    harness.settings.set(AuthSettings {
        default_provider: Some("claude-ai".into()),
        github_enterprise_domain: None,
    });
    harness.settings.fail_next("settings.json cannot be written");

    let failure = harness.service.logout(None).await.expect_err("the logout could not finish");
    match &failure {
        LogoutFailure::Failed { step, rolled_back, .. } => {
            assert_eq!(*step, CommitStep::ApplySettings);
            assert!(*rolled_back, "the credential must have been put back");
        }
        other => panic!("expected a reported failure, got {other:?}"),
    }
    assert!(failure.profile_is_intact());
    assert_eq!(
        harness.store.read_only("llmauth:claude-ai").await.unwrap(),
        blob,
        "a logout that could not finish must not silently lose the credential"
    );
}

#[tokio::test]
async fn a_logout_whose_rollback_fails_says_so_instead_of_reporting_signed_out() {
    let primary = Arc::new(InMemoryStore::new());
    let flaky: Arc<FlakyStore> = FlakyStore::over(Arc::clone(&primary));
    let harness = harness_over_store(
        Arc::clone(&flaky) as Arc<dyn CredentialStore>,
        Arc::clone(&primary),
        &[],
        ClaudeAiConfig::production("c"),
    )
    .await;
    seed(&harness, &oauth("claude-ai", "signed-in")).await;
    harness.settings.set(AuthSettings {
        default_provider: Some("claude-ai".into()),
        github_enterprise_domain: None,
    });
    harness.settings.fail_next("settings.json cannot be written");
    // And the write that would put the credential back also fails.
    flaky.fail_set_of("llmauth:claude-ai");

    let failure = harness.service.logout(None).await.expect_err("the logout could not finish");
    match &failure {
        LogoutFailure::RestorationFailed { failed_step, restore_step, .. } => {
            assert_eq!(*failed_step, CommitStep::ApplySettings);
            assert_eq!(
                *restore_step,
                CommitStep::RestoreCredential(ProviderIdentity::ClaudeAi)
            );
        }
        other => panic!("expected a restoration failure, got {other:?}"),
    }
    assert!(!failure.profile_is_intact(), "the profile may be incomplete and must say so");
    let rendered = failure.to_string();
    assert!(!rendered.contains("cannot be written"), "raw detail must not leak: {rendered}");
    assert!(!rendered.contains("signed out"), "{rendered}");
    flaky.stop_failing();
}

#[tokio::test]
async fn a_logout_whose_delete_fails_restores_the_credential_it_could_not_remove() {
    let primary = Arc::new(InMemoryStore::new());
    let flaky: Arc<FlakyStore> = FlakyStore::over(Arc::clone(&primary));
    let harness = harness_over_store(
        Arc::clone(&flaky) as Arc<dyn CredentialStore>,
        Arc::clone(&primary),
        &[],
        ClaudeAiConfig::production("c"),
    )
    .await;
    seed(&harness, &oauth("claude-ai", "signed-in")).await;
    let keys_before = keys(&harness).await;
    flaky.fail_delete_of("llmauth:claude-ai");

    let failure = harness.service.logout(None).await.expect_err("the delete failed");
    match &failure {
        LogoutFailure::Failed { step, rolled_back, .. } => {
            assert_eq!(*step, CommitStep::RemoveDisplaced(ProviderIdentity::ClaudeAi));
            assert!(*rolled_back);
        }
        other => panic!("expected a reported failure, got {other:?}"),
    }
    flaky.stop_failing();
    assert!(harness.store.read_only("llmauth:claude-ai").await.unwrap().is_some());
    assert_eq!(
        keys(&harness).await,
        keys_before,
        "an interrupted logout must not leave a half-written retirement marker"
    );
}

// ── 7. Verification ──────────────────────────────────────────────────────────

#[tokio::test]
async fn verification_bypasses_a_warm_cache_and_reports_a_server_failure_as_unverified() {
    let harness = harness().await;
    let client = Arc::new(CachingClient::new(Err(LlmError::Api {
        status: 403,
        message: "model listing is not available for this account".into(),
        kind: coda_llm::FailureKind::Permanent,
        retry_after: None,
        body: None,
    })));

    let report = harness.service.verify(client.as_ref()).await;

    assert_eq!(client.refresh_count(), 1, "the cache must be bypassed");
    assert!(
        matches!(report.outcome, VerificationOutcome::Unverified { .. }),
        "{:?}",
        report.outcome
    );
    assert!(!report.is_verified());
    assert!(!report.to_string().contains("model listing is not available"), "{report}");
}

#[tokio::test]
async fn an_empty_model_list_is_not_a_successful_probe() {
    let harness = harness().await;
    let client = Arc::new(CachingClient::new(Ok(Vec::new())));
    let report = harness.service.verify(client.as_ref()).await;
    assert!(!report.is_verified(), "an empty list proves nothing: {report}");
}

#[tokio::test]
async fn a_live_probe_that_returns_models_is_verified() {
    let harness = harness().await;
    let client = Arc::new(CachingClient::new(Ok(vec![model("claude-opus-5")])));
    let report = harness.service.verify(client.as_ref()).await;
    assert!(report.is_verified(), "{report}");
    assert_eq!(report.provider_id.as_deref(), Some("anthropic"));
}

#[tokio::test]
async fn a_rejected_credential_is_reported_as_needing_a_new_login() {
    let harness = harness().await;
    let client = Arc::new(CachingClient::new(Err(LlmError::Unauthorized("invalid token".into()))));
    let report = harness.service.verify(client.as_ref()).await;
    assert!(matches!(report.outcome, VerificationOutcome::Rejected { .. }), "{:?}", report.outcome);
    assert!(!report.to_string().contains("invalid token"), "{report}");
}

// ── Commit ownership ─────────────────────────────────────────────────────────

#[tokio::test]
async fn a_commit_dropped_by_its_caller_still_reaches_a_terminal_state() {
    let harness = harness().await;
    seed(&harness, &oauth("github-copilot", "existing")).await;
    let prepared = harness
        .service
        .prepare_login_with_probe(
            LoginRequest::api_key(ApiKeySource::Prompt),
            ScriptedUi::with_key("sk-ant-new").as_ref(),
            Some(working_probe()),
        )
        .await
        .expect("prepared");

    // The host tears its UI down mid-commit: the future is dropped.
    let commit = harness.service.commit_login(prepared);
    tokio::pin!(commit);
    let dropped = tokio::time::timeout(Duration::from_millis(1), &mut commit).await;
    drop(commit);

    // Teardown awaits the service instead of guessing.
    harness.service.await_pending_commit().await;

    let stored = harness.store.read_only("llmauth:anthropic-api-key").await.unwrap();
    let copilot = harness.store.read_only("llmauth:github-copilot").await.unwrap();
    assert!(
        stored.is_some() && copilot.is_none(),
        "the commit must have run to completion, not stopped half-way \
         (timeout result was {dropped:?})"
    );
    assert_eq!(harness.settings.current().default_provider.as_deref(), Some("anthropic"));
}

// ── An environment login selects the provider and stores no key ──────────────
//
// `auth login api-key --use-env` promises to *use* the exported
// `ANTHROPIC_API_KEY` rather than save a copy of it. What is persisted is the
// selection alone: `defaultProvider: anthropic`, with no credential blob for
// that identity anywhere in the profile.

const ENV_KEY: &str = "sk-ant-env-only-SECRET";

async fn environment_harness() -> Harness {
    harness_with(&[("ANTHROPIC_API_KEY", ENV_KEY)], ClaudeAiConfig::production("c")).await
}

/// A second service over the *same* profile and settings, with its own
/// environment — what a later process sees.
async fn service_over(harness: &Harness, env: &[(&str, &str)]) -> AuthService {
    AuthService::builder(
        Arc::clone(&harness.store),
        Arc::clone(harness.store.coordinator()),
    )
    .with_settings(Arc::clone(&harness.settings) as Arc<dyn coda_auth::service::AuthSettingsPort>)
    .with_environment(Arc::new(MapEnvironment::new(env)) as Arc<dyn AuthEnvironment>)
    .with_claude_config(ClaudeAiConfig::production("c"))
    .build()
    .await
    .expect("the provider context resolves")
}

/// Every value the profile holds, so a leak can be looked for wherever it
/// might have landed — a credential blob, a retirement marker, anything.
async fn stored_values(harness: &Harness) -> Vec<(String, String)> {
    let mut out = Vec::new();
    for key in keys(harness).await {
        let value = harness.primary.get(&key).await.unwrap().unwrap_or_default();
        out.push((key, value));
    }
    out
}

async fn assert_no_env_key_anywhere(harness: &Harness) {
    for (key, value) in stored_values(harness).await {
        assert!(!key.contains(ENV_KEY), "the key material leaked into a store key: {key}");
        assert!(
            !value.contains(ENV_KEY),
            "the key material leaked into the profile under {key}"
        );
    }
    let settings = format!("{:?}", harness.settings.current());
    assert!(!settings.contains(ENV_KEY), "the key material leaked into settings: {settings}");
}

async fn prepare_environment_login(
    harness: &Harness,
    probe: Arc<dyn LlmClient>,
) -> Result<coda_auth::service::PreparedLogin, PrepareFailure> {
    harness
        .service
        .prepare_login_with_probe(
            LoginRequest::api_key(ApiKeySource::Environment),
            ScriptedUi::with_key("unused").as_ref(),
            Some(probe),
        )
        .await
}

#[tokio::test]
async fn an_environment_login_saves_the_selection_and_never_the_key() {
    let harness = environment_harness().await;

    let prepared = prepare_environment_login(&harness, working_probe())
        .await
        .expect("a live key prepares");
    assert!(prepared.uses_environment_key(), "the intent must be visible to the host");
    assert!(!format!("{prepared:?}").contains(ENV_KEY));

    let outcome = harness.service.commit_login(prepared).await;
    assert!(matches!(outcome, CommitOutcome::Committed { .. }), "{outcome:?}");

    assert!(
        harness.store.read_only("llmauth:anthropic-api-key").await.unwrap().is_none(),
        "an environment login must not write a credential blob for the key it uses"
    );
    assert_no_env_key_anywhere(&harness).await;
    assert_eq!(harness.settings.current().default_provider.as_deref(), Some("anthropic"));

    // The selection resolves, and says the key comes from the process.
    let selection = harness.service.selected_provider().await.expect("a selection");
    assert_eq!(
        selection,
        coda_auth::service::Selection {
            identity: ProviderIdentity::AnthropicApiKey,
            source: coda_auth::service::SelectionSource::SavedDefault,
            origin: coda_auth::service::CredentialOrigin::Environment,
        },
    );
}

#[tokio::test]
async fn a_later_process_without_the_variable_fails_closed() {
    let harness = environment_harness().await;
    let prepared = prepare_environment_login(&harness, working_probe()).await.expect("prepared");
    assert!(matches!(
        harness.service.commit_login(prepared).await,
        CommitOutcome::Committed { .. }
    ));

    let later = service_over(&harness, &[]).await;
    let error = later.selected_provider().await.expect_err("no key, no selection");
    assert_eq!(
        error,
        coda_auth::service::SelectionError::NeedsLogin {
            identity: ProviderIdentity::AnthropicApiKey,
            source: coda_auth::service::SelectionSource::SavedDefault,
        },
        "a saved environment selection must never resolve without the variable"
    );
}

#[tokio::test]
async fn logging_out_an_environment_selection_clears_only_the_targeted_default() {
    for target in [Some(ProviderIdentity::AnthropicApiKey), None] {
        let harness = environment_harness().await;
        let prepared = prepare_environment_login(&harness, working_probe()).await.unwrap();
        assert!(harness.service.commit_login(prepared).await.engine_may_start());

        let unrelated = harness.service.logout(Some(ProviderIdentity::GithubCopilot)).await.unwrap();
        assert!(!unrelated.cleared_default_provider);
        assert_eq!(harness.settings.current().default_provider.as_deref(), Some("anthropic"));

        let report = harness.service.logout(target).await.unwrap();
        assert!(report.removed.is_empty());
        assert!(report.cleared_default_provider);
        assert!(report.environment_api_key_still_available);
        assert!(harness.settings.current().default_provider.is_none());
        let selection = harness.service.selected_provider().await.unwrap();
        assert_ne!(selection.source, coda_auth::service::SelectionSource::SavedDefault);
        let later = service_over(&harness, &[]).await;
        assert!(matches!(
            later.selected_provider().await,
            Err(coda_auth::service::SelectionError::NoCredentials)
        ));
    }
}

#[tokio::test]
async fn an_environment_login_replaces_the_stored_key_of_the_same_identity() {
    let harness = environment_harness().await;
    seed(&harness, &api_key_credential("sk-ant-previously-stored")).await;
    seed(&harness, &oauth("github-copilot", "existing")).await;

    let prepared = prepare_environment_login(&harness, working_probe()).await.expect("prepared");
    let replaces = prepared.replaces().to_vec();
    assert!(
        replaces.contains(&ProviderIdentity::AnthropicApiKey),
        "the stored key this login removes must be disclosed too: {replaces:?}"
    );
    assert!(replaces.contains(&ProviderIdentity::GithubCopilot), "{replaces:?}");

    let outcome = harness.service.commit_login(prepared).await;
    assert!(matches!(outcome, CommitOutcome::Committed { .. }), "{outcome:?}");
    assert!(harness.store.read_only("llmauth:anthropic-api-key").await.unwrap().is_none());
    assert!(harness.store.read_only("llmauth:github-copilot").await.unwrap().is_none());
    assert!(
        harness.store.is_retired("llmauth:anthropic-api-key").await.unwrap(),
        "the removed key must stay removed, even if a legacy source recovers later"
    );
    assert_no_env_key_anywhere(&harness).await;
}

#[tokio::test]
async fn an_environment_login_retires_an_absent_key_so_nothing_can_revive_it() {
    let harness = environment_harness().await;
    let prepared = prepare_environment_login(&harness, working_probe()).await.expect("prepared");
    assert!(matches!(
        harness.service.commit_login(prepared).await,
        CommitOutcome::Committed { .. }
    ));
    assert!(
        harness.store.is_retired("llmauth:anthropic-api-key").await.unwrap(),
        "a source that was unavailable at commit time must not be able to override the \
         environment selection later"
    );
}

#[tokio::test]
async fn an_environment_login_checks_the_key_before_it_removes_the_stored_one() {
    let harness = environment_harness().await;
    seed(&harness, &api_key_credential("sk-ant-previously-stored")).await;
    let before = keys(&harness).await;

    let probe = Arc::new(CachingClient::new(Err(LlmError::Unauthorized("invalid x-api-key".into()))));
    let failure = prepare_environment_login(&harness, Arc::clone(&probe) as Arc<dyn LlmClient>)
        .await
        .expect_err("a rejected key must not displace the stored one");
    assert!(matches!(failure, PrepareFailure::Rejected(_)), "{failure:?}");
    assert_eq!(probe.refresh_count(), 1, "the check must really run");
    assert_eq!(keys(&harness).await, before, "nothing may be touched");
    assert_eq!(harness.settings.applied_count(), 0);

    // And the convenience flag cannot buy its way past that check either.
    let probe = Arc::new(CachingClient::new(Err(LlmError::Unauthorized("invalid x-api-key".into()))));
    let failure = harness
        .service
        .prepare_login_with_probe(
            LoginRequest::api_key(ApiKeySource::Environment).without_validation(),
            ScriptedUi::with_key("unused").as_ref(),
            Some(Arc::clone(&probe) as Arc<dyn LlmClient>),
        )
        .await
        .expect_err("without_validation may not displace a readable stored key unchecked");
    assert!(matches!(failure, PrepareFailure::Rejected(_)), "{failure:?}");
    assert_eq!(probe.refresh_count(), 1);
    assert_eq!(keys(&harness).await, before);
}

#[tokio::test]
async fn a_missing_environment_key_is_named_and_changes_nothing() {
    let harness = harness_with(&[], ClaudeAiConfig::production("c")).await;
    seed(&harness, &api_key_credential("sk-ant-previously-stored")).await;
    let before = keys(&harness).await;

    // No variable exported: there is nothing to sign in with.
    let failure = prepare_environment_login(&harness, working_probe())
        .await
        .expect_err("no variable, no login");
    assert!(failure.to_string().contains("ANTHROPIC_API_KEY is not set"), "{failure}");
    assert_eq!(keys(&harness).await, before);
    assert_eq!(harness.settings.applied_count(), 0);
    assert_eq!(harness.settings.current(), AuthSettings::default());
}

fn api_key_credential(key: &str) -> Credential {
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

#[tokio::test]
async fn the_service_reuses_one_manager_rather_than_building_one_per_call() {
    let harness = harness().await;
    let first = harness.service.manager();
    let second = harness.service.manager();
    assert!(
        Arc::ptr_eq(&first, &second),
        "a fresh manager per call would discard the mutation gate that makes \
         concurrent refreshes and logouts safe"
    );
}

#[tokio::test]
async fn the_service_reads_the_injected_environment_and_not_the_process() {
    // The ambient-key answer must come from the port. If it came from the real
    // process environment, this assertion would depend on the machine running
    // the tests — and a test could leak a developer's key into a decision.
    let harness = harness_with(&[("ANTHROPIC_API_KEY", "sk-injected")], ClaudeAiConfig::production("c")).await;
    let reads_before = harness.environment.reads();

    let status = harness.service.status().await.expect("status");

    assert!(status.environment_api_key, "the injected key must be what is reported");
    assert!(
        harness.environment.reads() > reads_before,
        "the environment port must actually be consulted"
    );

    let empty_environment = harness_with(&[], ClaudeAiConfig::production("c")).await;
    assert!(
        !empty_environment.service.status().await.expect("status").environment_api_key,
        "and an empty environment map means no ambient key, whatever the process has"
    );
}





