//! What the *pre-commit* credential check may conclude.
//!
//! An API key proves nothing by being well-formed, so a login that would
//! displace a working account probes it first. What that probe is allowed to
//! conclude is the point:
//!
//! * the provider refused it → refuse the login, with the status the provider
//!   actually answered with and never a fabricated one;
//! * the provider answered about *entitlement* (`403` on the model list) →
//!   the credential could not be **checked**, which is not the same as being
//!   refused and must not delete the account that currently works.

use std::sync::Arc;

use async_trait::async_trait;
use coda_auth::coordination::LocalCoordinator;
use coda_auth::failure::AuthFailure;
use coda_auth::service::{
    ApiKeySource, AuthEnvironment, AuthService, AuthSettingsPort, InMemoryAuthSettings,
    LoginRequest, LoginUi, MapEnvironment, PrepareFailure,
};
use coda_auth::store::{open_profile_storage, Profile};
use coda_auth::{AuthError, Secret};
use coda_llm::{ChatRequest, LlmClient, LlmError, ModelInfo, ResponseStream};

#[path = "common/mod.rs"]
mod common;

/// A probe whose model listing answers exactly one way.
#[derive(Debug)]
struct ScriptedProbe {
    answer: fn() -> Result<Vec<ModelInfo>, LlmError>,
}

#[async_trait]
impl LlmClient for ScriptedProbe {
    fn provider_id(&self) -> &str {
        "anthropic"
    }

    async fn list_models(&self) -> Result<Vec<ModelInfo>, LlmError> {
        (self.answer)()
    }

    async fn refresh_models(&self) -> Result<Vec<ModelInfo>, LlmError> {
        (self.answer)()
    }

    async fn stream(&self, _request: ChatRequest) -> Result<ResponseStream, LlmError> {
        panic!("verification must never run a completion")
    }
}

/// A UI that supplies one key and nothing else.
struct KeyOnly(Secret<String>);

#[async_trait]
impl LoginUi for KeyOnly {
    async fn api_key(&self) -> Result<Secret<String>, AuthError> {
        Ok(Secret::new(self.0.expose().clone()))
    }
}

async fn service(root: &std::path::Path) -> (AuthService, Arc<InMemoryAuthSettings>) {
    let storage = open_profile_storage(&Profile::isolated(root)).expect("profile opens");
    let settings = Arc::new(InMemoryAuthSettings::new());
    let service = AuthService::builder(
        Arc::clone(&storage.profile),
        Arc::new(LocalCoordinator::new()),
    )
    .with_settings(Arc::clone(&settings) as Arc<dyn AuthSettingsPort>)
    .build()
    .await
    .expect("builds");
    (service, settings)
}

async fn prepare_with(
    probe: fn() -> Result<Vec<ModelInfo>, LlmError>,
) -> (tempfile::TempDir, Result<(), PrepareFailure>) {
    let root = tempfile::tempdir().expect("temp profile");
    let (service, _settings) = service(root.path()).await;
    let ui = KeyOnly(Secret::new("sk-ant-probe-key".into()));
    let result = service
        .prepare_login_with_probe(
            LoginRequest::api_key(ApiKeySource::Prompt),
            &ui,
            Some(Arc::new(ScriptedProbe { answer: probe }) as Arc<dyn LlmClient>),
        )
        .await
        .map(|_prepared| ());
    (root, result)
}

/// A `403` model list means "we could not check", not "you were refused" —
/// and the login stops rather than storing an unchecked credential.
#[tokio::test]
async fn a_403_model_list_leaves_the_credential_unchecked_not_refused() {
    let (_root, result) = prepare_with(|| {
        Err(LlmError::from_model_discovery_status(
            403,
            r#"{"error":{"message":"no entitlement"}}"#,
            None,
        ))
    })
    .await;

    match result.expect_err("an unchecked credential is not prepared") {
        PrepareFailure::ValidationUnavailable { reason, displaces_a_working_account } => {
            assert!(!displaces_a_working_account, "nothing was stored to displace");
            // Not a network fault, and not a refusal.
            assert_ne!(reason, AuthFailure::Network);
            assert!(
                !matches!(reason, AuthFailure::OAuthRejected { .. }),
                "an entitlement answer is not a refusal: {reason:?}"
            );
        }
        other => panic!("a 403 model list must not be a rejection: {other:?}"),
    }
}

