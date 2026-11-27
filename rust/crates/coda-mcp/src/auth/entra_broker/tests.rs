use std::collections::{HashMap, VecDeque};
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

use super::*;
use crate::auth::broker::UnavailableBroker;
use crate::auth::test_http::{RawHandler, TestRequest, TestResponse, spawn_raw_server};
use crate::error::McpError;
use crate::http_client::McpHttpClient;

// ── fake broker ───────────────────────────────────────────────────────────────

type Script = Mutex<VecDeque<Result<BrokerToken, BrokerError>>>;

#[derive(Default)]
struct FakeBroker {
    silent: Script,
    interactive: Script,
    silent_calls: AtomicUsize,
    interactive_calls: AtomicUsize,
    no_interaction: AtomicBool,
    delay: Option<Duration>,
    requests: Mutex<Vec<BrokerRequest>>,
}

impl FakeBroker {
    fn new() -> Arc<Self> {
        Arc::new(Self::default())
    }

    fn with_delay(delay: Duration) -> Arc<Self> {
        Arc::new(Self { delay: Some(delay), ..Self::default() })
    }

    fn silent(&self, r: Result<BrokerToken, BrokerError>) -> &Self {
        self.silent.lock().unwrap().push_back(r);
        self
    }

    fn interactive(&self, r: Result<BrokerToken, BrokerError>) -> &Self {
        self.interactive.lock().unwrap().push_back(r);
        self
    }

    fn silent_calls(&self) -> usize {
        self.silent_calls.load(Ordering::SeqCst)
    }

    fn interactive_calls(&self) -> usize {
        self.interactive_calls.load(Ordering::SeqCst)
    }

    fn request(&self, i: usize) -> BrokerRequest {
        self.requests.lock().unwrap()[i].clone()
    }

    async fn next(
        &self,
        script: &Script,
        counter: &AtomicUsize,
        request: &BrokerRequest,
    ) -> Result<BrokerToken, BrokerError> {
        counter.fetch_add(1, Ordering::SeqCst);
        self.requests.lock().unwrap().push(request.clone());
        if let Some(d) = self.delay {
            tokio::time::sleep(d).await;
        }
        let next = script.lock().unwrap().pop_front();
        next.unwrap_or_else(|| Err(BrokerError::Failed("no scripted response".into())))
    }
}

#[async_trait]
impl TokenBroker for FakeBroker {
    fn is_available(&self) -> bool {
        true
    }

    fn can_interact(&self) -> bool {
        !self.no_interaction.load(Ordering::SeqCst)
    }

    async fn acquire_silent(&self, request: &BrokerRequest) -> Result<BrokerToken, BrokerError> {
        self.next(&self.silent, &self.silent_calls, request).await
    }

    async fn acquire_interactive(
        &self,
        request: &BrokerRequest,
    ) -> Result<BrokerToken, BrokerError> {
        self.next(&self.interactive, &self.interactive_calls, request).await
    }
}

fn tok(value: &str, ttl_secs: i64) -> Result<BrokerToken, BrokerError> {
    Ok(BrokerToken {
        access_token: Secret::new(value.to_owned()),
        expires_at_unix: unix_now() + ttl_secs,
        account: Some(BrokerAccount { id: "acc-1".into(), username: "user@contoso.com".into() }),
    })
}

fn interaction_required() -> Result<BrokerToken, BrokerError> {
    Err(BrokerError::InteractionRequired("AADSTS50076".into()))
}

fn configured() -> McpAuthConfig {
    McpAuthConfig {
        mode: McpAuthMode::EntraBroker,
        client_id: Some("cid".into()),
        tenant: Some("tid".into()),
        scopes: vec!["api://x/.default".into()],
        ..Default::default()
    }
}

fn options(broker: Arc<dyn TokenBroker>, cache: &Arc<BrokerTokenCache>) -> EntraBrokerOptions {
    EntraBrokerOptions {
        broker,
        cache: Arc::clone(cache),
        ..EntraBrokerOptions::defaults("contoso")
    }
}

