use std::sync::{Arc, atomic::{AtomicUsize, Ordering}};
use std::time::Duration;

use coda_auth::{AuthProvider, Credential, CredentialKind, CredentialManager, CredentialManagerSource, Secret};
use coda_auth::provider::{ApiKeyProvider, CopilotProvider};
use coda_auth::store::{CredentialStore, InMemoryStore};
use coda_llm::{CredentialSource, LlmClient};
use coda_llm::anthropic::{AnthropicClient, AnthropicConfig};
use coda_llm::copilot::{CopilotClient, CopilotConfig};
use tokio::io::{AsyncReadExt, AsyncWriteExt};

struct Probe {
    url: String,
    hits: Arc<AtomicUsize>,
    requests: Arc<std::sync::Mutex<Vec<String>>>,
    task: tokio::task::JoinHandle<()>,
}

impl Probe {
    async fn start() -> Self {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}", listener.local_addr().unwrap());
        let hits = Arc::new(AtomicUsize::new(0));
        let recorded = hits.clone();
        let requests = Arc::new(std::sync::Mutex::new(Vec::new()));
        let request_log = requests.clone();
        let task = tokio::spawn(async move {
            while let Ok((mut socket, _)) = listener.accept().await {
                let mut request = Vec::new();
                let mut chunk = [0u8; 1024];
                loop {
                    let read = socket.read(&mut chunk).await.unwrap();
                    if read == 0 { break; }
                    request.extend_from_slice(&chunk[..read]);
                    if request.windows(4).any(|window| window == b"\r\n\r\n") { break; }
                }
                recorded.fetch_add(1, Ordering::SeqCst);
                request_log.lock().unwrap().push(String::from_utf8(request).unwrap());
                let body = r#"{"data":[]}"#;
                let response = format!(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
                    body.len(), body,
                );
                socket.write_all(response.as_bytes()).await.unwrap();
            }
        });
        Self { url, hits, requests, task }
    }
}

impl Drop for Probe {
    fn drop(&mut self) {
        self.task.abort();
    }
}

fn client(copilot: bool, url: &str, source: Arc<dyn CredentialSource>) -> Box<dyn LlmClient> {
    if copilot {
        Box::new(CopilotClient::new(
            CopilotConfig::with_token("cached-static-token")
                .with_header("User-Agent", "stale-agent")
                .with_header("Editor-Version", "stale-editor")
                .with_base_url(url).with_credential_source(source),
        ).unwrap())
    } else {
        Box::new(AnthropicClient::new(
            AnthropicConfig::api_key("cached-static-key")
                .with_base_url(url).with_credential_source(source),
        ).unwrap())
    }
}

#[tokio::test]
async fn logout_prevents_cached_static_authentication_for_both_clients() {
    for copilot in [false, true] {
        let probe = Probe::start().await;
        let id = if copilot { "github-copilot" } else { "anthropic-api-key" };
        let provider: Arc<dyn AuthProvider> = if copilot {
            Arc::new(CopilotProvider::new(coda_auth::provider::copilot::CopilotConfig::default_public()))
        } else {
            Arc::new(ApiKeyProvider)
        };
        let store = Arc::new(InMemoryStore::new());
        let manager = Arc::new(CredentialManager::new(store, [provider]));
        let credential = if copilot {
            Credential {
                provider_id: id.into(), kind: CredentialKind::OAuth,
                access_token: Some(Secret::new("dynamic-test-token".into())),
                refresh_token: None, api_key: None, expires_at: None,
                scopes: Vec::new(), account: None,
            }
        } else {
            ApiKeyProvider::credential("dynamic-test-key")
        };
        manager.store_credential(id, &credential).await.unwrap();
        let source = Arc::new(CredentialManagerSource::new(manager.clone(), id));
        let client = client(copilot, &probe.url, source);
        tokio::time::timeout(Duration::from_secs(5), client.refresh_models()).await.unwrap().unwrap();
        assert_eq!(probe.hits.load(Ordering::SeqCst), 1);
        if copilot {
            let requests = probe.requests.lock().unwrap();
            for name in ["user-agent", "editor-version"] {
                let values: Vec<_> = requests[0].lines().filter_map(|line| {
                    let (key, value) = line.split_once(':')?;
                    key.eq_ignore_ascii_case(name).then_some(value.trim())
                }).collect();
                assert_eq!(values.len(), 1, "{name}");
                assert!(!values[0].starts_with("stale-"));
            }
        }

        manager.logout(id).await.unwrap();
        let result = tokio::time::timeout(Duration::from_secs(5), client.refresh_models()).await.unwrap();
        assert!(result.is_err(), "copilot={copilot}: removal must not select a cached static token");
        assert_eq!(probe.hits.load(Ordering::SeqCst), 1, "no request after logout");
        let request = coda_llm::ChatRequest::new("test-model", vec![coda_llm::Message::user("hello")]);
        let stream = tokio::time::timeout(Duration::from_secs(5), client.stream(request)).await.unwrap();
        assert!(stream.is_err(), "a completion must not reuse static auth either");
        assert_eq!(probe.hits.load(Ordering::SeqCst), 1);
    }

}