/// A `401` is the provider refusing the identity, and the refusal carries the
/// status the provider gave rather than one this code invented.
#[tokio::test]
async fn a_401_model_list_is_a_refusal_that_never_invents_a_status() {
    let (_root, result) = prepare_with(|| {
        Err(LlmError::from_model_discovery_status(401, r#"{"error":{"message":"bad"}}"#, None))
    })
    .await;

    match result.expect_err("a refused credential is not prepared") {
        PrepareFailure::Rejected(failure) => {
            // The Anthropic client reports a 401 as `Unauthorized`, which
            // carries no status; the honest classification is therefore
            // "the value entered is not valid", not a fabricated HTTP 401.
            assert_eq!(failure, AuthFailure::InvalidInput, "no status may be invented");
            assert!(!failure.to_string().contains("401"), "{failure}");
        }
        other => panic!("a 401 must be a refusal: {other:?}"),
    }
}

/// An unreachable provider is a network fault, and stays one.
#[tokio::test]
async fn an_unreachable_provider_is_reported_as_a_network_fault() {
    let (_root, result) =
        prepare_with(|| Err(LlmError::Transport("connection refused".into()))).await;

    match result.expect_err("an unchecked credential is not prepared") {
        PrepareFailure::ValidationUnavailable { reason, .. } => {
            assert_eq!(reason, AuthFailure::Network);
        }
        other => panic!("expected an unavailable check: {other:?}"),
    }
}

/// A provider that answers with models is the one case that proceeds.
#[tokio::test]
async fn a_working_key_is_prepared() {
    let (_root, result) = prepare_with(|| Ok(vec![ModelInfo::new("claude-test")])).await;
    assert!(result.is_ok(), "a checked credential must be preparable: {result:?}");
}


// ── Where the pre-commit probe actually goes ─────────────────────────────────

/// A service whose environment is exactly `pairs` — never the developer's.
async fn service_with_env(
    root: &std::path::Path,
    pairs: &[(&str, &str)],
) -> AuthService {
    let storage = open_profile_storage(&Profile::isolated(root)).expect("profile opens");
    AuthService::builder(Arc::clone(&storage.profile), Arc::new(LocalCoordinator::new()))
        .with_settings(Arc::new(InMemoryAuthSettings::new()) as Arc<dyn AuthSettingsPort>)
        .with_environment(Arc::new(MapEnvironment::new(pairs)) as Arc<dyn AuthEnvironment>)
        .build()
        .await
        .expect("builds")
}

/// The check that gates the commit has to happen at the host the key will
/// actually be spent at. Proved on the wire, against a loopback listener the
/// shipping `ANTHROPIC_BASE_URL` points at: no injected probe client, so this
/// is the client the service builds for itself.
#[tokio::test]
async fn the_precommit_probe_is_sent_to_the_configured_endpoint_with_the_key_on_it() {
    let server = common::http::FakeHttp::start(vec![(
        200,
        r#"{"data":[{"type":"model","id":"claude-fixture"}]}"#.to_owned(),
    )])
    .await;
    let root = tempfile::tempdir().expect("temp profile");
    let service = service_with_env(root.path(), &[("ANTHROPIC_BASE_URL", &server.base_url)]).await;

    let ui = KeyOnly(Secret::new("sk-ant-endpoint-probe".into()));
    let prepared = service
        .prepare_login(LoginRequest::api_key(ApiKeySource::Prompt), &ui)
        .await
        .expect("the configured endpoint answered, so the login prepares");
    assert!(prepared.is_verified(), "a probe that answered is proof");

    let mut server = server;
    let request = server.next_request().expect("the probe reached the configured endpoint");
    assert!(request.starts_with("GET /v1/models "), "{request}");
    assert!(
        request.to_lowercase().contains("x-api-key: sk-ant-endpoint-probe"),
        "the probe did not carry the key being signed in with"
    );
    assert_eq!(server.hits(), 1, "exactly one uncached listing");
}

/// The same seam, for the selection that stores no key: the exported value is
/// normalised once and that normalised value is what the probe sends, so
/// "verified" is a statement about the bytes an engine will later put on the
/// wire.
#[tokio::test]
async fn an_environment_key_is_normalised_before_it_reaches_the_configured_endpoint() {
    let server = common::http::FakeHttp::start(vec![(
        200,
        r#"{"data":[{"type":"model","id":"claude-fixture"}]}"#.to_owned(),
    )])
    .await;
    let root = tempfile::tempdir().expect("temp profile");
    let service = service_with_env(
        root.path(),
        &[
            ("ANTHROPIC_BASE_URL", &server.base_url),
            ("ANTHROPIC_API_KEY", "\u{1}\r\n  sk-ant-padded-env \t"),
        ],
    )
    .await;

    let prepared = service
        .prepare_login(LoginRequest::api_key(ApiKeySource::Environment), &KeyOnly(Secret::new(String::new())))
        .await
        .expect("the exported key checks out at the configured endpoint");
    assert!(prepared.uses_environment_key(), "no key may be carried to the commit");

    let mut server = server;
    let request = server.next_request().expect("the probe reached the configured endpoint");
    let lowered = request.to_lowercase();
    assert!(lowered.contains("x-api-key: sk-ant-padded-env\r\n"), "{request}");
}

/// A refused override stops the login **before** a key is prompted for, read
/// or sent — and never falls back to Anthropic's own host, which is where the
/// key would otherwise go.
#[tokio::test]
async fn a_refused_endpoint_override_stops_before_a_key_is_read() {
    /// A UI that fails the test if it is ever asked for anything.
    struct NeverAsked;

    #[async_trait]
    impl LoginUi for NeverAsked {
        async fn api_key(&self) -> Result<Secret<String>, AuthError> {
            panic!("a refused endpoint must stop before a key is collected")
        }
    }

    for bad in [
        "http://gateway.example.com",
        "https://user:pw@gateway.example.com",
        "https://gateway.example.com/?key=SECRET-IN-QUERY",
        "ftp://gateway.example.com",
        "not-a-url",
    ] {
        let root = tempfile::tempdir().expect("temp profile");
        let service = service_with_env(
            root.path(),
            &[("ANTHROPIC_BASE_URL", bad), ("ANTHROPIC_API_KEY", "sk-ant-exported")],
        )
        .await;

        for source in [ApiKeySource::Prompt, ApiKeySource::Environment] {
            let failure = service
                .prepare_login(LoginRequest::api_key(source), &NeverAsked)
                .await
                .expect_err("a refused endpoint must not prepare a login");
            let rendered = format!("{failure} {failure:?}");
            assert!(
                matches!(failure, PrepareFailure::EndpointRejected { .. }),
                "{bad} produced {rendered}"
            );
            assert_eq!(failure.failure(), Some(AuthFailure::InvalidEndpoint), "{bad}");
            assert!(!rendered.contains("SECRET-IN-QUERY"), "{bad}: {rendered}");
            assert!(!rendered.contains("sk-ant-exported"), "{bad}: {rendered}");
        }

        // Nothing was written, and `status` still reports the fault rather
        // than failing on it.
        let status = service.status().await.expect("status is still readable");
        assert!(status.stored().is_empty(), "{bad} wrote a credential");
        assert!(status.anthropic_endpoint.is_err(), "{bad} resolved to something");
    }
}

/// With nothing configured, the resolution is Anthropic's own host — the
/// behaviour every existing profile already has.
#[tokio::test]
async fn no_override_resolves_to_anthropics_own_host() {
    let root = tempfile::tempdir().expect("temp profile");
    let service = service_with_env(root.path(), &[]).await;
    let endpoint = service.anthropic_endpoint().expect("the default is valid");
    assert!(endpoint.is_default());
    assert_eq!(endpoint.base_url(), coda_auth::service::DEFAULT_ANTHROPIC_BASE_URL);
}