fn provider_with(config: McpAuthConfig, opts: EntraBrokerOptions) -> EntraBrokerProvider {
    EntraBrokerProvider::new(
        reqwest::Client::new(),
        &reqwest::Url::parse("https://mcp.example.com/mcp").unwrap(),
        config,
        opts,
    )
    .expect("provider")
}

fn provider(broker: &Arc<FakeBroker>) -> EntraBrokerProvider {
    let cache = Arc::new(BrokerTokenCache::new());
    provider_with(configured(), options(broker.clone(), &cache))
}

async fn token_of(p: &EntraBrokerProvider) -> Option<String> {
    p.get_access_token().await.map(|t| t.expose().clone())
}

fn last_error(p: &EntraBrokerProvider) -> String {
    p.status().unwrap().last_error.unwrap_or_default()
}

// ── unit tests ────────────────────────────────────────────────────────────────

#[test]
fn missing_client_id_is_rejected_with_guidance() {
    let cache = Arc::new(BrokerTokenCache::new());
    let err = EntraBrokerProvider::new(
        reqwest::Client::new(),
        &reqwest::Url::parse("https://mcp.example.com/mcp").unwrap(),
        McpAuthConfig { client_id: None, ..configured() },
        options(FakeBroker::new(), &cache),
    )
    .err()
    .expect("missing clientId must fail");
    assert!(err.contains("auth.clientId"), "{err}");
    assert!(err.contains("dynamic client registration"), "{err}");
}

#[tokio::test]
async fn configured_target_acquires_silently_without_any_401() {
    let broker = FakeBroker::new();
    broker.silent(tok("t1", 3600));
    let p = provider(&broker);

    assert_eq!(token_of(&p).await.as_deref(), Some("t1"));
    assert_eq!(token_of(&p).await.as_deref(), Some("t1"), "second call is served from cache");
    assert_eq!(broker.silent_calls(), 1);
    assert_eq!(broker.interactive_calls(), 0);

    let req = broker.request(0);
    assert_eq!(req.authority, "https://login.microsoftonline.com/tid");
    assert_eq!(req.client_id, "cid");
    assert_eq!(req.scopes, vec!["api://x/.default".to_owned()]);

    let status = p.status().unwrap();
    assert_eq!(status.mode, McpAuthMode::EntraBroker);
    assert_eq!(status.account.as_deref(), Some("user@contoso.com"));
    assert!(status.expires_at_unix.unwrap() > unix_now());
    assert_eq!(status.last_error, None);
}

#[tokio::test]
async fn interaction_required_escalates_to_one_interactive_call() {
    let broker = FakeBroker::new();
    broker.silent(interaction_required()).interactive(tok("t1", 3600));
    let p = provider(&broker);

    assert_eq!(token_of(&p).await.as_deref(), Some("t1"));
    assert_eq!(broker.silent_calls(), 1);
    assert_eq!(broker.interactive_calls(), 1);
}

#[tokio::test]
async fn cancelled_prompt_is_not_reshown_until_login() {
    let broker = FakeBroker::new();
    broker
        .silent(interaction_required())
        .silent(interaction_required())
        .interactive(Err(BrokerError::Cancelled));
    let p = provider(&broker);

    assert!(!p.handle_unauthorized(None).await);
    assert!(!p.handle_unauthorized(None).await);
    assert_eq!(broker.interactive_calls(), 1, "declined prompt must not be re-shown");
    assert!(last_error(&p).contains("/mcp login contoso"), "{}", last_error(&p));

    broker.silent(interaction_required()).interactive(tok("t2", 3600));
    let r = p.login().await;
    assert!(r.succeeded, "{:?}", r.error);
    assert_eq!(broker.interactive_calls(), 2);
    assert_eq!(token_of(&p).await.as_deref(), Some("t2"));
    assert_eq!(p.status().unwrap().last_error, None);
}

#[tokio::test]
async fn no_console_window_means_no_prompt_and_a_login_hint() {
    let broker = FakeBroker::new();
    broker.no_interaction.store(true, Ordering::SeqCst);
    broker.silent(interaction_required());
    let p = provider(&broker);

    assert_eq!(token_of(&p).await, None);
    assert_eq!(broker.interactive_calls(), 0);
    let err = last_error(&p);
    assert!(err.contains("no console window"), "{err}");
    assert!(err.contains("`/mcp login contoso`"), "{err}");
}