#[derive(Debug)]
struct NoOverride;

#[async_trait::async_trait]
impl CredentialSource for NoOverride {
    async fn auth_headers(&self) -> Result<Option<Vec<(String, String)>>, coda_llm::LlmError> {
        Ok(None)
    }
}

#[tokio::test]
async fn an_explicit_generic_no_override_preserves_static_authentication() {
    for copilot in [false, true] {
        let probe = Probe::start().await;
        let client = client(copilot, &probe.url, Arc::new(NoOverride));
        tokio::time::timeout(Duration::from_secs(5), client.refresh_models()).await.unwrap().unwrap();
        let requests = probe.requests.lock().unwrap();
        assert_eq!(requests.len(), 1);
        assert!(requests[0].contains(if copilot { "cached-static-token" } else { "cached-static-key" }));
    }
}

#[tokio::test]
async fn claude_auth_mode_headers_are_unique_on_models_and_completion_requests() {
    use coda_auth::provider::claude_ai::{ClaudeAiConfig, ClaudeAiProvider, OAUTH_BETA_HEADER};
    let probe = Probe::start().await;
    let manager = Arc::new(CredentialManager::new(
        Arc::new(InMemoryStore::new()),
        [Arc::new(ClaudeAiProvider::new(ClaudeAiConfig::production("test-client"))) as Arc<dyn AuthProvider>],
    ));
    manager.store_credential("claude-ai", &Credential {
        provider_id: "claude-ai".into(), kind: CredentialKind::OAuth,
        access_token: Some(Secret::new("test-claude-token".into())),
        refresh_token: None, api_key: None, expires_at: None,
        scopes: Vec::new(), account: None,
    }).await.unwrap();
    let mut config = AnthropicConfig::api_key("unused-static-key")
        .with_base_url(&probe.url)
        .with_credential_source(Arc::new(CredentialManagerSource::new(manager, "claude-ai")));
    config.extra_headers.push(("Anthropic-Beta".into(), format!("{OAUTH_BETA_HEADER},extra-feature")));
    let client = AnthropicClient::new(config).unwrap();
    tokio::time::timeout(Duration::from_secs(5), client.refresh_models()).await.unwrap().unwrap();
    let request = coda_llm::ChatRequest::new("test-model", vec![coda_llm::Message::user("hello")]);
    let stream = tokio::time::timeout(Duration::from_secs(5), client.stream(request)).await.unwrap();
    drop(stream);

    let requests = probe.requests.lock().unwrap();
    assert_eq!(requests.len(), 2);
    for request in requests.iter() {
        let values: Vec<_> = request.lines().filter_map(|line| {
            let (name, value) = line.split_once(':')?;
            name.eq_ignore_ascii_case("anthropic-beta").then_some(value.trim())
        }).collect();
        assert_eq!(values.len(), 1);
        let flags: Vec<_> = values[0].split(',').map(str::trim).collect();
        assert_eq!(flags.iter().filter(|flag| **flag == OAUTH_BETA_HEADER).count(), 1);
        assert!(flags.contains(&"extra-feature"));
    }
}

struct BrokenStore;

#[async_trait::async_trait]
impl CredentialStore for BrokenStore {
    async fn get(&self, _: &str) -> Result<Option<String>, coda_auth::AuthError> {
        Err(coda_auth::AuthError::Store("private-detail-sentinel".into()))
    }
    async fn set(&self, _: &str, _: &str) -> Result<(), coda_auth::AuthError> {
        panic!("a failed read must not write")
    }
    async fn delete(&self, _: &str) -> Result<(), coda_auth::AuthError> {
        panic!("a failed read must not delete")
    }
}

#[tokio::test]
async fn a_credential_store_failure_is_safe_and_stops_the_request() {
    let probe = Probe::start().await;
    let manager = Arc::new(CredentialManager::new(
        Arc::new(BrokenStore), [Arc::new(ApiKeyProvider) as Arc<dyn AuthProvider>],
    ));
    let source = Arc::new(CredentialManagerSource::new(manager, "anthropic-api-key"));
    let client = client(false, &probe.url, source);
    let result = tokio::time::timeout(Duration::from_secs(5), client.refresh_models()).await.unwrap();
    assert!(result.is_err(), "store failure must not silently use static authentication");
    let text = result.unwrap_err().to_string();
    assert!(text.contains("credential store"), "{text}");
    assert!(!text.contains("private-detail-sentinel"));
    assert_eq!(probe.hits.load(Ordering::SeqCst), 0);
}
