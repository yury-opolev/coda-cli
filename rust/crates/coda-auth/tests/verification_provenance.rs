//! Verification must say who refused.
//!
//! A credential source that fails *here* — an unreadable store, nothing
//! stored, a connection that was replaced — stops the request before it is
//! sent, and reports an authentication error because that is the only channel
//! it has. Reading that as "the provider rejected your credential; sign in
//! again" is wrong twice over: nothing was sent, and the advice sends the user
//! to overwrite a credential that may be perfectly recoverable.
//!
//! These tests contrast the two readings deliberately: the source-less probe
//! is the old behaviour, and the source-aware probe is the corrected one.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;

use async_trait::async_trait;
use coda_auth::error::AuthError;
use coda_auth::provider::copilot::{CopilotConfig as CopilotAuthConfig, CopilotDeployment};
use coda_auth::provider::{ApiKeyProvider, AuthProvider};
use coda_auth::service::{
    copilot_connection, verify_client, verify_client_with_source, CopilotContextCell,
    UnverifiedReason, VerificationOutcome,
};
use coda_auth::store::{CredentialStore, InMemoryStore};
use coda_auth::{Credential, CredentialKind, CredentialManager, CredentialManagerSource, Secret};
use coda_llm::anthropic::{AnthropicClient, AnthropicConfig};
use coda_llm::CredentialSource;
use tokio::io::{AsyncReadExt, AsyncWriteExt};

/// A loopback endpoint that answers every request with one canned response.
struct Endpoint {
    url: String,
    hits: Arc<AtomicUsize>,
    task: tokio::task::JoinHandle<()>,
}

impl Endpoint {
    async fn answering(status_line: &'static str, body: &'static str) -> Self {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let url = format!("http://{}", listener.local_addr().expect("addr"));
        let hits = Arc::new(AtomicUsize::new(0));
        let counter = Arc::clone(&hits);
        let task = tokio::spawn(async move {
            while let Ok((mut socket, _)) = listener.accept().await {
                let mut request = Vec::new();
                let mut chunk = [0u8; 1024];
                loop {
                    let Ok(read) = socket.read(&mut chunk).await else { break };
                    if read == 0 {
                        break;
                    }
                    request.extend_from_slice(&chunk[..read]);
                    if request.windows(4).any(|window| window == b"\r\n\r\n") {
                        break;
                    }
                }
                counter.fetch_add(1, Ordering::SeqCst);
                let response = format!(
                    "{status_line}\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\
                     Connection: close\r\n\r\n{body}",
                    body.len(),
                );
                let _ = socket.write_all(response.as_bytes()).await;
            }
        });
        Self { url, hits, task }
    }

    fn hits(&self) -> usize {
        self.hits.load(Ordering::SeqCst)
    }
}

impl Drop for Endpoint {
    fn drop(&mut self) {
        self.task.abort();
    }
}

/// A store whose reads fail the way a locked or foreign-user profile does.
#[derive(Debug)]
struct BrokenStore;

#[async_trait]
impl CredentialStore for BrokenStore {
    async fn get(&self, _: &str) -> Result<Option<String>, AuthError> {
        Err(AuthError::StoreUndecryptable {
            key: "llmauth:anthropic-api-key".into(),
            detail: "private-detail-sentinel".into(),
        })
    }
    async fn set(&self, _: &str, _: &str) -> Result<(), AuthError> {
        Ok(())
    }
    async fn delete(&self, _: &str) -> Result<(), AuthError> {
        Ok(())
    }
}

fn api_key_credential(key: &str) -> Credential {
    Credential {
        provider_id: coda_auth::provider::api_key::PROVIDER_ID.to_owned(),
        kind: CredentialKind::ApiKey,
        api_key: Some(Secret::new(key.to_owned())),
        access_token: None,
        refresh_token: None,
        expires_at: None,
        scopes: Vec::new(),
        account: None,
    }
}

fn manager_over(store: Arc<dyn CredentialStore>) -> Arc<CredentialManager> {
    Arc::new(CredentialManager::new(store, [Arc::new(ApiKeyProvider) as Arc<dyn AuthProvider>]))
}

fn anthropic(url: &str, source: Arc<dyn CredentialSource>) -> AnthropicClient {
    AnthropicClient::new(
        AnthropicConfig::api_key("")
            .with_base_url(url.to_owned())
            .with_retry(coda_llm::RetryPolicy::none())
            .with_credential_source(source),
    )
    .expect("client")
}

/// The two model-discovery clients, over the same loopback endpoint.
fn model_client(
    copilot: bool,
    url: &str,
    source: Arc<dyn CredentialSource>,
) -> Box<dyn coda_llm::LlmClient> {
    if copilot {
        Box::new(
            coda_llm::CopilotClient::new(
                coda_llm::CopilotConfig::with_token("")
                    .with_base_url(url.to_owned())
                    .with_retry(coda_llm::RetryPolicy::none())
                    .with_credential_source(source),
            )
            .expect("copilot client"),
        )
    } else {
        Box::new(anthropic(url, source))
    }
}