#[tokio::test]
async fn near_expiry_refreshes_silently_and_keeps_old_token_on_failure() {
    let broker = FakeBroker::new();
    broker
        .silent(tok("short", 100))
        .silent(Err(BrokerError::Failed("AADSTS-transient".into())))
        .silent(tok("renewed", 3600));
    let p = provider(&broker);

    assert_eq!(token_of(&p).await.as_deref(), Some("short"));
    assert_eq!(token_of(&p).await.as_deref(), Some("short"), "refresh failed: old token kept");
    assert_eq!(broker.silent_calls(), 2);
    assert!(last_error(&p).contains("AADSTS-transient"));
    assert_eq!(token_of(&p).await.as_deref(), Some("renewed"));
    assert_eq!(broker.silent_calls(), 3);
    assert_eq!(broker.interactive_calls(), 0);
}

#[tokio::test]
async fn expired_token_is_not_served_when_refresh_fails() {
    let broker = FakeBroker::new();
    broker.silent(tok("stale", -5)).silent(Err(BrokerError::Failed("down".into())));
    let p = provider(&broker);
    assert_eq!(token_of(&p).await.as_deref(), Some("stale"), "fresh acquisition is returned");
    assert_eq!(token_of(&p).await, None);
}

#[tokio::test]
async fn unauthorized_with_token_reacquires_exactly_once() {
    let broker = FakeBroker::new();
    broker.silent(tok("t1", 3600)).silent(tok("t2", 3600));
    let p = provider(&broker);

    assert_eq!(token_of(&p).await.as_deref(), Some("t1"));
    assert!(p.handle_unauthorized(Some(r#"Bearer error="invalid_token""#)).await);
    assert_eq!(broker.silent_calls(), 2);
    assert_eq!(token_of(&p).await.as_deref(), Some("t2"));
    assert_eq!(broker.silent_calls(), 2);
}

#[tokio::test]
async fn logout_stops_automatic_sign_in_until_login() {
    let broker = FakeBroker::new();
    broker.silent(tok("t1", 3600));
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    let cache = Arc::new(BrokerTokenCache::new());
    let p = provider_with(
        configured(),
        EntraBrokerOptions { store: Some(Arc::clone(&store)), ..options(broker.clone(), &cache) },
    );
    assert_eq!(token_of(&p).await.as_deref(), Some("t1"));

    let r = p.logout().await;
    assert!(r.succeeded);
    assert_eq!(token_of(&p).await, None);
    assert!(!p.handle_unauthorized(None).await);
    assert_eq!(broker.silent_calls(), 1, "no broker call while signed out");
    assert_eq!(p.status().unwrap().account, None);
    assert!(last_error(&p).contains("/mcp login contoso"));

    // A restart (fresh cache, same store) stays signed out.
    let restarted = provider_with(
        configured(),
        EntraBrokerOptions {
            store: Some(Arc::clone(&store)),
            ..options(broker.clone(), &Arc::new(BrokerTokenCache::new()))
        },
    );
    assert_eq!(token_of(&restarted).await, None);
    assert_eq!(broker.silent_calls(), 1);

    broker.silent(tok("t2", 3600));
    assert!(restarted.login().await.succeeded);
    assert_eq!(token_of(&restarted).await.as_deref(), Some("t2"));
    let saved = store
        .get("mcp-broker-account:https://mcp.example.com/mcp")
        .await
        .unwrap()
        .expect("account persisted");
    assert!(saved.contains("\"signedOut\":false"), "{saved}");
    assert!(saved.contains("acc-1"), "{saved}");
    assert!(!saved.contains("t2"), "tokens are never persisted: {saved}");
}

#[tokio::test]
async fn persisted_account_is_reused_after_restart() {
    let broker = FakeBroker::new();
    broker.silent(tok("t1", 3600)).silent(tok("t2", 3600));
    let store: Arc<dyn CredentialStore> = Arc::new(InMemoryStore::new());
    let make = || {
        provider_with(
            configured(),
            EntraBrokerOptions {
                store: Some(Arc::clone(&store)),
                ..options(broker.clone(), &Arc::new(BrokerTokenCache::new()))
            },
        )
    };
    assert!(token_of(&make()).await.is_some());
    assert!(token_of(&make()).await.is_some());
    assert_eq!(broker.request(0).account_id, None);
    assert_eq!(broker.request(1).account_id.as_deref(), Some("acc-1"));
}

#[tokio::test]
async fn shared_cache_serves_a_new_provider_without_a_broker_call() {
    let broker = FakeBroker::new();
    broker.silent(tok("t1", 3600));
    let cache = Arc::new(BrokerTokenCache::new());
    let first = provider_with(configured(), options(broker.clone(), &cache));
    assert!(token_of(&first).await.is_some());
    let restarted = provider_with(configured(), options(broker.clone(), &cache));
    assert_eq!(token_of(&restarted).await.as_deref(), Some("t1"));
    assert_eq!(broker.silent_calls(), 1);
}

#[tokio::test]
async fn concurrent_requests_share_one_broker_call() {
    let broker = FakeBroker::with_delay(Duration::from_millis(50));
    broker.silent(tok("t1", 3600));
    let p = provider(&broker);

    let tokens = futures::future::join_all((0..8).map(|_| token_of(&p))).await;
    assert!(tokens.iter().all(|t| t.as_deref() == Some("t1")), "{tokens:?}");
    assert_eq!(broker.silent_calls(), 1);
}

#[tokio::test(start_paused = true)]
async fn hung_broker_times_out_with_actionable_error() {
    let broker = FakeBroker::with_delay(Duration::from_secs(3600));
    broker.silent(tok("never", 3600));
    let p = provider(&broker);

    assert_eq!(token_of(&p).await, None);
    let err = last_error(&p);
    assert!(err.contains("did not respond within 30s"), "{err}");
    assert!(err.contains("/mcp login contoso"), "{err}");
}

#[tokio::test]
async fn unavailable_broker_delegates_with_entra_broker_status() {
    let cache = Arc::new(BrokerTokenCache::new());
    let p = provider_with(
        configured(),
        options(Arc::new(UnavailableBroker::new("not Windows")), &cache),
    );
    assert_eq!(p.status().unwrap().mode, McpAuthMode::EntraBroker);
    assert_eq!(token_of(&p).await, None);
    assert!(p.logout().await.succeeded);
}

// ── integration: McpHttpClient against a fake Entra-protected MCP server ─────

fn mcp_server(accepted: &'static str) -> Arc<RawHandler> {
    Arc::new(move |req: &TestRequest, port: u16| {
        if req.path == "/.well-known/oauth-protected-resource" {
            return TestResponse::json(
                200,
                serde_json::json!({
                    "resource": format!("http://127.0.0.1:{port}/mcp"),
                    "authorization_servers": ["https://login.microsoftonline.com/tid/v2.0"],
                    "scopes_supported": ["api://x/.default"],
                })
                .to_string(),
            );
        }
        if req.path != "/mcp" || req.method != "POST" {
            return TestResponse::json(404, "{}");
        }
        if req.header("authorization") != Some(format!("Bearer {accepted}").as_str()) {
            return TestResponse {
                status: 401,
                headers: vec![(
                    "WWW-Authenticate".into(),
                    format!(
                        "Bearer resource_metadata=\"http://127.0.0.1:{port}/.well-known/oauth-protected-resource\""
                    ),
                )],
                body: "{}".into(),
            };
        }
        let msg: serde_json::Value = serde_json::from_str(&req.body).unwrap_or_default();
        let Some(id) = msg.get("id").cloned() else {
            return TestResponse::json(202, "");
        };
        let result = match msg.get("method").and_then(|m| m.as_str()) {
            Some("initialize") => serde_json::json!({
                "protocolVersion": "2025-06-18",
                "capabilities": { "tools": {} },
                "serverInfo": { "name": "fake-entra", "version": "1" },
            }),
            Some("tools/list") => serde_json::json!({
                "tools": [{ "name": "echo", "inputSchema": { "type": "object" } }],
            }),
            _ => serde_json::json!({}),
        };
        TestResponse::json(
            200,
            serde_json::json!({ "jsonrpc": "2.0", "id": id, "result": result }).to_string(),
        )
    })
}

async fn connect(port: u16, auth: Arc<dyn McpAuthProvider>) -> Result<McpHttpClient, McpError> {
    McpHttpClient::connect_unvetted(
        "contoso",
        &format!("http://127.0.0.1:{port}/mcp"),
        &HashMap::new(),
        Some(auth),
        Duration::from_secs(10),
        Duration::from_secs(10),
    )
    .await
}

fn http_provider(
    port: u16,
    config: McpAuthConfig,
    opts: EntraBrokerOptions,
) -> Arc<EntraBrokerProvider> {
    Arc::new(
        EntraBrokerProvider::new(
            reqwest::Client::new(),
            &reqwest::Url::parse(&format!("http://127.0.0.1:{port}/mcp")).unwrap(),
            config,
            opts,
        )
        .expect("provider"),
    )
}

/// Only a client id: the target comes from PRM discovery on the first 401.
fn discovered() -> McpAuthConfig {
    McpAuthConfig {
        mode: McpAuthMode::EntraBroker,
        client_id: Some("cid".into()),
        ..Default::default()
    }
}

#[tokio::test]
async fn http_silent_success_after_prm_discovery() {
    let (port, _) = spawn_raw_server(mcp_server("good")).await;
    let broker = FakeBroker::new();
    broker.silent(tok("good", 3600));
    let cache = Arc::new(BrokerTokenCache::new());
    let p = http_provider(port, discovered(), options(broker.clone(), &cache));

    let client = connect(port, p).await.expect("connected");
    assert_eq!(client.tools.len(), 1);
    assert_eq!(broker.silent_calls(), 1);
    assert_eq!(broker.interactive_calls(), 0);
    let req = broker.request(0);
    assert_eq!(req.authority, "https://login.microsoftonline.com/tid");
    assert_eq!(req.scopes, vec!["api://x/.default".to_owned()]);
}

#[tokio::test]
async fn http_silent_then_interactive_connects() {
    let (port, _) = spawn_raw_server(mcp_server("good")).await;
    let broker = FakeBroker::new();
    broker.silent(interaction_required()).interactive(tok("good", 3600));
    let cache = Arc::new(BrokerTokenCache::new());
    let p = http_provider(port, discovered(), options(broker.clone(), &cache));

    connect(port, p).await.expect("connected");
    assert_eq!(broker.silent_calls(), 1);
    assert_eq!(broker.interactive_calls(), 1);
}

#[tokio::test]
async fn http_unavailable_broker_falls_back_without_opening_a_browser() {
    let (port, _) = spawn_raw_server(mcp_server("good")).await;
    let logs: Arc<Mutex<Vec<String>>> = Arc::default();
    let sink = Arc::clone(&logs);
    let cache = Arc::new(BrokerTokenCache::new());
    let opts = EntraBrokerOptions {
        open_browser: Some(Arc::new(|_: &str| -> Result<(), String> {
            panic!("the headless fallback must never open a browser")
        })),
        log: Some(Arc::new(move |m: &str| sink.lock().unwrap().push(m.to_owned()))),
        ..options(Arc::new(UnavailableBroker::new("not Windows")), &cache)
    };
    let p = http_provider(port, discovered(), opts);

    assert!(connect(port, p.clone()).await.is_err());
    let status = p.status().unwrap();
    assert_eq!(status.mode, McpAuthMode::EntraBroker);
    let err = status.last_error.unwrap_or_default();
    assert!(err.contains("`/mcp login contoso`"), "{err}");
    assert!(logs.lock().unwrap().iter().any(|m| m.contains("/mcp login contoso")));
}

#[tokio::test]
async fn http_rejected_token_is_reacquired_once() {
    let (port, _) = spawn_raw_server(mcp_server("good")).await;
    let broker = FakeBroker::new();
    broker.silent(tok("bad", 3600)).silent(tok("good", 3600));
    let cache = Arc::new(BrokerTokenCache::new());
    let p = http_provider(port, configured(), options(broker.clone(), &cache));

    connect(port, p).await.expect("connected");
    assert_eq!(broker.silent_calls(), 2);
    assert_eq!(broker.interactive_calls(), 0);
}