/// The store cannot be read, so no request is sent — and the report must not
/// blame the provider for a refusal it never made.
#[tokio::test]
async fn a_store_that_cannot_be_read_is_unverified_not_rejected() {
    let endpoint = Endpoint::answering("HTTP/1.1 200 OK", r#"{"data":[]}"#).await;
    let source = Arc::new(CredentialManagerSource::new(
        manager_over(Arc::new(BrokenStore)),
        coda_auth::provider::api_key::PROVIDER_ID,
    ));
    let client = anthropic(&endpoint.url, Arc::clone(&source) as Arc<dyn CredentialSource>);

    // The old reading: an authentication error with no idea where it came
    // from, reported as the provider refusing the credential.
    let blind = verify_client(&client).await;
    assert_eq!(blind.outcome, VerificationOutcome::Rejected { status: None });
    assert!(blind.to_string().contains("sign in again"), "{blind}");

    // The corrected reading: this machine could not produce a credential.
    let aware = verify_client_with_source(&client, Some(source.as_ref())).await;
    assert_eq!(
        aware.outcome,
        VerificationOutcome::Unverified { reason: UnverifiedReason::CredentialUnavailable }
    );
    let text = aware.to_string();
    assert!(!text.contains("sign in again"), "{text}");
    assert!(!text.contains("private-detail-sentinel"), "{text}");
    assert_eq!(endpoint.hits(), 0, "nothing may be sent when no credential could be produced");
}

/// Signed out, then probed: still not a provider rejection.
#[tokio::test]
async fn a_profile_with_nothing_stored_is_unverified_not_rejected() {
    let endpoint = Endpoint::answering("HTTP/1.1 200 OK", r#"{"data":[]}"#).await;
    let source = Arc::new(CredentialManagerSource::new(
        manager_over(Arc::new(InMemoryStore::new())),
        coda_auth::provider::api_key::PROVIDER_ID,
    ));
    let client = anthropic(&endpoint.url, Arc::clone(&source) as Arc<dyn CredentialSource>);

    let report = verify_client_with_source(&client, Some(source.as_ref())).await;
    assert_eq!(
        report.outcome,
        VerificationOutcome::Unverified { reason: UnverifiedReason::CredentialUnavailable }
    );
    assert_eq!(endpoint.hits(), 0);
}

/// A Copilot source whose context was replaced refuses locally. It is a client
/// that must be rebuilt, not an account that must sign in again.
#[tokio::test]
async fn a_superseded_copilot_connection_is_unverified_not_rejected() {
    let endpoint = Endpoint::answering("HTTP/1.1 200 OK", r#"{"data":[]}"#).await;
    let store = Arc::new(InMemoryStore::new());
    let credential = api_key_credential("sk-stored");
    store
        .set(
            &format!("llmauth:{}", coda_auth::provider::api_key::PROVIDER_ID),
            &serde_json::to_string(&credential).expect("serialize"),
        )
        .await
        .expect("seed");

    let cell = CopilotContextCell::initial(
        CopilotAuthConfig::default_public(),
        CopilotDeployment::Public,
        None,
    );
    let connection = copilot_connection(manager_over(store), Arc::clone(&cell));
    // Another login lands: the reference this connection holds is now stale.
    cell.publish(CopilotAuthConfig::default_public(), CopilotDeployment::Public, &credential);

    let client = anthropic(&endpoint.url, Arc::clone(&connection.source));
    let report = verify_client_with_source(&client, Some(connection.source.as_ref())).await;
    assert_eq!(
        report.outcome,
        VerificationOutcome::Unverified { reason: UnverifiedReason::CredentialUnavailable }
    );
    assert_eq!(endpoint.hits(), 0);
}

/// The case that must keep working: a credential this machine produced fine,
/// and a provider that answered `401`. That *is* a rejection, and knowing
/// about the source must not soften it.
#[tokio::test]
async fn a_provider_that_answers_401_is_still_a_rejection() {
    let endpoint = Endpoint::answering(
        "HTTP/1.1 401 Unauthorized",
        r#"{"error":{"message":"invalid x-api-key"}}"#,
    )
    .await;
    let store = Arc::new(InMemoryStore::new());
    store
        .set(
            &format!("llmauth:{}", coda_auth::provider::api_key::PROVIDER_ID),
            &serde_json::to_string(&api_key_credential("sk-stored")).expect("serialize"),
        )
        .await
        .expect("seed");
    let source = Arc::new(CredentialManagerSource::new(
        manager_over(store),
        coda_auth::provider::api_key::PROVIDER_ID,
    ));
    let client = anthropic(&endpoint.url, Arc::clone(&source) as Arc<dyn CredentialSource>);

    let report = verify_client_with_source(&client, Some(source.as_ref())).await;
    assert_eq!(report.outcome, VerificationOutcome::Rejected { status: None });
    assert!(report.to_string().contains("sign in again"), "{report}");
    assert!(endpoint.hits() >= 1, "the provider really was asked");
    assert!(!source.last_failure_was_local(), "the credential itself was produced fine");
}

/// A model endpoint that answers `403` is telling us about *entitlement*, not
/// about the identity. Both clients must carry that through as "could not
/// check", or a signed-in user is told to sign in again — and follows the
/// advice by overwriting a credential that works.
#[tokio::test]
async fn a_real_403_from_the_model_list_is_unverified_for_both_clients() {
    let store = Arc::new(InMemoryStore::new());
    store
        .set(
            &format!("llmauth:{}", coda_auth::provider::api_key::PROVIDER_ID),
            &serde_json::to_string(&api_key_credential("sk-stored")).expect("serialize"),
        )
        .await
        .expect("seed");
    let manager = manager_over(Arc::clone(&store) as Arc<dyn CredentialStore>);

    for copilot in [false, true] {
        let endpoint = Endpoint::answering(
            "HTTP/1.1 403 Forbidden",
            r#"{"error":{"message":"no model listing entitlement"}}"#,
        )
        .await;
        let source = Arc::new(CredentialManagerSource::new(
            Arc::clone(&manager),
            coda_auth::provider::api_key::PROVIDER_ID,
        )) as Arc<dyn CredentialSource>;
        let client = model_client(copilot, &endpoint.url, Arc::clone(&source));

        let report = verify_client_with_source(client.as_ref(), Some(source.as_ref())).await;
        assert_eq!(
            report.outcome,
            VerificationOutcome::Unverified { reason: UnverifiedReason::Forbidden },
            "copilot={copilot}: a 403 model list is an entitlement answer, not a refusal",
        );
        let text = report.to_string();
        assert!(!text.contains("sign in again"), "copilot={copilot}: {text}");
        assert!(!text.contains("no model listing entitlement"), "copilot={copilot}: {text}");
        assert!(endpoint.hits() >= 1, "copilot={copilot}: the provider really was asked");
    }
}

/// And a real `401` from the same endpoint must stay a refusal for both.
#[tokio::test]
async fn a_real_401_from_the_model_list_is_a_rejection_for_both_clients() {
    let store = Arc::new(InMemoryStore::new());
    store
        .set(
            &format!("llmauth:{}", coda_auth::provider::api_key::PROVIDER_ID),
            &serde_json::to_string(&api_key_credential("sk-stored")).expect("serialize"),
        )
        .await
        .expect("seed");
    let manager = manager_over(Arc::clone(&store) as Arc<dyn CredentialStore>);

    for copilot in [false, true] {
        let endpoint =
            Endpoint::answering("HTTP/1.1 401 Unauthorized", r#"{"error":{"message":"bad"}}"#)
                .await;
        let source = Arc::new(CredentialManagerSource::new(
            Arc::clone(&manager),
            coda_auth::provider::api_key::PROVIDER_ID,
        )) as Arc<dyn CredentialSource>;
        let client = model_client(copilot, &endpoint.url, Arc::clone(&source));

        let report = verify_client_with_source(client.as_ref(), Some(source.as_ref())).await;
        assert!(report.is_rejected(), "copilot={copilot}: {report:?}");
    }
}

/// A source that answered is never blamed for a later failure.
#[tokio::test]
async fn a_source_that_succeeded_does_not_report_a_local_failure() {
    let endpoint = Endpoint::answering("HTTP/1.1 500 Server Error", "{}").await;
    let store = Arc::new(InMemoryStore::new());
    store
        .set(
            &format!("llmauth:{}", coda_auth::provider::api_key::PROVIDER_ID),
            &serde_json::to_string(&api_key_credential("sk-stored")).expect("serialize"),
        )
        .await
        .expect("seed");
    let source = Arc::new(CredentialManagerSource::new(
        manager_over(store),
        coda_auth::provider::api_key::PROVIDER_ID,
    ));
    let client = anthropic(&endpoint.url, Arc::clone(&source) as Arc<dyn CredentialSource>);

    let report = verify_client_with_source(&client, Some(source.as_ref())).await;
    assert!(
        matches!(
            report.outcome,
            VerificationOutcome::Unverified { reason: UnverifiedReason::ProviderError { .. } }
        ),
        "{report:?}"
    );
    assert!(!source.last_failure_was_local());
}

/// The flag tracks the *latest* attempt, so a store that recovers is not
/// remembered as broken for the rest of the process.
#[tokio::test]
async fn the_provenance_flag_follows_the_latest_attempt() {
    let store = Arc::new(InMemoryStore::new());
    let manager = manager_over(Arc::clone(&store) as Arc<dyn CredentialStore>);
    let source =
        CredentialManagerSource::new(manager, coda_auth::provider::api_key::PROVIDER_ID);

    assert!(source.auth_headers().await.is_err());
    assert!(source.last_failure_was_local(), "nothing stored is a local fault");

    store
        .set(
            &format!("llmauth:{}", coda_auth::provider::api_key::PROVIDER_ID),
            &serde_json::to_string(&api_key_credential("sk-stored")).expect("serialize"),
        )
        .await
        .expect("seed");
    assert!(source.auth_headers().await.is_ok());
    assert!(!source.last_failure_was_local(), "a success clears the flag");
}
